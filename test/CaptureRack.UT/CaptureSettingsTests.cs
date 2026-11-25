using System.Text;
using System.Text.Json;
using CaptureRack.App;
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
            DeckLink = new DeckLinkSettings
            {
                AnalogNtscIre = "Ire75",
                AnalogComponentLevel = "Betacam",
                AnalogAudioLevel = "Pro",
                AnalogAudioGainOverride = true,
                AnalogAudioGainLeftDb = 3.5,
                AnalogAudioGainRightDb = -1.5,
                AnalogAudioGainLinked = false
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

    private static CaptureSettings Read(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return CaptureSettingsStore.Read(stream);
    }
}
