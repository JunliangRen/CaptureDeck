using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CaptureRack.Core;

namespace CaptureRack.DeckLink;

public sealed record RecordingResult(string Path, int VideoFrames, long AudioSampleFrames,
    VideoMode Mode, CapturePixelFormat VideoFormat, CaptureAudioFormat AudioFormat)
{
    public RecordingTiming? Timing { get; init; }
}

internal sealed class MovRecording : IDisposable
{
    private readonly string path;
    private readonly string stagingPath;
    private readonly VideoMode mode;
    private readonly int rowBytes;
    private readonly int audioFrameBytes;
    private readonly CaptureAudioFormat audioFormat;
    private readonly BlockingCollection<Packet> packets = new(16);
    private readonly Task<RecordingResult> writer;
    private bool finalized;

    public long VideoTimeScale => mode.TimeScale;

    public MovRecording(string outputPath, VideoMode mode, CapturePixelFormat format, CaptureAudioFormat audioFormat)
    {
        path = Path.GetFullPath(outputPath);
        if (!Path.GetExtension(path).Equals(".mov", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Recording output must end in .mov.");
        if (File.Exists(path))
            throw new IOException("The output MOV already exists. Pick a new file name.");
        if (mode.Width % 2 != 0)
            throw new InvalidOperationException("Uncompressed 4:2:2 MOV requires an even video width.");

        this.mode = mode;
        this.audioFormat = audioFormat;
        audioFrameBytes = CaptureAudio.BytesPerFrame(audioFormat);
        rowBytes = StoredRowBytes(mode.Width, format);
        stagingPath = Path.Combine(Path.GetDirectoryName(path)!, $".capturerack-{Guid.NewGuid():N}.partial");

        try
        {
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
        if (!packets.TryAdd(packet))
            throw new IOException("Storage did not keep up with the uncompressed capture. Recording stopped.");
    }

    public void CheckWriter()
    {
        if (writer.IsFaulted)
            writer.GetAwaiter().GetResult();
    }

    public RecordingResult Finish()
    {
        packets.CompleteAdding();
        var result = writer.GetAwaiter().GetResult();
        finalized = true;
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

    private RecordingResult WriteMovie(FileStream stream, CapturePixelFormat format, CaptureAudioFormat audioFormat)
    {
        using (stream)
        {
            var movie = new QuickTimeMovie(stream, mode, format, checked(rowBytes * mode.Height), audioFormat);
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
                Timing = movie.Timing
            };
        }
    }

    public void Dispose()
    {
        if (!finalized)
        {
            packets.CompleteAdding();
            try
            {
                writer.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Capture or Finish owns reporting the original error.
            }

            while (packets.TryTake(out var packet))
                ArrayPool<byte>.Shared.Return(packet.Bytes);
            File.Delete(stagingPath);
        }
        packets.Dispose();
    }

    private sealed record Packet(byte[] Bytes, int Length, long Time, long Duration, int AudioSamples,
        VideoSignal Signal);
}
