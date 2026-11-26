using System.Text.Json;
using System.Text.Json.Serialization;
using CaptureRack.SignalCapture;

namespace CaptureRack.Probe;

internal static class RfCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static int Run(string[] args)
    {
        var json = args.Length > 0 && args[^1] == "--json";
        if (json)
            args = args[..^1];

        try
        {
            switch (args)
            {
                case [] or ["--help"] or ["-h"]:
                    Console.WriteLine("CaptureRack.Probe rf list [--json]");
                    Console.WriteLine("CaptureRack.Probe rf probe <instance-id> [--json]");
                    Console.WriteLine("CaptureRack.Probe rf sdr rtl-sdr --help");
                    return 0;
                case ["sdr", "rtl-sdr", .. var sdr]:
                    return RtlSdrCommand.Run(sdr, json);
                case ["list"]:
                    List(json);
                    return 0;
                case ["probe", var id] when !string.IsNullOrWhiteSpace(id) && !id.StartsWith("--", StringComparison.Ordinal):
                    Probe(id, json);
                    return 0;
                default:
                    return Error("Invalid RF command. See CaptureRack.Probe rf --help.", 2, json);
            }
        }
        catch (ArgumentException ex) { return Error(ex.Message, 2, json); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PlatformNotSupportedException)
        {
            return Error(ex.Message, 1, json);
        }
    }

    private static void List(bool json)
    {
        var devices = WinUsbRfProbe.ListDevices();
        if (json)
        {
            WriteJson(new { type = "rf_device_list", transport = "WinUSB", devices });
            return;
        }
        if (devices.Count == 0)
            Console.WriteLine("No supported RF USB device IDs found.");
        foreach (var device in devices)
            Console.WriteLine($"{device.InstanceId}  {device.Model}  {device.UsbId}  driver={device.DriverService ?? "(none)"}");
    }

    private static void Probe(string id, bool json)
    {
        var result = WinUsbRfProbe.Probe(id);
        if (json)
        {
            WriteJson(new { type = "rf_device_probe", transport = "WinUSB", result });
            return;
        }
        var endpoint = result.StreamEndpoint;
        Console.WriteLine($"{result.Device.Model} {result.Device.UsbId} · WinUSB · {result.Firmware}");
        Console.WriteLine($"USB bcdUSB 0x{result.UsbSpecification}, bcdDevice 0x{result.UsbDeviceRelease}");
        Console.WriteLine($"Interface {endpoint.InterfaceNumber}/{endpoint.AlternateSetting} · bulk IN 0x{endpoint.Address:X2} · {endpoint.MaxPacketBytes} bytes per USB packet");
        Console.WriteLine($"Bulk link speed (packet-size inference): {result.LinkSpeedHint}");
        if (result.RecommendUsb3) Console.WriteLine("Use a SuperSpeed USB port and cable for 40 MSPS.");
        Console.WriteLine($"WinUSB maximum transfer: {result.MaximumTransferBytes:N0} bytes");
        if (result.Fx3Commit is { } commit)
            Console.WriteLine($"FX3: {result.FirmwareRelease ?? "unmapped build"} ({commit})");
        if (result.GatewareCommit is { } gateware)
            Console.WriteLine($"FPGA: {gateware}");
        if (result.RegisterMapVersion is byte version)
            Console.WriteLine($"Register map: {version}");
    }

    private static int Error(string message, int code, bool json)
    {
        Console.Error.WriteLine(message);
        if (json)
            WriteJson(new { type = "error", exit_code = code, error = message });
        return code;
    }

    internal static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
}
