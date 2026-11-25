namespace CaptureRack.Core;

// Elapsed and duration are relative to the recording, including an interval already open when it began.
public sealed record VideoSignalLoss(TimeSpan Elapsed, TimeSpan Duration, bool StartedUnlocked, bool Restored);
