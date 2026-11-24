using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;

namespace CaptureDeck.FireWire;

public static class TsFile
{
    public static (TsLayout Layout, TsStatistics Analysis) Analyze(string path, TsLayout? layout = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        var count = stream.ReadAtLeast(buffer, buffer.Length, false);
        var detected = layout ?? TsAnalyzer.Detect(buffer.AsSpan(0, count));
        var analyzer = new TsAnalyzer(detected);
        while (count > 0)
        {
            analyzer.Feed(buffer.AsSpan(0, count));
            count = stream.Read(buffer);
        }
        analyzer.Finish();
        return (detected, analyzer.Statistics);
    }
}

internal sealed class TsRecording : IDisposable
{
    private const int QueueCapacity = 64 * 1024 * 1024;
    private readonly Channel<(byte[] Buffer, int Length)> queue = Channel.CreateUnbounded<(byte[], int)>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object gate = new();
    private readonly FileStream file;
    private readonly Task writer;
    private readonly TsAnalyzer analyzer;
    private readonly string outputPath;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long receivedBytes;
    private long receivedSamples;
    private long discontinuitySamples;
    private long firstSample;
    private long lastSample;
    private long writtenBytes;
    private long queuedBytes;
    private long peakQueueBytes;
    private long rejectedBytes;
    private long processedBytes;
    private int queued;
    private bool finalized;
    private bool closed;
    private (int Code, string Message)? failure;

    public TsRecording(string outputPath, TsLayout layout)
    {
        this.outputPath = outputPath;
        PartialPath = outputPath + ".partial";
        analyzer = new TsAnalyzer(layout);
        file = new FileStream(PartialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 1, FileOptions.SequentialScan);
        writer = Task.Run(Write);
    }

    public string PartialPath { get; }
    public TsStatistics Analysis => analyzer.Statistics;
    public bool Finalized => finalized;
    public long ReceivedBytes => Interlocked.Read(ref receivedBytes);
    public long ReceivedSamples => Interlocked.Read(ref receivedSamples);
    public long DiscontinuitySamples => Interlocked.Read(ref discontinuitySamples);
    public long WrittenBytes => Interlocked.Read(ref writtenBytes);
    public long QueuedBytes => Interlocked.Read(ref queuedBytes);
    public long PeakQueueBytes => Interlocked.Read(ref peakQueueBytes);
    public long RejectedBytes => Interlocked.Read(ref rejectedBytes);
    public long ProcessedBytes => Interlocked.Read(ref processedBytes);
    public bool HasSamples => Interlocked.Read(ref firstSample) != 0;
    public TimeSpan Elapsed => clock.Elapsed;
    public TimeSpan SinceFirstSample => TimeSpan.FromTicks(Math.Max(0, clock.Elapsed.Ticks - Interlocked.Read(ref firstSample)));
    public TimeSpan SinceLastSample => TimeSpan.FromTicks(Math.Max(0, clock.Elapsed.Ticks - Interlocked.Read(ref lastSample)));
    public bool InitialDiscontinuity { get; private set; }
    public (int Code, string Message)? Failure { get { lock (gate) return failure; } }

    public void Fail(int code, string message)
    {
        lock (gate)
            failure ??= (code, message);
    }

    public bool TryReceive(ReadOnlySpan<byte> bytes, bool discontinuity)
    {
        lock (gate)
        {
            if (closed)
                return false;
            var now = Math.Max(1, clock.Elapsed.Ticks);
            if (receivedSamples == 0)
            {
                Interlocked.Exchange(ref firstSample, now);
                InitialDiscontinuity = discontinuity;
            }
            Interlocked.Exchange(ref lastSample, now);
            Interlocked.Add(ref receivedBytes, bytes.Length);
            Interlocked.Increment(ref receivedSamples);
            if (discontinuity)
                Interlocked.Increment(ref discontinuitySamples);

            if (failure is not null || bytes.Length > QueueCapacity - queued)
            {
                Interlocked.Add(ref rejectedBytes, bytes.Length);
                failure ??= (6, "Capture queue exceeded 64 MiB before the writer could drain it.");
                return false;
            }

            byte[]? buffer = null;
            try
            {
                buffer = ArrayPool<byte>.Shared.Rent(bytes.Length);
                bytes.CopyTo(buffer);
                if (!queue.Writer.TryWrite((buffer, bytes.Length)))
                {
                    Interlocked.Add(ref rejectedBytes, bytes.Length);
                    failure ??= (6, "Capture queue closed.");
                    return false;
                }
                buffer = null; // The writer now owns the rented buffer.
                queued += bytes.Length;
                Interlocked.Add(ref queuedBytes, bytes.Length);
                Interlocked.Exchange(ref peakQueueBytes, Math.Max(peakQueueBytes, queued));
                return true;
            }
            catch (OutOfMemoryException ex)
            {
                Interlocked.Add(ref rejectedBytes, bytes.Length);
                failure ??= (6, ex.Message);
                return false;
            }
            finally
            {
                if (buffer is not null)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private async Task Write()
    {
        var writeFailed = false;
        await foreach (var block in queue.Reader.ReadAllAsync())
        {
            lock (gate)
                queued -= block.Length;
            try
            {
                // After a writer error, return any remaining pooled buffers.
                // A capture-side error still allows data already queued to drain.
                if (!writeFailed)
                {
                    var data = block.Buffer.AsSpan(0, block.Length);
                    analyzer.Feed(data);
                    Interlocked.Add(ref processedBytes, block.Length);
                    file.Write(data);
                    Interlocked.Add(ref writtenBytes, block.Length);
                }
            }
            catch (Exception ex)
            {
                writeFailed = true;
                Fail(5, ex.Message);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(block.Buffer);
            }
        }
    }

    public void Complete()
    {
        Close();
        if (Failure is null)
        {
            if (!HasSamples)
                Fail(7, "Capture ended without receiving any data.");
            else if (Analysis.TrailingBytes != 0 || Analysis.SyncErrors + Analysis.MalformedPackets == Analysis.Packets)
                Fail(4, "Capture ended with an incomplete packet or no structurally valid TS packets.");
        }
        if (Failure is null)
        {
            try
            {
                // Same-volume rename without replacement, with the same write-through guarantee as the native probe.
                WindowsFile.MoveWriteThrough(PartialPath, outputPath);
                finalized = true;
            }
            catch (IOException ex)
            {
                Fail(5, ex.Message);
            }
        }
    }

    private void Close()
    {
        lock (gate)
        {
            if (closed)
                return;
            closed = true;
            queue.Writer.TryComplete();
        }
        writer.GetAwaiter().GetResult();
        analyzer.Finish();
        try { file.Flush(flushToDisk: true); }
        catch (IOException ex) { Fail(5, ex.Message); }
        try { file.Dispose(); }
        catch (IOException ex) { Fail(5, ex.Message); }
    }

    public void Dispose() => Close();
}

internal static partial class WindowsFile
{
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW",
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16, SetLastError = true)]
    private static partial int MoveFileEx(string source, string destination, uint flags);

    public static void MoveWriteThrough(string source, string destination)
    {
        const uint WriteThrough = 8;
        if (MoveFileEx(source, destination, WriteThrough) == 0)
            throw new IOException($"Could not finalize {destination}.",
                new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError()));
    }
}
