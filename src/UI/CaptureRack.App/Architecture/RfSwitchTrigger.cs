namespace CaptureRack.App;

// The decoder can publish a field after the next transfer has arrived. Retain a
// short absolute sample history during tape file playback or live head-switch inspection.
internal sealed class RfSwitchTrigger
{
    private const int Capacity = 1 << 22;
    private short[] ring = [];
    private long begin, end;

    public void Add(ReadOnlySpan<short> samples, long first)
    {
        if (ring.Length == 0) ring = new short[Capacity];
        if (first != end) begin = first;
        if (samples.Length > Capacity) { first += samples.Length - Capacity; samples = samples[^Capacity..]; }
        int offset = (int)(first & (Capacity - 1));
        int part = Math.Min(samples.Length, Capacity - offset);
        samples[..part].CopyTo(ring.AsSpan(offset));
        samples[part..].CopyTo(ring);
        end = first + samples.Length;
        begin = Math.Max(begin, end - Capacity);
    }

    public short[]? Find(TimeSpan fieldStart, int rate)
    {
        int perUs = rate / 1_000_000;
        long center = (long)Math.Round(fieldStart.TotalSeconds * rate);
        long start = center - 1200 * perUs;
        const int bins = 1600;
        if (perUs == 0 || start < begin || start + bins * perUs > end || ring.Length == 0) return null;
        double sum = 0;
        for (int i = 0; i < bins * perUs; i++) sum += At(start + i);
        double mean = sum / (bins * perUs);
        var prefix = new double[bins + 1];
        for (int i = 0; i < bins; i++)
        {
            double magnitude = 0;
            for (int j = 0; j < perUs; j++) magnitude += Math.Abs(At(start + i * perUs + j) - mean);
            prefix[i + 1] = prefix[i] + magnitude;
        }
        var steps = new double[bins - 12];
        int best = 6;
        double peak = 0;
        for (int i = 6; i < bins - 6; i++)
        {
            double left = prefix[i] - prefix[i - 6], right = prefix[i + 6] - prefix[i];
            double step = steps[i - 6] = Math.Abs(right - left) / Math.Max(1, right + left);
            if (step > peak) { peak = step; best = i; }
        }
        Array.Sort(steps);
        if (peak < Math.Max(0.1, 8 * steps[steps.Length / 2])) return null;
        var trace = new short[256];
        long origin = start + best * perUs - trace.Length / 2;
        if (origin < begin || origin + trace.Length > end) return null;
        for (int i = 0; i < trace.Length; i++) trace[i] = At(origin + i);
        return trace;
    }

    private short At(long sample) => ring[(int)(sample & (Capacity - 1))];
}
