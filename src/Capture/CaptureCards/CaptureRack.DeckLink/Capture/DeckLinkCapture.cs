using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CaptureRack.DeckLink;

public static class DeckLinkCapture
{
    private static readonly Guid IteratorClass = new("BA6C6F44-6DA5-4DCE-94AA-EE2D1372A676");
    private static readonly Guid ApiInformationClass = new("263CA19F-ED09-482E-9F9D-84005783A237");
    private const uint Ntsc = 0x6E747363;
    private const uint NoConversion = 0x6E6F6E65;
    private const uint VideoInputConnection = 0x7669636E;
    private const uint AudioInputConnection = 0x6169636E;
    private const uint DetectedInputMode = 0x6476696D;
    private const uint FormatDetection = 1;
    private const uint SupportsInputFormatDetection = 0x696E6664;

    public static string GetApiVersion()
    {
        var api = Activate<IDeckLinkApiInformation>(ApiInformationClass);
        try
        {
            Check(api.GetInt(0x76657273, out var version), "BMDDeckLinkAPIVersion");
            return $"{version >> 24}.{(version >> 16) & 255}.{(version >> 8) & 255}";
        }
        finally
        {
            Marshal.ReleaseComObject(api);
        }
    }

    public static IReadOnlyList<CaptureDevice> GetDevices(bool includeSources = false)
    {
        List<CaptureDevice> devices = [];
        VisitDevices((index, device) =>
        {
            if (device is IDeckLinkInput input)
                devices.Add(ReadDevice(index, device, input, includeSources));
            else
            {
                Check(device.GetDisplayName(out var name), "GetDisplayName");
                devices.Add(new(index, name, "Unknown", "Unknown", [], false, false, [], [], false));
            }
            return true;
        });
        return devices;
    }

    // The caller runs this on an MTA worker for the lifetime of the stream.
    public static CaptureStats Run(int index, uint? requestedMode, TimeSpan? duration,
        Action<VideoMode, CaptureStats> progress, CancellationToken cancellation,
        CaptureRouting? routing = null, PreviewFeed? preview = null,
        CapturePixelFormat pixelFormat = CapturePixelFormat.V210, string? recordingPath = null,
        CaptureAudioFormat audioFormat = CaptureAudioFormat.Pcm16, PreviewRecorder? previewRecorder = null)
    {
        if (recordingPath is not null && requestedMode is null)
            throw new InvalidOperationException("Choose a fixed input mode before recording.");
        if (previewRecorder is not null && (preview is null || requestedMode is null || recordingPath is not null))
            throw new ArgumentException("Live recording requires a fixed-mode preview.", nameof(previewRecorder));
        if (audioFormat is not (CaptureAudioFormat.Pcm16 or CaptureAudioFormat.Pcm32))
            throw new ArgumentOutOfRangeException(nameof(audioFormat));

        CaptureStats? result = null;
        VisitDevices((current, device) =>
        {
            if (current != index)
                return true;
            if (device is not IDeckLinkInput input)
                throw new InvalidOperationException($"Device {index} has no video input.");

            using var route = routing is { } selected ? new InputRoutingScope(device, selected) : null;
            var info = ReadDevice(index, device, input, includeSources: false, pixelFormat);
            result = RunCapture(device, input, info, requestedMode, duration, progress, cancellation, preview,
                pixelFormat, recordingPath, audioFormat, previewRecorder, routing?.Video ?? 0);
            return false;
        });
        return result ?? throw new InvalidOperationException($"DeckLink device {index} was not found.");
    }

