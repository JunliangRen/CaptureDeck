using System.Globalization;
using CaptureDeck.SignalCapture;

namespace CaptureDeck.Probe;

internal static class RtlSdrCommand
{
    public static int Run(string[] args, bool json)
    {
        switch (args)
        {
            case [] or ["--help"] or ["-h"]:
                Console.WriteLine("CaptureDeck.Probe rf sdr rtl-sdr list [--json]");
                Console.WriteLine("CaptureDeck.Probe rf sdr rtl-sdr probe <index> [--json]");
                Console.WriteLine("CaptureDeck.Probe rf sdr rtl-sdr configure <index> [--frequency <Hz>] [--sample-rate <Hz>] [--gain auto|<tenths-dB>] [--agc on|off] [--vga <0–8>] [--ppm <integer>] [--json]");
                return 0;
            case ["list"]:
                var devices = RtlSdrProbe.List();
                if (json)
                    RfCommand.WriteJson(new { type = "rtl_sdr_device_list", devices });
                else if (devices.Count == 0)
                    Console.WriteLine("No RTL-SDR devices found.");
                else
                    foreach (var device in devices)
                        Console.WriteLine($"{device.Index}  {device.Name}  {device.Manufacturer} {device.Product}  {device.Serial}");
                return 0;
            case ["probe", var index]:
                PrintCapabilities(RtlSdrProbe.Probe(Unsigned(index, "device index")), json);
                return 0;
            case ["configure", var index, .. var options]:
                var configured = RtlSdrProbe.Configure(Unsigned(index, "device index"), ParseSettings(options));
                if (json)
                    RfCommand.WriteJson(new { type = "rtl_sdr_configured", result = configured });
                else
                {
                    Console.WriteLine($"{configured.ActualFrequencyHz:N0} Hz · {configured.ActualSampleRateHz:N0} samples/s · {configured.ActualPpm} ppm");
                    Console.WriteLine($"Gain: {GainText(configured.GainTenthsDb)} · AGC: {(configured.Requested.DigitalAgc ? "on" : "off")}");
                    if (configured.AppliedVgaIndex is int vga)
                        Console.WriteLine($"Applied VGA index: 0x{vga:X2}");
                }
                return 0;
            default:
                throw new ArgumentException("Invalid RTL-SDR command. See rf sdr rtl-sdr --help.");
        }
    }

    private static void PrintCapabilities(RtlSdrCapabilities value, bool json)
    {
        if (json)
        {
            RfCommand.WriteJson(new { type = "rtl_sdr_device_probe", capabilities = value });
            return;
        }

        Console.WriteLine($"{value.Device.Index}  {value.Device.Name} · {value.Tuner}");
        Console.WriteLine($"Manual tuner gains: {string.Join(", ", value.GainsTenthsDb.Select(gain => GainText(gain)))}");
        if (value.SupportsManualVga)
            Console.WriteLine("Manual VGA: indices 0x00–0x08 (default 0x08)");
        else if (value.IsBlogV4)
            Console.WriteLine("Manual VGA requires the patched librtlsdr used by MISRC-GUI.");
    }

    private static string GainText(int? gain) => gain is int tenths ? $"{tenths / 10.0:F1} dB" : "auto";

    private static RtlSdrSettings ParseSettings(string[] args)
    {
        var settings = new RtlSdrSettings();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var option = args[index];
            if (!seen.Add(option) || index + 1 >= args.Length)
                throw new ArgumentException($"Missing value or repeated option: {option}.");
            var value = args[index + 1];
            settings = option switch
            {
                "--frequency" => settings with { FrequencyHz = Unsigned(value, option) },
                "--sample-rate" => settings with { SampleRateHz = Unsigned(value, option) },
                "--gain" => settings with { GainTenthsDb = value == "auto" ? null : Integer(value, option) },
                "--agc" => settings with { DigitalAgc = value switch
                {
                    "on" => true, "off" => false,
                    _ => throw new ArgumentException("--agc expects on or off.")
                } },
                "--vga" => settings with { VgaIndex = Integer(value, option) },
                "--ppm" => settings with { Ppm = Integer(value, option) },
                _ => throw new ArgumentException($"Unknown RTL-SDR option: {option}.")
            };
        }
        return settings;
    }

    private static uint Unsigned(string value, string name) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new ArgumentException($"Invalid {name}: {value}.");

    private static int Integer(string value, string name) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new ArgumentException($"Invalid {name}: {value}.");
}
