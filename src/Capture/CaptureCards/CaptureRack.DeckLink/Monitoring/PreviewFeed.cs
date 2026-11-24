using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CaptureRack.DeckLink;

public enum PreviewDeinterlaceMode { Off, BobWeave, Field }

public enum PreviewColorMatrix { Auto, Bt601, Bt709 }

// The SDK callback copies a borrowed frame into a single pending slot. The capture
// worker converts the latest frame and the UI consumes the latest completed bitmap.
// Preview-only processing (deinterlacing, matrix override) never alters recorded MOV frames.
public sealed class PreviewFeed
{
    private const uint UpperFieldFirst = 0x75707072; // uppr
    private const uint LowerFieldFirst = 0x6C6F7772; // lowr

    private readonly LatestFrame incoming = new();
    private readonly LatestFrame outgoing = new();
    private PreviewFrame? source;
    private PreviewFrame? converted;
    private VideoScopes? scopes;
    private byte[] previousLuma = [];
    private int previousLumaWidth;
    private int previousLumaHeight;
    private byte[] currentLuma = [];
    private long lastScopeUpdate;
    private int deinterlaceMode = (int)PreviewDeinterlaceMode.BobWeave;
    private int colorMatrix = (int)PreviewColorMatrix.Auto;

    public long PresentedSequence { get; private set; }
    public long SkippedFrames => incoming.SkippedFrames + outgoing.SkippedFrames;
    public VideoScopes? Scopes => Volatile.Read(ref scopes);

    public PreviewDeinterlaceMode DeinterlaceMode
    {
        get => (PreviewDeinterlaceMode)Volatile.Read(ref deinterlaceMode);
        set => Volatile.Write(ref deinterlaceMode, (int)value);
    }

    public PreviewColorMatrix ColorMatrix
    {
        get => (PreviewColorMatrix)Volatile.Read(ref colorMatrix);
        set => Volatile.Write(ref colorMatrix, (int)value);
    }

    internal void Offer(IntPtr bytes, int width, int height, int stride, ulong size, bool noInput,
        CapturePixelFormat format, uint fieldDominance = 0)
    {
        var minimumStride = format == CapturePixelFormat.V210 ? ((width + 5L) / 6) * 16 : ((width + 1L) / 2) * 4;
        if (width <= 0 || height <= 0 || stride < minimumStride ||
            (ulong)stride * (ulong)height > size)
            return;

        incoming.Offer(bytes, width, height, stride, noInput, format, fieldDominance);
    }

    internal void ConvertLatest(int lumaScaleBits)
    {
        if (!incoming.TryTake(ref source) || source is null)
            return;

        var input = source;
        var mode = DeinterlaceMode;
        var interlaced = input.FieldDominance is UpperFieldFirst or LowerFieldFirst;
        var stepX = (input.Width + 959) / 960;
        // Keep full scanlines when deinterlacing interlaced SD/HD up to 1080i so Bob-Weave sees both fields.
        var stepY = interlaced && mode != PreviewDeinterlaceMode.Off && input.Height <= 1080
            ? 1 : stepX;
        var width = (input.Width + stepX - 1) / stepX;
        var height = (input.Height + stepY - 1) / stepY;

        converted ??= new PreviewFrame();
        converted.Resize(width, height, checked(width * 4));
        converted.SignalWidth = input.Width;
        converted.SignalHeight = input.Height;
        converted.NoInput = input.NoInput;
        converted.Format = input.Format;
        converted.FieldDominance = input.FieldDominance;

        ConvertToBgra(input, converted, stepX, stepY, ColorMatrix,
            interlaced ? mode : PreviewDeinterlaceMode.Off, input.FieldDominance == LowerFieldFirst);

        outgoing.Publish(ref converted);
        if (lastScopeUpdate == 0 || Stopwatch.GetElapsedTime(lastScopeUpdate) >= TimeSpan.FromMilliseconds(125))
        {
            Volatile.Write(ref scopes, VideoScopes.Analyze(input, lumaScaleBits));
            lastScopeUpdate = Stopwatch.GetTimestamp();
        }
    }

    public bool TryTakeLatest(ref PreviewFrame? frame)
    {
        if (!outgoing.TryTake(ref frame))
            return false;

        PresentedSequence++;
        return true;
    }

