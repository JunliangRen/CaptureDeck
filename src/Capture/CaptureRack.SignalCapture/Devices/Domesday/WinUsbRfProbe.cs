namespace CaptureRack.SignalCapture;

public static class WinUsbRfProbe
{
    public static IReadOnlyList<RfUsbDevice> ListDevices()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The RF USB probe requires Windows with WinUSB.");

        var devices = new List<RfUsbDevice>();
        foreach (var id in RfUsbDiscovery.GetPresentUsbIds())
        {
            if (DomesdayUsb.TryParseDeviceId(id, out var vendor, out var product))
                devices.Add(new RfUsbDevice(id, "Domesday Duplicator", vendor, product, RfUsbDiscovery.GetDriverService(id)));
        }
        devices.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.InstanceId, right.InstanceId));
        return devices;
    }

    public static RfUsbProbeResult Probe(string instanceId)
    {
        var device = ListDevices().FirstOrDefault(candidate =>
                string.Equals(candidate.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("RF device is not present. Use rf list to get its current instance ID.");
        if (!device.WinUsbBound)
            throw new IOException($"The selected RF device has driver {device.DriverService ?? "(none)"}. Bind WinUSB before probing.");

        var path = RfUsbDiscovery.GetInterfacePath(device.InstanceId);
        using var usb = WinUsbDevice.Open(path);
        var session = new DomesdaySession(path);
        RfUsbProbeResult? result = null;
        try { result = Inspect(device, usb); }
        catch (Exception ex) { session.Fail(ex); }
        session.Cleanup("Restore probe alternate setting", usb.RestoreAlternate);
        session.ThrowIfFailed();
        return result!;
    }

    internal static RfUsbProbeResult Inspect(RfUsbDevice device, WinUsbDevice usb)
    {
        var (usbVersion, vendor, product, release, productIndex) = usb.ReadDeviceDescriptor();
        if (vendor != device.VendorId || product != device.ProductId)
            throw new InvalidDataException("The opened USB device no longer matches the selected instance.");

        var firmware = DomesdayUsb.Classify(vendor, product, release);
        if (firmware == RfUsbFirmware.UnsupportedProtocol)
            throw new InvalidDataException($"Unsupported RF firmware protocol (bcdDevice 0x{release:X4}).");

        var endpoint = firmware == RfUsbFirmware.Legacy ? usb.SelectLegacyEndpoint() : DomesdayUsb.SelectStreamEndpoint(firmware, usb.ReadBulkInEndpoints())
            ?? throw new InvalidDataException(firmware == RfUsbFirmware.ProtocolV1
                ? "Protocol-v1 requires exactly one 0x81 bulk IN endpoint with 1024-byte packets on interface 0, alternate setting 0."
                : "The active legacy USB interface has no bulk IN endpoint.");
        var maximumTransfer = usb.ReadMaximumTransferSize(endpoint.Address);
        byte[]? identity = firmware == RfUsbFirmware.ProtocolV1 ? ReadRegisterIdentity(usb) : null;
        var productName = identity is null ? null : usb.ReadProductString(productIndex);
        int bracket = productName?.LastIndexOf('(') ?? -1;
        string? fx3 = bracket >= 0 && productName!.EndsWith(')') ? productName[(bracket + 1)..^1] : null;
        string? gateware = identity is not null && (identity[2] & 2) != 0
            ? System.Text.Encoding.ASCII.GetString(identity, 3, 8).TrimEnd('\0') : null;

        return new RfUsbProbeResult(device, firmware, $"{usbVersion:X4}", $"{release:X4}",
            endpoint, maximumTransfer, identity?[1])
        {
            Fx3Commit = fx3,
            GatewareCommit = gateware is null ? null : gateware + ((identity![2] & 1) != 0 ? "-dirty" : "")
        };
    }

    private static byte[] ReadRegisterIdentity(WinUsbDevice usb)
    {
        usb.SetControlTimeout(1000);
        return ReadRegisterIdentity(buffer => usb.ReadVendorControl(0xb7, buffer, "Read RF firmware identity"));
    }

    internal static byte[] ReadRegisterIdentity(Func<byte[], uint> read)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            var identity = new byte[12];
            uint received;
            try { received = read(identity); }
            catch (IOException ex) when (ex.InnerException is System.ComponentModel.Win32Exception win32)
            {
                throw new DomesdayIdentityException("RfIdentityUsbError",
                    "Cannot read the Domesday Duplicator firmware identity (WinUSB {0}).", ex, win32.NativeErrorCode);
            }

            if (received == identity.Length)
            {
                if (DomesdayUsb.IsSupportedIdentity(identity)) return identity;
                throw new DomesdayIdentityException("RfIdentityMismatch",
                    "Unsupported Domesday Duplicator firmware identity (signature 0x{0:X2}, register map {1}, image {2}).",
                    null, identity[0], identity[1], identity[11]);
            }
            if (attempt == maxAttempts)
                throw new DomesdayIdentityException("RfIdentityShortRead",
                    "Firmware identity incomplete after {0} reads (last read {1} of {2} bytes).",
                    null, attempt, received, identity.Length);
            Thread.Sleep(25);
        }
    }
}

public sealed class DomesdayIdentityException : IOException
{
    private readonly object[] arguments;
    private readonly string resourceKey;

    internal DomesdayIdentityException(string resourceKey, string message, Exception? inner, params object[] arguments)
        : base(string.Format(message, arguments), inner)
    {
        this.resourceKey = resourceKey;
        this.arguments = arguments;
    }

    public string Localize(Func<string, string> lookup) => string.Format(lookup(resourceKey), arguments);
}
