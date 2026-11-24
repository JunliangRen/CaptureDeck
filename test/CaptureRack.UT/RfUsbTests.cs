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