    private void ConvertToBgra(PreviewFrame source, PreviewFrame destination, int stepX, int stepY,
        PreviewColorMatrix matrix, PreviewDeinterlaceMode deinterlace, bool bottomFieldFirst)
    {
        var useBt601 = matrix switch
        {
            PreviewColorMatrix.Bt601 => true,
            PreviewColorMatrix.Bt709 => false,
            _ => source.SignalHeight <= 576
        };
        var red = useBt601 ? 409 : 459;
        var greenCb = useBt601 ? 100 : 55;
        var greenCr = useBt601 ? 208 : 136;
        var blue = useBt601 ? 516 : 541;
        var output = destination.Pixels.AsSpan();
        var width = destination.Width;
        var height = destination.Height;

        var recordLuma = deinterlace == PreviewDeinterlaceMode.BobWeave && stepY == 1;
        if (recordLuma)
        {
            var pixelCount = width * height;
            if (currentLuma.Length < pixelCount)
                currentLuma = new byte[pixelCount];
        }

        for (var row = 0; row < height; row++)
        {
            var input = source.Pixels.AsSpan(row * stepY * source.Stride, source.Stride);
            var words = MemoryMarshal.Cast<byte, uint>(input);
            var rowOffset = row * destination.Stride;
            var lumaRowOffset = row * width;
            for (int pixel = 0, x = 0; pixel < width; pixel++, x += stepX)
            {
                ReadYuv(input, words, x, source.Format, out var y, out var cb, out var cr);
                if (recordLuma)
                    currentLuma[lumaRowOffset + pixel] = (byte)(y >> 2);

                var luma = 298 * (y - 64);
                var index = rowOffset + pixel * 4;
                output[index] = ToByte(luma + blue * cb);
                output[index + 1] = ToByte(luma - greenCb * cb - greenCr * cr);
                output[index + 2] = ToByte(luma + red * cr);
                output[index + 3] = 255;
            }
        }

        if (stepY == 1 && height >= 4)
        {
            if (deinterlace == PreviewDeinterlaceMode.Field)
                ApplySingleFieldBob(output, width, height, destination.Stride, bottomFieldFirst);
            else if (deinterlace == PreviewDeinterlaceMode.BobWeave)
                ApplyMotionAdaptiveBobWeave(output, width, height, destination.Stride, bottomFieldFirst);
        }

        if (recordLuma)
        {
            var pixelCount = width * height;
            if (previousLuma.Length < pixelCount)
                previousLuma = new byte[pixelCount];
            Buffer.BlockCopy(currentLuma, 0, previousLuma, 0, pixelCount);
            previousLumaWidth = width;
            previousLumaHeight = height;
        }
    }

    // Pure single-field Bob: keeps dominant field lines and linearly interpolates alternate lines.
    internal static void ApplySingleFieldBob(Span<byte> bgra, int width, int height, int stride, bool bottomFieldFirst)
    {
        var secondaryParity = bottomFieldFirst ? 0 : 1;
        for (var row = secondaryParity; row < height; row += 2)
        {
            var prevRow = Math.Max(0, row - 1) * stride;
            var nextRow = Math.Min(height - 1, row + 1) * stride;
            var currRow = row * stride;
            for (var x = 0; x < width * 4; x += 4)
            {
                bgra[currRow + x] = (byte)((bgra[prevRow + x] + bgra[nextRow + x] + 1) >> 1);
                bgra[currRow + x + 1] = (byte)((bgra[prevRow + x + 1] + bgra[nextRow + x + 1] + 1) >> 1);
                bgra[currRow + x + 2] = (byte)((bgra[prevRow + x + 2] + bgra[nextRow + x + 2] + 1) >> 1);
            }
        }
    }

    // Motion-adaptive Bob-Weave: weaves crisp full-detail pixels in static regions, smoothly
    // transitions to vertical Bob interpolation wherever spatial combing or inter-frame motion occurs.
    internal void ApplyMotionAdaptiveBobWeave(Span<byte> bgra, int width, int height, int stride, bool bottomFieldFirst)
    {
        var hasPrev = previousLumaWidth == width && previousLumaHeight == height;
        var secondaryParity = bottomFieldFirst ? 0 : 1;
        const int lowThreshold = 10;
        const int highThreshold = 34;

        for (var row = secondaryParity; row < height; row += 2)
        {
            var upY = Math.Max(0, row - 1);
            var downY = Math.Min(height - 1, row + 1);
            var upRow = upY * stride;
            var downRow = downY * stride;
            var currRow = row * stride;
            var upLumaRow = upY * width;
            var downLumaRow = downY * width;
            var currLumaRow = row * width;

            for (var x = 0; x < width; x++)
            {
                var yCurr = currentLuma[currLumaRow + x];
                var yUp = currentLuma[upLumaRow + x];
                var yDown = currentLuma[downLumaRow + x];

                // Spatial comb metric: how far current scanline deviates from its vertical neighbors
                // beyond natural vertical edge slope.
                var avgNeighbor = (yUp + yDown) >> 1;
                var spatialComb = Math.Abs(yCurr - avgNeighbor) - (Math.Abs(yUp - yDown) >> 1);
                var temporalDiff = hasPrev ? Math.Abs(yCurr - previousLuma[currLumaRow + x]) : 0;

                var motion = Math.Max(spatialComb, temporalDiff);
                if (motion <= lowThreshold)
                    continue; // Static pixel -> pure Weave (full vertical detail)

                var idx = x * 4;
                var bobB = (bgra[upRow + idx] + bgra[downRow + idx] + 1) >> 1;
                var bobG = (bgra[upRow + idx + 1] + bgra[downRow + idx + 1] + 1) >> 1;
                var bobR = (bgra[upRow + idx + 2] + bgra[downRow + idx + 2] + 1) >> 1;

                if (motion >= highThreshold)
                {
                    bgra[currRow + idx] = (byte)bobB;
                    bgra[currRow + idx + 1] = (byte)bobG;
                    bgra[currRow + idx + 2] = (byte)bobR;
                }
                else
                {
                    // Smooth blend between Weave and Bob to prevent shimmering edges.
                    var weight = (motion - lowThreshold) * 256 / (highThreshold - lowThreshold);
                    var inv = 256 - weight;
                    bgra[currRow + idx] = (byte)((bgra[currRow + idx] * inv + bobB * weight) >> 8);
                    bgra[currRow + idx + 1] = (byte)((bgra[currRow + idx + 1] * inv + bobG * weight) >> 8);
                    bgra[currRow + idx + 2] = (byte)((bgra[currRow + idx + 2] * inv + bobR * weight) >> 8);
                }
            }
        }
    }

