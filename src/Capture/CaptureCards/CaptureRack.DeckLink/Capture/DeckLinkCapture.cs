using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CaptureRack.DeckLink;

public static partial class DeckLinkCapture
{
    private static readonly Guid IteratorClass = new("BA6C6F44-6DA5-4DCE-94AA-EE2D1372A676");
    private static readonly Guid ApiInformationClass = new("263CA19F-ED09-482E-9F9D-84005783A237");
    private const uint Ntsc = 0x6E747363;
    private const uint NoConversion = 0x6E6F6E65;
    private const uint VideoInputConnection = 0x7669636E;
    private const uint AudioInputConnection = 0x6169636E;
    private const uint VideoInputSignalLocked = 0x7669736C; // visl
    private const uint VancSourceLine1Mapping = 0x76736C31; // vsl1, followed by vsl2 and vsl3
    private const int VancMappingCount = 3;
    private const uint AnalogVideoInputFlags = 0x61766966; // avif
    private const uint AnalogAudioConsumerLevels = 0x6161636C; // aacl
    private const uint AnalogAudioInputScaleChannel1 = 0x61697331; // ais1
    private const uint AnalogAudioInputScaleChannel2 = 0x61697332; // ais2
    private const long AnalogFlagCompositeSetup75 = 1L << 0;
    private const long AnalogFlagComponentBetacamLevels = 1L << 1;
    private const uint DetectedInputMode = 0x6476696D;
    private const uint FormatDetection = 1;
    private const uint SupportsInputFormatDetection = 0x696E6664;
    private const uint MaximumAudioChannelsAttribute = 0x6D616368; // mach

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
        CaptureAudioFormat audioFormat = CaptureAudioFormat.Pcm16, PreviewRecorder? previewRecorder = null,
        AnalogInputOptions? analogOptions = null, LocalAudioMonitor? localAudioMonitor = null,
        uint audioChannels = CaptureAudio.Channels)
    {
        if (recordingPath is not null && requestedMode is null)
            throw new InvalidOperationException("Choose a fixed input mode before recording.");
        if (previewRecorder is not null && (preview is null || recordingPath is not null))
            throw new ArgumentException("Live recording requires a preview.", nameof(previewRecorder));
        if (audioFormat is not (CaptureAudioFormat.Pcm16 or CaptureAudioFormat.Pcm32))
            throw new ArgumentOutOfRangeException(nameof(audioFormat));
        if (!CaptureAudio.IsValidChannelCount(audioChannels))
            throw new ArgumentOutOfRangeException(nameof(audioChannels), "DeckLink audio channels must be 2, 8, or 16.");

        CaptureStats? result = null;
        VisitDevices((current, device) =>
        {
            if (current != index)
                return true;
            if (device is not IDeckLinkInput input)
                throw new InvalidOperationException($"Device {index} has no video input.");

            using var route = routing is { } selected ? new InputRoutingScope(device, selected, analogOptions) : null;
            var info = ReadDevice(index, device, input, includeSources: false, pixelFormat);
            var activeAudioInput = routing?.Audio ?? info.ActiveAudioInputId;
            var effectiveChannels = CaptureAudio.NormalizeChannels(
                audioChannels, info.MaxAudioChannels, activeAudioInput);
            result = RunCapture(device, input, info, requestedMode, duration, progress, cancellation, preview,
                pixelFormat, recordingPath, audioFormat, previewRecorder, localAudioMonitor, effectiveChannels);
            return false;
        });
        return result ?? throw new InvalidOperationException($"DeckLink device {index} was not found.");
    }

    // Read only the currently routed input. Do not switch routes or start capture to determine the shape.
    public static uint? GetLockedVideoInputMode(CaptureDevice selected, uint connection)
    {
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
                device is IDeckLinkStatus status && status.GetFlag(VideoInputSignalLocked, out var locked) == 0 && locked != 0 &&
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
        var supportsVancInputMapping = false;
        var supportsAnalogVideoFlags = false;
        var supportsConsumerAudioLevels = false;
        var supportsAnalogAudioGain = false;
        if (device is IDeckLinkConfiguration configuration)
        {
            if (configuration.GetInt(VideoInputConnection, out var videoValue) == 0)
                video = (uint)videoValue;
            if (configuration.GetInt(AudioInputConnection, out var audioValue) == 0)
                audio = (uint)audioValue;
            if (includeSources)
            {
                supportsVancInputMapping = CanReadVancMappings(configuration);
                supportsAnalogVideoFlags = configuration.GetInt(AnalogVideoInputFlags, out _) == 0;
                supportsConsumerAudioLevels = configuration.GetFlag(AnalogAudioConsumerLevels, out _) == 0;
                supportsAnalogAudioGain = configuration.GetFloat(AnalogAudioInputScaleChannel1, out _) == 0;
            }
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
        var maxAudioChannels = attributes is not null && attributes.GetInt(MaximumAudioChannelsAttribute, out var maxCh) == 0 && maxCh > 0
            ? (int)Math.Min(maxCh, AudioMonitorRoute.MaxSupportedChannels)
            : (int)CaptureAudio.Channels;
        return new(index, name, DescribeVideo(video), DescribeAudio(audio), currentModes, true, supportsDetection,
            videoSources, audioSources, device is IDeckLinkEncoderInput)
        {
            PersistentId = persistentId,
            ActiveAudioInputId = audio,
            MaxAudioChannels = maxAudioChannels,
            SupportsVancInputMapping = supportsVancInputMapping,
            AnalogCapabilities = new AnalogInputCapabilities(
                supportsAnalogVideoFlags, supportsConsumerAudioLevels, supportsAnalogAudioGain)
        };
    }

    private static bool CanReadVancMappings(IDeckLinkConfiguration configuration)
    {
        for (uint index = 0; index < VancMappingCount; index++)
        {
            if (configuration.GetInt(VancSourceLine1Mapping + index, out _) != 0)
                return false;
        }

        return true;
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
        PreviewRecorder? previewRecorder, LocalAudioMonitor? localAudioMonitor, uint audioChannels)
    {
        var modes = info.Modes;
        if (modes.Count == 0)
            throw new InvalidOperationException("No capture modes for the selected video format and input.");

        var status = device as IDeckLinkStatus;
        var detectedMode = status is not null &&
            status.GetInt(DetectedInputMode, out var detected) == 0 ? (uint)detected : 0;
        var mode = modes.FirstOrDefault(mode => mode.Id == (requestedMode ?? detectedMode));
        var useDetection = requestedMode is null && info.SupportsFormatDetection;

        if (requestedMode is not null && mode is null)
            throw new InvalidOperationException($"Mode '{FormatFourCc(requestedMode.Value)}' is unavailable for the current input.");
        if (mode is null && !useDetection)
            throw new InvalidOperationException("Input mode was not detected. Pass a mode code from the list command.");

        mode ??= modes.FirstOrDefault(mode => mode.Id == Ntsc) ?? modes[0];
        var detect = useDetection;
        while (true)
        {
            var stats = Capture(input, status, mode, duration, detect, progress, stop, preview, format, recordingPath,
                audioFormat, previewRecorder, automaticMode: requestedMode is null, localAudioMonitor, audioChannels);
            if (stats.Error is not null || stats.NextMode is not { } next || stop.IsCancellationRequested)
                return stats;

            mode = modes.FirstOrDefault(candidate => candidate.Id == next)
                ?? throw new InvalidOperationException($"Detected mode '{FormatFourCc(next)}' is unavailable in the selected pixel format.");
            // The live UI keeps tracking input changes. The duration-based probe runs its resolved mode.
            detect = useDetection && previewRecorder is not null;
        }
    }

    private static CaptureStats Capture(IDeckLinkInput input, IDeckLinkStatus? status, VideoMode mode, TimeSpan? duration,
        bool detect, Action<VideoMode, CaptureStats> progress, CancellationToken stop, PreviewFeed? preview,
        CapturePixelFormat format, string? recordingPath, CaptureAudioFormat audioFormat,
        PreviewRecorder? previewRecorder, bool automaticMode, LocalAudioMonitor? localAudioMonitor, uint audioChannels)
    {
        var empty = new CaptureStats();
        var readAudio = previewRecorder?.ReadAudioFromUi != true;
        if (stop.IsCancellationRequested)
            return empty;

        var lumaScaleBits = format == CapturePixelFormat.V210 ? 10 : 8;
        Check(input.EnableVideoInput(mode.Id, (uint)format, detect ? FormatDetection : 0), "EnableVideoInput");
        try
        {
            var (inputAudioFormat, enabledChannels) = EnableAudioInputWithFallback(
                input, audioFormat, audioChannels, preferPcm32Preview: preview is not null && recordingPath is null);
            try
            {
                using var recording = recordingPath is null ? null : new MovRecording(recordingPath, mode, format, audioFormat, audioChannels: enabledChannels);
                var callback = new CaptureCallback(preview, format, audioFormat, mode, recording, automaticMode, localAudioMonitor, audioChannels: enabledChannels);
                if (inputAudioFormat != audioFormat)
                    callback.UseMonitoringAudioFormat(inputAudioFormat);
                Check(input.SetCallback(callback), "SetCallback");
                try
                {
                    Check(input.StartStreams(), "StartStreams");
                    try
                    {
                        previewRecorder?.Connect(callback);
                        var clock = Stopwatch.StartNew();
                        var nextReport = TimeSpan.Zero;
                        var nextLockPoll = TimeSpan.Zero;
                        var fieldInterval = mode.PreviewFieldInterval;
                        while (!stop.IsCancellationRequested)
                        {
                            recording?.CheckWriter();
                            preview?.ConvertLatest(lumaScaleBits, fieldInterval);
                            var elapsed = clock.Elapsed;
                            if (preview is not null && status is not null && elapsed >= nextLockPoll)
                            {
                                if (callback.IsWaitingForVideoFrames() &&
                                    status.GetFlag(VideoInputSignalLocked, out var locked) == 0)
                                    callback.ObserveHardwareLock(locked != 0);
                                nextLockPoll = elapsed + TimeSpan.FromMilliseconds(50);
                            }
                            var report = elapsed >= nextReport;
                            var switchMode = detect && callback.TryBeginModeChange();
                            var snapshot = callback.Snapshot(report || switchMode, readAudio: readAudio);
                            if (report || switchMode)
                            {
                                progress(mode, snapshot);
                                nextReport = clock.Elapsed + TimeSpan.FromMilliseconds(250);
                            }
                            if (snapshot.Error is not null || switchMode)
                                break;

                            var interval = TimeSpan.FromMilliseconds(preview is null ? 250 :
                                mode.IsInterlaced && preview.DeinterlaceMode == PreviewDeinterlaceMode.BobWeave2x ? 4 : 16);
                            if (duration is { } limit)
                            {
                                var remaining = limit - clock.Elapsed;
                                if (remaining <= TimeSpan.Zero)
                                    break;
                                interval = remaining < interval ? remaining : interval;
                            }

                            if (preview is null)
                                stop.WaitHandle.WaitOne(interval);
                            else if (!stop.IsCancellationRequested)
                                preview.WaitForInput(interval);
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
                    Check(input.SetCallback(null), "Clear callback");
                }

                var result = callback.Snapshot(publishMonitor: true, readAudio: readAudio);
                if (recording is not null)
                    result.Recording = recording.Finish();
                return result;
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

    private static (CaptureAudioFormat Format, uint Channels) EnableAudioInputWithFallback(
        IDeckLinkInput input, CaptureAudioFormat requestedFormat, uint requestedChannels, bool preferPcm32Preview)
    {
        var candidates = CaptureAudio.ChannelFallbackOrder(requestedChannels);
        var lastHr = 0;
        foreach (var ch in candidates)
        {
            if (preferPcm32Preview && requestedFormat == CaptureAudioFormat.Pcm16 &&
                input.EnableAudioInput(CaptureAudio.SampleRate, (uint)CaptureAudioFormat.Pcm32, ch) >= 0)
                return (CaptureAudioFormat.Pcm32, ch);

            lastHr = input.EnableAudioInput(CaptureAudio.SampleRate, (uint)requestedFormat, ch);
            if (lastHr >= 0)
                return (requestedFormat, ch);
        }

        Check(lastHr, "EnableAudioInput");
        return (requestedFormat, CaptureAudio.Channels);
    }

    private static T Activate<T>(Guid clsid) where T : class =>
        (T)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;

    internal static void Check(int result, string operation)
    {
        if (result < 0)
            throw new COMException($"{operation} failed (0x{result:X8}).", result);
    }

    public static double EstimateRecordingBytesPerSecond(VideoMode mode, CapturePixelFormat video,
        CaptureAudioFormat audio, uint audioChannels = CaptureAudio.Channels) =>
        (double)MovRecording.StoredRowBytes(mode.Width, video) * mode.Height * mode.FramesPerSecond +
        CaptureAudio.SampleRate * CaptureAudio.BytesPerFrame(audio, audioChannels);

    public static string FormatFourCc(uint value) =>
        Encoding.ASCII.GetString([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private sealed class VancMappingScope : IDisposable
    {
        private readonly IDeckLinkConfiguration configuration;
        private readonly long[] original = new long[VancMappingCount];
        private readonly long[] current = new long[VancMappingCount];

        public VancMappingScope(IDeckLinkConfiguration configuration)
        {
            this.configuration = configuration;
            for (var index = 0; index < VancMappingCount; index++)
            {
                Check(configuration.GetInt(VancSourceLine1Mapping + (uint)index, out original[index]),
                    $"Read VANC picture line {index + 1}");
                current[index] = original[index];
            }
        }

        public void Apply(ReadOnlySpan<int> lines)
        {
            for (var index = 0; index < VancMappingCount; index++)
            {
                // Enable the first line before dependent lines, or disable it last.
                var destination = DestinationIndex(index, lines[0]);
                Check(SetLine(destination, lines[destination]), $"Set VANC picture line {destination + 1}");
            }
        }

        public void Dispose()
        {
            var firstFailure = 0;
            for (var index = 0; index < VancMappingCount; index++)
            {
                var destination = DestinationIndex(index, original[0]);
                var result = SetLine(destination, original[destination]);
                if (firstFailure == 0 && result < 0)
                    firstFailure = result;
            }

            Check(firstFailure, "Restore previous VANC input mapping");
        }

        private int SetLine(int destination, long value)
        {
            if (current[destination] == value)
                return 0;
            var result = configuration.SetInt(VancSourceLine1Mapping + (uint)destination, value);
            if (result >= 0)
                current[destination] = value;
            return result;
        }

        private static int DestinationIndex(int index, long firstLine) =>
            firstLine == 0 ? VancMappingCount - 1 - index : index;
    }

    private sealed class InputRoutingScope : IDisposable
    {
        private readonly IDeckLinkConfiguration configuration;
        private readonly long originalVideo;
        private readonly long originalAudio;
        private VancMappingScope? vancMappings;
        private long originalAnalogFlags;
        private int originalConsumerAudio;
        private double originalGainLeft;
        private double originalGainRight;
        private bool videoChanged;
        private bool audioChanged;
        private bool analogFlagsChanged;
        private bool consumerAudioChanged;
        private bool gainLeftChanged;
        private bool gainRightChanged;

        public InputRoutingScope(IDeckLink device, CaptureRouting routing, AnalogInputOptions? analog)
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

                if (analog is not null)
                    ApplyAnalogOptions(routing, analog);
                if (routing.VancInput is { } vanc && (vanc.Enabled || CanReadVancMappings(configuration)))
                {
                    vancMappings = new(configuration);
                    vanc = vanc.Normalized();
                    vancMappings.Apply(vanc.Enabled ? [vanc.Line1, vanc.Line2, vanc.Line3] : [0, 0, 0]);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void ApplyAnalogOptions(CaptureRouting routing, AnalogInputOptions analog)
        {
            // Analog video flags (IRE 7.5 setup for Composite/S-Video, Betacam levels for Component).
            var isCompositeOrSVideo = routing.Video is 16 or 32;
            var isComponent = routing.Video == 8;
            if (((isCompositeOrSVideo && analog.NtscIre != AnalogNtscIreMode.Driver) ||
                (isComponent && analog.ComponentLevel != AnalogComponentLevelMode.Driver)) &&
                configuration.GetInt(AnalogVideoInputFlags, out originalAnalogFlags) == 0)
            {
                var updated = originalAnalogFlags;
                if (isCompositeOrSVideo && analog.NtscIre != AnalogNtscIreMode.Driver)
                {
                    updated = analog.NtscIre == AnalogNtscIreMode.Ire75
                        ? updated | AnalogFlagCompositeSetup75
                        : updated & ~AnalogFlagCompositeSetup75;
                }
                if (isComponent && analog.ComponentLevel != AnalogComponentLevelMode.Driver)
                {
                    updated = analog.ComponentLevel == AnalogComponentLevelMode.Betacam
                        ? updated | AnalogFlagComponentBetacamLevels
                        : updated & ~AnalogFlagComponentBetacamLevels;
                }
                if (updated != originalAnalogFlags && configuration.SetInt(AnalogVideoInputFlags, updated) == 0)
                    analogFlagsChanged = true;
            }

            // Analog audio consumer/pro level & per-channel hardware gain (Analog / XLR / RCA).
            var isAnalogAudio = routing.Audio is 4 or 8 or 16;
            if (!isAnalogAudio)
                return;

            if (analog.AudioLevel != AnalogAudioLevelMode.Driver &&
                configuration.GetFlag(AnalogAudioConsumerLevels, out originalConsumerAudio) == 0)
            {
                var consumerFlag = analog.AudioLevel == AnalogAudioLevelMode.Consumer ? 1 : 0;
                if (consumerFlag != originalConsumerAudio &&
                    configuration.SetFlag(AnalogAudioConsumerLevels, consumerFlag) == 0)
                    consumerAudioChanged = true;
            }

            // Hardware gain scaling applies when consumer max-gain mode is not forced.
            if (analog.AudioLevel != AnalogAudioLevelMode.Consumer)
            {
                if (analog.GainLeftDb is { } leftDb &&
                    configuration.GetFloat(AnalogAudioInputScaleChannel1, out originalGainLeft) == 0)
                {
                    var clamped = Math.Clamp(leftDb, -12.0, 12.0);
                    if (configuration.SetFloat(AnalogAudioInputScaleChannel1, clamped) == 0)
                        gainLeftChanged = true;
                }
                if (analog.GainRightDb is { } rightDb &&
                    configuration.GetFloat(AnalogAudioInputScaleChannel2, out originalGainRight) == 0)
                {
                    var clamped = Math.Clamp(rightDb, -12.0, 12.0);
                    if (configuration.SetFloat(AnalogAudioInputScaleChannel2, clamped) == 0)
                        gainRightChanged = true;
                }
            }
        }

        public void Dispose()
        {
            try
            {
                vancMappings?.Dispose();
            }
            finally
            {
                try
                {
                    if (gainLeftChanged)
                        configuration.SetFloat(AnalogAudioInputScaleChannel1, originalGainLeft);
                    if (gainRightChanged)
                        configuration.SetFloat(AnalogAudioInputScaleChannel2, originalGainRight);
                    if (consumerAudioChanged)
                        configuration.SetFlag(AnalogAudioConsumerLevels, originalConsumerAudio);
                    if (analogFlagsChanged)
                        configuration.SetInt(AnalogVideoInputFlags, originalAnalogFlags);
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
}

public sealed record AnalogInputCapabilities(
    bool SupportsAnalogVideoFlags,
    bool SupportsConsumerAudioLevels,
    bool SupportsAnalogAudioGain);

public enum AnalogNtscIreMode { Driver, Ire0, Ire75 }

public enum AnalogComponentLevelMode { Driver, Smpte, Betacam }

public enum AnalogAudioLevelMode { Driver, Pro, Consumer }

public sealed record AnalogInputOptions
{
    public AnalogNtscIreMode NtscIre { get; init; } = AnalogNtscIreMode.Driver;
    public AnalogComponentLevelMode ComponentLevel { get; init; } = AnalogComponentLevelMode.Driver;
    public AnalogAudioLevelMode AudioLevel { get; init; } = AnalogAudioLevelMode.Driver;
    public double? GainLeftDb { get; init; }
    public double? GainRightDb { get; init; }
}

public sealed record CaptureDevice(int Index, string Name, string VideoInput, string AudioInput,
    IReadOnlyList<VideoMode> Modes, bool HasVideoInput, bool SupportsFormatDetection,
    IReadOnlyList<VideoInputSource> VideoSources, IReadOnlyList<AudioInputSource> AudioSources,
    bool HasHardwareEncoderInput)
{
    public long? PersistentId { get; init; }
    internal uint ActiveAudioInputId { get; init; } = 1;
    public int MaxAudioChannels { get; init; } = (int)CaptureAudio.Channels;
    public bool SupportsVancInputMapping { get; init; }
    public AnalogInputCapabilities AnalogCapabilities { get; init; } = new(false, false, false);
}

public sealed record VideoInputSource(uint Id, string Name, IReadOnlyList<VideoMode> TenBitModes,
    IReadOnlyList<VideoMode> EightBitModes)
{
    public IReadOnlyList<VideoMode> ModesFor(CapturePixelFormat format) =>
        format == CapturePixelFormat.V210 ? TenBitModes : EightBitModes;
}

public sealed record AudioInputSource(uint Id, string Name);

public sealed record CaptureRouting(uint Video, uint Audio)
{
    public VancInputOptions? VancInput { get; init; }
}

public sealed record VancInputOptions(bool Enabled = false, int Line1 = 18, int Line2 = 19, int Line3 = 20)
{
    private const int MaximumSourceLine = 30;

    public VancInputOptions Normalized() =>
        new(Enabled, Math.Clamp(Line1, 1, MaximumSourceLine), Math.Clamp(Line2, 0, MaximumSourceLine),
            Math.Clamp(Line3, 0, MaximumSourceLine));

    public VancInputOptions? WithDetectedLines(IReadOnlyList<VancScanLine> lines)
    {
        var mapped = lines.Where(line => line.Kind != VancLineKind.Waveform && line.Line is >= 1 and <= MaximumSourceLine)
            .Select(line => line.Line).Distinct().Order().Take(3).ToArray();
        if (mapped.Length == 0)
            return null;

        return new(Enabled, mapped[0], mapped.ElementAtOrDefault(1), mapped.ElementAtOrDefault(2));
    }
}

public enum CapturePixelFormat : uint { V210 = 0x76323130, Uyvy = 0x32767579, Bgra = 0x42475241 }

public enum CaptureAudioFormat : uint { Pcm16 = 16, Pcm32 = 32 }

public static class CaptureAudio
{
    public const uint SampleRate = 48000;
    public const uint Channels = 2;

    public static bool IsValidChannelCount(uint channels) => channels is 2 or 8 or 16;

    public static bool SupportsMultiChannelInput(uint audioInputId) => audioInputId is 1 or 2; // Embedded or AES/EBU

    public static uint[] SupportedChannelCounts(int maxDeviceChannels, uint audioInputId)
    {
        if (!SupportsMultiChannelInput(audioInputId) || maxDeviceChannels < 8)
            return [2];
        return maxDeviceChannels >= 16 ? [2, 8, 16] : [2, 8];
    }

    public static uint[] ChannelFallbackOrder(uint requested) => requested switch
    {
        >= 16 => [16, 8, 2],
        >= 8 => [8, 2],
        _ => [2]
    };

    public static uint NormalizeChannels(uint requested, int maxDeviceChannels, uint audioInputId)
    {
        var supported = SupportedChannelCounts(maxDeviceChannels, audioInputId);
        if (supported.Contains(requested))
            return requested;
        for (var i = supported.Length - 1; i >= 0; i--)
            if (supported[i] <= requested)
                return supported[i];
        return Channels;
    }

    public static bool SupportsRecordingFormat(CaptureAudioFormat input, CaptureAudioFormat output) =>
        output == CaptureAudioFormat.Pcm16 && input is CaptureAudioFormat.Pcm16 or CaptureAudioFormat.Pcm32 ||
        output == CaptureAudioFormat.Pcm32 && input == CaptureAudioFormat.Pcm32;

    public static int BytesPerFrame(CaptureAudioFormat format, uint channels = Channels) =>
        (int)((uint)format / 8 * channels);
}

public sealed record VideoMode(uint Id, string Name, int Width, int Height, long FrameDuration, long TimeScale,
    uint FieldDominance)
{
    public double FramesPerSecond => (double)TimeScale / FrameDuration;
    public bool IsInterlaced => PreviewFeed.IsInterlaced(FieldDominance);
    internal long PreviewFieldInterval => TimeScale > 0 && FrameDuration > 0
        ? (long)Math.Round(Stopwatch.Frequency * ((double)FrameDuration / TimeScale / 2))
        : 0;
    public string Description => $"{Name}  ·  {Width} × {Height}  ·  {FramesPerSecond:0.###} fps";
}

public enum VideoSignal { Unknown, NoInput, Present }
