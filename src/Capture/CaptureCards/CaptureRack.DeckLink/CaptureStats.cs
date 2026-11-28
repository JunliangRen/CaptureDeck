using CaptureRack.Audio;
using CaptureRack.Core;

namespace CaptureRack.DeckLink;

public sealed class CaptureStats
{
    public VideoSignal Signal { get; internal set; }
    public bool CanStartRecording { get; internal set; }
    public CaptureAudioFormat? InputAudioFormat { get; internal set; }
    public uint AudioChannels { get; internal set; } = CaptureAudio.Channels;
    public CaptureAudioInfo? AudioInfo => AudioPackets > 0 && InputAudioFormat is { } format
        ? new CaptureAudioInfo("PCM", (int)format, (int)CaptureAudio.SampleRate, (int)AudioChannels,
            (long)CaptureAudio.BytesPerFrame(format, AudioChannels) * CaptureAudio.SampleRate * 8)
        : null;
    public bool RecordingModeChanged { get; internal set; }
    public string? RecordingError { get; internal set; }
    public long VideoFrames { get; internal set; }
    public long NoInputFrames { get; internal set; }
    public long UnreadableNoInputFrames { get; internal set; }
    public long AudioPackets { get; internal set; }
    public long AudioSampleFrames { get; internal set; }
    public long AudioPeak { get; internal set; }
    public StereoLevels? AudioLevels { get; internal set; }
    public long MissingVideoFrames { get; internal set; }
    public IReadOnlyList<CaptureEvent> Events { get; internal set; } = [];
    public long VideoSignalLossCount { get; internal set; }
    public TimeSpan VideoSignalLossDuration { get; internal set; }
    public IReadOnlyList<VideoSignalLoss> VideoSignalLosses { get; internal set; } = [];
    internal long? FirstVideoTime;
    internal long? FirstAudioTime;
    public long? VideoTime { get; internal set; }
    public long? AudioTime { get; internal set; }
    public long TimestampRegressions { get; internal set; }
    internal uint DetectedMode;
    internal uint? NextMode;
    public string? VideoPayload { get; internal set; }
    public string? NoInputVideoPayload { get; internal set; }
    public string? AudioPayload { get; internal set; }
    public string? Error { get; internal set; }
    public RecordingResult? Recording { get; internal set; }

    public bool SupportsRecordingAudioFormat(CaptureAudioFormat output) =>
        InputAudioFormat is { } input && CaptureAudio.SupportsRecordingFormat(input, output);

    public bool Verified => Error is null && TimestampRegressions == 0 &&
        VideoPayload is not null && AudioPayload is not null &&
        VideoTime > FirstVideoTime && AudioTime > FirstAudioTime;

    internal CaptureStats Clone() => (CaptureStats)MemberwiseClone();
}
