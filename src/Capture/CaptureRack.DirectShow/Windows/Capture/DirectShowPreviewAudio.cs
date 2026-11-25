using System.Buffers.Binary;

namespace CaptureRack.DirectShow.Windows.Capture;

public static class DirectShowPreviewAudio
{
    public static bool Supports(DirectShowFormat? format) =>
        format is { IsPcm: true, BitsPerSample: 8 or 16 or 24 or 32, Channels: > 0 } &&
        format.BlockAlign >= format.Channels * (format.BitsPerSample / 8);

    public static (double Left, double Right) ReadPeaks(DirectShowFormat format, ReadOnlySpan<byte> data,
        Span<short> playback = default)
    {
        int bytesPerSample = format.BitsPerSample!.Value / 8;
        int block = format.BlockAlign!.Value;
        int rightOffset = format.Channels == 1 ? 0 : bytesPerSample;
        int left = 0, right = 0;
        int playbackIndex = 0;
        for (int offset = 0; offset <= data.Length - block; offset += block)
        {
            int l = ReadSample(data[offset..], bytesPerSample);
            int r = ReadSample(data[(offset + rightOffset)..], bytesPerSample);
            left = Math.Max(left, l == int.MinValue ? int.MaxValue : Math.Abs(l));
            right = Math.Max(right, r == int.MinValue ? int.MaxValue : Math.Abs(r));
            if (playbackIndex < playback.Length - 1)
            {
                playback[playbackIndex++] = (short)(l >> 16);
                playback[playbackIndex++] = (short)(r >> 16);
            }
        }
        return (left / 2147483648d, right / 2147483648d);
    }

    private static int ReadSample(ReadOnlySpan<byte> data, int count) => count switch
    {
        1 => (data[0] - 128) << 24,
        2 => BinaryPrimitives.ReadInt16LittleEndian(data) << 16,
        3 => data[0] << 8 | data[1] << 16 | data[2] << 24,
        _ => BinaryPrimitives.ReadInt32LittleEndian(data)
    };
}
