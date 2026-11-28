using System.Runtime.InteropServices;

namespace CaptureRack.DeckLink;

public enum VideoScopeWaveformMode { Luma, RgbParade }

public sealed record VideoScopeRaster(int WaveWidth, int WaveHeight, int VectorSize)
{
    internal static readonly VideoScopeRaster Default = new(VideoScopes.WaveformWidth, VideoScopes.WaveformHeight, VideoScopes.VectorSize);

    public static VideoScopeRaster FromDisplay(double waveformWidth, double waveformHeight, double vectorSide, double scale) =>
        new(RasterDimension(waveformWidth * scale, 1536), RasterDimension(waveformHeight * scale, 1536),
            RasterDimension(vectorSide * scale, 1024) + 1);

    private static int RasterDimension(double pixels, int maximum) => (int)(Math.Ceiling(Math.Clamp(pixels, 64, maximum) / 16) * 16);
}

public sealed record VideoScopes(byte[] LumaWaveform, byte[] Vectorscope, bool NoInput)
{
    public const int WaveformWidth = 512;
    public const int WaveformHeight = 288;
    public const int VectorSize = 385;
    public const double SetupIre = 7.5;

    public static double LumaCodeAtIre(int bits, double ire)
    {
        var multiplier = 1 << (bits - 8);
        return (16 + 219 * ire / 100) * multiplier;
    }

    public static double LumaIreYFraction(int bits, double ire) =>
        1 - LumaCodeAtIre(bits, ire) / ((1 << bits) - 1);

    public VideoScopeRaster Raster { get; init; } = VideoScopeRaster.Default;
    public int LumaScaleBits { get; init; }
    public int InputBits { get; init; }
    public double? LumaOutsideLegalFraction { get; init; }
    public VideoScopeWaveformMode WaveformMode { get; init; } = VideoScopeWaveformMode.Luma;
    internal VideoScopes? SecondField { get; init; }
    internal long SecondFieldDueAt { get; init; }

    internal static VideoScopes Analyze(PreviewFrame frame, int lumaScaleBits) =>
        new Renderer(VideoScopeRaster.Default).Analyze(frame, lumaScaleBits, VideoScopeWaveformMode.Luma, PreviewColorMatrix.Auto);

    internal static double MeasureLumaExcursions(PreviewFrame frame)
    {
        var outside = 0L;
        for (var row = 0; row < frame.Height; row++)
        {
            var bytes = frame.Pixels.AsSpan(row * frame.Stride, frame.Stride);
            if (frame.Format == CapturePixelFormat.Uyvy)
            {
                for (var x = 0; x < frame.Width; x++)
                    outside += Outside(bytes[x * 2 + 1], 16, 235);
                continue;
            }

            var words = MemoryMarshal.Cast<byte, uint>(bytes);
            var groups = frame.Width / 6;
            for (var group = 0; group < groups; group++)
            {
                var i = group * 4;
                var b = words[i + 1];
                var d = words[i + 3];
                outside += Outside10(words[i] >> 10) + Outside10(b) + Outside10(b >> 20) +
                    Outside10(words[i + 2] >> 10) + Outside10(d) + Outside10(d >> 20);
            }
            for (var x = groups * 6; x < frame.Width; x++)
            {
                PreviewFeed.ReadYuv(bytes, words, x, frame.Format, out var y, out _, out _);
                outside += Outside10((uint)y);
            }
        }

        return outside / ((double)frame.Width * frame.Height);

        static int Outside(uint value, uint low, uint high) => value < low || value > high ? 1 : 0;
        static int Outside10(uint value) => Outside(value & 1023, 64, 940);
    }

    internal sealed class Renderer(VideoScopeRaster raster)
    {
        private readonly Density wave = new(raster.WaveWidth * raster.WaveHeight);
        private readonly Density vector = new(raster.VectorSize * raster.VectorSize);
        private byte[]? waveGuides, vectorGuides;
        private (int Bits, VideoScopeWaveformMode Mode, bool Ire) guideOptions;

