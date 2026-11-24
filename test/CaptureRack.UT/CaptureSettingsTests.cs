using System.Text;
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

    [Fact]
    public void LoadsLegacyDeckLinkSettingsAndPreservesAutoMode()
    {
        var settings = Read("""
            {
              "DeviceId": 300,
              "DeviceName": "DeckLink Studio 4K",
              "VideoSource": 4,
              "AudioSource": 2,
              "ModeSelected": true,
              "ModeId": null,
              "VideoFormat": 1983000880,
              "AudioFormat": 32,
              "OutputFolder": "D:/Captures",
              "LanguageOverride": "zh-Hant"
            }
            """);

        Assert.Equal(DeckLinkBackend.BackendId, settings.CaptureBackendId);
        Assert.Null(settings.ControlDevice);
        Assert.Equal(new DeckLinkSettings
        {
            DeviceId = 300,
            DeviceName = "DeckLink Studio 4K",
            VideoSource = 4,
            AudioSource = 2,
            ModeSelected = true,
            VideoFormat = 1983000880,
            AudioFormat = 32
        }, settings.DeckLink);
        Assert.Equal("D:/Captures", settings.OutputFolder);
        Assert.Equal("zh-Hant", settings.LanguageOverride);
        Assert.Equal(settings, Read(CaptureSettingsStore.Serialize(settings)));
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
    public void UsesOldProductSettingsOnlyWhenNewFileIsMissing()
    {
        var directory = Directory.CreateTempSubdirectory("capturerack-settings-");
        var previousPath = Path.Combine(directory.FullName, "settings-old.json");
        var currentPath = Path.Combine(directory.FullName, "settings.json");
        try
        {
            File.WriteAllText(previousPath, """{"LanguageOverride":"ja","OutputFolder":"D:/Captures"}""");
            var restored = CaptureSettingsStore.Load(currentPath, previousPath);
            Assert.Equal("ja", restored.LanguageOverride);
            Assert.Equal("D:/Captures", restored.OutputFolder);

            File.WriteAllText(currentPath, CaptureSettingsStore.Serialize(restored with { LanguageOverride = "en" }));
            Assert.Equal("en", CaptureSettingsStore.Load(currentPath, previousPath).LanguageOverride);
        }
        finally
        {
            File.Delete(currentPath);
            File.Delete(previousPath);
            directory.Delete();
        }
    }

    private static CaptureSettings Read(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return CaptureSettingsStore.Read(stream);
    }
}
