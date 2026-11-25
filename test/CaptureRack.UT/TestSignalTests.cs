using CaptureRack.App;
using CaptureRack.DeckLink;
using CaptureRack.QualityControl;
using Xunit;

namespace CaptureRack.UT;

public sealed class TestSignalTests
{
    [Theory]
    [InlineData(TestSignalScenario.Normal, 80, 0, 0, 0)]
    [InlineData(TestSignalScenario.TimingFaults, 79, 12, 1, 1)]
    public async Task RecordsExactMediaAndReportsInjectedFaults(TestSignalScenario scenario,
        int videoFrames, int noInputFrames, int missingFrames, int audioResets)
    {
        var folder = Directory.CreateTempSubdirectory("capturerack-test-signal-");
        try
        {
            var path = Path.Combine(folder.FullName, $"{scenario}.mov");
            var feed = new PreviewFeed();
            var recorder = new PreviewRecorder();
            using var session = new TestSignal.Session(scenario, feed, recorder);
            Assert.False(session.Snapshot().CanStartRecording);

            session.Emit(0);
            PreviewFrame? frame = null;
            Assert.True(feed.TryTakeLatest(ref frame));
            Assert.NotNull(frame);
            Assert.Equal((720, 486), (frame.Width, frame.Height));
            Assert.False(frame.NoInput);
            Assert.NotEqual(frame.Pixels[0], frame.Pixels[120 * 4]);
            Assert.Equal(255, frame.Pixels[3]);
            Assert.NotNull(feed.Scopes);
            Assert.Equal(scenario == TestSignalScenario.TimingFaults ? 1.0 / 60 : 0,
                feed.Scopes.LumaOutsideLegalFraction!.Value, 8);
            Assert.True(session.Snapshot().AudioPeak > 0);
            Assert.True(session.Snapshot().CanStartRecording);
            var started = recorder.Start(path);
            for (var i = 1; i <= 80; i++)
            {
                session.Emit(i);
                if (i != 45)
                    continue;
                Assert.True(feed.TryTakeLatest(ref frame));
                Assert.Equal(scenario == TestSignalScenario.TimingFaults, frame!.NoInput);
                if (frame.NoInput)
                    Assert.Equal(new byte[] { 0, 0, 0, 255 }, frame.Pixels[..4]);
            }
            var stats = recorder.Finish();
            var log = CaptureQcLog.Create(stats, started, DateTimeOffset.UtcNow,
                "CaptureRack test signal", "Built-in", scenario.ToString(), "Stereo test tone");

            Assert.Null(log.CaptureError);
            Assert.Equal(videoFrames, log.Expected.VideoFrames);
            Assert.Equal(128128, log.Expected.AudioSampleFrames);
            Assert.Equal((720, 486, "bt", "v210", 16), (log.Expected.Width, log.Expected.Height,
                log.Expected.FieldOrder, log.Expected.VideoTag, log.Expected.AudioBits));
            Assert.Equal(noInputFrames, log.NoInputFrames);
            Assert.Equal(videoFrames - noInputFrames, log.ValidVideoFrames);
            Assert.Equal(80, log.AudioPackets);
            Assert.Equal(missingFrames, log.MissingVideoFrames);
            Assert.Equal(audioResets, log.Timing!.AudioTimestampResets);
            Assert.Equal(0, log.Timing.ApparentAudioGapSamples);
            if (scenario == TestSignalScenario.TimingFaults)
            {
                Assert.Equal(-4800m, log.Timing.FirstAudioIssue!.Offset);
                Assert.Equal(70, log.Timing.FirstAudioIssue.PacketNumber);
                Assert.Contains(stats.Events, item => item.Kind == CaptureEventKind.FrameGap &&
                    item.MissingFrames == 1 && item.Elapsed == TimeSpan.FromTicks(10_343_666));
                var interval = Assert.Single(log.VideoSignalLosses);
                Assert.Equal(1, log.VideoSignalLossCount);
                Assert.Equal(TimeSpan.FromTicks(15_015_000), interval.Elapsed);
                Assert.Equal(TimeSpan.FromTicks(4_004_000), interval.Duration);
                Assert.Equal(interval.Duration, log.VideoSignalLossDuration);
                Assert.True(interval.Restored);
                Assert.False(interval.StartedUnlocked);
                Assert.Contains(stats.Events, item => item.Kind == CaptureEventKind.SignalLost && item.Elapsed == interval.Elapsed);
                Assert.Contains(stats.Events, item => item.Kind == CaptureEventKind.SignalRestored &&
                    item.Elapsed == interval.Elapsed + interval.Duration && item.SignalLossDuration == interval.Duration);
            }
            else
            {
                Assert.Null(log.Timing.FirstAudioIssue);
                Assert.Empty(log.RecentEvents);
                Assert.Empty(log.VideoSignalLosses);
                Assert.Equal(0, log.VideoSignalLossCount);
            }

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".partial"));
            session.Emit(81);
            Assert.True(session.Snapshot().CanStartRecording);

            // The CI integration step supplies FFmpeg. Unit tests also run on machines without it.
            if (Environment.GetEnvironmentVariable("CAPTURERACK_TEST_SIGNAL_QC") == "1")
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var result = await QcReports.RunAsync(path, log, cancellation: timeout.Token);
                Assert.True(result.Status == QcStatus.Passed, result.Summary);
                Assert.Equal(videoFrames, result.DecodedVideoFrames);
                Assert.Equal(128128, result.DecodedAudioSampleFrames);
                Assert.True(File.Exists(result.CaptureLog));
                Assert.True(File.Exists(result.FrameMd5));
                Assert.True(File.Exists(result.QcToolsReport));
                if (scenario == TestSignalScenario.Normal)
                    Assert.Empty(result.Warnings);
                else
                {
                    Assert.Contains(result.Warnings, warning => warning.Contains("missing frames", StringComparison.OrdinalIgnoreCase));
                    Assert.Contains(result.Warnings, warning => warning.Contains("Video lock lost 1", StringComparison.OrdinalIgnoreCase));
                    using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(result.CaptureLog));
                    var capture = report.RootElement.GetProperty("capture");
                    var intervals = capture.GetProperty("videoSignalLosses");
                    Assert.Equal(1, intervals.GetArrayLength());
                    Assert.Equal("00:00:01.5015000", intervals[0].GetProperty("elapsed").GetString());
                    Assert.Equal("00:00:00.4004000", intervals[0].GetProperty("duration").GetString());
                    Assert.Contains(result.Warnings, warning => warning.Contains("no input", StringComparison.OrdinalIgnoreCase));
                    Assert.Contains(result.Warnings, warning => warning.Contains("audio timestamp", StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RealTimePreviewPublishesFramesAndStopsOnCancellation()
    {
        var feed = new PreviewFeed();
        var recorder = new PreviewRecorder();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stats = await Task.Run(() => TestSignal.Run(TestSignalScenario.Normal, feed, recorder, null, update =>
        {
            Assert.Equal(TestSignal.Mode, update.Mode);
            if (update.Stats.VideoFrames >= 2)
                stop.Cancel();
        }, stop.Token));
        PreviewFrame? frame = null;
        Assert.True(feed.TryTakeLatest(ref frame));
        Assert.True(stats.VideoFrames >= 2);
        Assert.Throws<InvalidOperationException>(() => recorder.Start("unused.mov"));
    }
}