        internal VideoScopeRaster Raster => raster;

        internal VideoScopes Analyze(PreviewFrame frame, int lumaScaleBits, VideoScopeWaveformMode mode, PreviewColorMatrix matrix,
            VideoScopes? reuse = null, int? field = null, bool measureLuma = true, bool ireScale = false)
        {
            var ire = ireScale && mode == VideoScopeWaveformMode.Luma;
            if (waveGuides is null || guideOptions != (lumaScaleBits, mode, ire))
            {
                waveGuides = BuildWaveGuides(lumaScaleBits, mode, ire);
                guideOptions = (lumaScaleBits, mode, ire);
            }
            vectorGuides ??= BuildVectorGuides();

            var parade = mode == VideoScopeWaveformMode.RgbParade;
            var step = Math.Max(1, frame.Height / (field is null ? 84 : 168));
            if (field is not null)
                step *= 2;
            var waveWidth = raster.WaveWidth;
            var channelWidth = (waveWidth - 2) / 3;
            var channelStride = channelWidth + 1;
            var columns = parade ? channelWidth : waveWidth;
            var useBt601 = matrix == PreviewColorMatrix.Bt601 ||
                matrix == PreviewColorMatrix.Auto && frame.SignalHeight <= 576;
            var redCr = useBt601 ? 409 : 459;
            var greenCb = useBt601 ? 100 : 55;
            var greenCr = useBt601 ? 208 : 136;
            var blueCb = useBt601 ? 516 : 541;
            var decoded = frame.Format == CapturePixelFormat.Bgra;
            var kr = useBt601 ? 0.299 : 0.2126;
            var kb = useBt601 ? 0.114 : 0.0722;
            var inputBits = frame.Format == CapturePixelFormat.V210 ? 10 : 8;
            var lumaMax = lumaScaleBits == 10 || inputBits == 10 ? 1023 : 1020;
            var vectorCenter = raster.VectorSize / 2;
            var chromaStep = 2 * Math.Max(1, (frame.Width + 1919) / 1920);
            for (var row = field ?? 0; row < frame.Height; row += step)
            {
                var bytes = frame.Pixels.AsSpan(row * frame.Stride, frame.Stride);
                var words = MemoryMarshal.Cast<byte, uint>(bytes);
                for (var column = 0; column < columns; column++)
                {
                    var x = column * frame.Width / columns;
                    if (parade && decoded)
                    {
                        var p = x * 4;
                        wave.Add(WaveY(bytes[p + 2], 255) * waveWidth + column);
                        wave.Add(WaveY(bytes[p + 1], 255) * waveWidth + channelStride + column);
                        wave.Add(WaveY(bytes[p], 255) * waveWidth + channelStride * 2 + column);
                        continue;
                    }
                    ReadScopeYuv(bytes, words, x, frame.Format, kr, kb, out var y, out var cb, out var cr);
                    if (!parade)
                    {
                        wave.Add(WaveY(y, lumaMax) * waveWidth + column);
                        continue;
                    }

                    var luma = 298 * (y - 64);
                    var r = Math.Clamp((luma + redCr * cr + 512) >> 10, 0, 255);
                    var g = Math.Clamp((luma - greenCb * cb - greenCr * cr + 512) >> 10, 0, 255);
                    var b = Math.Clamp((luma + blueCb * cb + 512) >> 10, 0, 255);
                    wave.Add(WaveY(r, 255) * waveWidth + column);
                    wave.Add(WaveY(g, 255) * waveWidth + channelStride + column);
                    wave.Add(WaveY(b, 255) * waveWidth + channelStride * 2 + column);
                }

                // The vectorscope samples source chroma pairs independently of the waveform layout.
                for (var x = 0; x < frame.Width; x += chromaStep)
                {
                    ReadScopeYuv(bytes, words, x, frame.Format, kr, kb, out _, out var cb, out var cr);
                    var u = Math.Clamp(vectorCenter + (int)Math.Round(cb * (vectorCenter / 512.0)), 0, raster.VectorSize - 1);
                    var v = Math.Clamp(vectorCenter - (int)Math.Round(cr * (vectorCenter / 512.0)), 0, raster.VectorSize - 1);
                    vector.Add(v * raster.VectorSize + u);
                }
            }

            var wavePixels = wave.Paint(waveGuides, reuse?.LumaWaveform, waveWidth, parade ? channelStride : 0);
            var vectorPixels = vector.Paint(vectorGuides, reuse?.Vectorscope, 0, 0);
            return new VideoScopes(wavePixels, vectorPixels, frame.NoInput)
            {
                Raster = raster,
                LumaScaleBits = lumaScaleBits,
                InputBits = inputBits,
                LumaOutsideLegalFraction = !measureLuma || frame.NoInput || decoded ? null : MeasureLumaExcursions(frame),
                WaveformMode = mode
            };
        }

