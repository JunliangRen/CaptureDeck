using System.Globalization;
using System.Text.Json;
using CaptureRack.DirectShow.Windows.FireWire.MsTape;
using CaptureRack.QualityControl;

namespace CaptureRack.Probe;

internal static class FireWireCommand
{
    private static readonly JsonSerializerOptions ReportJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static int Run(string[] args)
    {
        var json = args.Contains("--json", StringComparer.Ordinal);
        try
        {
            if (args is [] or ["--help"] or ["-h"])
            {
                Console.WriteLine("CaptureRack.Probe firewire list [--json]");
                Console.WriteLine("CaptureRack.Probe firewire probe --device <id> [--json]");
                Console.WriteLine("CaptureRack.Probe firewire capture --device <id> --output <path> [--format ts192] [--duration <seconds>] [--no-data-timeout <seconds>] [--json]");
                Console.WriteLine("CaptureRack.Probe firewire analyze --input <path> [--format ts188|ts192] [--json]");
                return 0;
            }

            var options = Parse(args);
            switch (args[0])
            {
                case "list":
                    return List(json);
                case "probe":
                    return Probe(options, json);
                case "capture":
                    return Capture(options, json);
                case "analyze":
                    return Analyze(options, json);
                default:
                    throw new ArgumentException($"Unknown FireWire command: {args[0]}.");
            }
        }
        catch (FireWireException ex) { return Error(ex.ExitCode, ex.Message, json); }
        catch (InvalidDataException ex) { return Error(4, ex.Message, json); }
        catch (ArgumentException ex) { return Error(2, ex.Message, json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error(5, ex.Message, json);
        }
    }

    private static int List(bool json)
    {
        var devices = FireWireCapture.List();
        if (json)
            WriteJson(new { schema_version = 1, type = "device_list", devices });
        else if (devices.Count == 0)
            Console.WriteLine("No MSTape devices found.");
        else
            foreach (var device in devices)
                Console.WriteLine($"{device.Id}  {device.Vendor} {device.Model}  {device.Name}");
        return 0;
    }

    private static int Probe(Dictionary<string, string> options, bool json)
    {
        var device = Required(options, "--device");
        var formats = FireWireCapture.GetFormats(device).Select(format => format.Format).ToArray();
        if (json)
            WriteJson(new { schema_version = 1, type = "device_probe", device_id = device, formats });
        else
            Console.WriteLine($"{device}: {(formats.Length == 0 ? "No supported native TS format" : string.Join(", ", formats))}");
        return 0;
    }

    private static int Capture(Dictionary<string, string> options, bool json)
    {
        if (options.TryGetValue("--format", out var format) && format != "ts192")
            throw new ArgumentException("Live FireWire capture requires native ts192. Use analyze for existing ts188 files.");
        var request = new FireWireRequest(Required(options, "--device"), Required(options, "--output"),
            Seconds(options, "--duration", 0, allowZero: true),
            Seconds(options, "--no-data-timeout", 30, allowZero: false));
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var result = FireWireCapture.Run(request,
                progress =>
                {
                    if (json)
                        WriteJson(new { schema_version = 1, type = "progress", progress.ElapsedSeconds, progress.ReceivedBytes, progress.WrittenBytes });
                    else
                        Console.Error.WriteLine($"received={progress.ReceivedBytes} written={progress.WrittenBytes}");
                },
                () => Console.Error.WriteLine("DirectShow started. Play the tape manually. Ctrl+C stops computer capture."),
                cancellation.Token);
            if (json)
                WriteJson(result);
            else
            {
                Console.WriteLine($"{(result.Success ? "FINISHED" : "FAILED")}: {result.WrittenBytes} bytes; file={result.DataPath}");
                Console.WriteLine($"Report: {result.ReportPath}");
            }
            if (result.Error is not null)
                Console.Error.WriteLine(result.Error);
            return result.ExitCode;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static TimeSpan Seconds(Dictionary<string, string> options, string name, double fallback, bool allowZero)
    {
        var seconds = fallback;
        if (options.TryGetValue(name, out var value) &&
            (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || (!allowZero && seconds <= 0) || seconds > 31536000))
            throw new ArgumentException($"Invalid seconds for {name}: {value}.");
        return TimeSpan.FromSeconds(seconds);
    }

    private static int Analyze(Dictionary<string, string> options, bool json)
    {
        var report = TsFile.Analyze(Required(options, "--input"), Format(options));
        var analysis = report.Analysis;
        if (json)
            Console.WriteLine(report.ToJson());
        else
            Console.WriteLine($"format={report.Format} input={analysis.InputBytes} packets={analysis.Packets} sync_errors={analysis.SyncErrors} malformed={analysis.MalformedPackets} TEI={analysis.Tei} continuity_errors={analysis.ContinuityErrors} pcr_backwards={analysis.PcrBackwards} trailing={analysis.TrailingBytes}");
        return report.ExitCode;
    }

    internal static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, ReportJson));

    private static int Error(int code, string message, bool json)
    {
        Console.Error.WriteLine(message);
        if (json)
            WriteJson(new { schema_version = 1, type = "error", success = false, exit_code = code, error = message });
        return code;
    }

    private static TsLayout? Format(Dictionary<string, string> options) =>
        options.GetValueOrDefault("--format") switch
        {
            null => null,
            "ts188" => TsLayout.Ts188,
            "ts192" => TsLayout.Ts192,
            _ => throw new ArgumentException("--format must be ts188 or ts192.")
        };

    private static string Required(Dictionary<string, string> options, string name) =>
        options.GetValueOrDefault(name) ?? throw new ArgumentException($"Required option: {name}.");

    private static Dictionary<string, string> Parse(string[] args)
    {
        string[] allowed = args[0] switch
        {
            "analyze" => ["--input", "--format"],
            "probe" => ["--device"],
            "capture" => ["--device", "--output", "--format", "--duration", "--no-data-timeout"],
            _ => []
        };
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            if (name == "--json")
            {
                if (!options.TryAdd(name, "true"))
                    throw new ArgumentException($"Duplicate option: {name}.");
                continue;
            }
            if (!allowed.Contains(name, StringComparer.Ordinal))
                throw new ArgumentException($"Unknown option: {name}.");
            if (++i == args.Length || args[i].Length == 0 || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {name}.");
            if (!options.TryAdd(name, args[i]))
                throw new ArgumentException($"Duplicate option: {name}.");
        }
        return options;
    }
}
