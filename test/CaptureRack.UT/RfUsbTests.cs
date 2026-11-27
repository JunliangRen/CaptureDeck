using CaptureRack.SignalCapture;
using Xunit;

namespace CaptureRack.UT;

public sealed class RfUsbTests
{
    [Theory]
    [InlineData(@"USB\VID_1D50&PID_603B\LEGACY", true)]
    [InlineData(@"usb\vid_1209&pid_2347\VERSION1", true)]
    [InlineData(@"USB\VID_04B4&PID_00F3\RECOVERY", false)]
    [InlineData(@"USB\VID_1209&PID_2347&MI_00\CHILD", false)]
    [InlineData(@"USB\VID_1209&PID_2347\", false)]
    [InlineData(@"USB\VID_1209&PID_2348\OTHER", false)]
    public void RecognizesOnlyRuntimeDeviceInstances(string instanceId, bool expected)
    {
        Assert.Equal(expected, DomesdayUsb.TryParseDeviceId(instanceId, out _, out _));
    }

    [Theory]
    [InlineData(0x1d50, 0x603b, 0x0000, RfUsbFirmware.Legacy)]
    [InlineData(0x1209, 0x2347, 0x0100, RfUsbFirmware.ProtocolV1)]
    [InlineData(0x1209, 0x2347, 0x0102, RfUsbFirmware.ProtocolV1)]
    [InlineData(0x1209, 0x2347, 0x0200, RfUsbFirmware.UnsupportedProtocol)]
    public void ClassifiesFirmwareByUsbDescriptor(int vendor, int product, int release, RfUsbFirmware expected)
    {
        Assert.Equal(expected, DomesdayUsb.Classify((ushort)vendor, (ushort)product, (ushort)release));
    }

    [Theory]
    [InlineData("bb2e11a", "3.2.0")]
    [InlineData("bb2e11aa", "3.2.0")]
    [InlineData("e61e886", "3.1.0")]
    [InlineData("e61e8868", "3.1.0")]
    [InlineData("bb2e11a-dirty", null)]
    [InlineData("1234567", null)]
    public void IdentifiesReleaseFromFx3Build(string commit, string? expected)
    {
        var probe = new RfUsbProbeResult(new("id", "DdD", 0x1209, 0x2347, "WinUSB"),
            RfUsbFirmware.ProtocolV1, "0300", "0100", new(0, 0, 0x81, 1024), 2097152, 2)
            { Fx3Commit = commit };
        Assert.Equal(expected, probe.FirmwareRelease);
    }

    [Fact]
    public void IdentityRetriesOnlyIncompleteReplies()
    {
        int calls = 0;
        var result = WinUsbRfProbe.ReadRegisterIdentity(buffer =>
        {
            calls++;
            if (calls == 1) return 0;
            if (calls == 2) return 11;
            buffer[0] = 0x44;
            buffer[1] = 2;
            buffer[11] = 1;
            return 12;
        });
        Assert.Equal(3, calls);
        Assert.True(DomesdayUsb.IsSupportedIdentity(result));
    }

    [Theory]
    [InlineData(0, 3, "RfIdentityShortRead")]
    [InlineData(11, 3, "RfIdentityShortRead")]
    [InlineData(12, 1, "RfIdentityMismatch")]
    public void IdentityFailureReportsTheRightReasonAndAttemptCount(uint length, int expected, string key)
    {
        int calls = 0;
        var error = Assert.Throws<DomesdayIdentityException>(() => WinUsbRfProbe.ReadRegisterIdentity(buffer =>
        {
            calls++;
            buffer[0] = 0x44;
            buffer[1] = 3;
            buffer[11] = 1;
            return length;
        }));
        Assert.Equal(expected, calls);
        Assert.Equal(key, error.Localize(resource => resource));
    }

    [Fact]
    public void ValidatesApplicationImageAndRegisterMap()
    {
        byte[] identity = [0x44, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1];
        Assert.True(DomesdayUsb.IsSupportedIdentity(identity));
        Assert.False(DomesdayUsb.IsSupportedIdentity(identity.AsSpan(0, 11)));
        identity[11] = 0;
        Assert.False(DomesdayUsb.IsSupportedIdentity(identity));
        identity[11] = 1;
        identity[1] = 3;
        Assert.False(DomesdayUsb.IsSupportedIdentity(identity));
    }

    [Fact]
    public void LegacySelectionPreservesInterfaceAlternateAndFirstEqualSizeEndpoint()
    {
        var small = new RfUsbEndpoint(0, 0, 0x81, 512);
        var best = new RfUsbEndpoint(3, 2, 0x84, 1024);
        Assert.Equal(best, DomesdayUsb.SelectStreamEndpoint(RfUsbFirmware.Legacy,
            [small, best, new(4, 1, 0x82, 1024)]));
        Assert.Null(DomesdayUsb.SelectStreamEndpoint(RfUsbFirmware.Legacy, []));
        var probe = new RfUsbProbeResult(new("id", "DdD", 0x1d50, 0x603b, "WinUSB"),
            RfUsbFirmware.Legacy, "0300", "0000", small, 262144, null);
        Assert.True(probe.RecommendUsb3);
        Assert.Contains("High-Speed", probe.LinkSpeedHint);
        Assert.False((probe with { StreamEndpoint = best }).RecommendUsb3);
    }

    [Fact]
    public void ProtocolV1RequiresOneMatchingStreamEndpoint()
    {
        var expected = new RfUsbEndpoint(0, 0, 0x81, 1024);
        var other = new RfUsbEndpoint(0, 0, 0x82, 1024);
        var highSpeed = expected with { MaxPacketBytes = 512 };
        var otherInterface = expected with { InterfaceNumber = 1 };
        Assert.Equal(expected, DomesdayUsb.SelectStreamEndpoint(RfUsbFirmware.ProtocolV1, [other, highSpeed, expected]));
        Assert.Null(DomesdayUsb.SelectStreamEndpoint(RfUsbFirmware.ProtocolV1, [highSpeed, other, otherInterface]));
        Assert.Null(DomesdayUsb.SelectStreamEndpoint(RfUsbFirmware.ProtocolV1, [expected, expected]));
        Assert.Equal(other, DomesdayUsb.SelectStreamEndpoint(RfUsbFirmware.Legacy, [highSpeed, other]));
    }
}
