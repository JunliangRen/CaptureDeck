using System.Diagnostics;
using System.Runtime.InteropServices;
using CaptureRack.Core;
using CaptureRack.DirectShow.Windows.Interop;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureRack.DirectShow.Windows.Capture;

public sealed record DirectShowCaptureRequest(string Device, int VideoFormat, int? AudioFormat, TimeSpan Duration)
{
    public DirectShowFormat? ExpectedVideo { get; init; }
    public DirectShowFormat? ExpectedAudio { get; init; }
}
public sealed record DirectShowStreamStats(long Samples, long Bytes, long TimestampedSamples,
    long DiscontinuitiesAfterFirst, long TimestampRegressions, long? FirstStart100ns, long? LastStart100ns, long? LastEnd100ns,
    int? MinSampleBytes, int? MaxSampleBytes);
public sealed record DirectShowCaptureResult(DirectShowDevice Device, DirectShowFormat VideoFormat, DirectShowFormat? AudioFormat,
    DirectShowStreamStats Video, DirectShowStreamStats? Audio, int? DriverDroppedFrames, string StopReason, string? Error)
{
    public bool ReceivedRequestedStreams => Video.Samples > 0 && (Audio is null || Audio.Samples > 0);
}

public sealed unsafe class DirectShowBackend : ICaptureBackend<DirectShowDevice, DirectShowCaptureRequest,
    DirectShowCaptureResult, DirectShowCaptureResult>
{
    public const string BackendId = "windows.directshow";
    public string Id => BackendId;
    public CaptureBackendCategory Category => CaptureBackendCategory.Video;

    public IReadOnlyList<DirectShowDevice> GetDevices(bool includeCapabilities = false)
    {
        CheckPlatform();
        using var apartment = new ComApartment();
        return CaptureSource.List();
    }

    public DirectShowDeviceCapabilities Probe(string selector)
    {
        CheckPlatform();
        using var apartment = new ComApartment();
        using var source = CaptureSource.Open(selector, out var device);
        return new(device, source.HasDroppedFrameCounter, source.Descriptions);
    }

    public DirectShowCaptureResult Run(DirectShowCaptureRequest request, Action<DirectShowCaptureResult> progress,
        CancellationToken cancellation) => Run(request, progress, cancellation, null);

    public DirectShowCaptureResult Run(DirectShowCaptureRequest request, Action<DirectShowCaptureResult> progress,
        CancellationToken cancellation, Action<string>? trace) =>
        RunCore(request, progress, cancellation, trace, null, null, null);

    public DirectShowCaptureResult RunPreview(DirectShowCaptureRequest request,
        Action<DirectShowFormat, DirectShowFormat?> onFormats, CaptureSampleHandler video,
        CaptureSampleHandler audio, CancellationToken cancellation) =>
        RunCore(request with { Duration = TimeSpan.MaxValue }, _ => { }, cancellation, null, onFormats, video, audio);

    private static DirectShowCaptureResult RunCore(DirectShowCaptureRequest request,
        Action<DirectShowCaptureResult> progress, CancellationToken cancellation, Action<string>? trace,
        Action<DirectShowFormat, DirectShowFormat?>? onFormats, CaptureSampleHandler? onVideo, CaptureSampleHandler? onAudio)
    {
        CheckPlatform();
        if (onFormats is not null)
            cancellation.ThrowIfCancellationRequested();
        if (request.Duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "Capture duration cannot be negative.");

        using var apartment = new ComApartment();
        trace?.Invoke("Open device and read advertised media types.");
        using var source = CaptureSource.Open(request.Device, out var device);
        var video = source.Select(request.VideoFormat, "video");
        var audio = request.AudioFormat is { } audioIndex ? source.Select(audioIndex, "audio") : null;
        if ((request.ExpectedVideo is { } expectedVideo && video.Format != expectedVideo) ||
            (request.ExpectedAudio is { } expectedAudio && audio?.Format != expectedAudio))
            throw new InvalidOperationException("The requested device formats changed. Refresh the device and try again.");
        if (audio is not null && audio.Pin == video.Pin)
            throw new InvalidOperationException("The selected video and audio formats must be on separate output pins.");

        string? error = null;
        void ReportFailure(string message) => Interlocked.CompareExchange(ref error, message, null);

        var videoStats = new StreamCounter();
        var audioStats = audio is null ? null : new StreamCounter();
        using var videoSink = new DirectShowSink(video.MediaType, sample => videoStats.Receive(sample) && (onVideo?.Invoke(sample) ?? true), ReportFailure);
        using var audioSink = audio is null ? null : new DirectShowSink(audio.MediaType,
            sample => audioStats!.Receive(sample) && (onAudio?.Invoke(sample) ?? true), ReportFailure);
        using var graph = new DirectShowGraph();
        graph.AddSource(source.Filter, device.Name);
        trace?.Invoke("Set video media type.");
        CaptureSource.SetFormat(video);
        if (audio is not null)
        {
            trace?.Invoke("Set audio media type.");
            CaptureSource.SetFormat(audio);
        }

        trace?.Invoke("Connect video pin to the managed sink.");
        var videoFormat = Connect(graph, video, videoSink);
        if (audio is not null)
            trace?.Invoke("Connect audio pin to the managed sink.");
        var audioFormat = audio is null ? null : Connect(graph, audio, audioSink!);
        onFormats?.Invoke(videoFormat, audioFormat);
        var reason = request.Duration == TimeSpan.Zero ? "negotiated" : "duration";
        var dropped = (int?)null;
        DirectShowCaptureResult Snapshot(string stopReason) => new(device, videoFormat, audioFormat,
            videoStats.Snapshot(), audioStats?.Snapshot(), dropped, stopReason, Volatile.Read(ref error));
        try
        {
            if (cancellation.IsCancellationRequested)
            {
                reason = "cancelled";
            }
            else if (request.Duration > TimeSpan.Zero)
            {
                graph.Start(useSystemClock: true, trace);
                trace?.Invoke("Receive samples.");
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < request.Duration)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        reason = "cancelled";
                        break;
                    }
                    if (Volatile.Read(ref error) is not null)
                        break;
                    if (graph.ReadEvents(ReportFailure) || videoSink.Ended)
                    {
                        reason = "end_of_stream";
                        break;
                    }
                    var remaining = request.Duration - clock.Elapsed;
                    if (remaining > TimeSpan.Zero)
                        cancellation.WaitHandle.WaitOne(remaining < TimeSpan.FromMilliseconds(50)
                            ? remaining : TimeSpan.FromMilliseconds(50));
                }
                trace?.Invoke("Read driver drop counter.");
                dropped = source.ReadDroppedFrames();
            }
        }
        finally
        {
            trace?.Invoke("Stop the DirectShow graph.");
            graph.Stop(ReportFailure);
            videoSink.Stop();
            audioSink?.Stop();
        }

        var result = Snapshot(Volatile.Read(ref error) is null ? reason : "error");
        progress(result);
        return result;
    }

    private static DirectShowFormat Connect(DirectShowGraph graph, CaptureSource.FormatEntry entry, DirectShowSink sink)
    {
        var kind = entry.Format.Kind;
        Check(graph.Connect(entry.Pin, entry.MediaType, sink, $"CaptureRack {kind} sink"),
            $"Connect DirectShow {kind} pin without conversion");
        DirectShowMediaType connected = default;
        try
        {
            Check(sink.ConnectionMediaType(&connected), $"Read negotiated DirectShow {kind} format");
            return CaptureMediaType.Describe(in connected, entry.Format.Index, entry.Format.PinId, entry.Format.PinName,
                includeBitmapHeader: kind == "video");
        }
        finally { DirectShowMediaType.Clear(&connected); }
    }

    private static void CheckPlatform()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("DirectShow probing requires a 64-bit Windows process.");
    }

    private sealed class StreamCounter
    {
        private readonly object gate = new();
        private long samples, bytes, timestamped, discontinuities, regressions;
        private long? firstStart, lastStart, lastEnd;
        private int? minSize, maxSize;

        public bool Receive(CaptureSample sample)
        {
            lock (gate)
            {
                if (samples != 0 && sample.Discontinuity)
                    discontinuities++;
                samples++;
                var length = sample.Bytes.Length;
                bytes += length;
                minSize = Math.Min(minSize ?? length, length);
                maxSize = Math.Max(maxSize ?? length, length);
                if (sample.StartTime is { } startTime)
                {
                    timestamped++;
                    if (startTime.Ticks < lastStart)
                        regressions++;
                    firstStart ??= startTime.Ticks;
                    lastStart = startTime.Ticks;
                    lastEnd = sample.EndTime?.Ticks;
                }
            }
            return true;
        }

        public DirectShowStreamStats Snapshot()
        {
            lock (gate)
                return new(samples, bytes, timestamped, discontinuities, regressions, firstStart, lastStart, lastEnd, minSize, maxSize);
        }
    }
}
