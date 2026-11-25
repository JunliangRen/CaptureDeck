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
    public void DeckLinkWorkspaceModuleBuildsSeverityCodedTelemetry()
    {
        var module = new DeckLinkWorkspaceModule();
        Assert.Equal(DeckLinkBackend.BackendId, module.Descriptor.BackendId);
        Assert.True(module.Descriptor.IsImplementedInUi);
        Assert.True(module.Descriptor.Capabilities.SupportsVideoPreview);
        Assert.True(module.Descriptor.Capabilities.SupportsVideoScopes);
        Assert.True(module.Descriptor.Capabilities.SupportsAudioMeters);

        string Localize(string key) => key == "CaptureCounts" ? "{0}/{1}/{2}/{3}/{4}" : key;

        var idleMetrics = module.BuildTelemetry(null, -5, Localize, CultureInfo.InvariantCulture);
        Assert.Equal(4, idleMetrics.Count);
        Assert.All(idleMetrics, metric => Assert.Equal(TelemetrySeverity.Normal, metric.Severity));
        Assert.Equal("0", idleMetrics.Single(m => m.Id == "skipped").Value);

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

        var activeMetrics = module.BuildTelemetry(activeStats, 7, Localize, CultureInfo.InvariantCulture);
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
        Assert.Equal([DeckLinkBackend.BackendId, TestSignal.BackendId, "windows.directshow", "windows.firewire"], activeSources.Select(s => s.BackendId));
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
        Assert.Equal("CaptureRack.Probe.exe directshow list", registry.Resolve("windows.directshow").Descriptor.CliCommandHint);
        Assert.IsType<TestSignalWorkspaceModule>(registry.Resolve(TestSignal.BackendId));

        Assert.Equal(3, registry.ByCategory(CaptureBackendCategory.Video).Count);
        Assert.Single(registry.ByCategory(CaptureBackendCategory.FireWire));
        Assert.Single(registry.ByCategory(CaptureBackendCategory.SignalCapture));
        Assert.Single(registry.ByCategory(CaptureBackendCategory.Audio));

        foreach (var module in registry.Modules)
        {
            var metrics = module.BuildTelemetry(null, 0, key => key, CultureInfo.InvariantCulture);
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
}
