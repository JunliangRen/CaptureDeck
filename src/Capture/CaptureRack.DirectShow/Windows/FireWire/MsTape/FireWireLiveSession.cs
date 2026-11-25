using System.Diagnostics;
using CaptureRack.Core;
using CaptureRack.DirectShow.Windows.Interop;
using CaptureRack.QualityControl;
using CaptureRack.Recording.TransportStream;

namespace CaptureRack.DirectShow.Windows.FireWire.MsTape;

public sealed record FireWireClip(string Path, long Bytes, TsStatistics Analysis, string? Error);

public sealed class FireWireClipWriter
{
    private readonly TsRecording recording;
    private readonly TaskCompletionSource<FireWireClip> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string path;

    internal FireWireClipWriter(string path, TsLayout layout)
    {
        this.path = path;
        if (Path.Exists(path))
            throw new IOException($"Output already exists: {path}.");
        recording = new TsRecording(path, layout);
    }

    public Task<FireWireClip> Completion => completion.Task;
    public long WrittenBytes => recording.WrittenBytes;
    internal bool Receive(CaptureSample sample) => recording.TryReceive(sample);

    // The session detaches this writer before calling Finish.
    internal void Finish(string? error)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using (recording)
                {
                    if (error is not null)
                        recording.Fail(6, error);
                    recording.Complete();
                    completion.TrySetResult(new(recording.Finalized ? path : recording.PartialPath,
                        recording.WrittenBytes, recording.Analysis, recording.Failure?.Message));
                }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
    }
}

public sealed class FireWireLiveSession
{
    private readonly object gate = new();
    private TsLayout? layout;
    private FireWireClipWriter? activeRecording;
    private long receivedBytes;
    private long lastInput;
    private readonly object infoGate = new();
    private FireWireTsMonitor? tsMonitor;
    private FireWireTsInfo? streamInfo;
    private FireWireTransportInfo? transportInfo;
    private int transportSupported = -1;
    internal static readonly TimeSpan InputGap = TimeSpan.FromSeconds(2);
    private bool ended;

    public FireWireTsInfo? StreamInfo => HasRecentInput ? Volatile.Read(ref streamInfo) : null;
    public FireWireTransportInfo? TransportInfo => Volatile.Read(ref transportInfo);
    public bool? SupportsTransportInfo => Volatile.Read(ref transportSupported) switch { 0 => false, 1 => true, _ => null };

    public long ReceivedBytes => Interlocked.Read(ref receivedBytes);
    public bool HasRecentInput => Interlocked.Read(ref lastInput) is var input && input != 0 &&
        Stopwatch.GetElapsedTime(input) < InputGap;

    public FireWireClipWriter StartRecording(string path)
    {
        lock (gate)
        {
            if (ended || layout is not { } negotiated || !HasRecentInput || activeRecording is not null)
                throw new InvalidOperationException("Wait for i.LINK stream data before starting recording.");
            return activeRecording = new FireWireClipWriter(path, negotiated);
        }
    }

    public void StopRecording(FireWireClipWriter writer)
    {
        lock (gate)
        {
            if (activeRecording != writer)
                return;
            activeRecording = null;
            writer.Finish(null);
        }
    }

    public unsafe void Run(string deviceId, CaptureSampleHandler? preview, CancellationToken stop, Action? streamChanged = null)
    {
        FireWireCapture.CheckPlatform();
        string? error = null;
        try
        {
            stop.ThrowIfCancellationRequested();
            using var apartment = new ComApartment();
            using var devices = MsTapeDevices.Scan();
            using var source = devices.Bind(deviceId);
            using var selected = MsTapeDevices.SelectOutput(source.Value);
            using var sink = new DirectShowSink(selected.MediaType, Receive, ReportFailure);
            using var graph = new FireWireGraph();
            try
            {
                graph.Connect(source.Value, selected, sink);
                stop.ThrowIfCancellationRequested();
                lock (gate)
                    layout = selected.Layout;
                tsMonitor = new FireWireTsMonitor(selected.Layout);
                using var transport = FireWireTransportReader.Open(source.Value);
                Volatile.Write(ref transportSupported, transport is null ? 0 : 1);
                graph.Start();
                var lastPoll = 0L;

                while (!stop.IsCancellationRequested && Volatile.Read(ref error) is null &&
                    !graph.ReadEvents(ReportFailure) && !sink.Ended)
                {
                    var now = Stopwatch.GetTimestamp();
                    if (transport is not null && (lastPoll == 0 || Stopwatch.GetElapsedTime(lastPoll, now).TotalSeconds >= 1))
                    {
                        Volatile.Write(ref transportInfo, transport.Read());
                        lastPoll = Stopwatch.GetTimestamp();
                    }
                    stop.WaitHandle.WaitOne(50);
                }
            }
            finally
            {
                graph.Stop(sink, ReportFailure);
            }

            if (error is not null)
                throw new FireWireException(6, error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error ??= ex.Message;
            throw;
        }
        finally
        {
            lock (gate)
            {
                ended = true;
                activeRecording?.Finish(error);
                activeRecording = null;
            }
        }

        bool Receive(CaptureSample sample)
        {
            if (stop.IsCancellationRequested)
                return true;
            lock (gate)
            {
                if (activeRecording is { } recording && !recording.Receive(sample))
                {
                    activeRecording = null;
                    recording.Finish(null);
                }
                Interlocked.Add(ref receivedBytes, sample.Bytes.Length);
            }
            if (!sample.Bytes.IsEmpty)
            {
                lock (infoGate)
                {
                    if (tsMonitor is { } monitor)
                    {
                        if (monitor.Feed(sample.Bytes))
                            streamChanged?.Invoke();
                        Volatile.Write(ref streamInfo, monitor.Snapshot);
                    }
                    Interlocked.Exchange(ref lastInput, Stopwatch.GetTimestamp());
                }
            }
            return preview is null || preview(sample);
        }

        void ReportFailure(string message) => Interlocked.CompareExchange(ref error, message, null);
    }
}