        private static void ReadScopeYuv(ReadOnlySpan<byte> bytes, ReadOnlySpan<uint> words, int x,
            CapturePixelFormat format, double kr, double kb, out int y, out int cb, out int cr)
        {
            if (format != CapturePixelFormat.Bgra)
            {
                PreviewFeed.ReadYuv(bytes, words, x, format, out y, out cb, out cr);
                return;
            }
            var p = x * 4;
            var b = bytes[p];
            var g = bytes[p + 1];
            var r = bytes[p + 2];
            var luma = kr * r + (1 - kr - kb) * g + kb * b;
            y = 64 + (int)Math.Round(luma * 876 / 255);
            cb = (int)Math.Round((b - luma) * 448 / (255 * (1 - kb)));
            cr = (int)Math.Round((r - luma) * 448 / (255 * (1 - kr)));
        }

        private int WaveY(int value, int maximum) =>
            ((maximum - value) * (raster.WaveHeight - 1) + maximum / 2) / maximum;

        private byte[] BuildWaveGuides(int bits, VideoScopeWaveformMode mode, bool ireScale)
        {
            var (width, height, _) = raster;
            var pixels = new byte[width * height * 4];
            var maximum = (1 << bits) - 1;
            var quarter = (maximum + 1) / 4;
            int IreRow(double ire) => (int)Math.Round(LumaIreYFraction(bits, ire) * (height - 1));
            int[] levels = ireScale
                ? [0, IreRow(75), IreRow(50), IreRow(25), height - 1]
                : [0, WaveY(quarter * 3, maximum), WaveY(quarter * 2, maximum), WaveY(quarter, maximum), height - 1];
            var black = IreRow(0);
            var white = IreRow(100);
            var setup = IreRow(SetupIre);
            var parade = mode == VideoScopeWaveformMode.RgbParade;
            var channelStride = (width - 2) / 3 + 1;
            for (var y = 0; y < height; y++)
            {
                var horizontal = levels.Contains(y);
                var legal = y == black || y == white;
                var setupGuide = !parade && y == setup;
                for (var x = 0; x < width; x++)
                {
                    var vertical = parade ? x == channelStride - 1 || x == channelStride * 2 - 1
                        : (y & 7) < 4 && (x == 0 || x == width / 4 || x == width / 2 || x == width * 3 / 4);
                    var accent = legal && (x & 15) < 8 || setupGuide && (x & 7) < 4;
                    PaintGuide(pixels, y * width + x, horizontal || vertical ? 1 : 0, accent ? 1 : 0);
                }
            }
            return pixels;
        }

        private static readonly (double Cb, double Cr)[] VectorTargets =
        [
            (-0.114, 0.375), (0.261, 0.309), (0.375, -0.066),
            (0.114, -0.375), (-0.261, -0.309), (-0.375, 0.066)
        ];

