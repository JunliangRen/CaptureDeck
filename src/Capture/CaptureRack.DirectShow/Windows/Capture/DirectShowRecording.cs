using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using CaptureRack.Core;
using CaptureRack.QualityControl;
using CaptureRack.Recording;
using CaptureRack.Recording.Matroska;

namespace CaptureRack.DirectShow.Windows.Capture;

public sealed class DirectShowRecording
{
    private const int QueueBudget = 128 * 1024 * 1024;
    private readonly object gate = new();
    private readonly Channel<Packet> queue = Channel.CreateUnbounded<Packet>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly NativeVideoTrack video;
    private readonly NativePcmTrack? audio;
    private readonly long started = Stopwatch.GetTimestamp();
    private int queuedBytes;
    private readonly DateTimeOffset beganUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? endedUtc;
    private readonly string? device;
    private readonly NativeCaptureFormat videoFormat;
    private readonly NativeCaptureFormat? audioFormat;
    private readonly StreamEvidence videoEvidence = new();
    private readonly StreamEvidence audioEvidence = new();
    private readonly Action<NativeContainerAttempt>? reportProgress;
    private readonly List<NativeContainerAttempt> attempts = [];
    private bool completed;
    private bool startedVideo;
    private readonly bool compressed;
    private readonly (string? FfmpegPath, string? FfprobePath) tools;

    private DirectShowRecording(string path, FileStream file, NativeVideoTrack video, NativePcmTrack? audio,
        DirectShowFormat inputVideo, DirectShowFormat? inputAudio, (string?, string?) tools,
        string? device, Action<NativeContainerAttempt>? reportProgress)
    {
        Path = path;
        this.video = video;
        this.audio = audio;
        compressed = IsCompressed(inputVideo);
        videoFormat = Describe(inputVideo);
        audioFormat = inputAudio is null ? null : Describe(inputAudio);
        this.device = device;
        this.reportProgress = reportProgress;
        this.tools = tools;
        Completion = Task.Run(() => WriteAsync(file));
    }

    public string Path { get; }
    public string PartialPath => Path + ".partial";
    public Task<string> Completion { get; }
    public string? ReportError { get; private set; }

    private static NativeCaptureFormat Describe(DirectShowFormat format) =>
        new(format.PinName, format.IsMpeg2Video ? "MPEG2" : DirectShowPreviewVideo.Encoding(format)
            ?? (format.IsPcm ? "PCM" : format.Description), format.Subtype,
            format.Width, format.Height, format.FrameInterval100ns, format.FieldOrder,
            format.SampleRate, format.Channels, format.BitsPerSample);

    public static bool Supports(DirectShowFormat video, DirectShowFormat? audio) =>
        (DirectShowPreviewVideo.Encoding(video) is not null || video.IsMpeg2Video && video.Width > 0 && video.Height > 0)
        && video.FrameInterval100ns is > 0
        && (audio is null || audio.IsPcm && audio.BitsPerSample is 8 or 16 or 24 or 32
            && audio.BlockAlign == audio.Channels * (audio.BitsPerSample / 8));

    public static string[] OutputExtensions(DirectShowFormat video) =>
        NativeRecordingContainer.Extensions(IsCompressed(video));

    private static bool IsCompressed(DirectShowFormat video) =>
        video.IsMpeg2Video || DirectShowPreviewVideo.Encoding(video) is { } encoding && DirectShowPreviewVideo.IsJpeg(encoding);

