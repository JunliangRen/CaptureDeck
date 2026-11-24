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

    private static CaptureSettings Read(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return CaptureSettingsStore.Read(stream);
    }
}
