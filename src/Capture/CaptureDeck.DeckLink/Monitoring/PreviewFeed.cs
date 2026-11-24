using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CaptureDeck.DeckLink;

// The SDK callback copies a borrowed frame into a single pending slot. The capture
// worker converts the latest frame and the UI consumes the latest completed bitmap.
public sealed class PreviewFeed
{
    private readonly LatestFrame incoming = new();
    private readonly LatestFrame outgoing = new();
    private PreviewFrame? source;
    private PreviewFrame? converted;
    private VideoScopes? scopes;
    private long lastScopeUpdate;

    public long PresentedSequence { get; private set; }
    public VideoScopes? Scopes => Volatile.Read(ref scopes);

    internal void Offer(IntPtr bytes, int width, int height, int stride, ulong size, bool noInput, CapturePixelFormat format)
    {
        var minimumStride = format == CapturePixelFormat.V210 ? ((width + 5L) / 6) * 16 : ((width + 1L) / 2) * 4;
        if (width <= 0 || height <= 0 || stride < minimumStride ||
            (ulong)stride * (ulong)height > size)
            return;

        incoming.Offer(bytes, width, height, stride, noInput, format);
    }

    internal void ConvertLatest(int lumaScaleBits)
    {
        if (!incoming.TryTake(ref source) || source is null)
            return;

        var input = source;
        var step = (input.Width + 959) / 960;
        var width = (input.Width + step - 1) / step;
        var height = (input.Height + step - 1) / step;
        converted ??= new PreviewFrame();
        converted.Resize(width, height, checked(width * 4));
        converted.SignalWidth = input.Width;
        converted.SignalHeight = input.Height;
        converted.NoInput = input.NoInput;
        converted.Format = input.Format;
        ConvertToBgra(input, converted, step);
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

    private static void ConvertToBgra(PreviewFrame source, PreviewFrame destination, int step)
    {
        // v210 has four little-endian words per six Y' samples and three Cb/Cr pairs.
        // The display conversion uses video range and BT.601 for SD or BT.709 for HD.
        var sd = source.SignalHeight <= 576;
        var red = sd ? 409 : 459;
        var greenCb = sd ? 100 : 55;
        var greenCr = sd ? 208 : 136;
        var blue = sd ? 516 : 541;
        var output = destination.Pixels.AsSpan();
        for (var row = 0; row < destination.Height; row++)
        {
            var input = source.Pixels.AsSpan(row * step * source.Stride, source.Stride);
            var words = MemoryMarshal.Cast<byte, uint>(input);
            for (int pixel = 0, x = 0; pixel < destination.Width; pixel++, x += step)
            {
                ReadYuv(input, words, x, source.Format, out var y, out var cb, out var cr);
                var luma = 298 * (y - 64);
                var index = row * destination.Stride + pixel * 4;
                output[index] = ToByte(luma + blue * cb);
                output[index + 1] = ToByte(luma - greenCb * cb - greenCr * cr);
                output[index + 2] = ToByte(luma + red * cr);
                output[index + 3] = 255;
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

        public void Offer(IntPtr bytes, int width, int height, int stride, bool noInput, CapturePixelFormat format)
        {
            if (!Monitor.TryEnter(gate))
                return;

            try
            {
                slot ??= new PreviewFrame();
                slot.Resize(width, height, stride);
                slot.SignalWidth = width;
                slot.SignalHeight = height;
                slot.NoInput = noInput;
                slot.Format = format;
                Marshal.Copy(bytes, slot.Pixels, 0, checked(stride * height));
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
