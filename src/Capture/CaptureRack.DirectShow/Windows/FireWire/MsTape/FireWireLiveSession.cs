using System.Diagnostics;
using CaptureRack.Core;
using CaptureRack.DirectShow.Windows.Interop;
using CaptureRack.QualityControl;
using CaptureRack.Recording;
using CaptureRack.Recording.TransportStream;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureRack.DirectShow.Windows.FireWire.MsTape;

public sealed record FireWireClip(string Path, long Bytes, TsStatistics? Analysis, DvStatistics? DvAnalysis, string? Error);

public sealed class FireWireClipWriter
{
    private readonly RawSampleRecording recording;
    private readonly TaskCompletionSource<FireWireClip> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string path;

    private int leadingBytes;

    internal FireWireClipWriter(string path, FireWireMediaFormat format, int leadingBytes = 0)
    {
        this.path = path;
        if (Path.Exists(path))
            throw new IOException($"Output already exists: {path}.");
        this.leadingBytes = leadingBytes;
        recording = format.Transport is { } layout ? new TsRecording(path, layout) : new DvRecording(path, format.Encoding);
    }

    public Task<FireWireClip> Completion => completion.Task;
    public long WrittenBytes => recording.WrittenBytes;
    internal bool Receive(CaptureSample sample)
    {
        if (leadingBytes != 0 && sample.Discontinuity && DvStreamAnalyzer.FrameSize(sample.Bytes) != 0)
            leadingBytes = 0;
        if (leadingBytes == 0)
            return recording.TryReceive(sample);
        var skipped = Math.Min(leadingBytes, sample.Bytes.Length);
        leadingBytes -= skipped;
        return skipped == sample.Bytes.Length || recording.TryReceive(new(sample.Bytes[skipped..],
            sample.Discontinuity, null, sample.EndTime));
    }

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
                        recording.WrittenBytes, (recording as TsRecording)?.Analysis, (recording as DvRecording)?.Analysis,
                        recording.Failure?.Message));
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
    private FireWireMediaFormat? format;
    private FireWireClipWriter? activeRecording;
    private long receivedBytes;
    private long lastInput;
    private FireWireTsMonitor? tsMonitor;
    private DvStreamAnalyzer? dvMonitor;
    private DvStatistics? dvInfo;
    private FireWireTsInfo? streamInfo;
    private FireWireTransportInfo? transportInfo;
    private int transportSupported = -1;
    internal static readonly TimeSpan InputGap = TimeSpan.FromSeconds(2);
    private bool ended;

    public FireWireTsInfo? StreamInfo => HasRecentInput ? Volatile.Read(ref streamInfo) : null;
    public DvStatistics? DvInfo => HasRecentInput ? Volatile.Read(ref dvInfo) : null;
    public FireWireTransportInfo? TransportInfo => Volatile.Read(ref transportInfo);
    public bool? SupportsTransportInfo => Volatile.Read(ref transportSupported) switch { 0 => false, 1 => true, _ => null };

    public long ReceivedBytes => Interlocked.Read(ref receivedBytes);
    public FireWireMediaFormat? Format { get { lock (gate) return format; } }
    public long ReceivedUnits => Format?.Transport is { } transport ? ReceivedBytes / transport.Stride : Volatile.Read(ref dvInfo)?.Frames ?? 0;
    public bool CanRecord
    {
        get
        {
            lock (gate)
                return CanRecordCore;
        }
    }
    private bool CanRecordCore => !ended && HasRecentInput && format is not null &&
        (dvMonitor is null || (dvInfo?.Frames > 0 && dvMonitor.BytesToFrameBoundary is not null));
    public bool HasRecentInput => Interlocked.Read(ref lastInput) is var input && input != 0 &&
        Stopwatch.GetElapsedTime(input) < InputGap;

    public FireWireClipWriter StartRecording(string path)
    {
        lock (gate)
        {
            if (!CanRecordCore || format is not { } negotiated || activeRecording is not null)
                throw new InvalidOperationException("Wait for i.LINK stream data before starting recording.");
            return activeRecording = new FireWireClipWriter(path, negotiated, dvMonitor?.BytesToFrameBoundary ?? 0);
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

    public unsafe void Run(string deviceId, CaptureSampleHandler? preview, CancellationToken stop, Action? streamChanged = null,
        string? simulationFfmpeg = null)
    {
        FireWireCapture.CheckPlatform();
        string? error = null;
        try
        {
            stop.ThrowIfCancellationRequested();
            using var apartment = new ComApartment();
            if (FireWireSimulation.All.FirstOrDefault(candidate => candidate.Id == deviceId) is { } simulation)
            {
                var executable = simulationFfmpeg ?? QcReports.FindTools().FfmpegPath ??
                    throw new IOException("FFmpeg is required to generate the simulated i.LINK source.");
                var fixture = simulation.MakeFixtureAsync(executable, stop).GetAwaiter().GetResult();
                RunSimulation(simulation, fixture, Receive, ReportFailure, stop);
            }
            else
            {
                using var devices = MsTapeDevices.Scan();
                var device = devices.Find(deviceId) ?? throw new FireWireException(3, $"Tape device not found: {deviceId}.");
                using var source = devices.Bind(deviceId);
                using var selected = MsTapeDevices.SelectOutput(source.Value, device.IsDv);
                try { RunGraph(source.Value, selected, Receive, ReportFailure, stop); }
                catch (FireWireException ex) when (ex.ExitCode == 4 && Format is null && selected.Format.Transport == TsLayout.Ts192)
                {
                    using var fallback = MsTapeDevices.SelectOutput(source.Value, requested: new(TsLayout.Ts188));
                    RunGraph(source.Value, fallback, Receive, ReportFailure, stop);
                }
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
                if (!sample.Bytes.IsEmpty)
                {
                    if (tsMonitor is { } monitor)
                    {
                        if (monitor.Feed(sample.Bytes))
                            streamChanged?.Invoke();
                        Volatile.Write(ref streamInfo, monitor.Snapshot);
                    }
                    if (dvMonitor is { } dv)
                    {
                        var previous = Volatile.Read(ref dvInfo);
                        dv.Feed(sample.Bytes, sample.Discontinuity);
                        var current = dv.Snapshot;
                        if (previous is not null && current.FormatChanges != previous.FormatChanges)
                            streamChanged?.Invoke();
                        Volatile.Write(ref dvInfo, current);
                    }
                    Interlocked.Exchange(ref lastInput, Stopwatch.GetTimestamp());
                }
            }
            return preview is null || preview(sample);
        }

        void ReportFailure(string message) => Interlocked.CompareExchange(ref error, message, null);
    }

    private unsafe void RunGraph(nint source, MsTapeOutput selected, CaptureSampleHandler receive,
        Action<string> failure, CancellationToken stop, DirectShowSink? simulatedSink = null,
        Action? replay = null)
    {
        int failed = 0;
        void DeviceFailure(string message)
        {
            Volatile.Write(ref failed, 1);
            failure(message);
        }
        using var nativeSink = simulatedSink is null ? FireWireGraph.CreateSink(selected, receive, DeviceFailure) : null;
        var sink = simulatedSink ?? nativeSink!;
        using var graph = new FireWireGraph();
        try
        {
            graph.Connect(source, selected, sink);
            stop.ThrowIfCancellationRequested();
            lock (gate)
            {
                format = selected.Format;
                tsMonitor = selected.Format.Transport is { } layout ? new FireWireTsMonitor(layout) : null;
                dvMonitor = selected.Format.IsDv ? new(selected.Format.Encoding) : null;
            }
            using var transport = replay is null ? FireWireTransportReader.Open(source) : null;
            Volatile.Write(ref transportSupported, transport is null ? 0 : 1);
            graph.Start();
            if (replay is not null)
            {
                replay();
                return;
            }

            var lastPoll = 0L;
            while (!stop.IsCancellationRequested && Volatile.Read(ref failed) == 0 && !graph.ReadEvents(DeviceFailure) && !sink.Ended)
            {
                var now = Stopwatch.GetTimestamp();
                if (transport is not null && (lastPoll == 0 || Stopwatch.GetElapsedTime(lastPoll, now).TotalSeconds >= 1))
                {
                    Volatile.Write(ref transportInfo, transport.Read());
                    lastPoll = now;
                }
                stop.WaitHandle.WaitOne(50);
            }
        }
        finally { graph.Stop(sink, failure); }
    }

    private unsafe void RunSimulation(FireWireSimulation simulation, byte[] fixture, CaptureSampleHandler receive,
        Action<string> failure, CancellationToken stop)
    {
        uint* block = stackalloc uint[8];
        new Span<uint>(block, 8).Clear();
        var media = new DirectShowMediaType { FixedSizeSamples = 1, SampleSize = (uint)(simulation.UnitBytes * simulation.SampleUnits) };
        if (simulation.Format.Transport is { } layout)
        {
            media.Major = MsTapeMediaTypes.StreamType;
            media.Subtype = MsTapeMediaTypes.Mpeg2TransportStride;
            if (layout == TsLayout.Ts192)
            {
                block[0] = 4;
                block[1] = 188;
                block[2] = 192;
                media.Format = (byte*)block;
                media.FormatSize = 12;
            }
        }
        else
        {
            media.Major = MsTapeMediaTypes.Interleaved;
            media.Subtype = MsTapeMediaTypes.DvSubtype(simulation.Format.Encoding);
            media.FormatType = MsTapeMediaTypes.DvInfo;
            media.Format = (byte*)block;
            media.FormatSize = 32;
        }
        var sampleSize = (int)media.SampleSize;
        using var sink = new DirectShowSink(&media, receive, failure);
        using var replayFilter = new FireWireReplayFilter(sink, sampleSize);
        using var source = new ComReference(Export(replayFilter, BaseFilter));
        using var selected = MsTapeDevices.SelectOutput(source.Value, simulation.Format.IsDv);
        RunGraph(source.Value, selected, receive, failure, stop, sink, Replay);

        void Replay()
        {
            var timer = Stopwatch.StartNew();
            long delivered = 0;
            while (!stop.IsCancellationRequested)
            {
                for (var offset = 0; offset < fixture.Length;)
                {
                    int count = Math.Min(sampleSize, fixture.Length - offset);
                    var start = TimeSpan.FromSeconds(delivered * simulation.SecondsPerUnit);
                    while (start - timer.Elapsed is var remaining && remaining > TimeSpan.Zero)
                        if (stop.WaitHandle.WaitOne(remaining))
                            return;
                    if (stop.IsCancellationRequested)
                        return;
                    delivered += count / simulation.UnitBytes;
                    var end = TimeSpan.FromSeconds(delivered * simulation.SecondsPerUnit);
                    var status = replayFilter.Send(fixture.AsSpan(offset, count), offset == 0, start.Ticks, end.Ticks);
                    if (status != Ok)
                        throw new FireWireException(6, $"The simulated i.LINK sample was rejected (0x{status:X8}).");
                    offset += count;
                }
            }
        }
    }
}
