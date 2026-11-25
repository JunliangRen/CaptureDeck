using System.Runtime.InteropServices.WindowsRuntime;
using System.Buffers;
using System.Threading.Channels;
using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.DirectShow.Windows.Capture;
using CaptureRack.QualityControl;
using Windows.Graphics.Imaging;

namespace CaptureRack.App.Views;

internal sealed class DirectShowPreviewSession(PreviewFeed feed)
{
    private readonly Channel<RawFrame> frames = Channel.CreateBounded<RawFrame>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
        frame => frame.Dispose());
    private DirectShowFormat? videoFormat;
    private readonly object recordingGate = new();
    private DirectShowRecording? recording;
    private bool ended;

    internal DirectShowRecording StartRecording(string path, string? device = null,
        Action<NativeContainerAttempt>? progress = null)
    {
        lock (recordingGate)
        {
            if (ended || videoFormat is null || recording is not null)
                throw new InvalidOperationException("The DirectShow recording cannot start in the current preview state.");
            recording = DirectShowRecording.Start(path, videoFormat, audioFormat, QcReports.FindTools(), device, progress);
            return recording;
        }
    }

    internal void FinishRecording(DirectShowRecording target)
    {
        lock (recordingGate)
        {
            if (recording == target)
                recording = null;
            target.Complete();
        }
    }

    private void EndRecording(Exception? error)
    {
        lock (recordingGate)
        {
            ended = true;
            recording?.Complete(error);
            recording = null;
        }
    }
    private DirectShowFormat? audioFormat;
    private string? encoding;
    private readonly object monitorGate = new();
    private LocalAudioMonitor? monitor;
    private StereoMonitor? spectrum;
    private long received, decoded, unreadable;
    private bool audioEnabled;
    private double audioVolume = 0.8;

    internal DirectShowFormat? NegotiatedVideo => Volatile.Read(ref videoFormat);
    internal DirectShowFormat? NegotiatedAudio => Volatile.Read(ref audioFormat);

    internal long Received => Interlocked.Read(ref received);
    internal long Decoded => Interlocked.Read(ref decoded);
    internal long Unreadable => Interlocked.Read(ref unreadable);
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

    internal async Task<DirectShowCaptureResult> RunAsync(DirectShowCaptureRequest request, CancellationToken stop)
    {
        var decoder = Task.Run(() => DecodeAsync(stop));
        try
        {
            var result = await Task.Run(() => new DirectShowBackend().RunPreview(request, SetFormats, ReceiveVideo, ReceiveAudio, stop))
                .ConfigureAwait(false);
            EndRecording(result.Error is { } error ? new IOException(error) : null);
            return result;
        }
        catch (Exception error)
        {
            EndRecording(error);
            throw;
        }
        finally
        {
            frames.Writer.TryComplete();
            try { await decoder.ConfigureAwait(false); }
            finally
            {
                while (frames.Reader.TryRead(out var pending))
                    pending.Dispose();
                lock (monitorGate)
                {
                    monitor?.Dispose();
                    monitor = null;
                }
            }
        }
    }

    private void SetFormats(DirectShowFormat video, DirectShowFormat? audio)
    {
        encoding = DirectShowPreviewVideo.Encoding(video);
        if (encoding is null && !video.IsMpeg2Video)
            throw new NotSupportedException(video.Description);
        lock (recordingGate)
        {
            Volatile.Write(ref audioFormat, audio);
            Volatile.Write(ref videoFormat, video);
        }
    }

    private bool ReceiveVideo(CaptureSample sample)
    {
        lock (recordingGate)
            recording?.Accept(true, sample);
        Interlocked.Increment(ref received);
        if (encoding is null)
            return true;
        if (sample.Bytes.IsEmpty || sample.Bytes.Length > 256 * 1024 * 1024)
        {
            Interlocked.Increment(ref unreadable);
            return true;
        }
        var buffer = ArrayPool<byte>.Shared.Rent(sample.Bytes.Length);
        sample.Bytes.CopyTo(buffer);
        var frame = new RawFrame(buffer, sample.Bytes.Length);
        if (!frames.Writer.TryWrite(frame))
            frame.Dispose();
        return true;
    }

    private bool ReceiveAudio(CaptureSample sample)
    {
        lock (recordingGate)
            recording?.Accept(false, sample);
        var audio = audioFormat;
        if (!DirectShowPreviewAudio.Supports(audio))
            return true;
        if (audio?.SampleRate is not (> 0 and <= 384000) ||
            sample.Bytes.Length / audio.BlockAlign!.Value is not (> 0 and <= 65536))
            return true;
        lock (monitorGate)
        {
            spectrum ??= new StereoMonitor(CaptureAudioFormat.Pcm32, (uint)audio.SampleRate.Value,
                (uint)(audio.ValidBitsPerSample is > 0 ? audio.ValidBitsPerSample.Value : audio.BitsPerSample!.Value));
            var active = monitor;
            if (audioEnabled && active is null)
                monitor = active = new LocalAudioMonitor((uint)audio.SampleRate.Value) { Enabled = true, Volume = audioVolume };

            if (active is { Enabled: true })
            {
                var length = sample.Bytes.Length / audio.BlockAlign.Value * 2;
                var buffer = ArrayPool<short>.Shared.Rent(length);
                try
                {
                    var stereo = buffer.AsSpan(0, length);
                    DirectShowPreviewAudio.ReadPeaks(audio, sample.Bytes, stereo, spectrum.Add);
                    active.SubmitPcm16(stereo);
                }
                finally { ArrayPool<short>.Shared.Return(buffer); }
            }
            else
                DirectShowPreviewAudio.ReadPeaks(audio, sample.Bytes, samples: spectrum.Add);
        }
        return true;
    }

    private async Task DecodeAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var raw in frames.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                using (raw)
                {
                    if (stop.IsCancellationRequested)
                        break;
                    var video = videoFormat!;
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
                            Interlocked.Increment(ref unreadable);
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or System.Runtime.InteropServices.COMException)
                    {
                        Interlocked.Increment(ref unreadable);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            while (frames.Reader.TryRead(out var pending))
                pending.Dispose();
        }
    }

    private static PreviewImage? DecodeRaw(RawFrame raw, DirectShowFormat format, string encoding, int width, int height)
    {
        var image = new PreviewImage(ArrayPool<byte>.Shared.Rent(width * height * 4), width, height, pooled: true);
        if (DirectShowPreviewVideo.TryConvert(format, encoding, raw.Buffer.AsSpan(0, raw.Length), image.Bytes, width, height))
            return image;
        image.Dispose();
        return null;
    }

    private static async Task<PreviewImage> DecodeJpegAsync(RawFrame raw, int width, int height)
    {
        using var data = new MemoryStream(raw.Buffer, 0, raw.Length, writable: false);
        using var stream = data.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.JpegDecoderId, stream);
        var transform = new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return new PreviewImage(pixels.DetachPixelData(), width, height, pooled: false);
    }

    private sealed class RawFrame(byte[] buffer, int length) : IDisposable
    {
        internal byte[] Buffer { get; } = buffer;
        internal int Length { get; } = length;
        public void Dispose() => ArrayPool<byte>.Shared.Return(Buffer);
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
