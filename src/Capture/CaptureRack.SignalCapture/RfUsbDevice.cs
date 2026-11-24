namespace CaptureRack.SignalCapture;

public sealed record RfUsbDevice(string InstanceId, string Model, ushort VendorId, ushort ProductId, string? DriverService)
{
    public string UsbId => $"{VendorId:X4}:{ProductId:X4}";
    public bool WinUsbBound => string.Equals(DriverService, "WinUSB", StringComparison.OrdinalIgnoreCase);
}

public sealed record RfUsbEndpoint(byte InterfaceNumber, byte AlternateSetting, byte Address, ushort MaxPacketBytes);
