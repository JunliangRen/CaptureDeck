using System.Runtime.InteropServices;

namespace CaptureRack.DeckLink;

public sealed record VideoScopes(byte[] LumaWaveform, byte[] Vectorscope, bool NoInput)
{
    public const int WaveformWidth = 512;
    public const int WaveformHeight = 288;
    public const int VectorSize = 385;
    private const int VectorCenter = VectorSize / 2;
    private const int InnerRing = VectorCenter * 7 / 16;
    private const int OuterRing = VectorCenter * 7 / 8;

    // 75% Rec.601/709 color bar target coordinates in normalized Cb/Cr (-0.5..+0.5)
    private static readonly (double Cb, double Cr)[] VectorTargets =
    [
        (-0.114, 0.375),  // R
        (0.261, 0.309),   // Mg
        (0.375, -0.066),  // B
        (0.114, -0.375),  // Cy
        (-0.261, -0.309), // G
        (-0.375, 0.066)   // Yl
    ];

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
        // Broadcast legal luma band (64..940 in 10-bit, 16..235 in 8-bit).
        var legalBlack = lumaScaleBits == 10 ? 64 : 16;
        var legalWhite = lumaScaleBits == 10 ? 940 : 235;
        var guideLegalBlack = WaveformY(legalBlack, scaleMaximum);
        var guideLegalWhite = WaveformY(legalWhite, scaleMaximum);

        for (var y = 0; y < WaveformHeight; y++)
        {
            var horizontalGuide = y == 0 || y == guideQuarter || y == guideHalf || y == guideThreeQuarters || y == WaveformHeight - 1;
            var legalGuide = y == guideLegalBlack || y == guideLegalWhite;
            for (var x = 0; x < WaveformWidth; x++)
            {
                var verticalGuide = x % (WaveformWidth / 4) == 0 && (y & 3) < 2;
                var legalDash = legalGuide && (x & 7) < 4;
                var line = horizontalGuide || verticalGuide;
                var index = y * WaveformWidth + x;
                Paint(wavePixels, index, waveform[index], line, legalDash);
            }
        }

        var vectorPixels = new byte[vectorscope.Length * 4];
        var targetMask = new byte[VectorSize * VectorSize];
        foreach (var (cb, cr) in VectorTargets)
        {
            var cx = Math.Clamp(VectorCenter + (int)Math.Round(cb * VectorCenter * 1.75), 6, VectorSize - 7);
            var cy = Math.Clamp(VectorCenter - (int)Math.Round(cr * VectorCenter * 1.75), 6, VectorSize - 7);
            for (var offset = -6; offset <= 6; offset++)
            {
                if (Math.Abs(offset) > 2)
                {
                    targetMask[(cy - 6) * VectorSize + (cx + offset)] = 1;
                    targetMask[(cy + 6) * VectorSize + (cx + offset)] = 1;
                    targetMask[(cy + offset) * VectorSize + (cx - 6)] = 1;
                    targetMask[(cy + offset) * VectorSize + (cx + 6)] = 1;
                }
            }
        }

        for (var y = 0; y < VectorSize; y++)
        {
            for (var x = 0; x < VectorSize; x++)
            {
                var dx = x - VectorCenter;
                var dy = y - VectorCenter;
                var radiusSquared = dx * dx + dy * dy;
                var axisLine = (dx == 0 || dy == 0) && radiusSquared <= OuterRing * OuterRing;
                var ringLine = Math.Abs(radiusSquared - InnerRing * InnerRing) <= InnerRing ||
                    Math.Abs(radiusSquared - OuterRing * OuterRing) <= OuterRing;
                // Subtle I-axis skin-tone reference line (~123 degrees from +Cb axis) in upper-left quadrant.
                var skinLine = dx <= 0 && dy <= 0 && radiusSquared <= OuterRing * OuterRing &&
                    Math.Abs(dx * 83 - dy * 55) <= 68 && ((x + y) & 3) < 2;
                var index = y * VectorSize + x;
                var isTarget = targetMask[index] != 0;
                Paint(vectorPixels, index, vectorscope[index], axisLine || ringLine, isTarget || skinLine);
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

    private static void Paint(byte[] pixels, int index, int count, bool graticule, bool accentGuide)
    {
        var offset = index * 4;
        // Optical dark studio canvas (#0B0D10) in BGRA
        int b = 16, g = 13, r = 11;
        if (graticule)
        {
            b = 58;
            g = 48;
            r = 41;
        }
        if (accentGuide)
        {
            b = 44;
            g = 96;
            r = 128;
        }

        if (count > 0)
        {
            var intensity = Math.Min(1.0, Math.Sqrt(count) * 0.24);
            // Cyan-emerald broadcast phosphor curve: teal -> vivid emerald -> mint highlight
            var pb = (int)(135 * intensity + 95 * intensity * intensity);
            var pg = (int)(205 * intensity + 45 * intensity * intensity);
            var pr = (int)(48 * intensity + 145 * intensity * intensity * intensity);
            b = Math.Min(255, b + pb);
            g = Math.Min(255, g + pg);
            r = Math.Min(255, r + pr);
        }

        pixels[offset] = (byte)b;
        pixels[offset + 1] = (byte)g;
        pixels[offset + 2] = (byte)r;
        pixels[offset + 3] = 255;
    }
}
