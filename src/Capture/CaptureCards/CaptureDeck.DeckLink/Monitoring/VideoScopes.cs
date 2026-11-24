using System.Runtime.InteropServices;

namespace CaptureDeck.DeckLink;

public sealed record VideoScopes(byte[] LumaWaveform, byte[] Vectorscope, bool NoInput)
{
    public const int WaveformWidth = 512;
    public const int WaveformHeight = 288;
    public const int VectorSize = 385;
    private const int VectorCenter = VectorSize / 2;
    private const int InnerRing = VectorCenter * 7 / 16;
    private const int OuterRing = VectorCenter * 7 / 8;

    public int LumaScaleBits { get; init; }
    public int InputBits { get; init; }

    internal static VideoScopes Analyze(PreviewFrame frame, int lumaScaleBits)
    {
        var waveform = new int[WaveformWidth * WaveformHeight];
        var vectorscope = new int[VectorSize * VectorSize];
        var rowStep = Math.Max(1, frame.Height / 84);
        // ReadYuv shifts 8-bit samples left by two bits, so 255 is returned as 1020.
        var inputBits = frame.Format == CapturePixelFormat.V210 ? 10 : 8;
        var lumaMaximum = inputBits == 10 ? 1023 : 255 * 4;
        for (var row = 0; row < frame.Height; row += rowStep)
        {
            var bytes = frame.Pixels.AsSpan(row * frame.Stride, frame.Stride);
            var words = MemoryMarshal.Cast<byte, uint>(bytes);
            for (var column = 0; column < WaveformWidth; column++)
            {
                var x = column * frame.Width / WaveformWidth;
                PreviewFeed.ReadYuv(bytes, words, x, frame.Format, out var y, out var cb, out var cr);
                waveform[WaveformY(y, lumaMaximum) * WaveformWidth + column]++;
                var u = Math.Clamp(VectorCenter + cb * VectorCenter / 512, 0, VectorSize - 1);
                var v = Math.Clamp(VectorCenter - cr * VectorCenter / 512, 0, VectorSize - 1);
                vectorscope[v * VectorSize + u]++;
            }
        }

        var wavePixels = new byte[waveform.Length * 4];
        var scaleMaximum = (1 << lumaScaleBits) - 1;
        var quarter = (scaleMaximum + 1) / 4;
        var guideQuarter = WaveformY(quarter, scaleMaximum);
        var guideHalf = WaveformY(quarter * 2, scaleMaximum);
        var guideThreeQuarters = WaveformY(quarter * 3, scaleMaximum);
        for (var y = 0; y < WaveformHeight; y++)
        {
            var horizontalGuide = y == 0 || y == guideQuarter || y == guideHalf || y == guideThreeQuarters || y == WaveformHeight - 1;
            for (var x = 0; x < WaveformWidth; x++)
            {
                var line = horizontalGuide || x % (WaveformWidth / 4) == 0;
                var index = y * WaveformWidth + x;
                Paint(wavePixels, index, waveform[index], line);
            }
        }

        var vectorPixels = new byte[vectorscope.Length * 4];
        for (var y = 0; y < VectorSize; y++)
        {
            for (var x = 0; x < VectorSize; x++)
            {
                var dx = x - VectorCenter;
                var dy = y - VectorCenter;
                var radiusSquared = dx * dx + dy * dy;
                var line = dx == 0 || dy == 0 ||
                    Math.Abs(radiusSquared - InnerRing * InnerRing) <= InnerRing ||
                    Math.Abs(radiusSquared - OuterRing * OuterRing) <= OuterRing;
                var index = y * VectorSize + x;
                Paint(vectorPixels, index, vectorscope[index], line);
            }
        }

        return new VideoScopes(wavePixels, vectorPixels, frame.NoInput)
        {
            LumaScaleBits = lumaScaleBits,
            InputBits = inputBits
        };
    }

    private static int WaveformY(int value, int maximum) =>
        ((maximum - value) * (WaveformHeight - 1) + maximum / 2) / maximum;

    private static void Paint(byte[] pixels, int index, int count, bool graticule)
    {
        var intensity = Math.Min(190, (int)(44 * Math.Sqrt(count)));
        var baseline = graticule ? 55 : 18;
        var offset = index * 4;
        pixels[offset] = (byte)(baseline + intensity * 3 / 4);
        pixels[offset + 1] = (byte)(baseline + intensity);
        pixels[offset + 2] = (byte)(baseline + intensity / 3);
        pixels[offset + 3] = 255;
    }
}
