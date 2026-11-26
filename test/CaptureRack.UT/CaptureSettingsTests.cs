using System.Text;
using System.Text.Json;
using CaptureRack.App;
using CaptureRack.DeckLink;
using CaptureRack.DeviceControl;
using Xunit;

namespace CaptureRack.UT;

public sealed class CaptureSettingsTests
{
    [Theory]
    [InlineData(null, "zh-HK", "zh-Hant")]
    [InlineData(null, "zh-MY", "zh-Hans")]
    [InlineData(null, "ja-JP", "ja")]
    [InlineData(null, "de-DE", "en")]
    [InlineData("system", "zh-SG", "zh-Hans")]
    [InlineData("en", "zh-TW", "en")]
    [InlineData("ja", "en-US", "ja")]
    public void UsesWindowsLanguageOnlyUntilAnExplicitLanguageIsSaved(
        string? savedLanguage, string windowsCulture, string expected)
    {
        var settings = new CaptureSettings { LanguageOverride = SettingsLanguage.Resolve(savedLanguage, windowsCulture) };
        var restored = Read(CaptureSettingsStore.Serialize(settings));

        Assert.Equal(expected, SettingsLanguage.Resolve(restored.LanguageOverride, "fr-FR"));
    }

    [Theory]
    [InlineData("{\"ModeSelected\":true,\"ModeId\":null}")]
    [InlineData("{\"DeckLink\":null}")]
    public void RejectsSettingsWithoutDeckLinkOptions(string json) =>
        Assert.Throws<JsonException>(() => Read(json));

    [Fact]
    public void DecodedPreviewPreservesItsNativeRaster()
    {
        const int width = 1440, height = 12;
        var pixels = Enumerable.Range(0, width * height * 4).Select(index => (byte)(index & 255)).ToArray();
        var feed = new PreviewFeed { DeinterlaceMode = PreviewDeinterlaceMode.Off };
        PreviewFrame? frame = null;
        feed.SubmitDecodedFrame(pixels, width, height, true, 29.97, width, height, 16d / 9, "MPEG-2 / yuv420p");
        Assert.True(feed.TryTakeLatest(ref frame));
        Assert.Equal(width, frame!.Width);
        Assert.Equal(height, frame.Height);
        Assert.Equal("MPEG-2 / yuv420p", frame.SignalFormat);
        Assert.Equal(CapturePixelFormat.Bgra, frame.Format);
        Assert.Equal(PreviewScanMode.TopFirst, frame.SignalScanMode);
        Assert.Equal(pixels, frame.Pixels[..pixels.Length]);
    }

    [Fact]
    public void DecodedPreviewSharesBobWeaveCadenceAndScopes()
    {
        var pixels = Enumerable.Range(0, 8 * 8 * 4)
            .Select(index => (byte)(index % 4 == 3 ? 255 : index / 32 % 2 == 0 ? 10 : 240)).ToArray();
        var feed = new PreviewFeed { DeinterlaceMode = PreviewDeinterlaceMode.BobWeave2x };
        PreviewFrame? frame = null;

        feed.SubmitDecodedFrame(pixels, 8, 8, true, 29.97, 720, 480, 4d / 3);
        Assert.True(feed.TryTakeLatest(ref frame, 0));
        Assert.True(frame!.IsInterlaced);
        Assert.Equal(720, frame.SignalWidth);
        Assert.Equal(4d / 3, frame.DisplayAspectRatio);
        Assert.Equal(10, frame.Pixels[32]);
        Assert.False(feed.TryTakeLatest(ref frame, frame.SecondFieldDueAt - 1));
        Assert.True(feed.TryTakeLatest(ref frame, frame!.SecondFieldDueAt));
        Assert.Equal(240, frame!.Pixels[0]);
        Assert.NotNull(feed.Scopes);
        Assert.Null(feed.Scopes!.LumaOutsideLegalFraction);

        feed.SubmitDecodedFrame(pixels, 8, 8, true, 29.97, 720, 480, 4d / 3);
        Assert.True(feed.TryTakeLatest(ref frame, 0));
        Assert.Equal(pixels, frame!.Pixels[..pixels.Length]);
        Assert.True(feed.TryTakeLatest(ref frame, frame.SecondFieldDueAt));
        Assert.Equal(pixels, frame!.Pixels[..pixels.Length]);

        pixels[32] = pixels[33] = pixels[34] = 180;
        feed.SubmitDecodedFrame(pixels, 8, 8, true, 29.97, 720, 480, 4d / 3);
        Assert.True(feed.TryTakeLatest(ref frame, 0));
        Assert.Equal(10, frame!.Pixels[32]);
        Assert.True(feed.TryTakeLatest(ref frame, long.MaxValue));

        feed.SubmitDecodedFrame(pixels, 8, 8, null, 29.97, 8, 8, 1, signalScanMode: PreviewScanMode.Progressive);
        Assert.True(feed.TryTakeLatest(ref frame, 0));
        Assert.False(frame!.IsInterlaced);
        Assert.Equal(PreviewScanMode.Progressive, frame.SignalScanMode);
        Assert.Equal(pixels, frame.Pixels[..pixels.Length]);
        Assert.False(feed.TryTakeLatest(ref frame, long.MaxValue));

        feed.SubmitDecodedFrame(pixels, 8, 8, false, 29.97, 8, 8, 1);
        Assert.True(feed.TryTakeLatest(ref frame, 0));
        Assert.Equal(PreviewScanMode.BottomFirst, frame!.SignalScanMode);
    }

