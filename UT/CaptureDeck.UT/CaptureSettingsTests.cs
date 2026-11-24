using System.Text;
using CaptureDeck.App;
using CaptureDeck.DeckLink;
using CaptureDeck.DeviceControl;
using Xunit;

namespace CaptureDeck.UT;

public sealed class CaptureSettingsTests
{
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

    private static CaptureSettings Read(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return CaptureSettingsStore.Read(stream);
    }
}
