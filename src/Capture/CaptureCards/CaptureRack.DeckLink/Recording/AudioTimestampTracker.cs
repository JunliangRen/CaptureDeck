using CaptureRack.Core;

namespace CaptureRack.DeckLink;

internal sealed class AudioTimestampTracker
{
    private long previousTimestamp;
    private int previousSamples;
    private long packets;
    private long? firstTimestamp;
    private long samples;
    private bool awaitingForwardInterval;

    public long Discontinuities { get; private set; }
    public long Resets { get; private set; }
    public decimal ApparentGapSamples { get; private set; }
    public decimal ApparentOverlapSamples { get; private set; }
    public decimal MaximumOffsetSamples { get; private set; }
    public RecordingTimestampIssue? FirstIssue { get; private set; }

    public void Observe(long timestamp, int sampleFrames, VideoSignal signal)
    {
        packets++;
        firstTimestamp ??= timestamp;
        var expectedFromStart = (decimal)firstTimestamp.Value + samples;
        var cumulativeOffset = timestamp - expectedFromStart;
        MaximumOffsetSamples = Math.Max(MaximumOffsetSamples, Math.Abs(cumulativeOffset));

        if (packets > 1)
        {
            var expected = (decimal)previousTimestamp + previousSamples;
            var offset = timestamp - expected;
            var reset = timestamp <= previousTimestamp;
            var discontinuity = reset || Math.Abs(offset) > 1;
            if (discontinuity)
            {
                Discontinuities++;
                if (reset)
                    Resets++;
                else if (!awaitingForwardInterval)
                {
                    if (offset > 0)
                        ApparentGapSamples += offset;
                    else
                        ApparentOverlapSamples -= offset;
                }
            }

            awaitingForwardInterval = reset;

            if (FirstIssue is null && (discontinuity || MaximumOffsetSamples > 1))
            {
                FirstIssue = new(packets, discontinuity ? expected : expectedFromStart, timestamp,
                    discontinuity ? offset : cumulativeOffset, previousSamples, sampleFrames, signal.ToString());
            }
        }
        previousTimestamp = timestamp;
        previousSamples = sampleFrames;
        samples += sampleFrames;
    }
}