    [Fact]
    public void RoundTripPreservesIndependentCaptureAndControlSelection()
    {
        var settings = new CaptureSettings
        {
            CaptureBackendId = "windows.firewire",
            ControlDevice = new ControlDeviceAddress("jvc.jlip", "serial-device"),
            DeckLink = new DeckLinkSettings { ModeSelected = true, ModeId = 0x6E747363 }
        };

        Assert.Equal(settings, Read(CaptureSettingsStore.Serialize(settings)));
    }

    [Fact]
    public void LegacySettingsWithoutUiPreferencesLoadStudioDefaults()
    {
        const string legacyJson = """{"CaptureBackendId":"windows.decklink","DeckLink":{"ModeSelected":false},"OutputFolder":"C:\\Captures"}""";
        var restored = Read(legacyJson);

        Assert.Equal("System", restored.Theme);
        Assert.Equal("Split", restored.WorkspaceLayout);
        Assert.True(restored.InspectorPinned);
        Assert.False(restored.ShowSafeArea);
        Assert.Equal("", restored.RecordingLabel);
        Assert.Equal(0.63, restored.SplitPreviewRatio);
        Assert.Equal("Luma", restored.ScopeWaveformMode);
        Assert.Equal("Code", restored.ScopeLumaScale);
        Assert.Equal(new VancInputOptions(false, 18, 19, 20), restored.DeckLink.VancInput);
        Assert.Equal(new DirectShowSettings(), restored.DirectShow);
        Assert.Equal(new FireWireSettings(), restored.FireWire);
        Assert.Equal("Normal", restored.TestSignalScenario);
    }

    [Fact]
    public void RoundTripPreservesUiPreferences()
    {
        var settings = new CaptureSettings
        {
            Theme = "Light",
            WorkspaceLayout = "Focus",
            InspectorPinned = false,
            ShowSafeArea = true,
            ShowNtsc480Guide = true,
            PreviewDeinterlace = "Field",
            PreviewAspectRatio = "Ratio16x9",
            PreviewColorMatrix = "Bt709",
            AudioMonitorEnabled = true,
            AudioMonitorVolume = 0.65,
            RecordingLabel = "VHS_Tape07_SideA",
            SplitPreviewRatio = 0.54,
            ScopeWaveformMode = "RgbParade",
            ScopeLumaScale = "Ire",
            TestSignalScenario = "TimingFaults",
            DirectShow = new DirectShowSettings
            {
                DeviceId = @"\\?\usb#vid_046d&pid_085e",
                PinId = "0",
                IsInfrared = false,
                Width = 3840,
                Height = 2160,
                FramesPerSecond = 30,
                Encoding = "MJPG",
                VideoFormatIndex = 12,
                AudioFormatIndex = 2
            },
            FireWire = new FireWireSettings
            {
                DeviceId = "simulated:dvcpro50-ntsc"
            },
            DeckLink = new DeckLinkSettings
            {
                AnalogNtscIre = "Ire75",
                AnalogComponentLevel = "Betacam",
                AnalogAudioLevel = "Pro",
                AnalogAudioGainOverride = true,
                AnalogAudioGainLeftDb = 3.5,
                AnalogAudioGainRightDb = -1.5,
                AnalogAudioGainLinked = false,
                VancInput = new VancInputOptions(true, 21, 0, 23)
            }
        };

        Assert.Equal(settings, Read(CaptureSettingsStore.Serialize(settings)));
    }

