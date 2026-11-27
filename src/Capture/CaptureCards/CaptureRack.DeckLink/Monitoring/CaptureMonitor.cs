using CaptureRack.Core;

namespace CaptureRack.DeckLink;

public enum CaptureEventKind { Unknown, TimestampRegression, FrameGap, NoInputAtStart, SignalLost, SignalRestored }

public sealed record CaptureEvent(TimeSpan Elapsed, string Description)
{
    public CaptureEventKind Kind { get; init; }
    public long MissingFrames { get; init; }
    public TimeSpan? SignalLossDuration { get; init; }

    public string TimeLabel =>
        $"{(int)Elapsed.TotalHours:00}:{Elapsed.Minutes:00}:{Elapsed.Seconds:00}.{Elapsed.Milliseconds:000}";
}

internal sealed class FrameTimeline(VideoMode mode, bool keepSignalHistory = false)
{
    private const int MaxEvents = 200;
    private readonly Queue<CaptureEvent> events = new();
    private IReadOnlyList<CaptureEvent>? snapshot;
    private long? firstTime;
    private readonly FrameTimestampTracker timestamps = new(mode.FrameDuration);
    private bool? previousNoInput;
    private TimeSpan? lossStarted;
    private bool startedUnlocked;
    private TimeSpan completedLossDuration;
    private readonly List<VideoSignalLoss>? signalLosses = keepSignalHistory ? [] : null;
    public long MissingFrames { get; private set; }
    public long SignalLossCount { get; private set; }

    public TimeSpan SignalLossDuration(TimeSpan elapsed) =>
        completedLossDuration + (lossStarted is { } start ? elapsed - start : TimeSpan.Zero);

    public IReadOnlyList<VideoSignalLoss> SignalLosses(TimeSpan elapsed)
    {
        if (signalLosses is null)
            return [];
        return lossStarted is { } start
            ? [.. signalLosses, new(start, elapsed - start, startedUnlocked, Restored: false)]
            : signalLosses.ToArray();
    }

    public void Observe(long time, bool noInput, TimeSpan? observedElapsed = null)
    {
        firstTime ??= time;
        var elapsedTicks = ((decimal)time - firstTime.Value) * TimeSpan.TicksPerSecond / mode.TimeScale;
        var elapsed = observedElapsed ?? TimeSpan.FromTicks((long)Math.Clamp(elapsedTicks, 0, TimeSpan.MaxValue.Ticks));

        if (timestamps.Observe(time) is { } interval)
        {
            if (interval.Corrected)
                Add(elapsed, CaptureEventKind.TimestampRegression, "Video timestamp discontinuity.");
            if (interval.SuspectedMissingFrames > 0)
            {
                MissingFrames += interval.SuspectedMissingFrames;
                Add(elapsed, CaptureEventKind.FrameGap,
                    $"Video timestamp gap · {interval.SuspectedMissingFrames:N0} suspected missing frame(s).",
                    interval.SuspectedMissingFrames);
            }
        }

        ObserveSignal(elapsed, noInput);
    }

    public void ObserveSignal(TimeSpan elapsed, bool noInput)
    {
        if (previousNoInput == noInput)
            return;

        if (noInput)
        {
            lossStarted = elapsed;
            startedUnlocked = previousNoInput is null;
            if (!startedUnlocked)
                SignalLossCount++;
            Add(elapsed, startedUnlocked ? CaptureEventKind.NoInputAtStart : CaptureEventKind.SignalLost,
                startedUnlocked ? "No input signal at start." : "Video input signal lost.");
        }
        else if (lossStarted is { } start)
        {
            var duration = elapsed - start;
            completedLossDuration += duration;
            signalLosses?.Add(new(start, duration, startedUnlocked, Restored: true));
            lossStarted = null;
            Add(elapsed, CaptureEventKind.SignalRestored, $"Video input signal restored after {duration.TotalSeconds:F3} s.",
                signalLossDuration: duration);
        }
        previousNoInput = noInput;
    }

    private void Add(TimeSpan elapsed, CaptureEventKind kind, string message, long missing = 0,
        TimeSpan? signalLossDuration = null)
    {
        if (events.Count == MaxEvents)
            events.Dequeue();
        events.Enqueue(new(elapsed, message) { Kind = kind, MissingFrames = missing, SignalLossDuration = signalLossDuration });
        snapshot = null;
    }

    public IReadOnlyList<CaptureEvent> Events() => snapshot ??= events.Reverse().ToArray();
}
