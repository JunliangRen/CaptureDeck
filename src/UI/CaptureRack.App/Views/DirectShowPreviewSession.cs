using System.Runtime.InteropServices.WindowsRuntime;
using System.Buffers;
using System.Threading.Channels;
using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.DirectShow.Windows.Capture;
using CaptureRack.QualityControl;
using CaptureRack.Recording;
using Windows.Graphics.Imaging;

namespace CaptureRack.App.Views;

internal sealed class DirectShowPreviewSession(PreviewFeed feed)
{
    private readonly DirectShowSampleSession samples = new();
    internal long SkippedBeforeDecode => samples.SkippedBeforeDecode;
    private readonly Channel<ControlRequest> controlRequests = Channel.CreateUnbounded<ControlRequest>(
        new UnboundedChannelOptions { SingleReader = true });
    private string stage = "Open device";

    internal DirectShowRecording StartRecording(string path, string? device = null,
        Action<NativeContainerAttempt>? progress = null, RecordingEncoding? encoding = null) =>
        samples.StartRecording(path, QcReports.FindTools(), device, progress, encoding);

    internal void FinishRecording(DirectShowRecording target) => samples.FinishRecording(target);
    private readonly object monitorGate = new();
    private LocalAudioMonitor? monitor;
    private StereoMonitor? spectrum;
    private long decoded, decodeFailures;
    private bool audioEnabled;
    private double audioVolume = 0.8;
    private AudioMonitorRoute audioRoute = AudioMonitorRoute.Default;

    internal DirectShowFormat? NegotiatedVideo => samples.NegotiatedVideo;
    internal DirectShowFormat? NegotiatedAudio => samples.NegotiatedAudio;

    internal long Received => samples.Received;
    internal long Decoded => Interlocked.Read(ref decoded);
    internal long Unreadable => samples.Unreadable + Interlocked.Read(ref decodeFailures);
    internal string DiagnosticStage => Volatile.Read(ref stage);
    internal StereoLevels? ReadAudioLevels()
    {
        StereoMonitor.AudioWindow? window;
        lock (monitorGate)
            window = spectrum?.ReadForDisplay();
        return window?.Analyze();
    }

    internal void SetAudioMonitor(bool enabled, double volume)
    {
        lock (monitorGate)
        {
            audioEnabled = enabled;
            audioVolume = volume;
            if (monitor is { } playback)
            {
                playback.Enabled = enabled;
                playback.Volume = volume;
            }
        }
    }

    internal void SetAudioMonitorRoute(AudioMonitorRoute route)
    {
        lock (monitorGate)
        {
            audioRoute = route;
            if (spectrum is not null)
                spectrum.Route = route;
        }
    }

    internal async Task<DirectShowCaptureResult> RunAsync(DirectShowCaptureRequest request, CancellationToken stop)
    {
        var decoder = Task.Run(() => DecodeAsync(stop));
        try
        {
            var result = await Task.Run(() => new DirectShowBackend().RunPreview(request, samples.SetFormats, samples.ReceiveVideo, ReceiveAudio,
                stop, ServiceControls, step =>
                {
                    if (step != "Stop the DirectShow graph.")
                        Volatile.Write(ref stage, step);
                }))
                .ConfigureAwait(false);
            samples.End(result.Error is { } error ? new IOException(error) : null);
            return result;
        }
        catch (Exception error)
        {
            samples.End(error);
            throw;
        }
        finally
        {
            controlRequests.Writer.TryComplete();
            while (controlRequests.Reader.TryRead(out var pendingControl))
                pendingControl.Completion.TrySetException(new InvalidOperationException("The DirectShow preview ended."));
            try { await decoder.ConfigureAwait(false); }
            finally
            {
                while (samples.Frames.TryRead(out var pending))
                    pending.Dispose();
                lock (monitorGate)
                {
                    monitor?.Dispose();
                    monitor = null;
                }
            }
        }
    }

