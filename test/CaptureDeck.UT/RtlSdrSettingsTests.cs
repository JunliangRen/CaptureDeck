using CaptureDeck.SignalCapture;
using Xunit;

namespace CaptureDeck.UT;

public sealed class RtlSdrSettingsTests
{
    private static RtlSdrCapabilities Capabilities(bool patched, RtlSdrTuner tuner = RtlSdrTuner.R828D,
        string product = "Blog V4") => RtlSdrCapabilities.FromDevice(
            new RtlSdrDevice(0, "RTL-SDR", "RTLSDRBlog", product, "123"), tuner, [0, 90, 197], patched);

    [Fact]
    public void ManualVgaRequiresBothTheBlogV4AndDriverSupport()
    {
        Assert.True(Capabilities(true).SupportsManualVga);
        Assert.False(Capabilities(false).SupportsManualVga);
        Assert.False(Capabilities(true, RtlSdrTuner.R820T).SupportsManualVga);
        Assert.False(Capabilities(true, product: "Other device").SupportsManualVga);
    }

    [Fact]
    public void AutoGainKeepsVgaUnsetEvenWithDigitalAgc()
    {
        var settings = new RtlSdrSettings(DigitalAgc: true);
        Assert.Null(settings.Validate(Capabilities(true)));
        Assert.Throws<ArgumentException>(() => (settings with { VgaIndex = 1 }).Validate(Capabilities(true)));
    }

    [Fact]
    public void ManualGainDefaultsToVgaEightForThePatchedBlogV4()
    {
        var settings = new RtlSdrSettings(GainTenthsDb: 0);
        Assert.Equal(8, settings.Validate(Capabilities(true)));
        Assert.Equal(0, (settings with { VgaIndex = 0 }).Validate(Capabilities(true)));
        Assert.Null(settings.Validate(Capabilities(false)));
        Assert.Throws<ArgumentException>(() => (settings with { VgaIndex = 4 }).Validate(Capabilities(false)));
        Assert.Throws<ArgumentException>(() => (settings with { VgaIndex = 9 }).Validate(Capabilities(true)));
    }

    [Fact]
    public void ManualGainMustMatchAValueReportedByTheTuner()
    {
        Assert.Null(new RtlSdrSettings(GainTenthsDb: 197).Validate(Capabilities(false)));
        Assert.Throws<ArgumentException>(() => new RtlSdrSettings(GainTenthsDb: 200).Validate(Capabilities(true)));
    }
}
