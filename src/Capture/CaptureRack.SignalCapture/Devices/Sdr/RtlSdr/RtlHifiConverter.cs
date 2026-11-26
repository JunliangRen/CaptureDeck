using System.Numerics;

namespace CaptureRack.SignalCapture;

// Fractional-delay phases reconstruct the selected complex input rate at 8 MSPS.
// The mirrored delay line keeps each FIR dot product contiguous across USB blocks.
internal sealed class RtlHifiConverter
{
    private const int Taps = 48;
    private static readonly float[][] Phases = Enumerable.Range(0, 256).Select(BuildPhase).ToArray();
    private readonly float[] iHistory = new float[Taps * 2], qHistory = new float[Taps * 2];
    private readonly double stepI, stepQ;
    private const int OutputRate = 8_000_000;
    private readonly uint center;
    private readonly int inputRate;
    private long fraction;
    public int OutputCapacity(int inputFrames) => checked((int)(((long)inputFrames * OutputRate + inputRate - 1) / inputRate));
    private uint phase;
    private double oscI = 1, oscQ;
    private int head, seen;
    private long emitted;

    public RtlHifiConverter(uint centerHz, int sampleRate = RfRecording.DefaultRtlSampleRate)
    {
        if (sampleRate <= 0 || sampleRate > 3_200_000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        inputRate = sampleRate;
        if (2L * centerHz <= sampleRate || 2L * centerHz + sampleRate >= OutputRate)
            throw new ArgumentOutOfRangeException(nameof(centerHz), "The tuned band must fit between DC and 4 MHz.");
        center = centerHz;
        double angle = 2 * Math.PI * center / 8_000_000;
        (stepQ, stepI) = Math.SinCos(angle);
    }

    private static float[] BuildPhase(int phase)
    {
        var weights = new float[Taps];
        for (int k = 0; k < weights.Length; k++)
        {
            double x = k - (Taps / 2 - 1 + phase / 256d);
            double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * k / (Taps - 1)) +
                0.08 * Math.Cos(4 * Math.PI * k / (Taps - 1));
            weights[k] = (float)(window * (x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x)));
        }
        float sum = weights.Sum();
        for (int k = 0; k < Taps; k++) weights[k] /= sum;
        return weights;
    }

    public int Process(ReadOnlySpan<byte> iq, Span<short> output)
    {
        int written = 0;
        for (int n = 0; n < iq.Length; n += 2)
        {
            iHistory[head] = iHistory[head + Taps] = iq[n] - 128;
            qHistory[head] = qHistory[head + Taps] = iq[n + 1] - 128;
            head = (head + 1) % Taps;
            if (seen < Taps / 2) { seen++; continue; }
            while (fraction < OutputRate)
            {
                var weights = Phases[(int)(fraction * Phases.Length / OutputRate)];
                fraction += inputRate;
                var iSum = Vector<float>.Zero;
                var qSum = Vector<float>.Zero;
                for (int k = 0; k < Taps; k += Vector<float>.Count)
                {
                    var w = new Vector<float>(weights, k);
                    iSum += w * new Vector<float>(iHistory, head + k);
                    qSum += w * new Vector<float>(qHistory, head + k);
                }
                double rf = Vector.Sum(iSum) * oscI - Vector.Sum(qSum) * oscQ;
                output[written++] = (short)Math.Clamp(Math.Round(rf * 128), short.MinValue, short.MaxValue);
                (oscI, oscQ) = (oscI * stepI - oscQ * stepQ, oscI * stepQ + oscQ * stepI);
                phase = (phase + center) % 8_000_000;
                if ((++emitted & 4095) == 0)
                    (oscQ, oscI) = Math.SinCos(2 * Math.PI * phase / 8_000_000);
            }
            fraction -= OutputRate;
        }
        return written;
    }

    public int Finish(Span<short> output)
    {
        if (seen == 0) return 0;
        Span<byte> silence = stackalloc byte[Taps];
        silence.Fill(128);
        return Process(silence, output);
    }
}
