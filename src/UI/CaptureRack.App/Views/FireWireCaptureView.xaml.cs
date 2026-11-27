using System.Runtime.InteropServices;
using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.DirectShow.Windows.FireWire.MsTape;
using CaptureRack.QualityControl;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App.Views;

public sealed partial class FireWireCaptureView : UserControl, ILiveMediaSource, ILiveMediaInspector
{
    private Func<string, string> localize = key => key;
    private Func<string> outputDirectory = () => "";
    private Func<string> recordingLabel = () => "";
    private FireWireLiveSession? session;
    private FireWirePreviewRecovery? decoder;
    private PreviewFeed? activePreview;
    private LocalAudioMonitor? monitor;
    private CancellationTokenSource? previewStop;
    private Task? previewTask;
    private Task? recordingTask;
    private FireWireClipWriter? recording;
    private FireWireSettings savedSelection = new();
    private bool deviceSelectionIsFallback;
    private InfoBarSeverity? activeNoticeSeverity;
    private bool discovering;
    private bool discovered;
    private bool updatingPicker;
    private string? ffmpeg;
    private bool decodeFailed;
    private bool hasAudio;
    private bool switchingProgram;
    private bool hasFieldOrder;
    private StereoMonitor? audio;
    private AudioMonitorRoute audioMonitorRoute = AudioMonitorRoute.Default;
    private readonly object audioGate = new();

    public bool IsCapturing => previewStop is not null;
    public bool IsStopping => previewStop?.IsCancellationRequested == true;
    public bool IsRecording => recording is not null;
    public bool IsFinalizing { get; private set; }
    public bool CanStart => FireWireCapture.IsSupported && !discovering && DevicePicker.SelectedItem is FireWireDevice;
    public bool SupportsRecording => CanStart;
    public bool CanRecord => IsCapturing && !IsStopping && session?.CanRecord == true;
    public bool HasInput => session?.HasRecentInput == true;
    public bool HasOpenDropdown => DevicePicker.IsDropDownOpen;
    internal PreviewFeed? ActivePreview => Volatile.Read(ref activePreview);
    public long ReceivedUnits => session?.ReceivedUnits ?? 0;
    private FireWireDevice? SelectedDevice => DevicePicker.SelectedItem as FireWireDevice;
    internal FireWireMediaFormat ExpectedFormat => session?.Format ??
        SelectedDevice?.SimulatedFormat ?? (SelectedDevice?.IsDv == true ? FireWireMediaFormat.Dv : new(TsLayout.Ts192));
    internal string FormatSummary => ExpectedFormat.IsDv
        ? (session?.DvInfo?.LastFrame?.Encoding ?? session?.Format?.Encoding ?? SelectedDevice?.SimulatedFormat?.Encoding)?.DisplayName() ?? "DV" :
        (session?.Format ?? SelectedDevice?.SimulatedFormat)?.Transport is { } layout ? $"MPEG-TS ({layout.Stride} B)" : "MPEG-TS";
    public long DecodedFrames => decoder?.DecodedFrames ?? 0;
    public bool HasPreview => ffmpeg is not null && CanStart && !decodeFailed;
    public CaptureAudioInfo? AudioInfo => Volatile.Read(ref switchingProgram) ? null :
        session?.DvInfo?.LastFrame is { AudioRate: int rate } frame
            ? new("DV PCM", frame.AudioBits, rate, frame.AudioChannels ?? decoder?.AudioInfo?.Channels, null) : decoder?.AudioInfo;
    public bool HasAudio => HasPreview && Volatile.Read(ref hasAudio);
    public bool HasDeinterlace => HasPreview && Volatile.Read(ref hasFieldOrder);
    public string? PreviewFailure { get; private set; }
    public string? PreviewHint => !IsCapturing ? "FwPreviewStandby" : ffmpeg is null ? "FwMissingFfmpeg" :
        decodeFailed ? "FwCaptureStillAvailable" : HasInput ? "FwReacquiringVideo" :
        SelectedDevice?.SimulatedFormat is not null ? "FwGeneratingSimulation" : "FwWaitForData";
    public string? SavedPath { get; private set; }
    public string? RecordingReportPath => null;
    public string? RecordingDetail => IsFinalizing ? L("QcFinishing") : recording is { } writer
        ? string.Format(L("FwRecordingProgress"), writer.WrittenBytes / 1048576d)
        : recordingDetail;
    private string? recordingDetail;
    public event EventHandler? StateChanged;
    public event EventHandler? SettingsPersistenceRequested;

