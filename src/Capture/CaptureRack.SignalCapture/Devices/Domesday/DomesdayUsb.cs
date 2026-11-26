using System.Globalization;

namespace CaptureRack.SignalCapture;

public enum RfUsbFirmware { Legacy, ProtocolV1, UnsupportedProtocol }

public sealed record RfUsbProbeResult(RfUsbDevice Device, RfUsbFirmware Firmware, string UsbSpecification,
    string UsbDeviceRelease, RfUsbEndpoint StreamEndpoint, uint MaximumTransferBytes, byte? RegisterMapVersion)
{
    // The bulk packet size describes the active link; bcdUSB only declares USB capability.
    public string LinkSpeedHint => StreamEndpoint.MaxPacketBytes switch
    {
        1024 => "SuperSpeed or faster",
        512 => "High-Speed (USB 2.0)",
        <= 64 => "Full-Speed or slower",
        _ => "Unknown"
    };
    public bool RecommendUsb3 => StreamEndpoint.MaxPacketBytes != 1024;
    public string? Fx3Commit { get; init; }
    public string? GatewareCommit { get; init; }
    public string? FirmwareRelease => Fx3Commit?.ToLowerInvariant() switch
    {
        "bb2e11a" or "bb2e11aa" => "3.2.0",
        "e61e886" or "e61e8868" => "3.1.0",
        _ => null
    };
}

internal static class DomesdayUsb
{
    private const ushort LegacyVendor = 0x1d50;
    private const ushort LegacyProduct = 0x603b;
    private const ushort CurrentVendor = 0x1209;
    private const ushort CurrentProduct = 0x2347;

    // MISRC-GUI ddd_protocol classifies the running firmware by VID/PID and
    // the high byte of bcdDevice. It is a protocol identifier, not a board revision.
    public static bool IsRuntimeDevice(ushort vendor, ushort product) =>
        (vendor, product) is (LegacyVendor, LegacyProduct) or (CurrentVendor, CurrentProduct);

    public static bool TryParseDeviceId(string instanceId, out ushort vendor, out ushort product)
    {
        vendor = product = 0;
        var id = instanceId.AsSpan();
        const string prefix = @"USB\VID_";
        if (id.Length <= prefix.Length + 14 || !id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var hardware = id[prefix.Length..];
        if (!hardware.Slice(4, 5).Equals("&PID_", StringComparison.OrdinalIgnoreCase) || hardware[13] != '\\')
            return false; // Physical device instances; exclude interface children.
        return ushort.TryParse(hardware[..4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out vendor) &&
            ushort.TryParse(hardware.Slice(9, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out product) &&
            IsRuntimeDevice(vendor, product);
    }

    public static RfUsbFirmware Classify(ushort vendor, ushort product, ushort release) =>
        (vendor, product) switch
        {
            (LegacyVendor, LegacyProduct) => RfUsbFirmware.Legacy,
            (CurrentVendor, CurrentProduct) when (release >> 8) == 1 => RfUsbFirmware.ProtocolV1,
            _ => RfUsbFirmware.UnsupportedProtocol
        };

    public static bool IsSupportedIdentity(ReadOnlySpan<byte> identity) =>
        identity.Length == 12 && identity[0] == 0x44 && identity[1] == 2 && identity[11] == 1;

    public static RfUsbEndpoint? SelectStreamEndpoint(RfUsbFirmware firmware, IEnumerable<RfUsbEndpoint> bulkIn)
    {
        if (firmware == RfUsbFirmware.Legacy)
            return bulkIn.OrderByDescending(endpoint => endpoint.MaxPacketBytes).FirstOrDefault();
        if (firmware != RfUsbFirmware.ProtocolV1)
            return null;

        RfUsbEndpoint? match = null;
        foreach (var endpoint in bulkIn)
        {
            if (endpoint is not { InterfaceNumber: 0, AlternateSetting: 0, Address: 0x81, MaxPacketBytes: 1024 })
                continue;
            if (match is not null)
                return null;
            match = endpoint;
        }
        return match;
    }
}