    internal async Task<DirectShowControl[]> AccessCameraControlsAsync(DirectShowControlChange? change, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<DirectShowControl[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!controlRequests.Writer.TryWrite(new(change, cancellation, completion)))
            throw new InvalidOperationException("The DirectShow preview ended.");
        return await completion.Task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private void ServiceControls(Func<DirectShowControlChange?, DirectShowControl[]> access)
    {
        if (!controlRequests.Reader.TryRead(out var request))
            return;
        if (request.Cancellation.IsCancellationRequested)
        {
            request.Completion.TrySetCanceled(request.Cancellation);
            return;
        }
        try { request.Completion.TrySetResult(access(request.Change)); }
        catch (Exception error) { request.Completion.TrySetException(error); }
    }

    private sealed record ControlRequest(DirectShowControlChange? Change, CancellationToken Cancellation,
        TaskCompletionSource<DirectShowControl[]> Completion);

    private bool ReceiveAudio(CaptureSample sample)
    {
        samples.ReceiveAudio(sample);
        var audio = NegotiatedAudio;
        if (!DirectShowPreviewAudio.Supports(audio))
            return true;
        if (audio?.SampleRate is not (> 0 and <= 384000) ||
            sample.Bytes.Length / audio.BlockAlign!.Value is not (> 0 and <= 65536))
            return true;
        lock (monitorGate)
        {
            var channels = Math.Clamp(audio.Channels!.Value, 1, AudioMonitorRoute.MaxSupportedChannels);
            if (spectrum is null || spectrum.Channels != channels)
            {
                spectrum = new StereoMonitor(CaptureAudioFormat.Pcm32, (uint)audio.SampleRate.Value,
                    (uint)(audio.ValidBitsPerSample is > 0 ? audio.ValidBitsPerSample.Value : audio.BitsPerSample!.Value),
                    channels: channels)
                {
                    Route = audioRoute
                };
            }
            var active = monitor;
            if (audioEnabled && active is null)
                monitor = active = new LocalAudioMonitor((uint)audio.SampleRate.Value) { Enabled = true, Volume = audioVolume };

            if (active is { Enabled: true })
            {
                var length = sample.Bytes.Length / audio.BlockAlign.Value * 2;
                var buffer = ArrayPool<short>.Shared.Rent(length);
                try
                {
                    var playbackIndex = 0;
                    DirectShowPreviewAudio.ProcessInterleaved(audio, sample.Bytes, frame =>
                    {
                        var (l, r) = spectrum.AddFrame(frame);
                        if (playbackIndex < length - 1)
                        {
                            buffer[playbackIndex++] = (short)Math.Clamp((int)Math.Round(l * 32767.0), short.MinValue, short.MaxValue);
                            buffer[playbackIndex++] = (short)Math.Clamp((int)Math.Round(r * 32767.0), short.MinValue, short.MaxValue);
                        }
                    });
                    active.SubmitPcm16(buffer.AsSpan(0, playbackIndex));
                }
                finally { ArrayPool<short>.Shared.Return(buffer); }
            }
            else
            {
                DirectShowPreviewAudio.ProcessInterleaved(audio, sample.Bytes, frame => _ = spectrum.AddFrame(frame));
            }
        }
        return true;
    }

    private async Task DecodeAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var raw in samples.Frames.ReadAllAsync(stop).ConfigureAwait(false))
            {
                using (raw)
                {
                    if (stop.IsCancellationRequested)
                        break;
                    var video = NegotiatedVideo!;
                    var encoding = samples.Encoding!;
                    var (width, height) = DirectShowPreviewVideo.DisplaySize(video.Width!.Value, video.Height!.Value);
                    var jpeg = DirectShowPreviewVideo.IsJpeg(encoding!);
                    var scanMode = video.FieldOrder switch
                    {
                        "top-first" => PreviewScanMode.TopFirst,
                        "bottom-first" => PreviewScanMode.BottomFirst,
                        "progressive" => PreviewScanMode.Progressive,
                        _ => PreviewScanMode.Unknown
                    };
                    bool? topFirst = jpeg ? null : scanMode switch
                    {
                        PreviewScanMode.TopFirst => true,
                        PreviewScanMode.BottomFirst => false,
                        _ => null
                    };
                    if (topFirst.HasValue && height > 1)
                        height &= ~1;
                    try
                    {
                        using var image = jpeg
                            ? await DecodeJpegAsync(raw, width, height).ConfigureAwait(false)
                            : DecodeRaw(raw, video, encoding!, width, height);
                        if (image is not null)
                        {
                            feed.SubmitDecodedFrame(image.Bytes, width, height, topFirst, video.FramesPerSecond ?? 0,
                                video.Width.Value, video.Height.Value, video.DisplayAspectRatio ?? 0, encoding, scanMode);
                            Interlocked.Increment(ref decoded);
                        }
                        else
                            Interlocked.Increment(ref decodeFailures);
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or System.Runtime.InteropServices.COMException)
                    {
                        Interlocked.Increment(ref decodeFailures);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            while (samples.Frames.TryRead(out var pending))
                pending.Dispose();
        }
    }

    private static PreviewImage? DecodeRaw(DirectShowSampleSession.RawFrame raw, DirectShowFormat format, string encoding, int width, int height)
    {
        var image = new PreviewImage(ArrayPool<byte>.Shared.Rent(width * height * 4), width, height, pooled: true);
        if (DirectShowPreviewVideo.TryConvert(format, encoding, raw.Buffer.AsSpan(0, raw.Length), image.Bytes, width, height))
            return image;
        image.Dispose();
        return null;
    }

    private static async Task<PreviewImage> DecodeJpegAsync(DirectShowSampleSession.RawFrame raw, int width, int height)
    {
        using var data = new MemoryStream(raw.Buffer, 0, raw.Length, writable: false);
        using var stream = data.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.JpegDecoderId, stream);
        var transform = new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return new PreviewImage(pixels.DetachPixelData(), width, height, pooled: false);
    }

    internal sealed class PreviewImage(byte[] buffer, int width, int height, bool pooled) : IDisposable
    {
        internal int Width { get; } = width;
        internal int Height { get; } = height;
        internal Span<byte> Bytes => buffer.AsSpan(0, Width * Height * 4);
        public void Dispose()
        {
            if (pooled)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
