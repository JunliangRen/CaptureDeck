using System.Globalization;
using System.Runtime.InteropServices;
using CaptureRack.App;
using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.QualityControl;
using Xunit;

namespace CaptureRack.UT;

public sealed class WorkspaceArchitectureTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("Tape04_SideA", "Tape04_SideA")]
    [InlineData("  Reel:01/02*\"?<>|  ", "Reel_01_02")]
    [InlineData("...___Tape-12___...", "Tape-12")]
    public void RecordingFileNamingSanitizesReelLabels(string? raw, string expected) =>
        Assert.Equal(expected, RecordingFileNaming.SanitizeLabel(raw));

    [Fact]
    public void RecordingFileNamingTruncatesOverlongLabelsTo48Chars()
    {
        var longLabel = new string('A', 70);
        var sanitized = RecordingFileNaming.SanitizeLabel(longLabel);
        Assert.Equal(48, sanitized.Length);
    }

    [Fact]
    public void RecordingFileNamingBuildsExpectedMovPathWithAndWithoutLabelAndCollisionSuffixes()
    {
        var time = new DateTime(2026, 11, 25, 9, 30, 45, 123);
        var defaultPath = RecordingFileNaming.BuildMovPath(@"C:\Captures", "", time, "10bit-Uncompressed", "16bit-PCM", _ => false);
        Assert.Equal(@"C:\Captures\CaptureRack_20261125_093045_123_10bit-Uncompressed_16bit-PCM.mov", defaultPath);

        var labeledPath = RecordingFileNaming.BuildMovPath(@"C:\Captures", "Tape04/SideA", time, "8bit-Uncompressed", "32bit-PCM", _ => false);
        Assert.Equal(@"C:\Captures\Tape04_SideA_CaptureRack_20261125_093045_123_8bit-Uncompressed_32bit-PCM.mov", labeledPath);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Captures\Tape04_CaptureRack_20261125_093045_123_10bit-Uncompressed_16bit-PCM.mov",
            @"C:\Captures\Tape04_CaptureRack_20261125_093045_123_2_10bit-Uncompressed_16bit-PCM.mov"
        };
        var collisionPath = RecordingFileNaming.BuildMovPath(@"C:\Captures", "Tape04", time, "10bit-Uncompressed", "16bit-PCM", existing.Contains);
        Assert.Equal(@"C:\Captures\Tape04_CaptureRack_20261125_093045_123_3_10bit-Uncompressed_16bit-PCM.mov", collisionPath);
    }

    [Fact]
    public void FireWireRecordingUsesTsExtensionAndMarks192InThePrefix()
    {
        var time = new DateTime(2026, 11, 25, 9, 30, 45, 123);
        Assert.Equal("CaptureRack_iLINK192_20261125_093045_123.ts", RecordingFileNaming.BuildFireWireName(null, time));
        Assert.Equal("Tape04_SideA_CaptureRack_iLINK192_20261125_093045_123.ts",
            RecordingFileNaming.BuildFireWireName("Tape04/SideA", time));
        Assert.Equal("Tape04_SideA_CaptureRack_iLINK192_YYYYMMDD_HHmmss_fff.ts",
            RecordingFileNaming.BuildFireWireName("Tape04/SideA", localTime: null));
        Assert.Equal("CaptureRack_iLINK188_20261125_093045_123.ts",
            RecordingFileNaming.BuildFireWireName(null, time, new(TsLayout.Ts188)));
        Assert.Equal("CaptureRack_iLINK_DV_20261125_093045_123.dv",
            RecordingFileNaming.BuildFireWireName(null, time, CaptureRack.DirectShow.Windows.FireWire.MsTape.FireWireMediaFormat.Dv));
    }

    [Fact]
    public void RecordingFileNamingBuildsDirectShowPreviewWithFormatAndContainers()
    {
        Assert.Equal("CaptureRack_YYYYMMDD_HHmmss_fff_DirectShow_YUY2_16bit-PCM.mov (.mov / .mp4 / .mkv)",
            RecordingFileNaming.BuildDirectShowPreviewName("", "YUY2", 16, [".mov", ".mp4", ".mkv"]));
        Assert.Equal("Tape01_CaptureRack_YYYYMMDD_HHmmss_fff_DirectShow_MPEG2.ts",
            RecordingFileNaming.BuildDirectShowPreviewName("Tape01", "MPEG2", null, [".ts"]));
        Assert.Equal("CaptureRack_YYYYMMDD_HHmmss_fff_DirectShow_Video.mkv",
            RecordingFileNaming.BuildDirectShowPreviewName(null, null, 0, null));
    }

    [Fact]
    public void DirectShowWorkspaceModuleBuildsSeverityCodedLiveTelemetry()
    {
        var module = new DirectShowWorkspaceModule();
        Assert.Equal(DirectShowWorkspaceModule.BackendId, module.Descriptor.BackendId);
        Assert.True(module.Descriptor.IsImplementedInUi);

        var clean = DirectShowWorkspaceModule.BuildLiveTelemetry(120, 120, 0, -2, k => k, CultureInfo.InvariantCulture);
        Assert.Equal(4, clean.Count);
        Assert.All(clean, m => Assert.Equal(TelemetrySeverity.Normal, m.Severity));
        Assert.Equal("0", clean.Single(m => m.Id == "ds-skipped").Value);

        var faulted = DirectShowWorkspaceModule.BuildLiveTelemetry(250, 240, 10, 4, k => k, CultureInfo.InvariantCulture);
        Assert.Equal(5, faulted.Count);
        Assert.Equal(TelemetrySeverity.Danger, faulted.Single(m => m.Id == "ds-unreadable").Severity);
        Assert.Equal("10", faulted.Single(m => m.Id == "ds-unreadable").Value);
        Assert.Equal(TelemetrySeverity.Caution, faulted.Single(m => m.Id == "ds-skipped").Severity);
    }

    [Theory]
    [InlineData(7, null, "—", TelemetrySeverity.Normal)]
    [InlineData(7, 0L, "0", TelemetrySeverity.Normal)]
    [InlineData(0, 2L, "2", TelemetrySeverity.Danger)]
    public void DirectShowKeepsDisplaySkipsSeparateFromWriterEvidence(int skips, long? rejected,
        string text, TelemetrySeverity severity)
    {
        var metrics = DirectShowWorkspaceModule.BuildLiveTelemetry(12, 11, 0, skips,
            key => key, CultureInfo.InvariantCulture, rejected);
        var writer = Assert.Single(metrics, m => m.Id == "ds-record-rejected");
        Assert.Equal(text, writer.Value);
        Assert.Equal(severity, writer.Severity);
        Assert.Equal(skips.ToString(), metrics.Single(m => m.Id == "ds-skipped").Value);
    }

    [Theory]
    [InlineData(null, false, "—")]
    [InlineData(0L, false, "0")]
    [InlineData(0L, true, "—")]
    [InlineData(19L, true, "≥19")]
    [InlineData(19L, false, "19")]
    public void FifoLossRequiresAnObservedInterval(long? samples, bool limited, string value)
    {
        var metric = RfFifoMetrics.DroppedSamples(samples, limited, "FIFO", CultureInfo.InvariantCulture);
        Assert.Equal(value, metric.Value);
        if (samples > 0) Assert.Equal(TelemetrySeverity.Danger, metric.Severity);
    }

    [Fact]
    public void MissingTsTelemetryDoesNotReportZeroContinuityErrors()
    {
        var missing = FireWireWorkspaceModule.BuildTsTelemetry(0, 0, null, 0, 0, 0, k => k, CultureInfo.InvariantCulture);
        Assert.Equal("—", missing.Single(m => m.Id == "ts-cc").Value);
    }

    [Fact]
    public void FireWireWorkspaceModuleBuildsTsAndDvLiveTelemetry()
    {
        var module = new FireWireWorkspaceModule();
        Assert.Equal(FireWireWorkspaceModule.BackendId, module.Descriptor.BackendId);
        Assert.True(module.Descriptor.IsImplementedInUi);

        var tsClean = FireWireWorkspaceModule.BuildTsTelemetry(5000, 150, 0, 0, 25_120_000, 0, k => k, CultureInfo.InvariantCulture);
        Assert.Equal("5,000", tsClean.Single(m => m.Id == "ts-packets").Value);
        Assert.Equal("25.12 Mb/s", tsClean.Single(m => m.Id == "ts-bitrate").Value);
        Assert.Equal(TelemetrySeverity.Normal, tsClean.Single(m => m.Id == "ts-cc").Severity);
        Assert.DoesNotContain(tsClean, m => m.Id == "ts-sync");

        var tsFault = FireWireWorkspaceModule.BuildTsTelemetry(6000, 180, 3, 2, 512_000, 5, k => k, CultureInfo.InvariantCulture);
        Assert.Equal("512 kb/s", tsFault.Single(m => m.Id == "ts-bitrate").Value);
        Assert.Equal(TelemetrySeverity.Danger, tsFault.Single(m => m.Id == "ts-sync").Severity);
        Assert.Equal(TelemetrySeverity.Danger, tsFault.Single(m => m.Id == "ts-cc").Severity);
        Assert.Equal(TelemetrySeverity.Caution, tsFault.Single(m => m.Id == "ts-skipped").Severity);

        var dvClean = FireWireWorkspaceModule.BuildDvTelemetry(300, 300, 0, 0, 0, k => k, CultureInfo.InvariantCulture);
        Assert.Equal("300", dvClean.Single(m => m.Id == "dv-frames").Value);
        Assert.Equal(TelemetrySeverity.Normal, dvClean.Single(m => m.Id == "dv-invalid").Severity);
        Assert.Contains(dvClean, m => m.Id == "dv-skipped");

        var dvFault = FireWireWorkspaceModule.BuildDvTelemetry(310, 300, 4, 2, 7, k => k, CultureInfo.InvariantCulture);
        Assert.Equal(TelemetrySeverity.Danger, dvFault.Single(m => m.Id == "dv-invalid").Severity);
        Assert.Equal(TelemetrySeverity.Caution, dvFault.Single(m => m.Id == "dv-disc").Severity);
        Assert.Equal(TelemetrySeverity.Caution, dvFault.Single(m => m.Id == "dv-skipped").Severity);
        Assert.Equal("7", dvFault.Single(m => m.Id == "dv-skipped").Value);
    }

    [Fact]
    public void WrapLayoutCalculatorHandlesInfiniteNaNZeroWidthAndMultilineWrappingConsistently()
    {
        Assert.Equal(double.PositiveInfinity, WrapLayoutCalculator.NormalizeChildAvailableWidth(double.PositiveInfinity));
        Assert.Equal(double.PositiveInfinity, WrapLayoutCalculator.NormalizeChildAvailableWidth(double.NaN));
        Assert.Equal(0d, WrapLayoutCalculator.NormalizeChildAvailableWidth(-12));
        Assert.Equal(320d, WrapLayoutCalculator.NormalizeChildAvailableWidth(320));

        // Zero-width visible item followed by real items must match between Measure and Arrange.
        (double Width, double Height)[] zeroLead = [(0, 20), (80, 30), (80, 30)];
        var (w1, h1) = WrapLayoutCalculator.Measure(zeroLead, availableWidth: 160, horizontalSpacing: 8, verticalSpacing: 6);
        var rects1 = WrapLayoutCalculator.Arrange(zeroLead, finalWidth: 160, horizontalSpacing: 8, verticalSpacing: 6);
        Assert.Equal(0, rects1[0].X);
        Assert.Equal(8, rects1[1].X);
        Assert.Equal(0, rects1[2].X); // 0 + 8 + 80 + 8 + 80 = 176 > 160 -> wraps to line 2!
        Assert.Equal(36, rects1[2].Y);
        Assert.Equal(88, w1);
        Assert.Equal(66, h1);

        // Infinite / NaN width places everything on one line.
        var (wInf, hInf) = WrapLayoutCalculator.Measure([(60, 24), (70, 30)], double.PositiveInfinity, 8, 6);
        Assert.Equal(138, wInf);
        Assert.Equal(30, hInf);
        var (wNan, hNan) = WrapLayoutCalculator.Measure([(60, 24), (70, 30)], double.NaN, 8, 6);
        Assert.Equal(138, wNan);
        Assert.Equal(30, hNan);
    }

    [Fact]
    public void DirectShowAndFireWireSettingsResolversPreserveSavedSelectionAcrossProbesAndFallbacks()
    {
        var savedDs = new DirectShowSettings
        {
            DeviceId = "dev-a",
            PinId = "0",
            IsInfrared = false,
            Width = 1920,
            Height = 1080,
            FramesPerSecond = 30,
            Encoding = "YUY2",
            VideoFormatIndex = 5,
            AudioFormatIndex = 2
        };

        // 1. Mid-probe on same device (capabilities not loaded yet): preserves AudioFormatIndex and all fields!
        var midProbe = DirectShowSettingsResolver.Export(savedDs, "DEV-A", isFallbackSelection: false,
            loadedCapabilitiesDeviceId: null, video: null, audioFormatIndex: null);
        Assert.Equal(savedDs, midProbe);

        // 2. Saved device unplugged -> automatic fallback to Device B: does NOT overwrite saved Device A!
        var fallback = DirectShowSettingsResolver.Export(savedDs, "dev-b", isFallbackSelection: true,
            loadedCapabilitiesDeviceId: "dev-b",
            video: new DirectShowFormatSnapshot("1", false, 640, 480, 30, "MJPG", 1), audioFormatIndex: null);
        Assert.Equal(savedDs, fallback);

        // 3. User switches to Device B while probe is in-flight or failed: does NOT copy Device A's format into Device B!
        var switchedInFlight = DirectShowSettingsResolver.Export(savedDs, "dev-b", isFallbackSelection: false,
            loadedCapabilitiesDeviceId: null, video: null, audioFormatIndex: null);
        Assert.Equal(new DirectShowSettings { DeviceId = "dev-b" }, switchedInFlight);

        // 4. User switches to Device B and probe succeeds: exports full Device B selection.
        var loadedB = DirectShowSettingsResolver.Export(savedDs, "dev-b", isFallbackSelection: false,
            loadedCapabilitiesDeviceId: "dev-b",
            video: new DirectShowFormatSnapshot("1", false, 3840, 2160, 30, "MJPG", 12), audioFormatIndex: 3);
        Assert.Equal("dev-b", loadedB.DeviceId);
        Assert.Equal(3840, loadedB.Width);
        Assert.Equal(12, loadedB.VideoFormatIndex);
        Assert.Equal(3, loadedB.AudioFormatIndex);

        // FireWire: fallback to simulated device when saved physical deck is unplugged preserves saved physical ID.
        var savedFw = new FireWireSettings { DeviceId = @"\\?\avc#victor_hm-dhx2" };
        Assert.Equal(savedFw, FireWireSettingsResolver.Export(savedFw, "simulated:dv25-ntsc", isFallbackSelection: true));
        Assert.Equal(savedFw, FireWireSettingsResolver.Export(savedFw, null, isFallbackSelection: false));
        Assert.Equal(new FireWireSettings { DeviceId = "simulated:dv25-ntsc" },
            FireWireSettingsResolver.Export(savedFw, "simulated:dv25-ntsc", isFallbackSelection: false));

        // Refresh while on a fallback device must target the saved device ID so re-plugging restores it.
        Assert.Equal("dev-a", DirectShowSettingsResolver.ResolveRefreshTargetDeviceId(savedDs, "dev-b", isFallbackSelection: true));
        Assert.Equal("dev-b", DirectShowSettingsResolver.ResolveRefreshTargetDeviceId(savedDs, "dev-b", isFallbackSelection: false));
        Assert.Equal(@"\\?\avc#victor_hm-dhx2",
            FireWireSettingsResolver.ResolveRefreshTargetDeviceId(savedFw, "simulated:dv25-ntsc", isFallbackSelection: true));
        Assert.Equal("simulated:dv25-ntsc",
            FireWireSettingsResolver.ResolveRefreshTargetDeviceId(savedFw, "simulated:dv25-ntsc", isFallbackSelection: false));

        // DirectShow format matching prefers metadata+index, then full metadata when driver indices shift.
        DirectShowFormatSnapshot[] shifted =
        [
            new("0", false, 640, 480, 30, "MJPG", Index: 5),   // Old index 5 now belongs to a different resolution!
            new("0", false, 1920, 1080, 60, "YUY2", Index: 7), // Different fps
            new("0", false, 1920, 1080, 30, "YUY2", Index: 9)  // Exact metadata match at shifted index 9
        ];
        Assert.Equal(9, DirectShowSettingsResolver.FindMatchingVideoFormatIndex(savedDs, shifted));

        // If only VideoFormatIndex was saved (no metadata), falls back to matching by index.
        Assert.Equal(5, DirectShowSettingsResolver.FindMatchingVideoFormatIndex(new DirectShowSettings { VideoFormatIndex = 5 }, shifted));
    }

    [Theory]
    [InlineData(null, TelemetrySeverity.Normal)]
    [InlineData(0.0, TelemetrySeverity.Normal)]
    [InlineData(-0.01, TelemetrySeverity.Normal)]
    [InlineData(0.0001, TelemetrySeverity.Caution)]
    [InlineData(0.0499, TelemetrySeverity.Caution)]
    [InlineData(0.05, TelemetrySeverity.Danger)]
    [InlineData(0.42, TelemetrySeverity.Danger)]
    public void MonitoringSeverityClassifiesLumaExcursionThresholds(double? fraction, TelemetrySeverity expected) =>
        Assert.Equal(expected, MonitoringSeverity.ClassifyLumaExcursion(fraction));

    [Theory]
    [InlineData("clip.mov", "OpenRecordedMov")]
    [InlineData("clip.mkv", "OpenRecordedMkv")]
    [InlineData("clip.MKV", "OpenRecordedMkv")]
    [InlineData("clip.ts", "OpenRecordedTs")]
    [InlineData("clip.ts192", "OpenRecordedTs")]
    [InlineData("clip.m2t", "OpenRecordedTs")]
    [InlineData("clip.dv", "OpenRecordedDv")]
    [InlineData(null, "OpenRecordedMov")]
    public void QcPresentationHelperSelectsFormatSpecificMediaLabels(string? path, string expectedKey) =>
        Assert.Equal(expectedKey, QcPresentationHelper.OpenMediaLabelKey(path));

    [Fact]
    public void QcPresentationHelperSelectsReportKeysAndStatusFileNameVisibility()
    {
        Assert.Equal("SaveDvJson", QcPresentationHelper.SaveReportLabelKey(QcEntryKind.Dv));
        Assert.Equal("SaveTsJson", QcPresentationHelper.SaveReportLabelKey(QcEntryKind.TransportStream));

        Assert.True(QcPresentationHelper.ShouldShowStatusFileName(recordingActive: true, qcActive: false, "Idle"));
        Assert.True(QcPresentationHelper.ShouldShowStatusFileName(recordingActive: false, qcActive: true, "Idle"));
        Assert.True(QcPresentationHelper.ShouldShowStatusFileName(false, false, "QcPassed"));
        Assert.True(QcPresentationHelper.ShouldShowStatusFileName(false, false, "TsCheckPassed"));
        Assert.True(QcPresentationHelper.ShouldShowStatusFileName(false, false, "DvCheckPassed"));
        Assert.False(QcPresentationHelper.ShouldShowStatusFileName(false, false, "Idle"));
        Assert.False(QcPresentationHelper.ShouldShowStatusFileName(false, false, "SignalPresent"));
    }

    [Fact]
    public void CameraControlEditHelperDetectsPendingChangesAndDefaultResetAvailability()
    {
        Assert.False(CameraControlEditHelper.HasPendingChanges(false, 128, false, 128, supportsAuto: true));
        Assert.True(CameraControlEditHelper.HasPendingChanges(false, 128, false, 140, supportsAuto: true));
        Assert.True(CameraControlEditHelper.HasPendingChanges(false, 128, true, 128, supportsAuto: true));
        Assert.False(CameraControlEditHelper.HasPendingChanges(true, 128, true, 200, supportsAuto: true));
        Assert.False(CameraControlEditHelper.HasPendingChanges(true, 128, false, 128, supportsAuto: false));

        Assert.False(CameraControlEditHelper.CanResetToDefault(null, 100, false, supportsManual: true));
        Assert.False(CameraControlEditHelper.CanResetToDefault(128, 128, false, supportsManual: true));
        Assert.True(CameraControlEditHelper.CanResetToDefault(128, 150, false, supportsManual: true));
        Assert.True(CameraControlEditHelper.CanResetToDefault(128, 128, true, supportsManual: true));
        Assert.False(CameraControlEditHelper.CanResetToDefault(128, 150, false, supportsManual: false));
    }

    [Fact]
    public void DeckLinkWorkspaceModuleBuildsSeverityCodedTelemetry()
    {
        var module = new DeckLinkWorkspaceModule();
        Assert.Equal(DeckLinkBackend.BackendId, module.Descriptor.BackendId);
        Assert.True(module.Descriptor.IsImplementedInUi);
        Assert.True(module.Descriptor.Capabilities.SupportsVideoPreview);
        Assert.True(module.Descriptor.Capabilities.SupportsVideoScopes);
        Assert.True(module.Descriptor.Capabilities.SupportsAudioMeters);

        string Localize(string key) => key == "CaptureCounts" ? "{0}/{1}/{2}/{3}/{4}" : key;

        var idleMetrics = module.BuildIdleTelemetry(Localize);
        Assert.Equal(4, idleMetrics.Count);
        Assert.All(idleMetrics, metric => Assert.Equal(TelemetrySeverity.Normal, metric.Severity));
        Assert.Equal("—", idleMetrics.Single(m => m.Id == "skipped").Value);

        var activeStats = new CaptureStats
        {
            VideoFrames = 1200,
            NoInputFrames = 3,
            MissingVideoFrames = 2,
            AudioPackets = 1200,
            Signal = VideoSignal.Present,
            CanStartRecording = true,
            RecordingModeChanged = false
        };

        var activeMetrics = DeckLinkWorkspaceModule.BuildVideoTelemetry(activeStats, 7, Localize, CultureInfo.InvariantCulture);
        Assert.Equal(TelemetrySeverity.Normal, activeMetrics.Single(m => m.Id == "valid").Severity);
        Assert.Equal("1,200", activeMetrics.Single(m => m.Id == "valid").Value);
        Assert.Equal(TelemetrySeverity.Caution, activeMetrics.Single(m => m.Id == "noinput").Severity);
        Assert.Equal(TelemetrySeverity.Danger, activeMetrics.Single(m => m.Id == "missing").Severity);
        Assert.Equal(TelemetrySeverity.Caution, activeMetrics.Single(m => m.Id == "skipped").Severity);
        Assert.Equal("1200/3/1200/2/7", activeMetrics[0].Tooltip);
    }

    [Fact]
    public void CaptureSourceCatalogCoversAllBackendCategories()
    {
        var catalog = new CaptureSourceCatalog(new DeckLinkWorkspaceModule());

        Assert.Equal(6, catalog.Sources.Count);
        Assert.Equal(3, catalog.ByCategory(CaptureBackendCategory.Video).Count);
        Assert.Single(catalog.ByCategory(CaptureBackendCategory.FireWire));
        Assert.Single(catalog.ByCategory(CaptureBackendCategory.SignalCapture));
        Assert.Single(catalog.ByCategory(CaptureBackendCategory.Audio));

        var activeSources = catalog.Sources.Where(s => s.IsImplementedInUi).ToArray();
        Assert.Equal([DeckLinkBackend.BackendId, TestSignal.BackendId, "windows.directshow", "windows.firewire", "windows.signalcapture"], activeSources.Select(s => s.BackendId));
    }

    [Fact]
    public void QcSessionHistoryOrdersNewestFirstDeduplicatesAndEvictsOldest()
    {
        var history = new QcSessionHistory(maxEntries: 3);
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var id4 = Guid.NewGuid();

        QcSessionHistoryEntry Entry(Guid id, string name, QcEntryKind kind = QcEntryKind.Mov) =>
            new(id, $@"C:\Captures\{name}", name, kind, DateTimeOffset.UtcNow, "QcPassed", "ok", TelemetrySeverity.Normal, []);

        history.Record(Entry(id1, "a.mov"));
        history.Record(Entry(id2, "b.ts", QcEntryKind.TransportStream));
        history.Record(Entry(id3, "c.mov"));

        Assert.Equal([id3, id2, id1], history.Entries.Select(e => e.Id).ToArray());

        // Re-recording an existing ID moves it to the top without duplicating.
        history.Record(Entry(id1, "a-updated.mov"));
        Assert.Equal(3, history.Entries.Count);
        Assert.Equal(id1, history.Entries[0].Id);
        Assert.Equal("a-updated.mov", history.Find(id1)?.FileName);

        // Adding a 4th distinct entry evicts the oldest (id2).
        history.Record(Entry(id4, "d.ts", QcEntryKind.TransportStream));
        Assert.Equal(3, history.Entries.Count);
        Assert.Null(history.Find(id2));
        Assert.Equal([id4, id1, id3], history.Entries.Select(e => e.Id).ToArray());

        history.Clear();
        Assert.Empty(history.Entries);
        Assert.Null(history.Find(id4));
    }

    [Fact]
    public void BroadcastMeterBallisticsAttackDecayPeakHoldAndIndependentClipReset()
    {
        var meter = new BroadcastMeterBallistics();

        // Instant attack to 0 dBFS on left and -12 dBFS on right.
        meter.UpdateInput(leftPeakLinear: 1.0, rightPeakLinear: Math.Pow(10, -12.0 / 20.0), totalLeftClips: 4, totalRightClips: 2);
        meter.StepFrame();

        Assert.InRange(meter.LeftNormalized, 0.999, 1.0);
        Assert.InRange(meter.LeftPeakNormalized, 0.999, 1.0);
        Assert.InRange(meter.RightNormalized, 0.79, 0.81);
        Assert.Equal(4, meter.VisibleLeftClips);
        Assert.Equal(2, meter.VisibleRightClips);

        // Reset only Left clip latch; Right stays latched and new Left clips increment from 0.
        meter.ResetLeftClipLatch();
        Assert.Equal(0, meter.VisibleLeftClips);
        Assert.Equal(2, meter.VisibleRightClips);

        meter.UpdateInput(leftPeakLinear: 0.0, rightPeakLinear: 0.0, totalLeftClips: 6, totalRightClips: 2);
        Assert.Equal(2, meter.VisibleLeftClips);
        Assert.Equal(2, meter.VisibleRightClips);

        // Drop signal to silence: main bar decays smoothly while peak hold dwells for 72 frames.
        for (var i = 0; i < 40; i++)
            meter.StepFrame();

        Assert.True(meter.LeftNormalized < 0.80, "Main bar should decay smoothly on silence.");
        Assert.InRange(meter.LeftPeakNormalized, 0.999, 1.0);

        // After dwelling past 72 frames, peak hold starts falling.
        for (var i = 0; i < 50; i++)
            meter.StepFrame();

        Assert.True(meter.LeftPeakNormalized < 0.99, "Peak hold should begin falling after 72-frame dwell.");

        // If a new capture session restarts clip counters at 0, baseline auto-resets cleanly.
        meter.UpdateInput(0.0, 0.0, totalLeftClips: 1, totalRightClips: 0);
        Assert.Equal(1, meter.VisibleLeftClips);
        Assert.Equal(0, meter.VisibleRightClips);
    }

    [Fact]
    public void ScopeRasterUsesPhysicalPixelsAndResolvesAdjacentTenBitCodes()
    {
        var request = VideoScopeRaster.FromDisplay(600, 512, 500, 2);
        Assert.True(request.WaveWidth >= 1200);
        Assert.Equal(1024, request.WaveHeight);
        Assert.True(request.VectorSize >= 1000 && request.VectorSize % 2 == 1);
        Assert.Equal(new VideoScopeRaster(1536, 1536, 1025), VideoScopeRaster.FromDisplay(3000, 2000, 2000, 2));

        var frame = new PreviewFrame { Format = CapturePixelFormat.V210 };
        frame.Resize(6, 2, 16);
        var words = MemoryMarshal.Cast<byte, uint>(frame.Pixels.AsSpan());
        for (var row = 0; row < frame.Height; row++)
        {
            var y = 501u + (uint)row;
            words[row * 4] = words[row * 4 + 2] = 512u | y << 10 | 512u << 20;
            words[row * 4 + 1] = words[row * 4 + 3] = y | 512u << 10 | y << 20;
        }

        var scopes = new VideoScopes.Renderer(request).Analyze(frame, 10,
            VideoScopeWaveformMode.Luma, PreviewColorMatrix.Auto);
        Assert.Equal(request, scopes.Raster);
        Assert.Equal(request.WaveWidth * request.WaveHeight * 4, scopes.LumaWaveform.Length);
        Assert.Equal(request.VectorSize * request.VectorSize * 4, scopes.Vectorscope.Length);
        const int x = 73;
        byte Green(int code) => scopes.LumaWaveform[((1023 - code) * request.WaveWidth + x) * 4 + 1];
        Assert.True(Green(501) > Green(500));
        Assert.True(Green(502) > Green(503));
    }

    [Theory]
    [InlineData(8, 1116, 924, 16, 32.425, 235)]
    [InlineData(10, 1117, 925, 64, 129.7, 940)]
    public void ScopeDrawsTheSetupGuideBetweenLegalBlackAndWhite(int bits, int guideRow, int quarterRow,
        int black, double setup, int white)
    {
        Assert.Equal(black, VideoScopes.LumaCodeAtIre(bits, 0));
        Assert.Equal(setup, VideoScopes.LumaCodeAtIre(bits, VideoScopes.SetupIre), 3);
        Assert.Equal(white, VideoScopes.LumaCodeAtIre(bits, 100));
        var frame = new PreviewFrame { Format = CapturePixelFormat.Uyvy };
        frame.Resize(2, 2, 4);
        frame.Pixels.AsSpan().Fill(128);
        var raster = new VideoScopeRaster(512, 1280, 129);
        var renderer = new VideoScopes.Renderer(raster);

        var luma = renderer.Analyze(frame, bits, VideoScopeWaveformMode.Luma, PreviewColorMatrix.Auto);
        var ire = renderer.Analyze(frame, bits, VideoScopeWaveformMode.Luma, PreviewColorMatrix.Auto, ireScale: true);
        var rgb = renderer.Analyze(frame, bits, VideoScopeWaveformMode.RgbParade, PreviewColorMatrix.Auto, ireScale: true);
        int RedAt(VideoScopes scopes, int row, int x) => scopes.LumaWaveform[(row * raster.WaveWidth + x) * 4 + 2];

        Assert.True(RedAt(luma, guideRow, 25) > RedAt(luma, guideRow - 1, 25));
        Assert.True(RedAt(luma, guideRow, 25) > RedAt(luma, guideRow, 29));
        Assert.Equal(RedAt(luma, guideRow, 25), RedAt(ire, guideRow, 25));
        Assert.True(RedAt(ire, quarterRow, 29) > RedAt(luma, quarterRow, 29));
        Assert.Equal(RedAt(rgb, guideRow - 1, 25), RedAt(rgb, guideRow, 25));
    }

    [Fact]
    public void AudioSpectrumUsesTwoDisplayWindowsFromOneThirtyFpsPacket()
    {
        var monitor = new StereoMonitor(CaptureAudioFormat.Pcm16, sampleRate: 48_000);
        for (var i = 0; i < 800; i++)
            monitor.Add(1000, -1000);
        for (var i = 0; i < 800; i++)
            monitor.Add(2000, -2000);

        var first = Assert.IsType<StereoMonitor.AudioWindow>(monitor.ReadForDisplay());
        var second = Assert.IsType<StereoMonitor.AudioWindow>(monitor.ReadForDisplay());
        Assert.Equal(1000 / 32768d, first.Left[^1]);
        Assert.Equal(2000 / 32768d, second.Left[^1]);
        Assert.Null(monitor.ReadForDisplay());
    }

    [Theory]
    [InlineData(0x75707072u, 220, 32)]
    [InlineData(0x6C6F7772u, 32, 220)]
    public void VideoScopesFollowBothFieldsInSourceOrder(uint dominance, int firstLuma, int secondLuma)
    {
        byte[] uyvy = [128, 220, 128, 220, 128, 32, 128, 32, 128, 220, 128, 220, 128, 32, 128, 32];
        var handle = GCHandle.Alloc(uyvy, GCHandleType.Pinned);
        try
        {
            var feed = new PreviewFeed { DeinterlaceMode = PreviewDeinterlaceMode.Off };
            feed.Offer(handle.AddrOfPinnedObject(), 2, 4, 4, (ulong)uyvy.Length, false,
                CapturePixelFormat.Uyvy, dominance);
            feed.ConvertLatest(8, fieldInterval: 1);

            feed.VisitScopes(field =>
            {
                Assert.True(field.SecondFieldDueAt > 0);
                AssertTrace(field, firstLuma, secondLuma);
            }, long.MinValue);
            feed.VisitScopes(field => AssertTrace(field, secondLuma, firstLuma), long.MaxValue);

            void AssertTrace(VideoScopes field, int present, int absent)
            {
                Assert.Equal(0d, field.LumaOutsideLegalFraction);
                Assert.True(Green(present) > Green(absent) + 50);

                byte Green(int luma)
                {
                    var row = ((255 - luma) * (field.Raster.WaveHeight - 1) + 127) / 255;
                    return field.LumaWaveform[(row * field.Raster.WaveWidth + 77) * 4 + 1];
                }
            }
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void VideoScopesRgbParadeSeparatesPrimaryColorChannels()
    {
        // Pure red in BT.601 Y'CbCr (8-bit): Y=81, Cb=90, Cr=240.
        const int width = 32;
        const int height = 16;
        var stride = width * 2;
        var uyvy = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x += 2)
            {
                var off = y * stride + x * 2;
                uyvy[off] = 90;      // Cb
                uyvy[off + 1] = 81;  // Y0
                uyvy[off + 2] = 240; // Cr
                uyvy[off + 3] = 81;  // Y1
            }
        }

        var handle = GCHandle.Alloc(uyvy, GCHandleType.Pinned);
        try
        {
            var feed = new PreviewFeed
            {
                ScopeWaveformMode = VideoScopeWaveformMode.RgbParade,
                ColorMatrix = PreviewColorMatrix.Bt601
            };
            feed.Offer(handle.AddrOfPinnedObject(), width, height, stride, (ulong)uyvy.Length,
                noInput: false, CapturePixelFormat.Uyvy);
            feed.ConvertLatest(8);

            var parade = feed.Scopes;
            Assert.NotNull(parade);
            Assert.Equal(VideoScopeWaveformMode.RgbParade, parade!.WaveformMode);
            Assert.Equal(8, parade.LumaScaleBits);
            Assert.Equal(8, parade.InputBits);

            // Pure BT.601 red (Y=81, Cb=90, Cr=240) produces R≈255 (top row y=0) and G≈0, B≈0 (bottom row y=H-1).
            const int thirdW = VideoScopes.WaveformWidth / 3;
            const int gStart = thirdW + 1;
            const int bStart = thirdW * 2 + 2;
            const int midX = thirdW / 2;
            const int topY = 0;
            const int bottomY = VideoScopes.WaveformHeight - 1;

            var topROff = (topY * VideoScopes.WaveformWidth + midX) * 4;
            var topGOff = (topY * VideoScopes.WaveformWidth + gStart + midX) * 4;
            var topBOff = (topY * VideoScopes.WaveformWidth + bStart + midX) * 4;

            var botROff = (bottomY * VideoScopes.WaveformWidth + midX) * 4;
            var botGOff = (bottomY * VideoScopes.WaveformWidth + gStart + midX) * 4;
            var botBOff = (bottomY * VideoScopes.WaveformWidth + bStart + midX) * 4;

            // Top row: R channel lit bright red (>200), G & B segments remain at guide baseline (<80).
            Assert.True(parade.LumaWaveform[topROff + 2] > 200, $"Expected bright R trace at top row, got {parade.LumaWaveform[topROff + 2]}.");
            Assert.True(parade.LumaWaveform[topGOff + 1] < 80, $"Expected dark G segment at top row, got {parade.LumaWaveform[topGOff + 1]}.");
            Assert.True(parade.LumaWaveform[topBOff] < 80, $"Expected dark B segment at top row, got {parade.LumaWaveform[topBOff]}.");

            // Bottom row: G & B channels lit (>200), R segment remains at guide baseline (<80).
            Assert.True(parade.LumaWaveform[botROff + 2] < 80, $"Expected dark R segment at bottom row, got {parade.LumaWaveform[botROff + 2]}.");
            Assert.True(parade.LumaWaveform[botGOff + 1] > 200, $"Expected bright G trace at bottom row, got {parade.LumaWaveform[botGOff + 1]}.");
            Assert.True(parade.LumaWaveform[botBOff] > 200, $"Expected bright B trace at bottom row, got {parade.LumaWaveform[botBOff]}.");
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void QcWarningFormatterCoversCaptureTimingClippingAndFieldFlags()
    {
        var timing = new RecordingTiming(
            SuspectedMissingVideoFrames: 2,
            VideoCorrections: 2,
            VideoInvalidDurations: 1,
            AudioDiscontinuities: 3,
            AudioTimestampResets: 0,
            ApparentAudioGapSamples: 96,
            ApparentAudioOverlapSamples: 12,
            MaximumAudioOffsetSamples: 84,
            FirstVideoCorrection: null,
            FirstAudioIssue: new RecordingTimestampIssue(10, 1000, 1048, 48, 48, 48, "Present"));

        var capture = new QcCaptureLog(
            Device: "DeckLink",
            Driver: "14.0",
            VideoSource: "SDI",
            AudioSource: "Embedded",
            InputMode: "NTSC",
            StartedUtc: DateTimeOffset.UtcNow,
            StoppedUtc: DateTimeOffset.UtcNow,
            Expected: new QcExpectedMedia(0, 0, "uppr", 16, "v210", 720, 486),
            ValidVideoFrames: 0,
            NoInputFrames: 4,
            MissingVideoFrames: 2,
            TimestampRegressions: 0,
            AudioPackets: 0,
            LeftClips: 5,
            RightClips: 0,
            RecentEvents: [])
        {
            InputModeChanged = true,
            Timing = timing,
            CaptureError = "disk full",
            UnreadableNoInputFrames = 1
        };

        var result = new QcResult(
            QcStatus.Passed,
            "ok",
            CaptureLog: "clip.capture.json",
            FrameMd5: null,
            QcToolsReport: null,
            Warnings: [])
        {
            DecodedVideoFrames = 0,
            DecodedAudioSampleFrames = 0,
            VideoFramesWithoutFieldFlags = 3
        };

        string Localize(string key) => key switch
        {
            "QcWarnCaptureEnded" => "ended:{0}",
            "QcWarnNoInput" => "noinput:{0}",
            "QcWarnUnreadableNoInput" => "unreadable:{0}",
            "QcWarnMissing" => "missing:{0}",
            "QcWarnVideoTiming" => "vtiming:{0}:{1}",
            "QcWarnAudioTiming" => "atiming:{0}:{1}",
            "QcWarnFieldFlags" => "fields:{0}",
            _ => key
        };

        var warnings = QcWarningFormatter.BuildMovWarnings(capture, result, Localize, CultureInfo.InvariantCulture);

        Assert.Contains("QcWarnNoVideo", warnings);
        Assert.Contains("QcWarnNoAudio", warnings);
        Assert.Contains("ended:disk full", warnings);
        Assert.Contains("QcWarnInputModeChange", warnings);
        Assert.Contains("noinput:4", warnings);
        Assert.Contains("unreadable:1", warnings);
        Assert.Contains("missing:2", warnings);
        Assert.Contains("vtiming:2:1", warnings);
        Assert.Contains("atiming:3:84", warnings);
        Assert.Contains("QcWarnClipping", warnings);
        Assert.Contains("fields:3", warnings);
    }

    [Fact]
    public void WorkspaceModuleRegistryResolvesBackendsAndBuildsBackendSpecificTelemetry()
    {
        var registry = new WorkspaceModuleRegistry();

        Assert.Equal(6, registry.Modules.Count);
        Assert.Equal(DeckLinkBackend.BackendId, registry.DefaultModule.Descriptor.BackendId);
        Assert.Equal(DeckLinkBackend.BackendId, registry.Resolve(null).Descriptor.BackendId);
        Assert.Equal(DeckLinkBackend.BackendId, registry.Resolve("unknown.backend").Descriptor.BackendId);
        Assert.Equal("windows.firewire", registry.Resolve("WINDOWS.FIREWIRE").Descriptor.BackendId);
        Assert.Equal("windows.signalcapture", registry.Resolve("windows.signalcapture").Descriptor.BackendId);
        Assert.Equal("windows.audio", registry.Resolve("windows.audio").Descriptor.BackendId);
        Assert.Equal("windows.directshow", registry.Resolve("windows.directshow").Descriptor.BackendId);
        Assert.IsType<DirectShowWorkspaceModule>(registry.Resolve("windows.directshow"));
        Assert.IsType<FireWireWorkspaceModule>(registry.Resolve("windows.firewire"));
        Assert.Equal("CaptureRack.Probe.exe directshow list", registry.Resolve("windows.directshow").Descriptor.CliCommandHint);
        Assert.IsType<TestSignalWorkspaceModule>(registry.Resolve(TestSignal.BackendId));

        Assert.Equal(3, registry.ByCategory(CaptureBackendCategory.Video).Count);
        Assert.Single(registry.ByCategory(CaptureBackendCategory.FireWire));
        Assert.Single(registry.ByCategory(CaptureBackendCategory.SignalCapture));
        Assert.Single(registry.ByCategory(CaptureBackendCategory.Audio));

        foreach (var module in registry.Modules)
        {
            var metrics = module.BuildIdleTelemetry(key => key);
            Assert.NotEmpty(metrics);
            Assert.All(metrics, m => Assert.False(string.IsNullOrWhiteSpace(m.Label)));
        }

        Assert.NotNull(registry.Resolve("windows.firewire").Descriptor.CliCommandHint);
        Assert.NotNull(registry.Resolve("windows.signalcapture").Descriptor.CliCommandHint);
    }

    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    public void WorkspaceModuleRegistryEnforcesIdleBackendSwitching(
        bool previewActive, bool recordingActive, bool recordingTransition, bool qcActive, bool expected) =>
        Assert.Equal(expected, WorkspaceModuleRegistry.CanSwitchBackend(previewActive, recordingActive, recordingTransition, qcActive));

    [Fact]
    public void AnalogInputOptionsBuilderMapsModesAndClampsGain()
    {
        var driverDefaults = AnalogInputOptionsBuilder.Build("Driver", "Driver", "Driver", false, 5.0, -5.0);
        Assert.Equal(AnalogNtscIreMode.Driver, driverDefaults.NtscIre);
        Assert.Equal(AnalogComponentLevelMode.Driver, driverDefaults.ComponentLevel);
        Assert.Equal(AnalogAudioLevelMode.Driver, driverDefaults.AudioLevel);
        Assert.Null(driverDefaults.GainLeftDb);
        Assert.Null(driverDefaults.GainRightDb);

        var custom = AnalogInputOptionsBuilder.Build("Ire75", "Betacam", "Pro", true, 18.0, -20.0);
        Assert.Equal(AnalogNtscIreMode.Ire75, custom.NtscIre);
        Assert.Equal(AnalogComponentLevelMode.Betacam, custom.ComponentLevel);
        Assert.Equal(AnalogAudioLevelMode.Pro, custom.AudioLevel);
        Assert.Equal(12.0, custom.GainLeftDb);
        Assert.Equal(-12.0, custom.GainRightDb);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void FullscreenRenderPolicyPausesMainWindowOnlyWhenCoveredOnSameDisplay(
        bool isFullscreen, bool coversMainDisplay, bool expectedPause) =>
        Assert.Equal(expectedPause, FullscreenRenderPolicy.ShouldPauseMainWindowRendering(isFullscreen, coversMainDisplay));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void FullscreenRenderPolicyGatesVideoScopesWhenMainWindowPaused(
        bool pauseMainWindow, bool layoutShowsScopes, bool expectedRender) =>
        Assert.Equal(expectedRender, FullscreenRenderPolicy.ShouldRenderVideoScopes(pauseMainWindow, layoutShowsScopes));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void FullscreenRenderPolicyGatesMainWindowMetersAndAnimations(bool pauseMainWindow, bool expectedActive)
    {
        Assert.Equal(expectedActive, FullscreenRenderPolicy.ShouldUpdateMainWindowMeters(pauseMainWindow));
        Assert.Equal(expectedActive, FullscreenRenderPolicy.ShouldRunMainWindowAnimations(pauseMainWindow));
    }

    [Fact]
    public void FullscreenRenderPolicyComputesNtsc480GuideHeightsAndRejectsInvalidInputs()
    {
        var (top486, bot486) = FullscreenRenderPolicy.ComputeNtsc480GuideHeights(486);
        Assert.Equal(4, top486, precision: 3);
        Assert.Equal(2, bot486, precision: 3);

        var (top1080, bot1080) = FullscreenRenderPolicy.ComputeNtsc480GuideHeights(1080);
        Assert.Equal(1080 * 4.0 / 486.0, top1080, precision: 3);
        Assert.Equal(1080 * 2.0 / 486.0, bot1080, precision: 3);

        // Very small preview clamps to minimum visible line thickness (2px top, 1px bottom).
        var (topSmall, botSmall) = FullscreenRenderPolicy.ComputeNtsc480GuideHeights(100);
        Assert.Equal(2, topSmall, precision: 3);
        Assert.Equal(1, botSmall, precision: 3);

        foreach (var invalid in new[] { 0d, -50d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var (t, b) = FullscreenRenderPolicy.ComputeNtsc480GuideHeights(invalid);
            Assert.Equal(0, t);
            Assert.Equal(0, b);
        }
    }

    [Fact]
    public void FullscreenRenderPolicyComputesPillarboxAndLetterboxSurfaceSizes()
    {
        // 4:3 content inside 1920x1080 fullscreen (padding 0) -> pillarboxed to 1440x1080.
        var (w43, h43) = FullscreenRenderPolicy.ComputePreviewSurfaceSize(1920, 1080, 4d / 3, padding: 0);
        Assert.Equal(1440, w43, precision: 3);
        Assert.Equal(1080, h43, precision: 3);

        // 16:9 content inside 1000x800 container with 14px per-side padding (28px total) -> available 972x772.
        var (w169, h169) = FullscreenRenderPolicy.ComputePreviewSurfaceSize(1000, 800, 16d / 9, padding: 14);
        Assert.Equal(972, w169, precision: 3);
        Assert.Equal(972 / (16d / 9), h169, precision: 3);

        // Negative or non-finite padding clamps cleanly to zero.
        var (wNegPad, hNegPad) = FullscreenRenderPolicy.ComputePreviewSurfaceSize(1920, 1080, 16d / 9, padding: -10);
        Assert.Equal(1920, wNegPad, precision: 3);
        Assert.Equal(1080, hNegPad, precision: 3);
        var (wNanPad, hNanPad) = FullscreenRenderPolicy.ComputePreviewSurfaceSize(1920, 1080, 16d / 9, padding: double.NaN);
        Assert.Equal(1920, wNanPad, precision: 3);
        Assert.Equal(1080, hNanPad, precision: 3);

        // Exact 16:9 fit with zero padding -> fills entire 2560x1440 display.
        var (wFull, hFull) = FullscreenRenderPolicy.ComputePreviewSurfaceSize(2560, 1440, 16d / 9, padding: 0);
        Assert.Equal(2560, wFull, precision: 3);
        Assert.Equal(1440, hFull, precision: 3);
    }

    [Theory]
    [InlineData(0, 1080, 1.5, 0)]
    [InlineData(1920, 0, 1.5, 0)]
    [InlineData(-100, 1080, 1.5, 0)]
    [InlineData(double.NaN, 1080, 1.5, 0)]
    [InlineData(1920, double.PositiveInfinity, 1.5, 0)]
    [InlineData(10, 10, 1.5, 14)]
    public void FullscreenRenderPolicyReturnsZeroForInvalidOrTooSmallBounds(
        double width, double height, double aspect, double padding)
    {
        var (w, h) = FullscreenRenderPolicy.ComputePreviewSurfaceSize(width, height, aspect, padding);
        Assert.Equal(0, w);
        Assert.Equal(0, h);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FullscreenRenderPolicyFallsBackToFourByThreeForInvalidAspectRatio(double invalidAspect)
    {
        var (w, h) = FullscreenRenderPolicy.ComputePreviewSurfaceSize(1920, 1080, invalidAspect, padding: 0);
        Assert.Equal(1440, w, precision: 3);
        Assert.Equal(1080, h, precision: 3);
    }

    [Fact]
    public void AudioMonitorRouteClampsByChannelCountAndDefaultsToPairZero()
    {
        Assert.Equal(AudioMonitorMode.Pair, AudioMonitorRoute.Default.Mode);
        Assert.Equal(0, AudioMonitorRoute.Default.PairIndex);
        Assert.Equal(AudioMonitorRoute.Default, AudioMonitorRoute.Pair(3).ClampForChannels(2));
        Assert.Equal(AudioMonitorRoute.Default, AudioMonitorRoute.Downmix.ClampForChannels(2));
        Assert.Equal(AudioMonitorRoute.Pair(3), AudioMonitorRoute.Pair(7).ClampForChannels(8));
        Assert.Equal(AudioMonitorRoute.Downmix, AudioMonitorRoute.Downmix.ClampForChannels(6));
    }

    [Fact]
    public void AudioChannelLayoutHelperDetectsSurroundAndFormatsLabels()
    {
        var layout51 = AudioChannelLayoutHelper.Detect("5.1(side)", 6);
        Assert.Equal(AudioChannelLayout.Surround51, layout51);
        Assert.Equal("C", AudioChannelLayoutHelper.ChannelCode(2, 6, layout51));
        Assert.Equal("LFE", AudioChannelLayoutHelper.ChannelCode(3, 6, layout51));
        Assert.Equal("3-4 (C/LFE)", AudioChannelLayoutHelper.PairShortLabel(1, 6, layout51));

        var layout71 = AudioChannelLayoutHelper.Detect("7.1", 8);
        Assert.Equal(AudioChannelLayout.Surround71, layout71);
        Assert.Equal("Lrs", AudioChannelLayoutHelper.ChannelCode(6, 8, layout71));
        Assert.Equal("Rrs", AudioChannelLayoutHelper.ChannelCode(7, 8, layout71));

        Assert.Equal("M", AudioChannelLayoutHelper.ChannelCode(0, 1, AudioChannelLayout.Mono));
        Assert.Equal("L", AudioChannelLayoutHelper.ChannelCode(0, 2, AudioChannelLayout.Stereo));
        Assert.Equal("8", AudioChannelLayoutHelper.ChannelCode(7, 8, AudioChannelLayout.Unknown));
        Assert.Equal("5-6", AudioChannelLayoutHelper.PairCompactLabel(2, 8));
    }

    [Fact]
    public void AudioMonitorMixerRoutesPairsDownmixesSurroundAndSoftLimits()
    {
        double[] eightCh = [0.2, -0.2, 0.5, -0.5, 0.0, 0.0, 0.0, 0.0];
        var (l1, r1) = AudioMonitorMixer.RouteFrame(eightCh, AudioMonitorRoute.Pair(0), AudioChannelLayout.Unknown, 0b11);
        Assert.Equal(0.2, l1, precision: 6);
        Assert.Equal(-0.2, r1, precision: 6);

        var (l2, r2) = AudioMonitorMixer.RouteFrame(eightCh, AudioMonitorRoute.Pair(1), AudioChannelLayout.Unknown, 0b11);
        Assert.Equal(0.5, l2, precision: 6);
        Assert.Equal(-0.5, r2, precision: 6);

        // Generic downmix uses only active pairs (2 active pairs -> equal-power 1/sqrt(2), NOT attenuated by silent pairs 3-4).
        var (ld, rd) = AudioMonitorMixer.RouteFrame(eightCh, AudioMonitorRoute.Downmix, AudioChannelLayout.Unknown, 0b0011);
        var expectedGeneric = (0.2 + 0.5) / Math.Sqrt(2);
        Assert.Equal(expectedGeneric, ld, precision: 5);
        Assert.Equal(-expectedGeneric, rd, precision: 5);

        // ITU-R BS.775 5.1 surround downmix: L + 0.707*C + 0.707*Ls.
        double[] surround51 = [0.2, 0.1, 0.4, 0.9, 0.2, 0.3];
        var (ls, rs) = AudioMonitorMixer.RouteFrame(surround51, AudioMonitorRoute.Downmix, AudioChannelLayout.Surround51, 0b111);
        const double coef = 0.7071067811865475;
        Assert.Equal(0.2 + 0.4 * coef + 0.2 * coef, ls, precision: 5);
        Assert.Equal(0.1 + 0.4 * coef + 0.3 * coef, rs, precision: 5);

        // Soft limiter stays transparent below 0.85 and caps extreme sums <= 1.0.
        Assert.Equal(0.5, AudioMonitorMixer.SoftLimit(0.5), precision: 6);
        var hot = AudioMonitorMixer.SoftLimit(2.5);
        Assert.InRange(hot, 0.95, 1.0);
        Assert.Equal(-hot, AudioMonitorMixer.SoftLimit(-2.5), precision: 6);
    }

    [Fact]
    public void StereoMonitorTracksAllChannelsAndRoutesMonitoredPairToSpectrumAndPhase()
    {
        var monitor = new StereoMonitor(CaptureAudioFormat.Pcm16, 48_000, channels: 8);
        monitor.Route = AudioMonitorRoute.Pair(1); // Monitor Ch 3-4

        short[] frame = new short[8];
        for (var i = 0; i < 1600; i++)
        {
            frame[0] = 2000;
            frame[1] = -2000;
            frame[2] = 16000;
            frame[3] = 16000; // In-phase on Ch 3-4
            frame[4] = short.MaxValue; // Clip on Ch 5
            frame[5] = 0;
            frame[6] = 0;
            frame[7] = 0;
            monitor.AddFrame(frame);
        }

        var levels = Assert.IsType<StereoMonitor.AudioWindow>(monitor.ReadForDisplay()).Analyze();
        Assert.Equal(8, levels.ChannelCount);
        Assert.Equal(AudioMonitorRoute.Pair(1), levels.MonitorRoute);
        Assert.Equal(16000 / 32768.0, levels.LeftPeak, precision: 4);
        Assert.Equal(16000 / 32768.0, levels.RightPeak, precision: 4);
        Assert.NotNull(levels.PhaseCorrelation);
        Assert.True(levels.PhaseCorrelation > 0.99);
        Assert.Equal(2000 / 32768.0, levels.PeakForChannel(0), precision: 4);
        Assert.Equal(0, levels.PeakForChannel(6), precision: 6);
        Assert.True(levels.ClipsForChannel(4) >= 1600);
        Assert.Equal(0, levels.ClipsForChannel(0));
    }

    [Fact]
    public void MultiChannelMeterPolicyAndActivityTrackerExpandWithHysteresisAndClipLatch()
    {
        var tracker = new MultiChannelActivityTracker(attackFrames: 3, holdFrames: 4);
        var ballistics = new MultiChannelMeterBallistics();

        StereoLevels MakeLevels(double p34, long clip56 = 0) =>
            new(0.2, 0.2, 0, 0, 1.0, null)
            {
                ChannelCount = 8,
                ChannelLayout = AudioChannelLayout.Unknown,
                MonitorRoute = AudioMonitorRoute.Default,
                ChannelPeaks = [0.2, 0.2, p34, p34, 0, 0, 0, 0],
                ChannelClips = [0, 0, 0, 0, clip56, 0, 0, 0]
            };

        Span<bool> clipped = stackalloc bool[4];

        // Initially quiet on Ch 3-8 -> Auto shows only Ch 1-2.
        var quiet = MakeLevels(0);
        tracker.Observe(quiet);
        ballistics.UpdateInput(quiet);
        ballistics.FillClippedPairs(clipped);
        Assert.Equal([0, 1], MultiChannelMeterPolicy.ComputeVisibleChannels(8, AudioMeterDisplayMode.Auto, AudioMonitorRoute.Default, tracker.ActivePairs, clipped));

        // 2 frames of signal on Ch 3-4 (< attack=3) does not pop open yet (glitch filter).
        tracker.Observe(MakeLevels(0.1));
        tracker.Observe(MakeLevels(0.1));
        Assert.Equal([0, 1], MultiChannelMeterPolicy.ComputeVisibleChannels(8, AudioMeterDisplayMode.Auto, AudioMonitorRoute.Default, tracker.ActivePairs, clipped));

        // 3rd frame reaches attack -> expands to Ch 1-4.
        tracker.Observe(MakeLevels(0.1));
        Assert.Equal([0, 1, 2, 3], MultiChannelMeterPolicy.ComputeVisibleChannels(8, AudioMeterDisplayMode.Auto, AudioMonitorRoute.Default, tracker.ActivePairs, clipped));

        // Goes silent: stays expanded for holdFrames-1 (3 frames) without collapsing.
        for (var i = 0; i < 3; i++)
        {
            tracker.Observe(quiet);
            Assert.Equal([0, 1, 2, 3], MultiChannelMeterPolicy.ComputeVisibleChannels(8, AudioMeterDisplayMode.Auto, AudioMonitorRoute.Default, tracker.ActivePairs, clipped));
        }
        // 4th silent frame completes hold -> collapses back to Ch 1-2.
        tracker.Observe(quiet);
        Assert.Equal([0, 1], MultiChannelMeterPolicy.ComputeVisibleChannels(8, AudioMeterDisplayMode.Auto, AudioMonitorRoute.Default, tracker.ActivePairs, clipped));

        // Clip on Ch 5 forces Ch 5-6 visible even when silent until clip latch is reset.
        var withClip = MakeLevels(0, clip56: 5);
        ballistics.UpdateInput(withClip);
        ballistics.FillClippedPairs(clipped);
        Assert.Equal([0, 1, 4, 5], MultiChannelMeterPolicy.ComputeVisibleChannels(8, AudioMeterDisplayMode.Auto, AudioMonitorRoute.Default, tracker.ActivePairs, clipped));

        ballistics.ResetClipLatch(4);
        ballistics.FillClippedPairs(clipped);
        Assert.Equal([0, 1], MultiChannelMeterPolicy.ComputeVisibleChannels(8, AudioMeterDisplayMode.Auto, AudioMonitorRoute.Default, tracker.ActivePairs, clipped));

        // Selecting Ch 7-8 as monitor route forces Ch 7-8 visible in Auto, and only Ch 7-8 in MonitoredPair.
        Assert.Equal([0, 1, 6, 7], MultiChannelMeterPolicy.ComputeVisibleChannels(8, AudioMeterDisplayMode.Auto, AudioMonitorRoute.Pair(3), tracker.ActivePairs, clipped));
        Assert.Equal([6, 7], MultiChannelMeterPolicy.ComputeVisibleChannels(8, AudioMeterDisplayMode.MonitoredPair, AudioMonitorRoute.Pair(3), tracker.ActivePairs, clipped));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], MultiChannelMeterPolicy.ComputeVisibleChannels(8, AudioMeterDisplayMode.All, AudioMonitorRoute.Default, tracker.ActivePairs, clipped));
    }

    [Fact]
    public void DeckLinkAudioChannelCapabilitiesAndFfv1CodecSelectionHandleMultiChannel()
    {
        Assert.Equal([2u], CaptureAudio.SupportedChannelCounts(16, audioInputId: 4)); // Analog
        Assert.Equal([2u, 8u], CaptureAudio.SupportedChannelCounts(8, audioInputId: 1)); // Embedded 8ch card
        Assert.Equal([2u, 8u, 16u], CaptureAudio.SupportedChannelCounts(16, audioInputId: 1)); // Embedded 16ch card
        Assert.Equal(8u, CaptureAudio.NormalizeChannels(16, maxDeviceChannels: 8, audioInputId: 1));
        Assert.Equal(2u, CaptureAudio.NormalizeChannels(8, maxDeviceChannels: 16, audioInputId: 8)); // Analog XLR
        Assert.Equal(32, CaptureAudio.BytesPerFrame(CaptureAudioFormat.Pcm32, 8));
        Assert.Equal(32, CaptureAudio.BytesPerFrame(CaptureAudioFormat.Pcm16, 16));

        var ffv1 = new CaptureRack.Recording.RecordingEncoding(CaptureRack.Recording.RecordingOutput.Ffv1);
        Assert.True(ffv1.UsesFlacAudio(8));
        Assert.False(ffv1.UsesFlacAudio(16));
        Assert.Equal("Encoded_FFV1_FLAC8", ffv1.FileTagForAudioChannels(8));
        Assert.Equal("Encoded_FFV1_PCM", ffv1.FileTagForAudioChannels(16));
        Assert.Contains("flac", ffv1.Arguments(fieldOrder: 0, audioBits: 16, audioChannels: 8));
        Assert.DoesNotContain("flac", ffv1.Arguments(fieldOrder: 0, audioBits: 16, audioChannels: 16));
        Assert.Contains("copy", ffv1.Arguments(fieldOrder: 0, audioBits: 16, audioChannels: 16));
        Assert.Equal([16u, 8u, 2u], CaptureAudio.ChannelFallbackOrder(16));
        Assert.Equal([8u, 2u], CaptureAudio.ChannelFallbackOrder(8));
        Assert.Equal([2u], CaptureAudio.ChannelFallbackOrder(2));

        // Panel width stays 86px for 2ch stereo, widens slightly for multi-channel 2-visible & 16ch.
        Assert.Equal(86, MultiChannelMeterPolicy.ComputePanelWidth(2, multiChannel: false));
        Assert.Equal(104, MultiChannelMeterPolicy.ComputePanelWidth(2, multiChannel: true));
        Assert.Equal(312, MultiChannelMeterPolicy.ComputePanelWidth(16, multiChannel: true));

        // QcWarningFormatter surfaces Ch 3-16 clipping even when Ch 1-2 have zero clips.
        var expected = new QcExpectedMedia(10, 4800, "tb", 16, "v210", 720, 486, AudioChannels: 8);
        var log = new QcCaptureLog("DeckLink", "14.0", "SDI", "Embedded", "NTSC",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, expected, 10, 0, 0, 0, 10, 0, 0, [])
        {
            ChannelClips = [0, 0, 0, 0, 3, 0, 0, 0]
        };
        var qcResult = new QcResult(QcStatus.Passed, "ok", "a.capture.json", null, null, []);
        var warnings = QcWarningFormatter.BuildMovWarnings(log, qcResult, key => key, CultureInfo.InvariantCulture);
        Assert.Contains("QcWarnClipping", warnings);
    }

    [Fact]
    public void QuickTimeMovieUsesLpcmV2ForMultiChannelPcm16AndSowtForStereoPcm16()
    {
        var mode = new VideoMode(0x6E747363, "525i59.94 NTSC", 8, 4, 1001, 30000, 0x6C6F7772);
        var videoBytes = new byte[MovRecording.StoredRowBytes(mode.Width, CapturePixelFormat.Uyvy) * mode.Height];

        using var stereoStream = new MemoryStream();
        var stereoMov = new QuickTimeMovie(stereoStream, mode, CapturePixelFormat.Uyvy, videoBytes.Length, CaptureAudioFormat.Pcm16, audioChannels: 2);
        stereoMov.WriteVideo(videoBytes, 0, mode.FrameDuration);
        stereoMov.WriteAudio(new byte[100 * CaptureAudio.BytesPerFrame(CaptureAudioFormat.Pcm16, 2)], 0, 100);
        stereoMov.Finish();
        var stereoText = System.Text.Encoding.ASCII.GetString(stereoStream.ToArray());
        Assert.Contains("sowt", stereoText);

        using var multiStream = new MemoryStream();
        var multiMov = new QuickTimeMovie(multiStream, mode, CapturePixelFormat.Uyvy, videoBytes.Length, CaptureAudioFormat.Pcm16, audioChannels: 8);
        multiMov.WriteVideo(videoBytes, 0, mode.FrameDuration);
        multiMov.WriteAudio(new byte[100 * CaptureAudio.BytesPerFrame(CaptureAudioFormat.Pcm16, 8)], 0, 100);
        multiMov.Finish();
        var multiText = System.Text.Encoding.ASCII.GetString(multiStream.ToArray());
        Assert.Contains("lpcm", multiText);
        Assert.DoesNotContain("sowt", multiText);
    }

    [Fact]
    public void TestSignalMultiChannelScenarioEmitsEightChannelAudio()
    {
        var feed = new PreviewFeed();
        var recorder = new PreviewRecorder { ReadAudioFromUi = true };
        using var session = new TestSignal.Session(TestSignalScenario.MultiChannel, feed, recorder);
        for (var i = 0; i < 6; i++)
            session.Emit(i);

        var stats = session.Snapshot(readAudio: false);
        Assert.Equal(8u, stats.AudioChannels);
        Assert.Equal(8, stats.AudioInfo?.Channels);

        var levels = recorder.ReadAudioLevels();
        Assert.NotNull(levels);
        Assert.Equal(8, levels!.ChannelCount);
        Assert.True(levels.PeakForChannel(0) > 0.05);
        Assert.Equal(0, levels.PeakForChannel(6));
    }

    [Fact]
    public void RfPeakBallisticsAttacksDecaysHoldsPeakAndLatchesClipsIndependently()
    {
        var ballistics = new RfPeakBallistics();
        Assert.Equal(0, ballistics.PositiveNormalized);
        Assert.Equal(0, ballistics.NegativeNormalized);

        var (newPos, newNeg) = ballistics.UpdateInput(minCode: -256, maxCode: 511, fullScale: 512, totalPositiveClips: 3, totalNegativeClips: 0);
        Assert.True(newPos);
        Assert.False(newNeg);
        Assert.Equal(1.0, ballistics.PositiveNormalized, precision: 3);
        Assert.Equal(0.5, ballistics.NegativeNormalized, precision: 3);
        Assert.Equal(1.0, ballistics.PositivePeakHold, precision: 3);
        Assert.Equal(3, ballistics.VisiblePositiveClips);
        Assert.Equal(0, ballistics.VisibleNegativeClips);

        // Input drops to zero -> displayed level decays smoothly while peak hold stays up initially.
        var (pos2, neg2) = ballistics.UpdateInput(minCode: 0, maxCode: 0, fullScale: 512, totalPositiveClips: 3, totalNegativeClips: 2);
        Assert.False(pos2);
        Assert.True(neg2);
        ballistics.StepFrame();
        Assert.InRange(ballistics.PositiveNormalized, 0.9, 0.99);
        Assert.Equal(1.0, ballistics.PositivePeakHold, precision: 3);

        // After enough frames past hold duration, peak hold also glides down cleanly.
        for (var i = 0; i < 120; i++)
            ballistics.StepFrame();
        Assert.Equal(0, ballistics.PositiveNormalized, precision: 4);
        Assert.True(ballistics.PositivePeakHold < 0.5);

        // Resetting clip latches zeroes visible count without affecting the other rail.
        ballistics.ResetPositiveClipLatch();
        Assert.Equal(0, ballistics.VisiblePositiveClips);
        Assert.Equal(2, ballistics.VisibleNegativeClips);
        ballistics.ResetNegativeClipLatch();
        Assert.Equal(0, ballistics.VisibleNegativeClips);
    }

    [Fact]
    public void RfDeviceSelectionHelperMatchesStableKeysAcrossRefreshes()
    {
        var dddKey = RfDeviceSelectionHelper.BuildDddKey(@"USB\VID_1209&PID_2347\123");
        var rtlKey = RfDeviceSelectionHelper.BuildRtlKey(0, "RTL-SDR Blog V4");
        string[] candidates = [dddKey, rtlKey, RfDeviceSelectionHelper.PlaybackDeviceKey];

        Assert.Equal(1, RfDeviceSelectionHelper.ResolveSelectionIndex(candidates, rtlKey, dddKey));
        Assert.Equal(0, RfDeviceSelectionHelper.ResolveSelectionIndex(candidates, "ddd:missing", dddKey));
        Assert.Equal(2, RfDeviceSelectionHelper.ResolveSelectionIndex(candidates, RfDeviceSelectionHelper.PlaybackDeviceKey, dddKey));
        Assert.Equal(0, RfDeviceSelectionHelper.ResolveSelectionIndex(candidates, null, "rtl:9:gone"));
        Assert.Equal(-1, RfDeviceSelectionHelper.ResolveSelectionIndex([], dddKey, dddKey));
    }

    [Fact]
    public void EncodingUxHelperValidatesBitrateDetectsInterlacedWarningAndEstimatesStorage()
    {
        Assert.Equal((true, (int?)null), EncodingUxHelper.ValidateBitrateText(""));
        Assert.Equal((true, (int?)null), EncodingUxHelper.ValidateBitrateText("   "));
        Assert.Equal((true, (int?)25000), EncodingUxHelper.ValidateBitrateText("25000"));
        Assert.Equal((false, (int?)null), EncodingUxHelper.ValidateBitrateText("0"));
        Assert.Equal((false, (int?)null), EncodingUxHelper.ValidateBitrateText("-500"));
        Assert.Equal((false, (int?)null), EncodingUxHelper.ValidateBitrateText("1500000"));
        Assert.Equal((false, (int?)null), EncodingUxHelper.ValidateBitrateText("25M"));

        Assert.True(EncodingUxHelper.RequiresProgressiveDeinterlaceWarning(
            CaptureRack.Recording.RecordingOutput.H265, sourceIsInterlaced: true, softwareDeinterlaceEnabled: false));
        Assert.True(EncodingUxHelper.RequiresProgressiveDeinterlaceWarning(
            CaptureRack.Recording.RecordingOutput.Av1, sourceIsInterlaced: true, softwareDeinterlaceEnabled: false));
        Assert.False(EncodingUxHelper.RequiresProgressiveDeinterlaceWarning(
            CaptureRack.Recording.RecordingOutput.H265, sourceIsInterlaced: true, softwareDeinterlaceEnabled: true));
        Assert.False(EncodingUxHelper.RequiresProgressiveDeinterlaceWarning(
            CaptureRack.Recording.RecordingOutput.H265, sourceIsInterlaced: false, softwareDeinterlaceEnabled: false));
        Assert.False(EncodingUxHelper.RequiresProgressiveDeinterlaceWarning(
            CaptureRack.Recording.RecordingOutput.H264, sourceIsInterlaced: true, softwareDeinterlaceEnabled: false));

        Assert.Null(EncodingUxHelper.EstimateSoftwareBytesPerSecond(
            CaptureRack.Recording.RecordingOutput.Native, 25000, 2, CaptureAudioFormat.Pcm16));
        Assert.Null(EncodingUxHelper.EstimateSoftwareBytesPerSecond(
            CaptureRack.Recording.RecordingOutput.Ffv1, 25000, 2, CaptureAudioFormat.Pcm16));
        Assert.Null(EncodingUxHelper.EstimateSoftwareBytesPerSecond(
            CaptureRack.Recording.RecordingOutput.H264, null, 2, CaptureAudioFormat.Pcm16));

        var bps = EncodingUxHelper.EstimateSoftwareBytesPerSecond(
            CaptureRack.Recording.RecordingOutput.H264, 20_000, 2, CaptureAudioFormat.Pcm16);
        Assert.NotNull(bps);
        // 20 Mbps = 2,500,000 B/s + 48kHz*4 B/s (192,000) * 1.03 = ~2,772,760 B/s
        Assert.InRange(bps!.Value, 2_700_000, 2_850_000);
    }
}
