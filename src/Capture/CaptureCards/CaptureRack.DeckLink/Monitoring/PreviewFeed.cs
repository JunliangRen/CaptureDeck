using System.Diagnostics;
using System.Buffers;
using System.Runtime.InteropServices;

namespace CaptureRack.DeckLink;

public enum PreviewDeinterlaceMode { Off, BobWeave, BobWeave2x }

public enum PreviewScanMode { Unknown, Progressive, TopFirst, BottomFirst, ProgressiveSegmented }

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
    private readonly object scopeGate = new();
    private VideoScopes? scopes;
    private VideoScopes? scopeBackBuffer;
    private volatile bool renderScopes = true;
    private volatile bool scopeIreScale;
    private VideoScopeRaster scopeRaster = VideoScopeRaster.Default;
    private VideoScopes.Renderer? scopeRenderer;
    private byte[] previousLuma = [];
    private int previousLumaWidth;
    private int previousLumaHeight;
    private uint previousFieldDominance;
    private byte[] currentLuma = [];
    private int deinterlaceMode = (int)PreviewDeinterlaceMode.BobWeave;
    private int colorMatrix = (int)PreviewColorMatrix.Auto;
    private int scopeWaveformMode = (int)VideoScopeWaveformMode.Luma;

    public long PresentedSequence { get; private set; }
    public long SkippedFrames => incoming.SkippedFrames + outgoing.SkippedFrames;
    public bool RenderScopes
    {
        get => renderScopes;
        set => renderScopes = value;
    }

    public bool ScopeIreScale
    {
        get => scopeIreScale;
        set => scopeIreScale = value;
    }

    public VideoScopes? Scopes
    {
        get
        {
            lock (scopeGate)
            {
                return scopes is { } current ? current with
                {
                    LumaWaveform = (byte[])current.LumaWaveform.Clone(),
                    Vectorscope = (byte[])current.Vectorscope.Clone(),
                    SecondField = null
                } : null;
            }
        }
    }

    // The visitor can copy pixels into its display surfaces before this buffer is recycled.
    public void VisitScopes(Action<VideoScopes> visitor) => VisitScopes(visitor, Stopwatch.GetTimestamp());

    internal void VisitScopes(Action<VideoScopes> visitor, long now)
    {
        lock (scopeGate)
        {
            if (scopes is { } current)
                visitor(current.SecondField is { } second && now >= current.SecondFieldDueAt ? second : current);
        }
    }

    internal void WaitForInput(TimeSpan timeout) => incoming.WaitForFrame(timeout);

    public void SetScopeDisplaySize(double waveformWidth, double waveformHeight, double vectorSide, double scale)
    {
        var current = Volatile.Read(ref scopeRaster);
        var requested = VideoScopeRaster.FromDisplay(waveformWidth, waveformHeight, vectorSide, scale);
        if (requested != current)
            Volatile.Write(ref scopeRaster, requested);
    }

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

    public VideoScopeWaveformMode ScopeWaveformMode
    {
        get => (VideoScopeWaveformMode)Volatile.Read(ref scopeWaveformMode);
        set => Volatile.Write(ref scopeWaveformMode, (int)value);
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

    // Called by a single decoder worker after it owns a decoded progressive or interlaced frame.
    public unsafe void SubmitDecodedFrame(ReadOnlySpan<byte> pixels, int width, int height,
        bool? topFieldFirst, double framesPerSecond, int signalWidth, int signalHeight, double displayAspectRatio,
        string? signalFormat = null, PreviewScanMode? signalScanMode = null)
    {
        var stride = checked(width * 4);
        if (width <= 0 || height <= 0 || pixels.Length < checked(stride * height))
            throw new ArgumentException("Invalid decoded BGRA frame.", nameof(pixels));
        var field = topFieldFirst switch { true => UpperFieldFirst, false => LowerFieldFirst, null => 0u };
        fixed (byte* address = pixels)
            incoming.Offer((IntPtr)address, width, height, stride, false, CapturePixelFormat.Bgra, field);
        var interval = framesPerSecond > 0 ? (long)(Stopwatch.Frequency / (2 * framesPerSecond)) : 0;
        ConvertLatest(8, interval, signalWidth, signalHeight, displayAspectRatio, signalFormat, signalScanMode);
    }

    internal static bool IsInterlaced(uint fieldDominance) =>
        fieldDominance is UpperFieldFirst or LowerFieldFirst;

    internal void ConvertLatest(int lumaScaleBits, long fieldInterval = 0,
        int signalWidth = 0, int signalHeight = 0, double displayAspectRatio = 0, string? signalFormat = null,
        PreviewScanMode? signalScanMode = null)
    {
        if (!incoming.TryTake(ref source) || source is null)
            return;

        var input = source;
        var mode = input.IsInterlaced ? DeinterlaceMode : PreviewDeinterlaceMode.Off;
        var decoded = input.Format == CapturePixelFormat.Bgra;
        var stepX = decoded ? 1 : (input.Width + 959) / 960;
        // Keep full scanlines when deinterlacing interlaced SD/HD up to 1080i so Bob-Weave sees both fields.
        var stepY = mode != PreviewDeinterlaceMode.Off && input.Height <= 1080
            ? 1 : stepX;
        var width = (input.Width + stepX - 1) / stepX;
        var height = (input.Height + stepY - 1) / stepY;

        converted ??= new PreviewFrame();
        converted.Resize(width, height, checked(width * 4));
        input.SignalWidth = converted.SignalWidth = signalWidth > 0 ? signalWidth : input.Width;
        input.SignalHeight = converted.SignalHeight = signalHeight > 0 ? signalHeight : input.Height;
        converted.DisplayAspectRatio = displayAspectRatio;
        converted.SignalFormat = signalFormat;
        converted.NoInput = input.NoInput;
        converted.Format = input.Format;
        converted.FieldDominance = input.FieldDominance;
        converted.SignalScanMode = signalScanMode ?? (input.FieldDominance switch
        {
            UpperFieldFirst => PreviewScanMode.TopFirst,
            LowerFieldFirst => PreviewScanMode.BottomFirst,
            0x70726F67 => PreviewScanMode.Progressive, // prog
            0x70736666 => PreviewScanMode.ProgressiveSegmented, // psff
            _ => PreviewScanMode.Unknown
        });

        var deinterlace = mode != PreviewDeinterlaceMode.Off && stepY == 1 && height >= 4;
        var doubleRate = deinterlace && mode == PreviewDeinterlaceMode.BobWeave2x;
        var bottomFieldFirst = input.FieldDominance == LowerFieldFirst;
        if (decoded)
            CopyDecodedPixels(input, converted, deinterlace);
        else
            ConvertToBgra(input, converted, stepX, stepY, ColorMatrix, deinterlace);
        converted.SecondFieldDueAt = 0;
        if (previousFieldDominance != input.FieldDominance || input.NoInput)
            previousLumaWidth = 0;
        if (doubleRate)
        {
            var secondFieldPixels = converted.CopyPixelsForSecondField();
            ApplyMotionAdaptiveBobWeave(secondFieldPixels, width, height, converted.Stride, !bottomFieldFirst);
        }
        if (deinterlace)
        {
            ApplyMotionAdaptiveBobWeave(converted.Pixels, width, height, converted.Stride, bottomFieldFirst);
            (previousLuma, currentLuma) = (currentLuma, previousLuma);
            previousLumaWidth = width;
            previousLumaHeight = height;
            previousFieldDominance = input.FieldDominance;
        }
        else
        {
            previousLumaWidth = 0;
        }

        if (doubleRate)
            converted.SecondFieldDueAt = Stopwatch.GetTimestamp() + fieldInterval;
        outgoing.Publish(ref converted);
        if (!RenderScopes)
            return;

        var raster = Volatile.Read(ref scopeRaster);
        if (scopeRenderer?.Raster != raster)
            scopeRenderer = new VideoScopes.Renderer(raster);

        var waveformMode = ScopeWaveformMode;
        var matrix = ColorMatrix;
        var splitFields = fieldInterval > 0 && input.IsInterlaced && input.Height >= 2 && !input.NoInput;
        int? firstField = splitFields ? (bottomFieldFirst ? 1 : 0) : null;
        var ireScale = ScopeIreScale;
        var next = scopeRenderer.Analyze(input, lumaScaleBits, waveformMode, matrix, scopeBackBuffer, firstField, ireScale: ireScale);
        if (firstField is int parity)
        {
            var second = scopeRenderer.Analyze(input, lumaScaleBits, waveformMode, matrix,
                scopeBackBuffer?.SecondField, 1 - parity, measureLuma: false, ireScale: ireScale);
            next = next with
            {
                SecondField = second with { LumaOutsideLegalFraction = next.LumaOutsideLegalFraction },
                SecondFieldDueAt = Stopwatch.GetTimestamp() + fieldInterval
            };
        }

        lock (scopeGate)
            (scopes, scopeBackBuffer) = (next, scopes);
    }

    public bool TryTakeLatest(ref PreviewFrame? frame) =>
        TryTakeLatest(ref frame, Stopwatch.GetTimestamp());

    internal bool TryTakeLatest(ref PreviewFrame? frame, long now)
    {
        // Finish the displayed field pair before taking the next completed input frame.
        if (frame is { SecondFieldDueAt: > 0 })
        {
            if (DeinterlaceMode == PreviewDeinterlaceMode.BobWeave2x)
            {
                if (now < frame.SecondFieldDueAt)
                    return false;

                frame.PresentSecondField();
                PresentedSequence++;
                return true;
            }

            frame.SecondFieldDueAt = 0;
        }

        if (!outgoing.TryTake(ref frame))
            return false;

        PresentedSequence++;
        return true;
    }

    private void CopyDecodedPixels(PreviewFrame input, PreviewFrame output, bool recordLuma)
    {
        var pixels = input.Pixels.AsSpan(0, input.Stride * input.Height);
        pixels.CopyTo(output.Pixels);
        if (!recordLuma)
            return;

        var packed = MemoryMarshal.Cast<byte, uint>(pixels);
        if (currentLuma.Length < packed.Length)
            currentLuma = new byte[packed.Length];
        var luma = currentLuma.AsSpan(0, packed.Length);
        for (var i = 0; i < packed.Length; i++)
        {
            var pixel = packed[i];
            luma[i] = (byte)((29 * (pixel & 255) + 150 * ((pixel >> 8) & 255) +
                77 * ((pixel >> 16) & 255) + 128) >> 8);
        }
    }

    private void ConvertToBgra(PreviewFrame source, PreviewFrame destination, int stepX, int stepY,
        PreviewColorMatrix matrix, bool recordLuma)
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

        if (recordLuma && currentLuma.Length < width * height)
            currentLuma = new byte[width * height];

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
    }

    // Motion-adaptive Bob-Weave: weaves crisp full-detail pixels in static regions, smoothly
    // transitions to vertical Bob interpolation where moving fields produce spatial combing.
    private void ApplyMotionAdaptiveBobWeave(Span<byte> bgra, int width, int height, int stride, bool bottomFieldFirst)
    {
        var hasPrev = previousLumaWidth == width && previousLumaHeight == height;
        var secondaryParity = bottomFieldFirst ? 0 : 1;
        const int lowThreshold = 10;
        const int highThreshold = 34;

        for (var row = secondaryParity; row < height; row += 2)
        {
            var upY = row == 0 ? 1 : row - 1;
            var downY = row == height - 1 ? row - 1 : row + 1;
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

                // A static horizontal line can have the same vertical profile as a comb tooth.
                // Once a previous frame is available, require motion in the local three-line window.
                var temporal = hasPrev ? Math.Max(
                    Math.Abs(yCurr - previousLuma[currLumaRow + x]), Math.Max(
                    Math.Abs(yUp - previousLuma[upLumaRow + x]),
                    Math.Abs(yDown - previousLuma[downLumaRow + x]))) : highThreshold;
                if (temporal <= lowThreshold)
                    continue;

                var spatialComb = Math.Abs(yCurr - ((yUp + yDown) >> 1)) - (Math.Abs(yUp - yDown) >> 1);
                var motion = Math.Min(spatialComb, temporal);
                if (motion <= lowThreshold)
                    continue;

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
                Monitor.Pulse(gate);
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

        public void WaitForFrame(TimeSpan timeout)
        {
            lock (gate)
            {
                if (!ready && timeout > TimeSpan.Zero)
                    Monitor.Wait(gate, timeout);
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
    private byte[] secondFieldPixels = [];

    public byte[] Pixels { get; private set; } = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }
    public int SignalWidth { get; internal set; }
    public int SignalHeight { get; internal set; }
    public double DisplayAspectRatio { get; internal set; }
    public string? SignalFormat { get; internal set; }
    public PreviewScanMode SignalScanMode { get; internal set; }
    public bool NoInput { get; internal set; }
    public CapturePixelFormat Format { get; internal set; }
    public uint FieldDominance { get; internal set; }
    public bool IsInterlaced => PreviewFeed.IsInterlaced(FieldDominance);
    internal long SecondFieldDueAt { get; set; }

    internal Span<byte> CopyPixelsForSecondField()
    {
        var length = checked(Stride * Height);
        if (secondFieldPixels.Length < length)
            secondFieldPixels = new byte[length];

        Pixels.AsSpan(0, length).CopyTo(secondFieldPixels);
        return secondFieldPixels.AsSpan(0, length);
    }

    internal void PresentSecondField()
    {
        (Pixels, secondFieldPixels) = (secondFieldPixels, Pixels);
        SecondFieldDueAt = 0;
    }

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