        private byte[] BuildVectorGuides()
        {
            var size = raster.VectorSize;
            var center = size / 2;
            var outer = center * 7.0 / 8;
            var inner = outer / 2;
            var pixels = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                var dy = y - center;
                for (var x = 0; x < size; x++)
                {
                    var dx = x - center;
                    var radius = Math.Sqrt(dx * dx + dy * dy);
                    var distance = Math.Min(Math.Abs(radius - inner), Math.Abs(radius - outer));
                    if (radius <= outer)
                        distance = Math.Min(distance, Math.Min(Math.Abs(dx), Math.Abs(dy)));
                    var skin = dx <= 0 && dy <= 0 && radius <= outer && ((x + y) & 7) < 4
                        ? Coverage(Math.Abs(dx * 83 - dy * 55) / 99.569) : 0;
                    PaintGuide(pixels, y * size + x, Coverage(distance), skin);
                }
            }

            var targetRadius = Math.Max(3, (int)Math.Round(6.0 * size / VectorSize));
            foreach (var (cb, cr) in VectorTargets)
            {
                var cx = center + (int)Math.Round(cb * center * 1.75);
                var cy = center - (int)Math.Round(cr * center * 1.75);
                for (var offset = -targetRadius; offset <= targetRadius; offset++)
                {
                    if (Math.Abs(offset) <= targetRadius / 3)
                        continue;
                    PaintGuide(pixels, (cy - targetRadius) * size + cx + offset, 0, 1);
                    PaintGuide(pixels, (cy + targetRadius) * size + cx + offset, 0, 1);
                    PaintGuide(pixels, (cy + offset) * size + cx - targetRadius, 0, 1);
                    PaintGuide(pixels, (cy + offset) * size + cx + targetRadius, 0, 1);
                }
            }
            return pixels;
        }

        private static double Coverage(double distance) => Math.Clamp(1.1 - distance, 0, 1);

        private static void PaintGuide(byte[] pixels, int index, double guide, double accent)
        {
            var offset = index * 4;
            pixels[offset] = (byte)(16 + (58 - 16) * guide * (1 - accent) + (44 - 16) * accent);
            pixels[offset + 1] = (byte)(13 + (48 - 13) * guide * (1 - accent) + (96 - 13) * accent);
            pixels[offset + 2] = (byte)(11 + (41 - 11) * guide * (1 - accent) + (128 - 11) * accent);
            pixels[offset + 3] = 255;
        }

        private sealed class Density(int length)
        {
            private readonly int[] counts = new int[length];
            private readonly List<int> touched = [];

            internal void Add(int index)
            {
                if (counts[index]++ == 0)
                    touched.Add(index);
            }

            internal byte[] Paint(byte[] guides, byte[]? pixels, int width, int channelWidth)
            {
                if (pixels?.Length != guides.Length)
                    pixels = new byte[guides.Length];
                guides.CopyTo(pixels, 0);
                foreach (var index in touched)
                {
                    var intensity = Math.Min(1.0, Math.Sqrt(counts[index]) * (channelWidth == 0 ? 0.24 : 0.28));
                    var bright = intensity * intensity;
                    var offset = index * 4;
                    var channel = channelWidth == 0 ? -1 : index % width / channelWidth;
                    var (b, g, r) = channel switch
                    {
                        0 => (65 * intensity + 45 * bright, 65 * intensity + 60 * bright, 225 * intensity + 25 * bright),
                        1 => (90 * intensity + 50 * bright, 220 * intensity + 30 * bright, 55 * intensity + 55 * bright),
                        2 => (230 * intensity + 25 * bright, 120 * intensity + 65 * bright, 55 * intensity + 55 * bright),
                        _ => (135 * intensity + 95 * bright, 205 * intensity + 45 * bright, 48 * intensity + 145 * bright * intensity)
                    };
                    pixels[offset] = (byte)Math.Min(255, pixels[offset] + (int)b);
                    pixels[offset + 1] = (byte)Math.Min(255, pixels[offset + 1] + (int)g);
                    pixels[offset + 2] = (byte)Math.Min(255, pixels[offset + 2] + (int)r);
                    counts[index] = 0;
                }
                touched.Clear();
                return pixels;
            }
        }
    }
}
