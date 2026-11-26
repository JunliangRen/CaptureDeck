using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using CaptureRack.Core;
using CaptureRack.Recording;
using CaptureRack.Recording.Matroska;

namespace CaptureRack.DeckLink;

public sealed record RecordingResult(string Path, int VideoFrames, long AudioSampleFrames,
    VideoMode Mode, CapturePixelFormat VideoFormat, CaptureAudioFormat AudioFormat)
{
    public uint AudioChannels { get; init; } = CaptureAudio.Channels;
    public RecordingTiming? Timing { get; init; }
    public bool Deinterlaced { get; init; }
}

internal sealed class MovRecording : IDisposable
{
    private readonly string path;
    private readonly string stagingPath;
    private readonly VideoMode mode;
    private readonly int rowBytes;
    private readonly int audioFrameBytes;
    private readonly uint audioChannels;
    private readonly CaptureAudioFormat audioFormat;
    private readonly BlockingCollection<Packet> packets = new(16);
    private readonly Channel<Packet>? encodedPackets;
    private readonly Task<RecordingResult> writer;
    private readonly bool software;
    private readonly LiveEncodedRecording? liveEncoder;
    private string? interruption;
    private bool finalized;

    public long VideoTimeScale => mode.TimeScale;
    internal bool IsSoftware => software;

