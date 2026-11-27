using System.Reflection;
using CaptureRack.DeckLink;
using CaptureRack.QualityControl;
using CaptureRack.Recording;
using CaptureRack.SignalCapture;

namespace CaptureRack.Probe;

internal static class ValidationCommand
{
    public static int Run(string[] args)
    {
        try
        {
            if (args is ["identity"])
            {
                Console.WriteLine(typeof(ValidationCommand).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
                return 0;
            }
            if (args is ["--help"] or ["-h"])
            {
                Console.WriteLine("validate identity | crash-source <new-mkv> | av-clock <device-index> <seconds> | ddd-ramp <device-id> <new-prefix> <seconds>");
                return 0;
            }
            if (args is ["crash-source", var path])
            {
                // The validation supervisor terminates this child after observing a durable cluster.
                var recorder = new PreviewRecorder();
                bool started = false;
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                TestSignal.Run(TestSignalScenario.Normal, new PreviewFeed(), recorder, null, progress =>
                {
                    if (!started && progress.Stats.CanStartRecording)
                    {
                        recorder.Start(path, encoding: new RecordingEncoding(RecordingOutput.Ffv1));
                        started = true;
                    }
                }, timeout.Token);
                throw new IOException("Supervisor did not interrupt the recording at a checkpoint.");
            }
            if (args is ["av-clock", var indexText, var durationText] &&
                int.TryParse(indexText, out int index) && index >= 0 &&
                int.TryParse(durationText, out int durationSeconds) && durationSeconds is >= 1 and <= 28800)
            {
                var backend = new DeckLinkBackend();
                var scan = backend.ScanDevices();
                var device = scan.Devices.SingleOrDefault(d => d.Index == index)
                    ?? throw new IOException("Selected DeckLink device is unavailable.");
                long? firstOffset = null;
                long peak = 0, drift = 0, observations = 0;
                VideoMode? mode = null;
                void Observe(CaptureStats stats)
                {
                    if (stats.AudioTime is not long audio || stats.VideoTime is not long video) return;
                    long offset = audio - video;
                    firstOffset ??= offset;
                    drift = offset - firstOffset.Value;
                    peak = Math.Max(peak, Math.Abs(drift));
                    observations++;
                }
                var stats = backend.Run(new(index, null, TimeSpan.FromSeconds(durationSeconds)), update =>
                {
                    if (mode is not null && mode.Id != update.Mode.Id)
                        throw new IOException("Video mode changed during the clock measurement.");
                    mode = update.Mode;
                    Observe(update.Stats);
                }, CancellationToken.None);
                Observe(stats);
                RfCommand.WriteJson(new { device.Name, scan.ApiVersion, mode = mode?.Name,
                    stats.VideoFrames, stats.AudioSampleFrames, stats.TimestampRegressions,
                    stats.Error, stats.NoInputFrames, observations, timestampUnitsPerSecond = 48000,
                    relativeOffsetTicks = drift, peakAbsoluteRelativeOffsetTicks = peak });
                return stats.Verified && stats.NoInputFrames == 0 && observations >= 2 ? 0 : 1;
            }
            if (args is ["ddd-ramp", var deviceId, var prefix, var duration] &&
                int.TryParse(duration, out int seconds) && seconds is >= 1 and <= 28800)
            {
                var tools = QcReports.FindTools();
                var ffmpeg = tools.FfmpegPath ?? throw new IOException("FFmpeg is required.");
                var identity = WinUsbRfProbe.Probe(deviceId);
                if (identity.FirmwareRelease != "3.2.0")
                    throw new IOException("The hardware baseline requires Domesday Duplicator 3.2.");
                var driverPath = Path.Combine(Environment.SystemDirectory, "drivers", "winusb.sys");
                RfCommand.WriteJson(new { identity.Device.DriverService, driverVersion = File.Exists(driverPath)
                    ? System.Diagnostics.FileVersionInfo.GetVersionInfo(driverPath).FileVersion : null });
                var capture = new RfRecording(identity.Device, null, false, 0, false, RfFileFormat.Flac, TestRamp: true);
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                var qc = capture.Capture(prefix, ffmpeg, () => null, _ => { }, stop.Token)
                    ?? throw new IOException("Missing live ramp measurement.");
                qc.VerifyAndPublish();
                return 0;
            }
            Console.Error.WriteLine("validate crash-source <new-mkv> | validate av-clock <device-index> <seconds> | validate ddd-ramp <device-id> <new-prefix> <seconds:1-28800>");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
