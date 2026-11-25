using CaptureRack.Core;
using CaptureRack.QualityControl;
using Xunit;

namespace CaptureRack.QualityControl.UT;

public sealed class RecordingWarningsTests
{
    [Fact]
    public void ReportsSavedPortionMissingTracksAndTimingCorrectionsSeparately()
    {
        var issue = new RecordingTimestampIssue(3, 1600, 1605, 5, 800, 800, "Present");
        var timing = new RecordingTiming(3, 2, 1, 4, 1, 1600, 12, 2400, issue, issue);
        var capture = new QcCaptureLog("Fixture", "Driver", "SDI", "Embedded", "NTSC",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            new QcExpectedMedia(2, 0, "progressive", 16, "v210", 320, 240),
            2, 0, 3, 4, 0, null, null, [])
        {
            Timing = timing,
            CaptureError = "Input stopped.",
            UnreadableNoInputFrames = 1
        };

        var warnings = QcReports.CaptureWarnings(capture);
        Assert.Contains(warnings, w => w.Contains("No audio samples"));
        Assert.Contains(warnings, w => w.Contains("no writable video payload"));
        Assert.Contains(warnings, w => w.Contains("suggest 3 missing frames"));
        Assert.Contains(warnings, w => w.Contains("writable portion was saved"));
        Assert.Contains(warnings, w => w.Contains("2 timestamp intervals corrected"));
        Assert.Contains(warnings, w => w.Contains("PCM was written continuously"));
        Assert.DoesNotContain(warnings, w => w.Contains("timestamp regressions"));
    }
}
