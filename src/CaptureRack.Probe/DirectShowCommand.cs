using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CaptureRack.DirectShow.Windows.Capture;

namespace CaptureRack.Probe;

internal static class DirectShowCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static int Run(string[] args)
    {
        if (args is ["inspect-worker", var requestPath, var resultPath])
            return Inspect(requestPath, resultPath);

        var json = args.Contains("--json");
        var trace = args.Contains("--trace");
        args = args.Where(arg => arg is not ("--json" or "--trace")).ToArray();
        var backend = new DirectShowBackend();

        try
        {
            if (args is ["list"])
            {
                var devices = backend.GetDevices();
                if (json)
                    PrintJson(devices);
                else
                    foreach (var device in devices)
                        Console.WriteLine($"{device.Index}: {device.Name}\n  {device.Id}");
                return 0;
            }

            if (args is ["probe", var deviceSelector])
            {
                var result = backend.Probe(deviceSelector);
                if (json)
                    PrintJson(result);
                else
                {
                    Console.WriteLine(result.Device.Name);
                    Console.WriteLine($"Driver dropped-frame counter: {(result.HasDroppedFrameCounter ? "available" : "unavailable")}");
                    foreach (var format in result.Formats)
                        Console.WriteLine($"  {format.Index}: {format.PinName} ({format.PinId}) · {format.Description}");
                }
                return 0;
            }

            if (args is ["controls", _] or ["controls", _, _, _])
            {
                DirectShowControlChange? change = null;
                if (args.Length == 4)
                {
                    if (args[3] == "auto")
                        change = new(args[2], 0, DirectShowControlMode.Auto);
                    else if (int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                        change = new(args[2], value, DirectShowControlMode.Manual);
                    else
                        return Usage(1);
                }
                var controls = backend.CameraControls(args[1], change);
                if (json)
                    PrintJson(controls);
                else if (controls.Length == 0)
                    Console.WriteLine("No supported camera controls reported.");
                else
                    foreach (var control in controls)
                    {
                        var values = control.Choices is { } choices ? string.Join(", ", choices) :
                            $"{control.Minimum}..{control.Maximum}, step {control.Step}";
                        Console.WriteLine($"{control.Id}: {control.Value} ({control.CurrentMode}); " +
                            $"{values}; default {control.DefaultValue?.ToString(CultureInfo.InvariantCulture) ?? "—"}; " +
                            $"{control.SupportedModes}; {control.Interface}");
                    }
                return 0;
            }

            var worker = args.FirstOrDefault() == "capture-worker";
            var negotiate = args.FirstOrDefault() == "negotiate";
            if (negotiate)
            {
                if (args.Length is not (3 or 4))
                    return Usage(1);
                args = ["negotiate", args[1], "0", .. args[2..]];
            }

            if (args.Length is 4 or 5 && (worker || negotiate || args[0] == "capture") &&
                int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var duration) &&
                duration >= (negotiate || worker ? 0 : 1) && duration <= 3600 &&
                int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var video))
            {
                int? audio = null;
                if (args.Length == 5)
                {
                    if (!int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                        return Usage(1);
                    audio = index;
                }
                var request = new DirectShowCaptureRequest(args[1], video, audio, TimeSpan.FromSeconds(duration));
                return worker ? Capture(backend, request, json, trace) : RunWorker(args, duration, json, trace);
            }

            return Usage(args is ["--help"] or ["-h"] ? 0 : 1);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Inspect(string requestPath, string resultPath)
    {
        try
        {
            var request = JsonSerializer.Deserialize<DirectShowInspectionRequest>(File.ReadAllText(requestPath))
                ?? throw new InvalidDataException("Could not read the DirectShow inspection request.");
            var backend = new DirectShowBackend();
            object result = request.Operation switch
            {
                DirectShowInspectionOperation.List => backend.GetDevices(),
                DirectShowInspectionOperation.Probe => backend.Probe(request.Device!),
                DirectShowInspectionOperation.Controls => backend.CameraControls(request.Device!, request.ControlChange),
                DirectShowInspectionOperation.DiagnosticDevice when request.Device is { } device =>
                    backend.DescribeDeviceForDiagnostics(device, request.IncludeControls),
                DirectShowInspectionOperation.DiagnosticCheck when request.Device is { } device && request.Video is { } video =>
                    backend.CheckCaptureForDiagnostics(new(device, video.Index, request.Audio?.Index,
                        TimeSpan.FromSeconds(request.Sample ? 2 : 0)) { ExpectedVideo = video, ExpectedAudio = request.Audio },
                        CancellationToken.None),
                DirectShowInspectionOperation.Negotiate or DirectShowInspectionOperation.Sample when
                    request.Device is { } device && request.Video is { } video =>
                    backend.Run(new DirectShowCaptureRequest(device, video.Index, request.Audio?.Index,
                        TimeSpan.FromSeconds(request.Operation == DirectShowInspectionOperation.Sample ? 2 : 0))
                    {
                        ExpectedVideo = video,
                        ExpectedAudio = request.Audio
                    }, _ => { }, CancellationToken.None),
                _ => throw new InvalidDataException("Invalid DirectShow inspection operation.")
            };
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunWorker(string[] args, int duration, bool json, bool trace)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the probe executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(DirectShowCommand).Assembly.Location);
        start.ArgumentList.Add("directshow");
        start.ArgumentList.Add("capture-worker");
        foreach (var arg in args.Skip(1))
            start.ArgumentList.Add(arg);
        if (json)
            start.ArgumentList.Add("--json");
        if (trace)
            start.ArgumentList.Add("--trace");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the DirectShow worker.");
            var elapsed = Stopwatch.StartNew();
            var deadline = TimeSpan.FromSeconds(duration + 12);
            var askedToStop = false;
            while (!process.WaitForExit(100))
            {
                if (!askedToStop && cancellation.IsCancellationRequested)
                {
                    askedToStop = true;
                    deadline = elapsed.Elapsed + TimeSpan.FromSeconds(3);
                    try { process.StandardInput.WriteLine("cancel"); }
                    catch (IOException) { }
                }

                if (elapsed.Elapsed < deadline)
                    continue;
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { return process.ExitCode; }
                Console.Error.WriteLine("Timed out in the DirectShow driver. Use --trace to locate the blocked call.");
                return cancellation.IsCancellationRequested ? 130 : 124;
            }
            return process.ExitCode;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static int Capture(DirectShowBackend backend, DirectShowCaptureRequest request, bool json, bool trace)
    {
        using var stop = new CancellationTokenSource();
        _ = Task.Run(() =>
        {
            if (Console.ReadLine() is null or "cancel")
            {
                try { stop.Cancel(); }
                catch (ObjectDisposedException) { } // The worker may have already completed.
            }
        });
        var result = backend.Run(request, _ => { }, stop.Token, trace ? Console.Error.WriteLine : null);
        if (json)
            PrintJson(result);
        else
        {
            Console.WriteLine(result.Device.Name);
            PrintStream(result.VideoFormat, result.Video);
            if (result.AudioFormat is { } format && result.Audio is { } stats)
                PrintStream(format, stats);
            Console.WriteLine($"Driver dropped frames: {result.DriverDroppedFrames?.ToString() ?? "unavailable"}");
            Console.WriteLine($"Stop: {result.StopReason}");
        }

        if (result.Error is { } error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        if (result.StopReason == "cancelled")
            return 130;
        if (result.ReceivedRequestedStreams || result.StopReason == "negotiated")
            return 0;
        Console.Error.WriteLine("At least one requested DirectShow stream delivered no samples.");
        return 2;
    }

    private static void PrintStream(DirectShowFormat format, DirectShowStreamStats stats)
    {
        Console.WriteLine($"{format.Kind}: {format.Description}");
        Console.WriteLine($"  {stats.Samples} samples, {stats.Bytes} bytes, " +
            $"{stats.TimestampedSamples} timestamped, {stats.DiscontinuitiesAfterFirst} discontinuities, " +
            $"{stats.TimestampRegressions} timestamp regressions");
        Console.WriteLine($"  Stream time (100 ns): {stats.FirstStart100ns} to {stats.LastEnd100ns ?? stats.LastStart100ns}");
    }

    private static void PrintJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));

    private static int Usage(int code)
    {
        Console.WriteLine("CaptureRack.Probe directshow list [--json]");
        Console.WriteLine("CaptureRack.Probe directshow probe <device-index-or-id> [--json]");
        Console.WriteLine("CaptureRack.Probe directshow controls <device-index-or-id> [<property> <value|auto>] [--json]");
        Console.WriteLine("CaptureRack.Probe directshow negotiate <device-index-or-id> <video-index> [audio-index] [--json] [--trace]");
        Console.WriteLine("CaptureRack.Probe directshow capture <device-index-or-id> <seconds> <video-index> [audio-index] [--json] [--trace]");
        Console.WriteLine("Use format indexes from probe. Capture validates sample delivery without writing media files.");
        return code;
    }
}