    // Read only the currently routed input. Do not switch routes or start capture to determine the shape.
    public static uint? GetLockedVideoInputMode(CaptureDevice selected, uint connection)
    {
        const uint signalLocked = 0x7669736C; // visl
        const uint persistentId = 0x70656964; // peid
        uint? mode = null;
        VisitDevices((index, device) =>
        {
            if (index != selected.Index)
                return true;

            if (selected.PersistentId is { } id && (device is not IDeckLinkProfileAttributes attributes ||
                attributes.GetInt(persistentId, out var currentId) != 0 || currentId != id))
                return false;
            if (device is IDeckLinkConfiguration configuration &&
                configuration.GetInt(VideoInputConnection, out var currentConnection) == 0 && currentConnection == connection &&
                device is IDeckLinkStatus status && status.GetFlag(signalLocked, out var locked) == 0 && locked != 0 &&
                status.GetInt(DetectedInputMode, out var detected) == 0)
                mode = (uint)detected;
            return false;
        });
        return mode;
    }

    private static void VisitDevices(Func<int, IDeckLink, bool> visit)
    {
        var iterator = Activate<IDeckLinkIterator>(IteratorClass);
        try
        {
            for (var index = 0; ; index++)
            {
                Check(iterator.Next(out var device), "IDeckLinkIterator.Next");
                if (device is null)
                    break;

                try
                {
                    if (!visit(index, device))
                        break;
                }
                finally
                {
                    // Casts to the device interfaces share this RCW.
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(iterator);
        }
    }

    private static CaptureDevice ReadDevice(int index, IDeckLink device, IDeckLinkInput input, bool includeSources,
        CapturePixelFormat pixelFormat = CapturePixelFormat.V210)
    {
        Check(device.GetDisplayName(out var name), "GetDisplayName");
        uint video = 0;
        uint audio = 0;
        if (device is IDeckLinkConfiguration configuration)
        {
            if (configuration.GetInt(VideoInputConnection, out var videoValue) == 0)
                video = (uint)videoValue;
            if (configuration.GetInt(AudioInputConnection, out var audioValue) == 0)
                audio = (uint)audioValue;
        }

        var attributes = device as IDeckLinkProfileAttributes;
        var supportsDetection = attributes is not null &&
            attributes.GetFlag(SupportsInputFormatDetection, out var supported) == 0 && supported != 0;
        List<VideoInputSource> videoSources = [];
        List<AudioInputSource> audioSources = [];
        var currentModes = ReadModes(input, video, pixelFormat);
        if (includeSources && attributes is not null)
        {
            if (attributes.GetInt(VideoInputConnection, out var videoMask) == 0)
            {
                foreach (var id in Connections(videoMask))
                {
                    var tenBit = id == video ? currentModes : ReadModes(input, id, CapturePixelFormat.V210);
                    videoSources.Add(new(id, DescribeVideo(id), tenBit, ReadModes(input, id, CapturePixelFormat.Uyvy)));
                }
            }

            if (attributes.GetInt(AudioInputConnection, out var audioMask) == 0)
                audioSources.AddRange(Connections(audioMask).Select(id => new AudioInputSource(id, DescribeAudio(id))));
        }

        const uint persistentIdAttribute = 0x70656964;
        long? persistentId = attributes is not null && attributes.GetInt(persistentIdAttribute, out var idValue) == 0
            ? idValue : null;
        return new(index, name, DescribeVideo(video), DescribeAudio(audio), currentModes, true, supportsDetection,
            videoSources, audioSources, device is IDeckLinkEncoderInput) { PersistentId = persistentId };
    }

    private static IEnumerable<uint> Connections(long mask)
    {
        for (var bit = 0; bit < 32; bit++)
        {
            var flag = 1u << bit;
            if ((mask & flag) != 0)
                yield return flag;
        }
    }

    private static string DescribeVideo(uint video) => video switch
    {
        1 => "SDI",
        2 => "HDMI",
        4 => "Optical SDI",
        8 => "Component",
        16 => "Composite",
        32 => "S-Video",
        64 => "Ethernet",
        128 => "Optical Ethernet",
        256 => "Internal",
        _ => $"0x{video:X}"
    };

    private static string DescribeAudio(uint audio) => audio switch
    {
        1 => "Embedded",
        2 => "AES/EBU",
        4 => "Analog",
        8 => "Analog XLR",
        16 => "Analog RCA",
        32 => "Microphone",
        64 => "Headphones",
        _ => $"0x{audio:X}"
    };

    private static List<VideoMode> ReadModes(IDeckLinkInput input, uint connection, CapturePixelFormat format)
    {
        Check(input.GetDisplayModeIterator(out var iterator), "GetDisplayModeIterator");
        List<VideoMode> modes = [];
        try
        {
            while (true)
            {
                Check(iterator.Next(out var mode), "IDeckLinkDisplayModeIterator.Next");
                if (mode is null)
                    return modes;

                try
                {
                    var id = mode.GetDisplayMode();
                    Check(input.DoesSupportVideoMode(connection, id, (uint)format, NoConversion, 0,
                        out _, out var supported), "DoesSupportVideoMode");
                    if (supported == 0)
                        continue;

                    Check(mode.GetName(out var name), "GetName");
                    Check(mode.GetFrameRate(out var duration, out var scale), "GetFrameRate");
                    modes.Add(new(id, name, mode.GetWidth(), mode.GetHeight(), duration, scale,
                        mode.GetFieldDominance()));
                }
                finally
                {
                    Marshal.ReleaseComObject(mode);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(iterator);
        }
    }

    private static CaptureStats RunCapture(IDeckLink device, IDeckLinkInput input, CaptureDevice info,
        uint? requestedMode, TimeSpan? duration, Action<VideoMode, CaptureStats> progress, CancellationToken stop,
        PreviewFeed? preview, CapturePixelFormat format, string? recordingPath, CaptureAudioFormat audioFormat,
        PreviewRecorder? previewRecorder, uint videoConnection)
    {
        var modes = info.Modes;
        if (modes.Count == 0)
            throw new InvalidOperationException("No capture modes for the selected video format and input.");

        var detectedMode = device is IDeckLinkStatus status &&
            status.GetInt(DetectedInputMode, out var detected) == 0 ? (uint)detected : 0;
        var mode = modes.FirstOrDefault(mode => mode.Id == (requestedMode ?? detectedMode));
        var useDetection = requestedMode is null && info.SupportsFormatDetection;

        if (requestedMode is not null && mode is null)
            throw new InvalidOperationException($"Mode '{FormatFourCc(requestedMode.Value)}' is unavailable for the current input.");
        if (mode is null && !useDetection)
            throw new InvalidOperationException("Input mode was not detected. Pass a mode code from the list command.");

        mode ??= modes.FirstOrDefault(mode => mode.Id == Ntsc) ?? modes[0];
        var stats = Capture(input, mode, duration, useDetection, progress, stop, preview, format, recordingPath, audioFormat, previewRecorder, videoConnection);
        if (useDetection && stats.Error is null && stats.DetectedMode != 0 &&
            stats.DetectedMode != mode.Id && !stop.IsCancellationRequested)
        {
            var next = modes.FirstOrDefault(candidate => candidate.Id == stats.DetectedMode)
                ?? throw new InvalidOperationException($"Detected mode '{FormatFourCc(stats.DetectedMode)}' is unavailable in the selected pixel format.");
            stats = Capture(input, next, duration, false, progress, stop, preview, format, recordingPath, audioFormat, previewRecorder, videoConnection);
        }

        return stats;
    }

    private static CaptureStats Capture(IDeckLinkInput input, VideoMode mode, TimeSpan? duration,
        bool detect, Action<VideoMode, CaptureStats> progress, CancellationToken stop, PreviewFeed? preview,
        CapturePixelFormat format, string? recordingPath, CaptureAudioFormat audioFormat,
        PreviewRecorder? previewRecorder, uint videoConnection)
    {
        var empty = new CaptureStats();
        if (stop.IsCancellationRequested)
            return empty;

        using var recording = recordingPath is null ? null : new MovRecording(recordingPath, mode, format, audioFormat);
        var lumaScaleBits = preview is null ? 0 : ReadMaximumVideoBits(input, videoConnection, mode.Id, format);
        var callback = new CaptureCallback(preview, format, audioFormat, mode, recording);
        Check(input.SetCallback(callback), "SetCallback");
        try
        {
            Check(input.EnableVideoInput(mode.Id, (uint)format, detect ? FormatDetection : 0), "EnableVideoInput");
            try
            {
                var inputAudioFormat = audioFormat;
                if (preview is not null && recordingPath is null && audioFormat == CaptureAudioFormat.Pcm16 &&
                    input.EnableAudioInput(CaptureAudio.SampleRate, (uint)CaptureAudioFormat.Pcm32, CaptureAudio.Channels) >= 0)
                {
                    inputAudioFormat = CaptureAudioFormat.Pcm32;
                }
                else
                {
                    Check(input.EnableAudioInput(CaptureAudio.SampleRate, (uint)audioFormat, CaptureAudio.Channels),
                        "EnableAudioInput");
                }
                try
                {
                    if (inputAudioFormat != audioFormat)
                        callback.UseMonitoringAudioFormat(inputAudioFormat);
                    Check(input.StartStreams(), "StartStreams");
                    try
                    {
                        previewRecorder?.Connect(callback);
                        var clock = Stopwatch.StartNew();
                        var nextReport = TimeSpan.Zero;
                        while (!stop.IsCancellationRequested)
                        {
                            recording?.CheckWriter();
                            preview?.ConvertLatest(lumaScaleBits);
                            var report = clock.Elapsed >= nextReport;
                            var snapshot = callback.Snapshot(report);
                            if (report)
                            {
                                progress(mode, snapshot);
                                nextReport = clock.Elapsed + TimeSpan.FromMilliseconds(250);
                            }
                            if (snapshot.Error is not null || (detect && snapshot.DetectedMode != 0 &&
                                snapshot.DetectedMode != mode.Id))
                                break;

                            var interval = TimeSpan.FromMilliseconds(preview is null ? 250 : 16);
                            if (duration is { } limit)
                            {
                                var remaining = limit - clock.Elapsed;
                                if (remaining <= TimeSpan.Zero)
                                    break;
                                interval = remaining < interval ? remaining : interval;
                            }

                            stop.WaitHandle.WaitOne(interval);
                        }
                    }
                    finally
                    {
                        try
                        {
                            Check(input.StopStreams(), "StopStreams");
                        }
                        finally
                        {
                            previewRecorder?.Disconnect();
                        }
                    }
                }
                finally
                {
                    Check(input.DisableAudioInput(), "DisableAudioInput");
                }
            }
            finally
            {
                Check(input.DisableVideoInput(), "DisableVideoInput");
            }
        }
        finally
        {
            Check(input.SetCallback(null), "Clear callback");
        }

        var result = callback.Snapshot(publishMonitor: true);
        if (recording is not null && result.Error is null)
            result.Recording = recording.Finish();
        return result;
    }

    private static int ReadMaximumVideoBits(IDeckLinkInput input, uint connection, uint mode, CapturePixelFormat activeFormat)
    {
        bool Supports(uint pixelFormat) =>
            input.DoesSupportVideoMode(connection, mode, pixelFormat, NoConversion, 0, out var actual, out var supported) >= 0 &&
            supported != 0 && actual == mode;

        const uint rgb12 = 0x52313242;   // R12B
        const uint rgb12Le = 0x5231324C; // R12L
        const uint rgb10 = 0x72323130;   // r210
        const uint rgb10X = 0x52313062;  // R10b
        const uint rgb10XLe = 0x5231306C; // R10l
        if (Supports(rgb12) || Supports(rgb12Le))
            return 12;
        if (activeFormat == CapturePixelFormat.V210 || Supports((uint)CapturePixelFormat.V210) ||
            Supports(rgb10) || Supports(rgb10X) || Supports(rgb10XLe))
            return 10;

        return 8;
    }

    private static T Activate<T>(Guid clsid) where T : class =>
        (T)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;

    internal static void Check(int result, string operation)
    {
        if (result < 0)
            throw new COMException($"{operation} failed (0x{result:X8}).", result);
    }

    public static double EstimateRecordingBytesPerSecond(VideoMode mode, CapturePixelFormat video, CaptureAudioFormat audio) =>
        (double)MovRecording.StoredRowBytes(mode.Width, video) * mode.Height * mode.FramesPerSecond +
        CaptureAudio.SampleRate * CaptureAudio.BytesPerFrame(audio);

    public static string FormatFourCc(uint value) =>
        Encoding.ASCII.GetString([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private sealed class InputRoutingScope : IDisposable
    {
        private readonly IDeckLinkConfiguration configuration;
        private readonly long originalVideo;
        private readonly long originalAudio;
        private bool videoChanged;
        private bool audioChanged;

        public InputRoutingScope(IDeckLink device, CaptureRouting routing)
        {
            configuration = device as IDeckLinkConfiguration
                ?? throw new InvalidOperationException("This DeckLink device does not expose input routing.");
            Check(configuration.GetInt(VideoInputConnection, out originalVideo), "Read previous video input");
            Check(configuration.GetInt(AudioInputConnection, out originalAudio), "Read previous audio input");
            try
            {
                Check(configuration.SetInt(VideoInputConnection, routing.Video), "Select video input");
                videoChanged = true;
                Check(configuration.SetInt(AudioInputConnection, routing.Audio), "Select audio input");
                audioChanged = true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                // Video routing can affect the audio routing. Restore it first.
                if (videoChanged)
                    Check(configuration.SetInt(VideoInputConnection, originalVideo), "Restore previous video input");
            }
            finally
            {
                if (videoChanged || audioChanged)
                    Check(configuration.SetInt(AudioInputConnection, originalAudio), "Restore previous audio input");
            }
        }
    }
}

public sealed record CaptureDevice(int Index, string Name, string VideoInput, string AudioInput,
    IReadOnlyList<VideoMode> Modes, bool HasVideoInput, bool SupportsFormatDetection,
    IReadOnlyList<VideoInputSource> VideoSources, IReadOnlyList<AudioInputSource> AudioSources,
    bool HasHardwareEncoderInput)
{
    public long? PersistentId { get; init; }
}

public sealed record VideoInputSource(uint Id, string Name, IReadOnlyList<VideoMode> TenBitModes,
    IReadOnlyList<VideoMode> EightBitModes)
{
    public IReadOnlyList<VideoMode> ModesFor(CapturePixelFormat format) =>
        format == CapturePixelFormat.V210 ? TenBitModes : EightBitModes;
}

public sealed record AudioInputSource(uint Id, string Name);

public sealed record CaptureRouting(uint Video, uint Audio);

public enum CapturePixelFormat : uint { V210 = 0x76323130, Uyvy = 0x32767579 }

public enum CaptureAudioFormat : uint { Pcm16 = 16, Pcm32 = 32 }

internal static class CaptureAudio
{
    public const uint SampleRate = 48000;
    public const uint Channels = 2;
    public static int BytesPerFrame(CaptureAudioFormat format) => (int)((uint)format / 8 * Channels);
}

public sealed record VideoMode(uint Id, string Name, int Width, int Height, long FrameDuration, long TimeScale,
    uint FieldDominance)
{
    public double FramesPerSecond => (double)TimeScale / FrameDuration;
    public string Description => $"{Name}  ·  {Width} × {Height}  ·  {FramesPerSecond:0.###} fps";
}

public enum VideoSignal { Unknown, NoInput, Present }