    [Fact]
    public void BobWeavePreservesStaticDetailsAndSmoothsCombing()
    {
        // 4x6 interlaced BGRA image: top half static subtle gradient, bottom half harsh alternating comb (0 vs 240).
        const int width = 4;
        const int height = 6;
        const int stride = width * 4;
        var feed = new CaptureRack.DeckLink.PreviewFeed
        {
            DeinterlaceMode = CaptureRack.DeckLink.PreviewDeinterlaceMode.BobWeave
        };

        // Construct a UYVY frame (4 bytes per 2 pixels: Cb Y0 Cr Y1) with combing on odd rows.
        var uyvyStride = width * 2;
        var uyvy = new byte[uyvyStride * height];
        for (var y = 0; y < height; y++)
        {
            // Rows 0..2: static gradual luma (100, 102, 104) -> Weave preserves row 1 = 102.
            // Rows 3..5: strong comb (row 2=104, row 3=230, row 4=104, row 5=230) -> Bob smooths row 3 near 104.
            byte luma = y switch
            {
                0 => 100,
                1 => 102,
                2 => 104,
                3 => 230,
                4 => 104,
                _ => 230
            };
            for (var x = 0; x < width; x += 2)
            {
                var off = y * uyvyStride + x * 2;
                uyvy[off] = 128;
                uyvy[off + 1] = luma;
                uyvy[off + 2] = 128;
                uyvy[off + 3] = luma;
            }
        }

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(uyvy, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            const uint uppr = 0x75707072;
            feed.Offer(handle.AddrOfPinnedObject(), width, height, uyvyStride, (ulong)uyvy.Length,
                noInput: false, CaptureRack.DeckLink.CapturePixelFormat.Uyvy, uppr);
            feed.ConvertLatest(8);
            CaptureRack.DeckLink.PreviewFrame? output = null;
            Assert.True(feed.TryTakeLatest(ref output));
            Assert.NotNull(output);

            // Row 1 (static): preserved near row 0/2 (~100-104 luma -> ~100 BGRA).
            // Row 3 (harsh comb 230 between 104 and 104): interpolated down near row 2/4 instead of 230!
            var row2B = output!.Pixels[2 * stride];
            var row3B = output.Pixels[3 * stride];
            Assert.InRange(Math.Abs(row3B - row2B), 0, 12);
        }
        finally
        {
            handle.Free();
        }
    }

    [Theory]
    [InlineData(0x75707072u, false, true)]
    [InlineData(0x6C6F7772u, true, true)]
    [InlineData(0x70726F67u, false, false)]
    public void BobWeave2xPacesFieldsInSourceOrderAndBypassesProgressive(
        uint fieldDominance, bool bottomFieldFirst, bool interlaced)
    {
        const int width = 2;
        const int height = 4;
        const int stride = width * 2;
        var pixels = new byte[] { 128, 16, 128, 16, 128, 235, 128, 235,
            128, 16, 128, 16, 128, 235, 128, 235 };
        var feed = new CaptureRack.DeckLink.PreviewFeed
        {
            DeinterlaceMode = CaptureRack.DeckLink.PreviewDeinterlaceMode.BobWeave2x
        };
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            feed.Offer(handle.AddrOfPinnedObject(), width, height, stride, (ulong)pixels.Length,
                noInput: false, CaptureRack.DeckLink.CapturePixelFormat.Uyvy, fieldDominance);
            feed.ConvertLatest(8, System.Diagnostics.Stopwatch.Frequency / 60);
            CaptureRack.DeckLink.PreviewFrame? output = null;
            Assert.True(feed.TryTakeLatest(ref output, 0));
            Assert.NotNull(output);
            Assert.Equal(interlaced, output.IsInterlaced);
            Assert.Equal(bottomFieldFirst ? (byte)255 : (byte)0, output.Pixels[0]);
            if (interlaced)
            {
                Assert.Equal(output.Pixels[0], output.Pixels[output.Stride]);
                var secondFieldDueAt = output.SecondFieldDueAt;
                Assert.True(secondFieldDueAt > 0);
                Assert.False(feed.TryTakeLatest(ref output, secondFieldDueAt - 1));
                Assert.True(feed.TryTakeLatest(ref output, secondFieldDueAt));
                Assert.Equal(bottomFieldFirst ? (byte)0 : (byte)255, output!.Pixels[0]);
                Assert.Equal(output.Pixels[0], output.Pixels[output.Stride]);
            }
            else
            {
                Assert.Equal((byte)255, output.Pixels[output.Stride]);
            }

            Assert.False(feed.TryTakeLatest(ref output, long.MaxValue));
            Assert.Equal(interlaced ? 2 : 1, feed.PresentedSequence);
        }
        finally
        {
            handle.Free();
        }
    }

    private static CaptureSettings Read(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return CaptureSettingsStore.Read(stream);
    }
}
