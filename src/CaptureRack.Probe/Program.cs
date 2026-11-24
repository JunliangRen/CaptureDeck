using System.Diagnostics;
using System.Runtime.InteropServices;
using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.QualityControl;

namespace CaptureRack.Probe;

internal static class Program
{
    private static readonly DeckLinkBackend DeckLink = new CaptureBackendRegistry(new DeckLinkBackend())
            .Get<DeckLinkBackend>(DeckLinkBackend.BackendId);
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "rf")
            return RfCommand.Run(args[1..]);

        if (args.Length > 0 && args[0] == "firewire")
            return FireWireCommand.Run(args[1..]);

        if (args is ["--help"] or ["-h"])
        {
            PrintUsage();
            return 0;
        }
        if (args is ["qc", var path])
            return CheckMov(path);

        if (args is not ["list"] &&
            (args.Length is < 3 or > 4 || args[0] != "capture" ||
             !int.TryParse(args[1], out var deviceIndex) || deviceIndex < 0 ||
             !int.TryParse(args[2], out var seconds) ||
             seconds is < 1 or > 3600 || (args.Length == 4 && !IsFourCc(args[3]))))
        {
            PrintUsage();
            return 1;
        }

        try
        {
            var scan = DeckLink.ScanDevices();
            if (scan.Availability == DeckLinkAvailability.UnsupportedPlatform)
            {
                Console.Error.WriteLine("DeckLink capture requires Windows x64. Offline QC is available.");
                return 1;
            }
            if (scan.Availability == DeckLinkAvailability.DriverUnavailable)
            {
                Console.Error.WriteLine("Blackmagic Desktop Video or its DeckLink COM component is unavailable.");
                return 1;
            }

            Console.WriteLine($"DeckLink API {scan.ApiVersion}");
            var devices = scan.Devices;
            if (args is ["list"])
            {
                foreach (var device in devices)
                {
                    PrintDevice(device);
                    Console.WriteLine($"  Hardware encoder input: {(device.HasHardwareEncoderInput ? "available" : "unavailable")}");
                    foreach (var mode in device.Modes)
                        Console.WriteLine($"  '{DeckLinkCapture.FormatFourCc(mode.Id)}'  {mode.Description}, {DeckLinkCapture.FormatFourCc(mode.FieldDominance)}");
                    Console.WriteLine($"  {device.Modes.Count} supported 10-bit YUV input modes");
                }
                if (devices.Count == 0)
                    Console.Error.WriteLine("No DeckLink devices found.");
                return devices.Count == 0 ? 1 : 0;
            }

            var selectedIndex = int.Parse(args[1]);
            var selected = devices.FirstOrDefault(device => device.Index == selectedIndex)
                ?? throw new InvalidOperationException($"Device {selectedIndex} not found.");
            PrintDevice(selected);
            return Capture(selected.Index, int.Parse(args[2]), args.Length == 4 ? ParseFourCc(args[3]) : null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("CaptureRack.Probe list");
        Console.WriteLine("CaptureRack.Probe capture <device-index> <seconds:1-3600> [mode-fourcc]");
        Console.WriteLine("CaptureRack.Probe qc <mov-path>");
        Console.WriteLine("CaptureRack.Probe firewire --help");
        Console.WriteLine("CaptureRack.Probe rf --help");
        Console.WriteLine("Capture uses the current Desktop Video input routing. Omit the mode for auto detection.");
        Console.WriteLine("Quote mode codes with spaces, for example \"pal \".");
    }

    private static int CheckMov(string path)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var result = QcReports.RunAsync(path, progress: Console.WriteLine, cancellation: stop.Token)
                .GetAwaiter().GetResult();
            Console.WriteLine($"QC {result.Status}: {result.Summary}");
            Console.WriteLine(result.CaptureLog);
            if (result.FrameMd5 is { } hashes)
                Console.WriteLine(hashes);
            if (result.QcToolsReport is { } scopes)
                Console.WriteLine(scopes);
            foreach (var warning in result.Warnings)
                Console.WriteLine($"Warning: {warning}");
            return result.Status == QcStatus.Passed ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static void PrintDevice(CaptureDevice device)
    {
        Console.WriteLine($"[{device.Index}] {device.Name}");
        Console.WriteLine(device.HasVideoInput
            ? $"  Current input: video {device.VideoInput}, audio {device.AudioInput}"
            : "  Video input unavailable.");
    }

    private static int Capture(int index, int seconds, uint? mode)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        Console.CancelKeyPress += cancel;
        try
        {
            var clock = Stopwatch.StartNew();
            double nextReport = 1;
            VideoMode? runningMode = null;
            var request = new DeckLinkCaptureRequest(index, mode, TimeSpan.FromSeconds(seconds));
            var result = DeckLink.Run(request, update =>
            {
                var (activeMode, stats) = update;
                if (activeMode != runningMode)
                {
                    runningMode = activeMode;
                    Console.WriteLine($"  Capture: {activeMode.Name} ('{DeckLinkCapture.FormatFourCc(activeMode.Id)}'), " +
                        $"v210, PCM 48 kHz / 16-bit / 2 channels, {seconds}s");
                }
                if (clock.Elapsed.TotalSeconds >= nextReport)
                {
                    PrintStats($"{clock.Elapsed.TotalSeconds,6:0.0}s", stats);
                    nextReport = clock.Elapsed.TotalSeconds + 1;
                }
            }, stop.Token);

            PrintStats("Result", result);
            return result.Verified ? 0 : 2;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static void PrintStats(string label, CaptureStats stats)
    {
        var signal = stats.Signal switch
        {
            VideoSignal.NoInput => "no-input",
            VideoSignal.Present => "present",
            _ => "unknown"
        };
        Console.WriteLine($"{label}: signal={signal}, video={stats.VideoFrames}, no-input={stats.NoInputFrames}, " +
            $"audio-packets={stats.AudioPackets}, PCM-sample-frames={stats.AudioSampleFrames}, PCM-peak={stats.AudioPeak}, " +
            $"video-pts={stats.VideoTime?.ToString() ?? "-"}, audio-pts={stats.AudioTime?.ToString() ?? "-"} " +
            $"(1/48000 s), timestamp-regressions={stats.TimestampRegressions}");

        if (label != "Result")
            return;

        if (stats.VideoPayload is not null)
            Console.WriteLine($"  Video payload: {stats.VideoPayload}");
        if (stats.NoInputVideoPayload is not null)
            Console.WriteLine($"  No-input video payload: {stats.NoInputVideoPayload}");
        if (stats.AudioPayload is not null)
            Console.WriteLine($"  Audio payload: {stats.AudioPayload}");
        if (stats.Error is not null)
            Console.Error.WriteLine($"  Callback error: {stats.Error}");
        if (!stats.Verified)
            Console.WriteLine("  Live 10-bit video and PCM with advancing timestamps were not both verified.");
    }

    private static bool IsFourCc(string text) =>
        text.Length == 4 && text.All(character => character is >= ' ' and <= '~');

    private static uint ParseFourCc(string text) =>
        ((uint)text[0] << 24) | ((uint)text[1] << 16) | ((uint)text[2] << 8) | text[3];

}
