using CaptureRack.DeckLink;
using CaptureRack.QualityControl;

namespace CaptureRack.App;

internal static class CaptureQcLog
{
    public static QcCaptureLog Create(CaptureStats stats, DateTimeOffset started, DateTimeOffset stopped,
        string device, string driver, string videoSource, string audioSource, string? captureError = null)
    {
        var saved = stats.Recording ?? throw new InvalidOperationException("Recording statistics are unavailable.");
        var field = DeckLinkCapture.FormatFourCc(saved.Mode.FieldDominance) switch
        {
            "uppr" => "tb",
            "lowr" => "bt",
            _ => "progressive"
        };
        var expected = new QcExpectedMedia(saved.VideoFrames, saved.AudioSampleFrames, field,
            (int)saved.AudioFormat, DeckLinkCapture.FormatFourCc((uint)saved.VideoFormat),
            saved.Mode.Width, saved.Mode.Height);
        return new QcCaptureLog(device, driver, videoSource, audioSource, saved.Mode.Description,
            started, stopped, expected, stats.VideoFrames, stats.NoInputFrames,
            saved.Timing?.SuspectedMissingVideoFrames ?? stats.MissingVideoFrames, stats.TimestampRegressions,
            stats.AudioPackets, stats.AudioLevels?.LeftClips, stats.AudioLevels?.RightClips,
            stats.Events.Select(item => new QcEvent(item.Elapsed, item.Description)).ToArray())
        {
            InputModeChanged = stats.RecordingModeChanged,
            Timing = saved.Timing,
            CaptureError = captureError ?? stats.Error,
            UnreadableNoInputFrames = stats.UnreadableNoInputFrames
        };
    }
}