    public static DirectShowRecording Start(string path, DirectShowFormat video, DirectShowFormat? audio,
        (string? FfmpegPath, string? FfprobePath) tools = default,
        string? device = null, Action<NativeContainerAttempt>? reportProgress = null)
    {
        if (!Supports(video, audio) || video.BitmapHeaderBase64 is not { } metadata)
            throw new NotSupportedException("The negotiated DirectShow format has no native recording mapping.");
        var bitmap = Convert.FromBase64String(metadata);
        // IR aliases are private DirectShow subtypes. Give the identical payload a standard decoder tag.
        var tag = video.InfraredEncoding switch
        {
            "L8_IR" => "Y800"u8,
            "MJPG_IR" => "MJPG"u8,
            _ => default
        };
        if (!tag.IsEmpty)
            tag.CopyTo(bitmap.AsSpan(16, 4));

        uint? order = video.FieldOrder switch { "progressive" => 0, "top-first" => 1, "bottom-first" => 6, _ => null };
        // Matroska's uncompressed mapping uses a raw pixel tag for little-endian 16-bit grayscale.
        byte[]? colourSpace = video.InfraredEncoding == "L16_IR" ? [(byte)'Y', (byte)'1', 0, 16] : null;
        var videoTrack = new NativeVideoTrack(bitmap, video.Width!.Value, video.Height!.Value,
            video.FrameInterval100ns!.Value, order, colourSpace, video.IsMpeg2Video);
        var audioTrack = audio is null ? null : new NativePcmTrack(audio.SampleRate!.Value,
            audio.Channels!.Value, audio.BitsPerSample!.Value, audio.BlockAlign!.Value);
        var file = new FileStream(path + ".partial", FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 256 * 1024);
        return new DirectShowRecording(path, file, videoTrack, audioTrack, video, audio, tools, device, reportProgress);
    }

    public void Accept(bool isVideo, CaptureSample sample)
    {
        var length = sample.Bytes.Length;
        if (length == 0 || !isVideo && audio is null)
            return;

        lock (gate)
        {
            if (completed)
                return;
            (isVideo ? videoEvidence : audioEvidence).Observe(sample);
            if (video.Mpeg2 && !startedVideo)
            {
                if (!isVideo || sample.Bytes.IndexOf(Mpeg2SequenceHeader) < 0)
                    return;
                startedVideo = true;
            }
            if (length > QueueBudget - Volatile.Read(ref queuedBytes))
            {
                Complete(new IOException("The DirectShow recording queue filled before samples could be written."));
                return;
            }
            if (!isVideo && length % audio!.BlockAlign != 0)
            {
                Complete(new IOException("The DirectShow source delivered an incomplete PCM sample."));
                return;
            }
            var bytes = ArrayPool<byte>.Shared.Rent(length);
            sample.Bytes.CopyTo(bytes);
            Interlocked.Add(ref queuedBytes, length);
            queue.Writer.TryWrite(new Packet(isVideo, bytes, length, sample.StartTime?.Ticks,
                sample.EndTime?.Ticks, Stopwatch.GetElapsedTime(started).Ticks));
        }
    }

    public void Complete(Exception? error = null)
    {
        lock (gate)
        {
            completed = true;
            endedUtc ??= DateTimeOffset.UtcNow;
            queue.Writer.TryComplete(error);
        }
    }

    private static ReadOnlySpan<byte> Mpeg2SequenceHeader => [0, 0, 1, 0xb3];

    private static bool IsMpeg2Keyframe(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> pictureStart = [0, 0, 1, 0];
        var offset = bytes.IndexOf(pictureStart);
        return offset >= 0 && bytes.Length - offset >= 6 && (bytes[offset + 5] >> 3 & 7) == 1;
    }

