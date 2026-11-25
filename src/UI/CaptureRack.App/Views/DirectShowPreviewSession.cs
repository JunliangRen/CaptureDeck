using System.Runtime.InteropServices.WindowsRuntime;
using System.Buffers;
using System.Threading.Channels;
using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.DirectShow.Windows.Capture;
using CaptureRack.QualityControl;
using Windows.Graphics.Imaging;

namespace CaptureRack.App.Views;

internal sealed class DirectShowPreviewSession
{
    private readonly Channel<RawFrame> frames = Channel.CreateBounded<RawFrame>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
        frame => frame.Dispose());
    private DirectShowFormat? videoFormat;
    private readonly object recordingGate = new();
    private DirectShowRecording? recording;
    private bool ended;

    internal DirectShowRecording StartRecording(string path)
    {
        lock (recordingGate)
        {
            if (ended || videoFormat is null || recording is not null)
                throw new InvalidOperationException("The DirectShow recording cannot start in the current preview state.");
            recording = DirectShowRecording.Start(path, videoFormat, audioFormat, QcReports.FindTools());
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
    private PreviewImage? latest;
    private long received, decoded, unreadable;
    private double leftPeak, rightPeak;
    private bool audioEnabled;

    internal long Received => Interlocked.Read(ref received);
    internal long Decoded => Interlocked.Read(ref decoded);
    internal long Unreadable => Interlocked.Read(ref unreadable);
    internal bool HasAudio => DirectShowPreviewAudio.Supports(Volatile.Read(ref audioFormat));
    internal (double Left, double Right) TakePeaks() =>
        (Interlocked.Exchange(ref leftPeak, 0), Interlocked.Exchange(ref rightPeak, 0));

    internal void SetAudioEnabled(bool enabled)
    {
        Volatile.Write(ref audioEnabled, enabled);
        lock (monitorGate)
        {
            if (monitor is { } playback)
                playback.Enabled = enabled;
        }
    }

    internal PreviewImage? TakeLatest() => Interlocked.Exchange(ref latest, null);

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
                TakeLatest()?.Dispose();
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
            videoFormat = video;
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
        if (monitor is null && Volatile.Read(ref audioEnabled) && audio!.SampleRate is > 0 and <= 384000)
        {
            lock (monitorGate)
            {
                monitor ??= new LocalAudioMonitor((uint)audio.SampleRate.Value);
                monitor.Enabled = Volatile.Read(ref audioEnabled);
            }
        }
        if (monitor is { Enabled: true } active && sample.Bytes.Length / audio!.BlockAlign!.Value is > 0 and <= 65536)
        {
            var length = sample.Bytes.Length / audio.BlockAlign.Value * 2;
            var buffer = ArrayPool<short>.Shared.Rent(length);
            try
            {
                var stereo = buffer.AsSpan(0, length);
                UpdatePeaks(DirectShowPreviewAudio.ReadPeaks(audio, sample.Bytes, stereo));
                active.SubmitPcm16(stereo);
            }
            finally { ArrayPool<short>.Shared.Return(buffer); }
        }
        else
            UpdatePeaks(DirectShowPreviewAudio.ReadPeaks(audio!, sample.Bytes));
        return true;
    }

    private void UpdatePeaks((double Left, double Right) peaks)
    {
        MaxPeak(ref leftPeak, peaks.Left);
        MaxPeak(ref rightPeak, peaks.Right);
    }

    private static void MaxPeak(ref double peak, double value)
    {
        double old;
        do
        {
            old = Volatile.Read(ref peak);
            if (value <= old)
                return;
        } while (Interlocked.CompareExchange(ref peak, value, old) != old);
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
                    try
                    {
                        var image = DirectShowPreviewVideo.IsJpeg(encoding!)
                            ? await DecodeJpegAsync(raw, width, height).ConfigureAwait(false)
                            : DecodeRaw(raw, video, encoding!, width, height);
                        if (image is not null)
                        {
                            Interlocked.Exchange(ref latest, image)?.Dispose();
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
