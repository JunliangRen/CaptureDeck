namespace CaptureRack.Core;

public sealed record CaptureAudioInfo(string Codec, int? BitDepth, int? SampleRateHz, int? Channels,
    long? BitRate, bool BitRateEstimated = false);
