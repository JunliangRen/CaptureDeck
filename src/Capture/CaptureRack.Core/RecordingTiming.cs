namespace CaptureRack.Core;

public sealed record RecordingTimestampIssue(long PacketNumber, decimal ExpectedTimestamp, long ActualTimestamp,
    decimal Offset, int? PreviousAudioSampleFrames, int? AudioSampleFrames, string InputSignal);

public sealed record RecordingTiming(long SuspectedMissingVideoFrames, long VideoCorrections, long VideoInvalidDurations,
    long AudioDiscontinuities, long AudioTimestampResets,
    decimal ApparentAudioGapSamples, decimal ApparentAudioOverlapSamples, decimal MaximumAudioOffsetSamples,
    RecordingTimestampIssue? FirstVideoCorrection, RecordingTimestampIssue? FirstAudioIssue);
