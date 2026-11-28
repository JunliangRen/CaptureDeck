namespace CaptureRack.DeckLink.Monitoring;

// Reuse the capture clock after an invalid interval without interpreting its first recovery step as a gap.
internal sealed class FrameTimestampTracker(long nominalDuration)
{
    private long? previous;
    private bool awaitingForwardInterval;

    public FrameInterval? Observe(long timestamp)
    {
        var prior = previous;
        previous = timestamp;
        if (prior is null)
            return null;

        var expected = (decimal)prior.Value + nominalDuration;
        var delta = (decimal)timestamp - prior.Value;
        if (delta <= 0 || delta > uint.MaxValue)
        {
            awaitingForwardInterval = true;
            return new((uint)nominalDuration, 0, true, expected);
        }

        if (awaitingForwardInterval)
        {
            awaitingForwardInterval = false;
            return new((uint)nominalDuration, 0, delta != nominalDuration, expected);
        }

        var frames = decimal.Round(delta / nominalDuration, 0, MidpointRounding.AwayFromZero);
        return new((uint)delta, Math.Max(0, (long)frames - 1), false, expected);
    }
}

internal readonly record struct FrameInterval(uint Duration, long SuspectedMissingFrames,
    bool Corrected, decimal ExpectedTimestamp);
