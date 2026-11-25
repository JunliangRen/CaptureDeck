using System.Numerics;

namespace CaptureRack.DeckLink;

internal sealed class VbiLineEvidence
{
    private const int MinimumObservations = 2;
    private byte[]? previousCandidate;
    private int repeatedWaveforms;
    private int captionObservations;

    public VancLineKind? Kind => captionObservations >= MinimumObservations ? VancLineKind.Caption608 :
        repeatedWaveforms >= MinimumObservations ? VancLineKind.Waveform : null;

    public void Observe(int line, ReadOnlySpan<byte> luma, IReadOnlyList<byte[]>? originalRows = null)
    {
        if (luma.Length <= 48 || Kind == VancLineKind.Caption608 || ResemblesUnmappedRow(luma, originalRows))
            return;

        if (HasCaption608(luma))
        {
            captionObservations++;
            return;
        }

        // Exclude the sync interval and active image rows from generic waveform reporting.
        if (line is < 10 or > 21 || repeatedWaveforms >= MinimumObservations ||
            !HasVbiWaveform(luma))
            return;

        repeatedWaveforms = previousCandidate is not null && MeanDifference(luma, previousCandidate) <= 10
            ? repeatedWaveforms + 1 : 1;
        previousCandidate = luma.ToArray();
    }

    private static bool ResemblesUnmappedRow(ReadOnlySpan<byte> row, IReadOnlyList<byte[]>? originals)
    {
        if (originals is null)
            return false;
        foreach (var original in originals)
        {
            if (row.Length == original.Length && MeanDifference(row, original) < 5)
                return true;
        }

        return false;
    }

    private static double MeanDifference(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var difference = 0;
        for (var x = 0; x < first.Length; x++)
            difference += Math.Abs(first[x] - second[x]);
        return (double)difference / first.Length;
    }

    private static bool HasVbiWaveform(ReadOnlySpan<byte> row)
    {
        var transitions = 0;
        var low = 255;
        var high = 0;
        var window = row[24..^24];
        for (var x = 0; x < window.Length; x++)
        {
            low = Math.Min(low, window[x]);
            high = Math.Max(high, window[x]);
        }

        if (high - low < 20)
            return false;
        var midpoint = (low + high) / 2;
        var previous = window[0] >= midpoint;
        foreach (var sample in window[1..])
        {
            var current = sample >= midpoint;
            if (current != previous)
                transitions++;
            previous = current;
        }

        return transitions is >= 6 and <= 160;
    }

    internal static bool HasCaption608(ReadOnlySpan<byte> row)
    {
        // 13.5 MHz / (32 × NTSC horizontal frequency) ≈ 26.8 samples per bit.
        const double period = 26.8125;
        const int clockCycles = 7;
        const int bits = 19; // Two zero start bits, one one start bit, then two odd-parity bytes.
        var searchEnd = Math.Min(120, row.Length - (int)Math.Ceiling((clockCycles + bits - 0.5) * period));
        var low = 255;
        var high = 0;
        foreach (var sample in row[..Math.Min(row.Length, 210)])
        {
            low = Math.Min(low, sample);
            high = Math.Max(high, sample);
        }

        if (high - low < 20)
            return false;
        var threshold = (low + high) / 2;
        for (var x = 1; x < searchEnd; x++)
        {
            if (row[x - 1] >= threshold || row[x] < threshold)
                continue;

            var matched = 0;
            for (var cycle = 0; cycle < clockCycles; cycle++)
            {
                var crest = x + (int)Math.Round((cycle + 0.25) * period);
                var trough = x + (int)Math.Round((cycle + 0.75) * period);
                if (row[crest] >= threshold + 3 && row[trough] <= threshold - 3)
                    matched++;
            }

            if (matched < 6)
                continue;

            for (var phase = 0; phase < 3; phase++)
            {
                var start = x + (clockCycles + phase * 0.2) * period;
                uint word = 0;
                for (var bit = 0; bit < bits; bit++)
                {
                    if (row[(int)Math.Round(start + bit * period)] >= threshold)
                        word |= 1u << bit;
                }
                if ((word & 7) == 4 && (BitOperations.PopCount((word >> 3) & 255) & 1) != 0 &&
                    (BitOperations.PopCount((word >> 11) & 255) & 1) != 0)
                    return true;
            }
        }

        return false;
    }
}
