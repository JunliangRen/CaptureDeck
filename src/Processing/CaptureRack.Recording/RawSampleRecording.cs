using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using CaptureRack.Core;
using CaptureRack.Recording.Windows;

namespace CaptureRack.Recording;

internal class RawSampleRecording : IDisposable
{
    private const int QueueCapacity = 64 * 1024 * 1024;
    private readonly Channel<(byte[] Buffer, int Length, bool Discontinuity)> queue = Channel.CreateUnbounded<(byte[], int, bool)>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object gate = new();
    private readonly object finalizationGate = new();
    private readonly FileStream file;
    private readonly Task writer;
    protected delegate void AnalyzeBytes(ReadOnlySpan<byte> data, bool discontinuity);
    private readonly AnalyzeBytes analyze;
    private readonly Action finishAnalysis;
    private readonly Func<string?> validate;
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

    protected RawSampleRecording(string outputPath, AnalyzeBytes analyze, Action finishAnalysis, Func<string?> validate)
    {
        this.outputPath = outputPath;
        PartialPath = outputPath + ".partial";
        this.analyze = analyze;
        this.finishAnalysis = finishAnalysis;
        this.validate = validate;
        file = new FileStream(PartialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 1, FileOptions.SequentialScan);
        writer = Task.Run(Write);
    }

    public string PartialPath { get; }
    public bool Finalized => Volatile.Read(ref finalized);
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

    public bool TryReceive(CaptureSample sample)
    {
        var bytes = sample.Bytes;
        lock (gate)
        {
            if (closed)
                return false;
            var now = Math.Max(1, clock.Elapsed.Ticks);
            if (receivedSamples == 0)
            {
                Interlocked.Exchange(ref firstSample, now);
                InitialDiscontinuity = sample.Discontinuity;
            }
            Interlocked.Exchange(ref lastSample, now);
            Interlocked.Add(ref receivedBytes, bytes.Length);
            Interlocked.Increment(ref receivedSamples);
            if (sample.Discontinuity)
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
                if (!queue.Writer.TryWrite((buffer, bytes.Length, sample.Discontinuity)))
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
                    analyze(data, block.Discontinuity);
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
        lock (finalizationGate)
        {
            Close();
            if (finalized || Failure is not null)
                return;
            if (!HasSamples)
            {
                Fail(7, "Capture ended without receiving any data.");
                return;
            }
            if (validate() is { } error)
            {
                Fail(4, error);
                return;
            }
            try
            {
                // Same-volume rename without replacement, with the same write-through guarantee as the native probe.
                WindowsFile.MoveWriteThrough(PartialPath, outputPath);
                Volatile.Write(ref finalized, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(5, ex.Message); }
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
        try
        {
            writer.GetAwaiter().GetResult();
            finishAnalysis();
            file.Flush(flushToDisk: true);
        }
        catch (Exception ex) { Fail(5, ex.Message); }
        finally
        {
            try { file.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(5, ex.Message); }
        }
    }

    public void Dispose()
    {
        lock (finalizationGate)
            Close();
    }
}