    private async Task<string> WriteAsync(FileStream file)
    {
        string? savedPath = null;
        string? failure = null;
        try
        {
            using (file)
            {
                var writer = new NativeMatroskaWriter(file, video, audio);
                var videoClock = new TrackClock();
                var audioClock = new TrackClock();
                long? nativeOrigin = null;
                try
                {
                    await foreach (var packet in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        try
                        {
                            if (packet.Start is { } start)
                                nativeOrigin ??= start - packet.Arrival;
                            var clock = packet.IsVideo ? videoClock : audioClock;
                            var nominal = packet.IsVideo ? video.FrameTicks :
                                packet.Length / audio!.BlockAlign * TimeSpan.TicksPerSecond / audio.SampleRate;
                            var mpeg2 = packet.IsVideo && video.Mpeg2;
                            var (time, end) = clock.Advance(packet, nativeOrigin, nominal, mpeg2);
                            var bytes = packet.Buffer.AsSpan(0, packet.Length);
                            writer.Write(packet.IsVideo, time, end, bytes, !mpeg2 || IsMpeg2Keyframe(bytes));
                            (packet.IsVideo ? videoEvidence : audioEvidence).Written(packet.Length);
                        }
                        finally { Release(packet); }
                    }
                }
                finally
                {
                    writer.Finish();
                    file.Flush(flushToDisk: true);
                }
                if (!videoClock.Seen)
                    throw new IOException("The DirectShow recording received no video frames.");
                if (audio is not null && !audioClock.Seen)
                    throw new IOException("The selected DirectShow audio stream delivered no samples.");
            }
            savedPath = await NativeRecordingContainer.FinalizeAsync(PartialPath, Path, compressed, tools, attempt =>
            {
                attempts.Add(attempt);
                reportProgress?.Invoke(attempt);
            }).ConfigureAwait(false);
            return savedPath;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            throw;
        }
        finally
        {
            Complete();
            while (queue.Reader.TryRead(out var packet))
                Release(packet);
            var reportMedia = savedPath ?? PartialPath;
            try
            {
                var media = new FileInfo(reportMedia);
                if (media.Exists)
                    await new NativeCaptureReport(media.Name, media.Length, device, beganUtc, endedUtc!.Value,
                        videoFormat, audioFormat, videoEvidence.Snapshot(), audio is null ? null : audioEvidence.Snapshot(),
                        video.Mpeg2 ? null : videoEvidence.WrittenPackets,
                        audio is null ? 0 : audioEvidence.WrittenBytes / audio.BlockAlign,
                        attempts, failure).SaveAsync(reportMedia).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ReportError = ex.Message;
            }
        }
    }

    private void Release(Packet packet)
    {
        ArrayPool<byte>.Shared.Return(packet.Buffer);
        Interlocked.Add(ref queuedBytes, -packet.Length);
    }

    private readonly record struct Packet(bool IsVideo, byte[] Buffer, int Length, long? Start, long? End, long Arrival);

    private sealed class StreamEvidence
    {
        private long received, missing, repeated, regressing, discontinuities;
        private long? lastStart;
        internal long WrittenPackets { get; private set; }
        internal long WrittenBytes { get; private set; }

        internal void Observe(CaptureSample sample)
        {
            if (sample.Discontinuity && received != 0)
                discontinuities++;
            received++;
            if (sample.StartTime is not { } start)
            {
                missing++;
                return;
            }
            if (start.Ticks == lastStart)
                repeated++;
            else if (start.Ticks < lastStart)
                regressing++;
            lastStart = start.Ticks;
        }

        internal void Written(int bytes)
        {
            WrittenPackets++;
            WrittenBytes += bytes;
        }

        internal NativeCaptureCounts Snapshot() =>
            new(received, WrittenPackets, WrittenBytes, missing, repeated, regressing, discontinuities);
    }

    private sealed class TrackClock
    {
        private long previousStart, next;
        internal bool Seen { get; private set; }

        internal (long Start, long End) Advance(Packet packet, long? nativeOrigin, long nominal, bool reordered)
        {
            var timestamp = packet.Start - nativeOrigin;
            var start = timestamp >= 0 && (reordered || !Seen || timestamp > previousStart)
                ? timestamp.Value
                : Seen ? next : packet.Arrival;
            var duration = packet.End - packet.Start;
            var end = start + (duration > 0 ? duration.Value : nominal);
            next = Math.Max(next, end);
            previousStart = start;
            Seen = true;
            return (start, end);
        }
    }
}