    public MovRecording(string outputPath, VideoMode mode, CapturePixelFormat format, CaptureAudioFormat audioFormat,
        RecordingEncoding? encoding = null, Action? beforeEncoderWrite = null, uint audioChannels = CaptureAudio.Channels)
    {
        path = Path.GetFullPath(outputPath);
        software = encoding?.IsSoftware == true;
        var extension = software ? ".mkv" : ".mov";
        if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Recording output must end in {extension}.");
        if (File.Exists(path))
            throw new IOException("The output MOV already exists. Pick a new file name.");
        if (mode.Width % 2 != 0)
            throw new InvalidOperationException("Uncompressed 4:2:2 MOV requires an even video width.");

        this.mode = mode;
        this.audioFormat = audioFormat;
        this.audioChannels = audioChannels;
        audioFrameBytes = CaptureAudio.BytesPerFrame(audioFormat, audioChannels);
        rowBytes = StoredRowBytes(mode.Width, format);
        stagingPath = Path.Combine(Path.GetDirectoryName(path)!, $".capturerack-{Guid.NewGuid():N}.partial");

        try
        {
            if (software)
            {
                var video = EncodedInput(mode, format, rowBytes);
                var audio = new NativePcmTrack(48000, (int)audioChannels, (int)audioFormat, audioFrameBytes);
                encodedPackets = Channel.CreateBounded<Packet>(new BoundedChannelOptions(16) { SingleReader = true });
                liveEncoder = new LiveEncodedRecording(path, encoding!, video, audio, beforeEncoderWrite: beforeEncoderWrite);
                writer = Task.Run(() => WriteEncoded(liveEncoder, format));
                return;
            }
            // Reserve the file before asking the SDK to start sending frames.
            var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                1024 * 1024, FileOptions.SequentialScan);
            writer = Task.Run(() => WriteMovie(stream, format, audioFormat));
        }
        catch
        {
            packets.Dispose();
            throw;
        }
    }

    internal static int StoredRowBytes(int width, CapturePixelFormat format) => format == CapturePixelFormat.V210
        ? checked(((width + 47) / 48) * 128)
        : checked(width * 2);

    public void SubmitVideo(IntPtr bytes, int width, int height, int stride, long time, long duration, VideoSignal signal = VideoSignal.Unknown)
    {
        if (width != mode.Width || height != mode.Height || stride < rowBytes)
            throw new InvalidOperationException("The captured frame geometry changed during recording.");

        liveEncoder?.CaptureDiagnostics.ObserveVideo(time, mode.FrameDuration);

        var length = checked(rowBytes * height);
        var data = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            if (stride == rowBytes)
                Marshal.Copy(bytes, data, 0, length);
            else
                for (var row = 0; row < height; row++)
                    Marshal.Copy(IntPtr.Add(bytes, row * stride), data, row * rowBytes, rowBytes);

            Enqueue(new Packet(data, length, time, duration, 0, signal));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(data);
            throw;
        }
    }

    public unsafe void SubmitAudio(IntPtr bytes, int sampleFrames, long time, CaptureAudioFormat inputFormat,
        VideoSignal signal = VideoSignal.Unknown)
    {
        var length = checked(sampleFrames * audioFrameBytes);
        var data = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            if (inputFormat == audioFormat)
            {
                Marshal.Copy(bytes, data, 0, length);
            }
            else if (inputFormat == CaptureAudioFormat.Pcm32 && audioFormat == CaptureAudioFormat.Pcm16)
            {
                // Retain the high signed word from each SDK PCM sample.
                var target = MemoryMarshal.Cast<byte, short>(data.AsSpan(0, length));
                var source = new ReadOnlySpan<int>((void*)bytes, target.Length);
                for (var sample = 0; sample < target.Length; sample++)
                    target[sample] = (short)(source[sample] >> 16);
            }
            else
            {
                throw new InvalidOperationException("Unsupported PCM input and output format combination.");
            }
            Enqueue(new Packet(data, length, time, 0, sampleFrames, signal));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(data);
            throw;
        }
    }

    private void Enqueue(Packet packet)
    {
        CheckWriter();
        var added = liveEncoder is null
            ? packets.TryAdd(packet)
            : liveEncoder.CaptureDiagnostics.TryEnqueue(encodedPackets!.Writer, packet, packet.Length, packet.AudioSamples == 0);
        if (!added)
        {
            var message = software
                ? "The software recording queue filled. The encoder or storage did not keep up with capture."
                : "Storage did not keep up with the uncompressed capture. Recording stopped.";
            if (software)
            {
                Interlocked.CompareExchange(ref interruption, message, null);
                CompleteQueue();
            }
            throw new IOException(message);
        }
    }

    private void CompleteQueue()
    {
        if (encodedPackets is null)
            packets.CompleteAdding();
        else
            liveEncoder!.CaptureDiagnostics.Complete(encodedPackets.Writer);
    }

    public void CheckWriter()
    {
        if (writer.IsFaulted)
            writer.GetAwaiter().GetResult();
        liveEncoder?.CheckHealth();
    }

    public RecordingResult Finish(string? captureError = null)
    {
        if (software && captureError is not null)
            Interlocked.CompareExchange(ref interruption, captureError, null);
        CompleteQueue();
        var result = writer.GetAwaiter().GetResult();
        finalized = true;
        if (software)
            return result;
        try
        {
            File.Move(stagingPath, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The MOV was finalized and is still available at '{stagingPath}'. " +
                $"Publishing to '{path}' failed: {ex.Message}", ex);
        }
        return result;
    }

    private static NativeVideoTrack EncodedInput(VideoMode mode, CapturePixelFormat format, int pitch)
    {
        var bitmap = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(bitmap, bitmap.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(4), mode.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(8), mode.Height);
        BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(14), format == CapturePixelFormat.V210 ? (short)20 : (short)16);
        BinaryPrimitives.WriteUInt32BigEndian(bitmap.AsSpan(16), (uint)format);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(20), checked(pitch * mode.Height));
        uint? order = mode.FieldDominance switch
        {
            0x75707072 => 1, // 'uppr'
            0x6c6f7772 => 6, // 'lowr'
            0x70726f67 => 0, // 'prog'
            _ => null
        };
        var frameTicks = (long)Math.Round(mode.FrameDuration * (double)TimeSpan.TicksPerSecond / mode.TimeScale);
        return new(bitmap, mode.Width, mode.Height, frameTicks, order, null);
    }

    private async Task<RecordingResult> WriteEncoded(LiveEncodedRecording live, CapturePixelFormat format)
    {
        using (live)
        {
            long? origin = null;
            long audioSamples = 0;
            int videoFrames = 0;
            long previousVideo = -1, previousAudio = -1;
            long videoEnd = 0, audioEnd = 0;
            long videoOffset = 0, audioOffset = 0;
            await foreach (var packet in live.CaptureDiagnostics.ReadAllAsync(encodedPackets!.Reader, static packet => packet.Length)
                .ConfigureAwait(false))
            {
                try
                {
                    var isVideo = packet.AudioSamples == 0;
                    var scale = isVideo ? mode.TimeScale : 48000;
                    var timestamp = (long)((decimal)packet.Time * TimeSpan.TicksPerSecond / scale);
                    var duration = (long)((decimal)(isVideo ? packet.Duration : packet.AudioSamples) * TimeSpan.TicksPerSecond / scale);
                    origin ??= timestamp;
                    ref var previous = ref (isVideo ? ref previousVideo : ref previousAudio);
                    ref var end = ref (isVideo ? ref videoEnd : ref audioEnd);
                    ref var offset = ref (isVideo ? ref videoOffset : ref audioOffset);
                    var start = Math.Max(0, timestamp - origin.Value + offset);
                    if (start <= previous)
                    {
                        offset += end - start;
                        start = end;
                    }
                    previous = start;
                    end = start + duration;
                    live.Write(isVideo, start, end, packet.Bytes.AsSpan(0, packet.Length));
                    if (isVideo) videoFrames++;
                    else audioSamples += packet.AudioSamples;
                }
                finally { ArrayPool<byte>.Shared.Return(packet.Bytes); }
            }
            if (videoFrames == 0)
                throw new IOException("The recording received no video frames.");
            var error = Volatile.Read(ref interruption);
            if (error is not null)
            {
                live.RecordInterruption(error);
                live.Finish(publish: false);
                throw new IOException($"{error} Recording prefix: {live.PartialPath}.");
            }
            live.Finish();
            return new(path, videoFrames, audioSamples, mode, format, audioFormat)
            {
                AudioChannels = audioChannels,
                Deinterlaced = live.Deinterlaced
            };
        }
    }

    private RecordingResult WriteMovie(FileStream stream, CapturePixelFormat format, CaptureAudioFormat audioFormat)
    {
        using (stream)
        {
            var movie = new QuickTimeMovie(stream, mode, format, checked(rowBytes * mode.Height), audioFormat, audioChannels);
            foreach (var packet in packets.GetConsumingEnumerable())
            {
                try
                {
                    if (packet.AudioSamples == 0)
                        movie.WriteVideo(packet.Bytes.AsSpan(0, packet.Length), packet.Time, packet.Duration, packet.Signal);
                    else
                        movie.WriteAudio(packet.Bytes.AsSpan(0, packet.Length), packet.Time, packet.AudioSamples, packet.Signal);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(packet.Bytes);
                }
            }

            movie.Finish();
            stream.Flush(flushToDisk: true);
            return new RecordingResult(path, movie.VideoFrames, movie.AudioSamples, mode, format, audioFormat)
            {
                AudioChannels = audioChannels,
                Timing = movie.Timing
            };
        }
    }

    public void Dispose()
    {
        if (!finalized)
        {
            if (software)
                Interlocked.CompareExchange(ref interruption, "Recording disposed before a clean stop.", null);
            CompleteQueue();
            try
            {
                writer.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Capture or Finish owns reporting the original error.
            }

            if (encodedPackets is not null)
            {
                while (liveEncoder!.CaptureDiagnostics.TryDequeue(encodedPackets.Reader, static packet => packet.Length, out var packet))
                    ArrayPool<byte>.Shared.Return(packet.Bytes);
            }
            else
            {
                while (packets.TryTake(out var packet))
                    ArrayPool<byte>.Shared.Return(packet.Bytes);
            }
            File.Delete(stagingPath);
        }
        packets.Dispose();
    }

    private sealed record Packet(byte[] Bytes, int Length, long Time, long Duration, int AudioSamples,
        VideoSignal Signal);
}