    internal static void ReadYuv(ReadOnlySpan<byte> input, ReadOnlySpan<uint> words, int x, CapturePixelFormat format,
        out int y, out int cb, out int cr)
    {
        if (format == CapturePixelFormat.V210)
        {
            var group = (x / 6) * 4;
            var offset = x % 6;
            var a = words[group];
            var b = words[group + 1];
            var c = words[group + 2];
            var d = words[group + 3];
            y = offset switch
            {
                0 => Component(a, 10), 1 => Component(b, 0), 2 => Component(b, 20),
                3 => Component(c, 10), 4 => Component(d, 0), _ => Component(d, 20)
            };
            cb = (offset < 2 ? Component(a, 0) : offset < 4 ? Component(b, 10) : Component(c, 20)) - 512;
            cr = (offset < 2 ? Component(a, 20) : offset < 4 ? Component(c, 0) : Component(d, 10)) - 512;
        }
        else
        {
            var group = (x / 2) * 4;
            y = input[group + 1 + (x % 2) * 2] * 4;
            cb = input[group] * 4 - 512;
            cr = input[group + 2] * 4 - 512;
        }
    }

    private static int Component(uint word, int shift) => (int)((word >> shift) & 1023);
    private static byte ToByte(int value) => (byte)Math.Clamp((value + 512) >> 10, 0, 255);

    private sealed class LatestFrame
    {
        private readonly object gate = new();
        private PreviewFrame? slot;
        private bool ready;
        private long skippedFrames;

        public long SkippedFrames => Interlocked.Read(ref skippedFrames);

        public void Offer(IntPtr bytes, int width, int height, int stride, bool noInput,
            CapturePixelFormat format, uint fieldDominance)
        {
            if (!Monitor.TryEnter(gate))
            {
                Interlocked.Increment(ref skippedFrames);
                return;
            }

            try
            {
                slot ??= new PreviewFrame();
                slot.Resize(width, height, stride);
                slot.SignalWidth = width;
                slot.SignalHeight = height;
                slot.NoInput = noInput;
                slot.Format = format;
                slot.FieldDominance = fieldDominance;
                Marshal.Copy(bytes, slot.Pixels, 0, checked(stride * height));
                if (ready)
                    Interlocked.Increment(ref skippedFrames);
                ready = true;
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }

        public void Publish(ref PreviewFrame? frame)
        {
            lock (gate)
            {
                if (ready)
                    Interlocked.Increment(ref skippedFrames);
                (slot, frame) = (frame, slot);
                ready = true;
            }
        }

        public bool TryTake(ref PreviewFrame? frame)
        {
            lock (gate)
            {
                if (!ready)
                    return false;

                (slot, frame) = (frame, slot);
                ready = false;
                return true;
            }
        }
    }
}

public sealed class PreviewFrame
{
    public byte[] Pixels { get; private set; } = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }
    public int SignalWidth { get; internal set; }
    public int SignalHeight { get; internal set; }
    public bool NoInput { get; internal set; }
    public CapturePixelFormat Format { get; internal set; }
    public uint FieldDominance { get; internal set; }

    internal void Resize(int width, int height, int stride)
    {
        var length = checked(stride * height);
        if (Pixels.Length < length)
            Pixels = new byte[length];

        Width = width;
        Height = height;
        Stride = stride;
    }
}