    public FireWireCaptureView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => RefreshTheme();
        Loaded += async (_, _) =>
        {
            RefreshTheme();
            if (!discovered)
                await RefreshAsync();
        };
    }

    internal void RestoreSettings(FireWireSettings settings) =>
        savedSelection = settings ?? new();

    internal FireWireSettings ExportSettings() =>
        FireWireSettingsResolver.Export(savedSelection, SelectedDevice?.Id, deviceSelectionIsFallback);

    internal void RefreshTheme()
    {
        if (NoticeBanner.Visibility == Visibility.Visible && activeNoticeSeverity is { } severity)
            ApplyNoticeTheme(severity);
    }

    public string? BuildFilenamePreview(string label) =>
        RecordingFileNaming.BuildFireWireName(label, localTime: null, ExpectedFormat);

    public IReadOnlyList<TelemetryMetric> BuildLiveTelemetry(long previewSkippedFrames,
        Func<string, string> localize, IFormatProvider formatProvider)
    {
        if (ExpectedFormat.IsDv || session?.DvInfo is not null)
        {
            var dv = session?.DvInfo;
            return FireWireWorkspaceModule.BuildDvTelemetry(
                dv?.Frames ?? ReceivedUnits, DecodedFrames, dv?.InvalidFrames ?? 0,
                dv?.Discontinuities ?? 0, previewSkippedFrames, localize, formatProvider);
        }

        var stream = session?.StreamInfo;
        return FireWireWorkspaceModule.BuildTsTelemetry(
            stream?.Packets ?? ReceivedUnits, DecodedFrames, stream?.ContinuityErrors,
            stream?.SyncErrors ?? 0, stream?.BitsPerSecond ?? 0, previewSkippedFrames, localize, formatProvider);
    }

    public void NotifyOutputFolderChanged() { }

    internal void Bind(Func<string, string> localizer, Func<string> directory, Func<string> label)
    {
        localize = localizer;
        outputDirectory = directory;
        recordingLabel = label;
        if (!IsCapturing)
            ffmpeg = QcReports.FindTools().FfmpegPath;
        DeviceSectionHeading.Text = L("FwDeviceSectionHeading");
        DeviceLabel.Text = L("Device");
        AutomationProperties.SetName(DevicePicker, L("Device"));
        RefreshLabel.Text = L("Refresh");
        AutomationProperties.SetName(RefreshButton, L("Refresh"));
        BindStreamInfo();
    }

    public void StopPreview()
    {
        if (previewStop is not { IsCancellationRequested: false } stop)
            return;
        if (recording is { } writer)
            FinishRecording(writer);
        stop.Cancel();
        UpdateControls();
    }

    public async Task StopForCloseAsync()
    {
        StopPreview();
        if (previewTask is { } task)
            await task;
        if (recordingTask is { } finishing)
            await finishing;
    }

    private string L(string key) => localize(key);

    private void UpdateControls()
    {
        var idle = !IsCapturing && !IsRecording && !discovering;
        DevicePicker.IsEnabled = idle && DevicePicker.Items.Count > 0;
        RefreshButton.IsEnabled = idle && FireWireCapture.IsSupported;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HideNotice()
    {
        activeNoticeSeverity = null;
        NoticeBanner.Visibility = Visibility.Collapsed;
    }

    private void ShowNotice(string key, InfoBarSeverity severity, string detail = "")
    {
        activeNoticeSeverity = severity;
        NoticeTitle.Text = L(key);
        NoticeMessage.Text = detail;
        NoticeMessage.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        ApplyNoticeTheme(severity);
        NoticeBanner.Visibility = Visibility.Visible;
    }

    private void ApplyNoticeTheme(InfoBarSeverity severity)
    {
        var (bgKey, borderKey, glyph) = severity switch
        {
            InfoBarSeverity.Error => ("StudioDangerSoftBrush", "StudioDangerBrush", "\uE783"),
            InfoBarSeverity.Warning => ("StudioCautionSoftBrush", "StudioCautionBrush", "\uE7BA"),
            InfoBarSeverity.Success => ("StudioSignalLockSoftBrush", "StudioSignalLockBrush", "\uE73E"),
            _ => ("StudioInfoSoftBrush", "StudioInfoBrush", "\uE946")
        };
        NoticeBanner.Background = StudioBrushes.Resolve(this, bgKey);
        NoticeBanner.BorderBrush = StudioBrushes.Resolve(this, borderKey);
        NoticeIcon.Foreground = StudioBrushes.Resolve(this, borderKey);
        NoticeIcon.Glyph = glyph;
    }

    private async Task RefreshAsync()
    {
        if (IsCapturing || IsRecording || discovering)
            return;
        discovered = true;
        if (!FireWireCapture.IsSupported)
        {
            ShowNotice("FwUnsupported", InfoBarSeverity.Warning);
            UpdateControls();
            return;
        }

        discovering = true;
        var previous = FireWireSettingsResolver.ResolveRefreshTargetDeviceId(
            savedSelection, (DevicePicker.SelectedItem as FireWireDevice)?.Id, deviceSelectionIsFallback);
        HideNotice();
        UpdateControls();
        try
        {
            var devices = await Task.Run(() => FireWireCapture.List(includeSimulated: true));
            PopulateDevicePicker(devices, previous);
            if (devices.All(device => device.SimulatedFormat is not null))
                ShowNotice("FwNoDevices", InfoBarSeverity.Informational);
        }
        catch (Exception error)
        {
            PopulateDevicePicker(FireWireCapture.SimulatedDevices, previous);
            ShowNotice("FwDeviceScanFailed", InfoBarSeverity.Error, error.Message);
        }
        finally
        {
            discovering = false;
            UpdateControls();
        }
    }

    private void PopulateDevicePicker(IEnumerable<FireWireDevice> devices, string? previous)
    {
        var display = devices.Select(device => device.SimulatedFormat is not null
            ? device with { Name = string.Format(L("FwSimulatedName"), device.Model) }
            : string.IsNullOrWhiteSpace(device.Model) ? device : device with { Name = $"{device.Vendor} {device.Model}".Trim() }).ToArray();
        var matched = string.IsNullOrWhiteSpace(previous)
            ? null
            : display.FirstOrDefault(device => string.Equals(device.Id, previous, StringComparison.OrdinalIgnoreCase));
        deviceSelectionIsFallback = matched is null && !string.IsNullOrWhiteSpace(previous);
        updatingPicker = true;
        DevicePicker.ItemsSource = display;
        DevicePicker.SelectedItem = matched ?? display.FirstOrDefault();
        updatingPicker = false;
        RefreshStreamInfo(force: true);
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void DeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPicker)
            return;
        deviceSelectionIsFallback = false;
        savedSelection = ExportSettings();
        SettingsPersistenceRequested?.Invoke(this, EventArgs.Empty);
        RefreshStreamInfo(force: true);
        UpdateControls();
    }

    public Task StartAsync(PreviewFeed feed)
    {
        if (CanStart && !IsCapturing && !IsRecording && DevicePicker.SelectedItem is FireWireDevice device)
            return previewTask = RunPreviewAsync(device, feed);
        return Task.CompletedTask;
    }

    private async Task RunPreviewAsync(FireWireDevice device, PreviewFeed feed)
    {
        using var stop = new CancellationTokenSource();
        using var playback = new LocalAudioMonitor(FireWirePreviewDecoder.AudioRate);
        previewStop = stop;
        monitor = playback;
        var live = new FireWireLiveSession();
        session = live;
        decodeFailed = false;
        PreviewFailure = null;
        hasAudio = false;
        switchingProgram = false;
        hasFieldOrder = false;
        lock (audioGate)
        {
            audioMonitorRoute = AudioMonitorRoute.Default;
            audio = new StereoMonitor(CaptureAudioFormat.Pcm16, FireWirePreviewDecoder.AudioRate);
        }
        HideNotice();
        Volatile.Write(ref activePreview, feed);
        var previewDecoder = ffmpeg is null ? null : new FireWirePreviewRecovery(device.IsDv);
        decoder = previewDecoder;
        CaptureSampleHandler? offer = previewDecoder is null ? null : previewDecoder.Offer;
        var decoding = previewDecoder is null ? Task.CompletedTask : DecodePreviewAsync(live, previewDecoder, ffmpeg!, playback, feed, stop.Token);
        UpdateControls();

        try
        {
            Action? changed = previewDecoder is null ? null : () =>
            {
                Volatile.Write(ref switchingProgram, true);
                Volatile.Write(ref videoDetails, null);
                previewDecoder.NotifyStreamChange();
            };
            await Task.Run(() => live.Run(device.Id, offer, stop.Token, changed, ffmpeg));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            PreviewFailure = error.Message;
            ShowNotice("PreviewFailed", InfoBarSeverity.Error, error.Message);
        }
        finally
        {
            stop.Cancel();
            await decoding;
            session = null;
            decoder = null;
            Volatile.Write(ref activePreview, null);
            Volatile.Write(ref videoDetails, null);
            RefreshStreamInfo(force: true);
            monitor = null;
            previewStop = null;
            hasAudio = false;
            hasFieldOrder = false;
            audio = null;
            UpdateControls();
        }
    }

    private async Task DecodePreviewAsync(FireWireLiveSession live, FireWirePreviewRecovery active, string executable, LocalAudioMonitor playback,
        PreviewFeed feed, CancellationToken stop)
    {
        var previous = double.NaN;
        var fps = 0d;
        try
        {
            await active.RunAsync(executable, (pixels, info) =>
            {
                if (Volatile.Read(ref switchingProgram))
                    return;
                var delta = info.Timestamp - previous;
                previous = info.Timestamp;
                if (delta is >= 0.005 and <= 0.2)
                    fps = 1 / delta;
                var format = info.VideoCodec switch
                {
                    "mpeg2video" => $"MPEG-2 / {info.PixelFormat}",
                    "dvvideo" => $"{(live.DvInfo?.LastFrame?.Encoding ?? live.Format?.Encoding)?.DisplayName() ?? "DV"} / {info.PixelFormat}",
                    "h264" => $"H.264 / {info.PixelFormat}",
                    "hevc" => $"HEVC / {info.PixelFormat}",
                    { } codec => $"{codec} / {info.PixelFormat}",
                    null => info.PixelFormat
                };
                var scan = L(info.TopFieldFirst switch
                {
                    true => "PreviewTopFirst",
                    false => "PreviewBottomFirst",
                    null => "DsProgressive"
                });
                var video = $"{info.Width} × {info.Height} · {format} · {scan}";
                if (fps > 0)
                    video += $" · {fps:0.##} fps";
                if (Volatile.Read(ref videoDetails) != video)
                    Volatile.Write(ref videoDetails, video);
                feed.SubmitDecodedFrame(pixels.Span, info.PixelWidth, info.PixelHeight,
                    info.TopFieldFirst, fps, info.Width, info.Height, info.DisplayAspectRatio, format,
                    info.TopFieldFirst is null ? PreviewScanMode.Progressive : null);
                Volatile.Write(ref hasFieldOrder, true);
            }, (pcm, channels, layoutName) =>
            {
                if (Volatile.Read(ref switchingProgram))
                    return;
                var samples = MemoryMarshal.Cast<byte, short>(pcm.Span);
                var safeChannels = Math.Clamp(channels, 1, AudioMonitorRoute.MaxSupportedChannels);
                var layout = AudioChannelLayoutHelper.Detect(layoutName, safeChannels);
                AudioMonitorRoute activeRoute;
                int activeMask;
                lock (audioGate)
                {
                    if (audio is null || audio.Channels != safeChannels ||
                        (layout != AudioChannelLayout.Unknown && audio.Layout != layout))
                    {
                        audio = new StereoMonitor(CaptureAudioFormat.Pcm16, FireWirePreviewDecoder.AudioRate,
                            channels: safeChannels, layout: layout)
                        {
                            Route = audioMonitorRoute
                        };
                    }
                    activeRoute = audio.Route;
                    for (var i = 0; i <= samples.Length - safeChannels; i += safeChannels)
                        audio.AddFrame(samples.Slice(i, safeChannels));
                    activeMask = audio.ActivePairMask;
                }
                playback.SubmitInterleavedPcm16(samples, safeChannels, activeRoute, layout, activeMask);
                Volatile.Write(ref hasAudio, true);
            }, () =>
            {
                Volatile.Write(ref videoDetails, null);
                previous = double.NaN;
                fps = 0;
                lock (audioGate)
                {
                    audioMonitorRoute = AudioMonitorRoute.Default;
                    audio = new StereoMonitor(CaptureAudioFormat.Pcm16, FireWirePreviewDecoder.AudioRate);
                }
                Volatile.Write(ref hasAudio, false);
                Volatile.Write(ref hasFieldOrder, false);
                feed = new PreviewFeed();
                Volatile.Write(ref activePreview, feed);
                Volatile.Write(ref switchingProgram, false);
            }, stop);
        }
        catch (Exception error) when (!stop.IsCancellationRequested)
        {
            decodeFailed = true;
            ShowNotice("FwDecodeFailed", InfoBarSeverity.Warning, error.Message);
            UpdateControls();
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }

    public StereoLevels? ReadAudioLevels()
    {
        StereoMonitor.AudioWindow? window;
        lock (audioGate)
            window = audio?.ReadForDisplay();
        return window?.Analyze();
    }

    public void SetAudioMonitor(bool enabled, double volume)
    {
        if (monitor is { } current)
        {
            current.Enabled = enabled;
            current.Volume = volume;
        }
    }

    public void SetAudioMonitorRoute(AudioMonitorRoute route)
    {
        lock (audioGate)
        {
            audioMonitorRoute = route;
            if (audio is not null)
                audio.Route = route;
        }
    }

    private void FinishRecording(FireWireClipWriter current)
    {
        IsFinalizing = true;
        session?.StopRecording(current);
        UpdateControls();
    }

    public Task ToggleRecordingAsync()
    {
        if (recording is { } current)
        {
            if (!IsFinalizing)
                FinishRecording(current);
            return recordingTask ?? Task.CompletedTask;
        }
        return recordingTask = RecordAsync();
    }

    private async Task RecordAsync()
    {
        if (session is not { } live || !CanRecord)
            return;

        HideNotice();
        try
        {
            var folder = outputDirectory();
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException(folder);
            var name = RecordingFileNaming.BuildFireWireName(recordingLabel(), DateTime.Now, ExpectedFormat);
            var writer = live.StartRecording(Path.Combine(folder, name));
            recording = writer;
            SavedPath = null;
            recordingDetail = null;
            IsFinalizing = false;
            UpdateControls();

            var clip = await writer.Completion;
            if (clip.Error is { } error)
            {
                recordingDetail = clip.Path;
                ShowNotice("FwRecordFailed", InfoBarSeverity.Error, error);
            }
            else
            {
                SavedPath = clip.Path;
                recordingDetail = clip.DvAnalysis is { } dv
                    ? string.Format(L("FwSavedDvRecording"), clip.Path, dv.Frames)
                    : string.Format(L("FwSavedRecording"), clip.Path, clip.Analysis?.Packets ?? 0);
            }
        }
        catch (Exception error)
        {
            recordingDetail = error.Message;
            ShowNotice("FwRecordFailed", InfoBarSeverity.Error, error.Message);
        }
        finally
        {
            recording = null;
            IsFinalizing = false;
            UpdateControls();
        }
    }
}
