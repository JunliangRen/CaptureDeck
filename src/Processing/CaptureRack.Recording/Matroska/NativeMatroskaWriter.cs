using System.Buffers.Binary;
using System.Text;

namespace CaptureRack.Recording.Matroska;

// Self-contained Matroska muxer for independent video frames and PCM. The block payload
// is the capture sample as received; the only synthesized bytes are container metadata.
internal sealed class NativeMatroskaWriter
{
    private const long TicksPerTimestamp = 1000; // 100 microseconds
    private const ulong TimestampScale = TicksPerTimestamp * 100; // nanoseconds
    private readonly Stream output;
    private readonly bool streaming;
    private readonly long durationPosition;
    private long clusterTime = -1, clusterSizePosition, duration;
    private readonly long segmentStart;
    private readonly List<(long Time, long Position)> cues = [];
    private bool clusterHasVideo;

    internal NativeMatroskaWriter(Stream output, NativeVideoTrack video, NativePcmTrack? audio, bool streaming = false)
    {
        this.output = output;
        this.streaming = streaming;
        Master(output, 0x1A45DFA3, e =>
        {
            UInt(e, 0x4286, 1); // EBMLVersion
            UInt(e, 0x42F7, 1);
            UInt(e, 0x42F2, 4);
            UInt(e, 0x42F3, 8);
            String(e, 0x4282, "matroska");
            UInt(e, 0x4287, 4);
            UInt(e, 0x4285, 2);
        });
        Id(output, 0x18538067); // Segment with unknown size, playable while being written.
        output.Write([0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        if (!streaming)
            segmentStart = output.Position;
        Master(output, 0x1549A966, e =>
        {
            UInt(e, 0x2AD7B1, TimestampScale);
            String(e, 0x4D80, "CaptureRack");
            String(e, 0x5741, "CaptureRack");
            if (!streaming)
                Float(e, 0x4489, 0); // Duration is the last 8 bytes of Info.
        });
        if (!streaming)
            durationPosition = output.Position - 8;
        Master(output, 0x1654AE6B, e =>
        {
            Master(e, 0xAE, t =>
            {
                Track(t, 1, 1, video.Mpeg2 ? "V_MPEG2" : video.ColourSpace is null ? "V_MS/VFW/FOURCC" : "V_UNCOMPRESSED");
                if (!video.Mpeg2 && video.ColourSpace is null)
                    Binary(t, 0x63A2, video.BitmapHeader);
                UInt(t, 0x23E383, (ulong)video.FrameTicks * 100);
                Master(t, 0xE0, v =>
                {
                    UInt(v, 0xB0, (ulong)video.Width);
                    UInt(v, 0xBA, (ulong)video.Height);
                    if (video.ColourSpace is { } colourSpace)
                        Binary(v, 0x2EB524, colourSpace);
                    if (video.FieldOrder is { } order)
                    {
                        UInt(v, 0x9A, order == 0 ? 2UL : 1UL);
                        UInt(v, 0x9D, order);
                    }
                });
            });
            if (audio is { } pcm)
                Master(e, 0xAE, t =>
                {
                    Track(t, 2, 2, "A_PCM/INT/LIT");
                    Master(t, 0xE1, a =>
                    {
                        Float(a, 0xB5, pcm.SampleRate);
                        UInt(a, 0x9F, (ulong)pcm.Channels);
                        UInt(a, 0x6264, (ulong)pcm.Bits);
                    });
                });
        });
    }

    internal void Write(bool video, long startTicks, long endTicks, ReadOnlySpan<byte> payload, bool keyframe = true)
    {
        var timestamp = startTicks / TicksPerTimestamp;
        if (clusterTime < 0 || timestamp - clusterTime >= 10_000 || timestamp - clusterTime < short.MinValue)
        {
            FinishCluster();
            Id(output, 0x1F43B675);
            if (!streaming)
                clusterSizePosition = output.Position;
            output.Write([0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
            clusterTime = timestamp;
            clusterHasVideo = false;
            UInt(output, 0xE7, (ulong)timestamp);
        }
        if (!streaming && video && keyframe && !clusterHasVideo)
        {
            cues.Add((timestamp, clusterSizePosition - 4 - segmentStart));
            clusterHasVideo = true;
        }
        Id(output, 0xA3);
        Size(output, (ulong)payload.Length + 4);
        Span<byte> block = [video ? (byte)0x81 : (byte)0x82, 0, 0, keyframe ? (byte)0x80 : (byte)0];
        BinaryPrimitives.WriteInt16BigEndian(block[1..], checked((short)(timestamp - clusterTime)));
        output.Write(block);
        output.Write(payload);
        duration = Math.Max(duration, endTicks);
    }

    internal void Finish()
    {
        if (streaming)
            return;
        FinishCluster();
        // Reserve a fixed-width Cues size and patch it to avoid buffering the whole index.
        Id(output, 0x1C53BB6B);
        var cueSizePosition = output.Position;
        Size(output, 0, 8);
        foreach (var (time, position) in cues.OrderBy(cue => cue.Time))
            Master(output, 0xBB, p =>
            {
                UInt(p, 0xB3, (ulong)time);
                Master(p, 0xB7, t =>
                {
                    UInt(t, 0xF7, 1);
                    UInt(t, 0xF1, (ulong)position);
                });
            });
        PatchSize(cueSizePosition);
        var end = output.Position;
        output.Position = durationPosition;
        WriteFloat(output, duration / (double)TicksPerTimestamp);
        output.Position = end;
    }

    internal static void WriteCueIndex(Stream output, IEnumerable<(long Time, long Cluster, long Block)> points) =>
        Master(output, 0x1C53BB6B, index =>
        {
            foreach (var (time, cluster, block) in points)
                Master(index, 0xBB, point =>
                {
                    UInt(point, 0xB3, (ulong)time);
                    Master(point, 0xB7, track =>
                    {
                        UInt(track, 0xF7, 1);
                        UInt(track, 0xF1, (ulong)cluster);
                        UInt(track, 0xF0, (ulong)block);
                    });
                });
        });

    private void FinishCluster()
    {
        if (!streaming && clusterTime >= 0)
            PatchSize(clusterSizePosition);
    }

    private void PatchSize(long position)
    {
        var end = output.Position;
        output.Position = position;
        Size(output, (ulong)(end - position - 8), 8);
        output.Position = end;
    }

    private static void Track(Stream output, byte number, byte kind, string codec)
    {
        UInt(output, 0xD7, number);
        UInt(output, 0x73C5, number);
        UInt(output, 0x83, kind);
        UInt(output, 0x9C, 0); // No lacing.
        String(output, 0x86, codec);
    }

    private static void Master(Stream target, uint id, Action<Stream> write)
    {
        using var payload = new MemoryStream();
        write(payload);
        Binary(target, id, payload.GetBuffer().AsSpan(0, (int)payload.Length));
    }

    private static void String(Stream output, uint id, string value) =>
        Binary(output, id, Encoding.ASCII.GetBytes(value));

    private static void Float(Stream output, uint id, double value)
    {
        Id(output, id);
        Size(output, 8);
        WriteFloat(output, value);
    }

    private static void WriteFloat(Stream output, double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void UInt(Stream output, uint id, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        var offset = 0;
        while (offset < 7 && bytes[offset] == 0)
            offset++;
        Binary(output, id, bytes[offset..]);
    }

    private static void Binary(Stream output, uint id, ReadOnlySpan<byte> value)
    {
        Id(output, id);
        Size(output, (ulong)value.Length);
        output.Write(value);
    }

    private static void Id(Stream output, uint id)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, id);
        var offset = 0;
        while (bytes[offset] == 0)
            offset++;
        output.Write(bytes[offset..]);
    }

    private static void Size(Stream output, ulong value, int length = 1)
    {
        while (value >= (1UL << (length * 7)) - 1)
            length++;
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value | (1UL << (length * 7)));
        output.Write(bytes[^length..]);
    }
}

internal sealed record NativeVideoTrack(byte[] BitmapHeader, int Width, int Height, long FrameTicks, uint? FieldOrder,
    byte[]? ColourSpace, bool Mpeg2 = false);
internal sealed record NativePcmTrack(int SampleRate, int Channels, int Bits, int BlockAlign);
