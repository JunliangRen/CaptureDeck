using System.Numerics;

namespace CaptureRack.SignalCapture;

// Centered Blackman-windowed sinc. Keep the filter history and sample phase across
// USB blocks. Zero padding at both file boundaries preserves the clip time origin.
internal sealed class DomesdayDecimator
{
    private readonly int factor, radius;
    private readonly float[] kernel;
    private float[] window = [];
    private int length, next;

    public DomesdayDecimator(int factor)
    {
        if (factor is not (2 or 4 or 8)) throw new ArgumentOutOfRangeException(nameof(factor));
        this.factor = factor;
        radius = 24 * factor;
        length = radius;
        int taps = 2 * radius + 1;
        kernel = new float[(taps + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count];
        double sum = 0, cutoff = 0.46 / factor;
        for (int i = 0; i < taps; i++)
        {
            int t = i - radius;
            double sinc = t == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * t) / (Math.PI * t);
            double angle = Math.PI * t / radius;
            kernel[i] = (float)(sinc * (0.42 + 0.5 * Math.Cos(angle) + 0.08 * Math.Cos(2 * angle)));
            sum += kernel[i];
        }
        for (int i = 0; i < taps; i++) kernel[i] /= (float)sum;
    }

    public int Finish(Span<short> output) => Process(new short[radius], output);

    public int Process(ReadOnlySpan<short> input, Span<short> output)
    {
        int required = length + input.Length + Vector<float>.Count;
        if (window.Length < required) Array.Resize(ref window, required);
        for (int i = 0; i < input.Length; i++) window[length++] = input[i];
        int count = 0;
        while (next + 2 * radius < length)
        {
            var sum = Vector<float>.Zero;
            for (int i = 0; i < kernel.Length; i += Vector<float>.Count)
                sum += new Vector<float>(window, next + i) * new Vector<float>(kernel, i);
            output[count++] = (short)Math.Clamp(MathF.Round(Vector.Sum(sum)), short.MinValue, short.MaxValue);
            next += factor;
        }
        Array.Copy(window, next, window, 0, length - next);
        length -= next;
        next = 0;
        return count;
    }
}
