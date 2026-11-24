using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.QualityControl;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.Windows.Storage.Pickers;

namespace CaptureRack.App;

public sealed partial class MainWindow : Window
{
    private static readonly DeckLinkBackend DeckLink = new CaptureBackendRegistry(new DeckLinkBackend())
            .Get<DeckLinkBackend>(DeckLinkBackend.BackendId);
    private CancellationTokenSource? captureStop;
    private CancellationTokenSource? qcStop;
    private Task<CaptureStats>? captureTask;
    private readonly DispatcherQueueTimer previewTimer;
    private readonly DispatcherQueueTimer detectedModeTimer;
    private bool checkingDetectedMode;
    private readonly Stopwatch recordingClock = new();
    private long displayedRecordingSeconds;
    private long displayedPreviewSkips;
    private string? qcOutputPath;
    private string? statusFilePath;
    private QcResult? lastQcResult;
    private PreviewFeed? preview;
    private PreviewFrame? previewFrame;
    private WriteableBitmap? previewBitmap;
    private Stream? bitmapPixels;
    private const double DefaultPreviewAspectRatio = 4d / 3;
    private const double StereoMeterGap = 12;
    private double previewAspectRatio = DefaultPreviewAspectRatio;
    private VideoMode? observedPreviewMode;
    private bool observedPreviewModeIsFallback;
    private (long? DeviceId, int? DeviceIndex, uint? Source, uint? Mode, CapturePixelFormat? Format) previewSelection;
    private double previewAvailableWidth;
    private double previewHeightLimit;
    private bool loading;
    private bool closing;
    private bool closeAfterQc;
    private bool recordingSession;
    private bool recordingTransition;
    private bool previewReady;
    private PreviewRecorder? previewRecorder;
    private DateTimeOffset recordingStarted;
    private bool pickingOutput;

    public MainWindow()
    {
        previewHoverTimer = DispatcherQueue.CreateTimer();
        previewHoverTimer.Interval = TimeSpan.FromMilliseconds(200);
        previewHoverTimer.IsRepeating = false;
        previewHoverTimer.Tick += (_, _) =>
        {
            previewHoverReady = true;
            UpdatePreviewFullscreenButton();
        };
        InitializeComponent();
        MonitorSelector.SelectedItem = PreviewTab;
        InitializeSettings();
        InitializeSettingsInput();
        previewTimer = DispatcherQueue.CreateTimer();
        previewTimer.Interval = TimeSpan.FromMilliseconds(1000d / 60);
        previewTimer.Tick += (_, _) =>
        {
            PresentPreview();
            ShowVideoScopes();
            if (recordingClock.IsRunning)
                UpdateRecordingClock();
        };
        detectedModeTimer = DispatcherQueue.CreateTimer();
        detectedModeTimer.Interval = TimeSpan.FromSeconds(2);
        detectedModeTimer.Tick += async (_, _) => await RefreshDetectedPreviewModeAsync();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 835));
        AppWindow.Closing += OnClosing;
        ((FrameworkElement)Content).Loaded += OnLoaded;
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var padding = WorkspacePage.Padding;
        var availableWidth = e.NewSize.Width - padding.Left - padding.Right;
        var wideSettings = availableWidth >= 800;
        SettingsRightColumn.Width = wideSettings ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SettingsColumns.ColumnSpacing = wideSettings ? 24 : 0;
        SettingsColumns.RowSpacing = wideSettings ? 0 : 20;
        Grid.SetColumn(SettingsSecondaryGroup, wideSettings ? 1 : 0);
        Grid.SetRow(SettingsSecondaryGroup, wideSettings ? 0 : 1);
        var stackFooter = availableWidth < 630;
        var stackAuxiliary = availableWidth < 460;
        var stackTelemetry = availableWidth < 810;
        FooterActionsColumn.Width = stackFooter ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        FooterStatusColumn.Width = stackFooter ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FooterLayout.ColumnSpacing = stackFooter ? 0 : 12;
        FooterLayout.RowSpacing = stackFooter ? 4 : 0;
        FooterActionCluster.RowSpacing = stackAuxiliary ? 4 : 0;
        Grid.SetColumn(StatusDetailsButton, stackFooter ? 0 : 1);
        Grid.SetRow(StatusDetailsButton, stackFooter ? 1 : 0);
        StatusDetailsButton.HorizontalAlignment = stackFooter ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        foreach (var control in new FrameworkElement[] { RecordingClock, CancelQcButton, QcResultsButton })
        {
            Grid.SetColumn(control, stackAuxiliary ? 0 : 1);
            Grid.SetRow(control, stackAuxiliary ? 1 : 0);
        }
        TelemetryStorageColumn.Width = stackTelemetry ? new GridLength(0) : GridLength.Auto;
        FooterTelemetry.ColumnSpacing = stackTelemetry ? 0 : 16;
        FooterTelemetry.RowSpacing = stackTelemetry ? 2 : 0;
        Grid.SetColumn(StorageStatus, stackTelemetry ? 0 : 1);
        Grid.SetRow(StorageStatus, stackTelemetry ? 1 : 0);
        StartActions.Orientation = availableWidth < 310 ? Orientation.Vertical : Orientation.Horizontal;

    }

    private void MonitorViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        previewAvailableWidth = e.NewSize.Width;
        previewHeightLimit = e.NewSize.Height;
        UpdateMonitorLayout();
    }

    private void UpdateMonitorLayout()
    {
        if (previewAvailableWidth <= 0 || previewHeightLimit <= 0)
            return;

        MonitorBody.MinHeight = previewHeightLimit;
        EventsView.Height = previewHeightLimit;
        WaveformPlot.Height = VectorPlot.Height = Math.Max(104, previewHeightLimit - 88);
        SpectrumPlot.Height = Math.Max(104, previewHeightLimit - 66);

        var meterSpace = previewFullscreen ? 0 : StereoMeter.Width + StereoMeterGap;
        var captionSpace = previewFullscreen ? 0 : 28;
        const double border = 2;
        var videoSpace = Math.Max(0, previewAvailableWidth - meterSpace - border);
        var height = Math.Min(videoSpace / previewAspectRatio, Math.Max(0, previewHeightLimit - captionSpace - border));
        var videoWidth = height * previewAspectRatio + border;
        PreviewView.Width = PreviewSurface.Width = videoWidth;
        PreviewSurface.Height = height + border;

        // Center the video when the meter fits beside it. Shift left only as far as needed.
        var unusedWidth = Math.Max(0, previewAvailableWidth - videoWidth - meterSpace);
        var leftMargin = Math.Clamp((previewAvailableWidth - videoWidth) / 2, 0, unusedWidth);
        PreviewView.Margin = new Thickness(leftMargin, 0, 0, 0);
        StereoMeter.Margin = new Thickness(0, 0, unusedWidth - leftMargin, 0);
        StereoMeter.Height = Math.Max(174, PreviewSurface.Height);
    }

    private void UpdatePreviewAspectRatio(int width, int height)
    {
        var aspectRatio = width <= 0 || height <= 0 || (width == 720 && height <= 576)
            ? DefaultPreviewAspectRatio : (double)width / height;
        if (previewAspectRatio == aspectRatio)
            return;

        previewAspectRatio = aspectRatio;
        UpdateMonitorLayout();
    }

    private void UpdatePreviewForSelection()
    {
        if (captureTask is not null || loading || !settingsReady)
            return;

        var device = DevicePicker.SelectedItem as CaptureDevice;
        var source = VideoInputPicker.SelectedItem as VideoInputSource;
        var selectedMode = ModePicker.SelectedItem as ModeOption;
        var format = FormatPicker.SelectedItem as FormatOption;
        var selection = (device?.PersistentId, device?.Index, source?.Id, selectedMode?.Id, format?.Id);
        if (previewSelection == selection)
            return;

        previewSelection = selection;
        observedPreviewMode = null;
        var mode = selectedMode?.Id is { } id && format is not null
            ? source?.ModesFor(format.Id).FirstOrDefault(candidate => candidate.Id == id) : null;
        ResetPreview(null);
        UpdatePreviewAspectRatio(mode?.Width ?? 0, mode?.Height ?? 0);
        if (selectedMode is { Id: null })
            _ = RefreshDetectedPreviewModeAsync();
    }

    private async Task RefreshDetectedPreviewModeAsync()
    {
        if (checkingDetectedMode || captureTask is not null || loading || closing ||
            ModePicker.SelectedItem is not ModeOption { Id: null } ||
            DevicePicker.SelectedItem is not CaptureDevice device ||
            VideoInputPicker.SelectedItem is not VideoInputSource source ||
            FormatPicker.SelectedItem is not FormatOption format)
            return;

        var selection = previewSelection;
        checkingDetectedMode = true;
        try
        {
            var id = await Task.Run(() => DeckLinkCapture.GetLockedVideoInputMode(device, source.Id));
            if (!closing && captureTask is null && previewSelection == selection)
                UpdateObservedPreviewMode(id is null ? null :
                    source.ModesFor(format.Id).FirstOrDefault(candidate => candidate.Id == id));
        }
        catch (COMException)
        {
            // A disconnected board has no idle mode to display.
        }
        finally
        {
            checkingDetectedMode = false;
        }
    }

    private void MonitorSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.SelectedItem;
        PreviewView.Visibility = selected == PreviewTab ? Visibility.Visible : Visibility.Collapsed;
        StereoMeter.Visibility = previewFullscreen ? Visibility.Collapsed : PreviewView.Visibility;
        ScopesView.Visibility = selected == ScopesTab ? Visibility.Visible : Visibility.Collapsed;
        SpectrumView.Visibility = selected == SpectrumTab ? Visibility.Visible : Visibility.Collapsed;
        EventsView.Visibility = selected == EventsTab ? Visibility.Visible : Visibility.Collapsed;
        UpdateMonitorLayout();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)sender).Loaded -= OnLoaded;
        await RestoreSettingsAsync();
        if (closing)
            return;
        await RefreshDevicesAsync();
        if (closing)
            return;
        storageTimer.Start();
        detectedModeTimer.Start();
        await RefreshStorageAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        if (loading || pickingOutput || captureTask is not null || qcStop is not null)
            return;

        SavePendingSettings();
        loading = true;
        UpdateControls();
        SetStatus("CheckingDevices", "CheckingDeckLink");
        try
        {
            var result = await Task.Run(() => DeckLink.ScanDevices(includeCapabilities: true));
            if (closing)
                return;

            sdkVersion = result.ApiVersion ?? "";
            RenderApiVersion();
            var devices = result.Devices.Where(device => device.HasVideoInput).ToArray();
            DevicePicker.ItemsSource = devices;
            RestoreDeviceSelections(devices);
            loading = false;
            UpdateControls();
            switch (result.Availability)
            {
                case DeckLinkAvailability.UnsupportedPlatform:
                    SetStatus("DeckLinkUnavailable", "DeckLinkUnsupportedPlatform");
                    break;
                case DeckLinkAvailability.DriverUnavailable:
                    SetStatus("DeckLinkUnavailable", "DeckLinkDriverUnavailable", InfoBarSeverity.Warning);
                    break;
                default:
                    if (devices.Length == 0)
                        SetStatus("NoDevice", "ConnectDevice", InfoBarSeverity.Warning);
                    else
                        ShowInputSelectionStatus();
                    break;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException)
        {
            if (closing)
                return;

            sdkVersion = "";
            RenderApiVersion();
            DevicePicker.ItemsSource = null;
            UpdateSelectedDevice();
            SetStatus("ScanFailed", () => ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            loading = false;
            if (!closing)
                UpdateControls();
        }
    }

    private void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading)
        {
            UpdateSelectedDevice();
            SettingsChanged();
        }
    }

    private void UpdateSelectedDevice()
    {
        var device = DevicePicker.SelectedItem as CaptureDevice;
        VideoInputPicker.ItemsSource = device?.VideoSources.Select(source => source with { Name = LocalVideoConnector(source) }).ToArray();
        AudioInputPicker.ItemsSource = device?.AudioSources.Select(source => source with { Name = LocalAudioConnector(source) }).ToArray();
        VideoInputPicker.SelectedIndex = -1;
        AudioInputPicker.SelectedIndex = -1;
        UpdateModeOptions();
        if (!loading)
            ShowInputSelectionStatus();
    }

    private void VideoInputPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModeOptions();
        SettingsChanged();
        if (!loading && captureTask is null)
            ShowInputSelectionStatus();
    }

    private void UpdateModeOptions()
    {
        var currentMode = ModePicker.SelectedItem as ModeOption;
        List<ModeOption> options = [];
        if (VideoInputPicker.SelectedItem is VideoInputSource video && FormatPicker.SelectedItem is FormatOption format &&
            video.ModesFor(format.Id) is { Count: > 0 } modes)
        {
            if (DevicePicker.SelectedItem is CaptureDevice { SupportsFormatDetection: true })
                options.Add(new(L("Auto"), null));
            options.AddRange(modes.Select(mode => new ModeOption(mode.Description, mode.Id)));
        }

        ModePicker.ItemsSource = options;
        ModePicker.SelectedItem = options.FirstOrDefault(option => currentMode is not null &&
            option.Id == currentMode.Id) ?? options.FirstOrDefault();
        UpdateControls();
    }

    private void InputPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateControls();
        SettingsChanged();
        if (!loading && captureTask is null)
            ShowInputSelectionStatus();
    }

    private void FormatPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModeOptions();
        SettingsChanged();
        if (!loading && captureTask is null)
            ShowInputSelectionStatus();
    }

    private void OutputPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateControls();
        SettingsChanged();
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        pickingOutput = true;
        UpdateControls();
        try
        {
            var picker = new FolderPicker(AppWindow.Id);
            var result = await picker.PickSingleFolderAsync();
            if (result is not null && !closing)
                OutputPath.Text = result.Path;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!closing)
            {
                ShowSettingsError("FolderError", ex.Message);
                SetStatus("FolderError", () => ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            pickingOutput = false;
            if (!closing)
                UpdateControls();
        }
    }

    private void ShowInputSelectionStatus()
    {
        if (DevicePicker.SelectedItem is CaptureDevice device &&
            (device.VideoSources.Count == 0 || device.AudioSources.Count == 0))
        {
            SetStatus("InputsUnavailable", "NoInputRoutes",
                InfoBarSeverity.Warning);
        }
        else if (VideoInputPicker.SelectedItem is VideoInputSource video &&
            FormatPicker.SelectedItem is FormatOption format && video.ModesFor(format.Id).Count == 0)
        {
            SetStatus("NoModes", "ChooseModeSource", InfoBarSeverity.Warning);
        }
        else
        {
            SetStatus("Idle", StartButton.IsEnabled
                ? "PreviewBeforeRecording"
                : "SelectInputs");
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (captureTask is not null)
        {
            captureStop?.Cancel();
            SetStatus("StoppingPreview", "ReleasingInput");
            UpdateControls();
            return;
        }

        await RunPreviewAsync();
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (qcStop is not null)
            return;

        if (recordingSession)
            await FinishRecordingAsync();
        else
            await BeginRecordingAsync();
    }

    private void CancelQcButton_Click(object sender, RoutedEventArgs e)
    {
        if (qcStop is { IsCancellationRequested: false } stop)
        {
            stop.Cancel();
            SetStatus("CancellingQc", "QcFinishing");
            UpdateControls();
        }
    }

    private async Task BeginRecordingAsync()
    {
        if (!previewReady || displayedStats?.CanStartRecording != true ||
            previewRecorder is not { } recorder || recordingTransition ||
            FormatPicker.SelectedItem is not FormatOption videoFormat ||
            AudioFormatPicker.SelectedItem is not AudioFormatOption audioFormat)
            return;

        var directory = OutputPath.Text.Trim();
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
        {
            holdRecordingStatus = true;
            UpdateLivePreviewStatus();
            SetStatus("ChooseOutput", "ExistingOutput", InfoBarSeverity.Warning);
            return;
        }

        recordingTransition = true;
        SetStatus("StartingRecording", "OpeningMov");
        UpdateControls();
        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var prefix = $"CaptureRack_{timestamp}";
            var formats = $"{videoFormat.FileTag}_{audioFormat.FileTag}";
            var opened = await Task.Run(() =>
            {
                var path = Path.Combine(directory, $"{prefix}_{formats}.mov");
                for (var suffix = 2; File.Exists(path); suffix++)
                    path = Path.Combine(directory, $"{prefix}_{suffix}_{formats}.mov");
                return (Path: path, Started: recorder.Start(path));
            });
            if (captureTask is null)
                throw new InvalidOperationException(L("PreviewStoppedOpening"));
            qcOutputPath = null;
            lastQcResult = null;
            recordingStarted = opened.Started;
            statusFilePath = opened.Path;
            recordingSession = true;
            displayedRecordingSeconds = 0;
            RecordingElapsed.Text = "00:00:00";
            RecordingClockLabel.Text = L("RecordingClock");
            recordingClock.Restart();
            UpdateRecordingIndicator(active: true);
            SetStatus("RecordingActive", "WritingMov");
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            SetStatus("RecordingStartFailed", () => ex.Message, InfoBarSeverity.Error);
            holdRecordingStatus = true;
        }
        finally
        {
            recordingTransition = false;
            UpdateControls();
            CloseAfterRecordingIfRequested();
        }
    }

    private async Task FinishRecordingAsync()
    {
        if (!recordingSession || recordingTransition || previewRecorder is not { } recorder ||
            DevicePicker.SelectedItem is not CaptureDevice device ||
            VideoInputPicker.SelectedItem is not VideoInputSource video ||
            AudioInputPicker.SelectedItem is not AudioInputSource audio)
            return;

        recordingTransition = true;
        StopRecordingClock();
        var stopped = DateTimeOffset.UtcNow;
        SetStatus("FinalizingMov", "WritingIndex");
        UpdateControls();
        try
        {
            var stats = await Task.Run(recorder.Finish);
            var saved = stats.Recording!;
            var modeField = DeckLinkCapture.FormatFourCc(saved.Mode.FieldDominance) switch
            {
                "uppr" => "tb",
                "lowr" => "bt",
                _ => "progressive"
            };
            var expected = new QcExpectedMedia(saved.VideoFrames, saved.AudioSampleFrames, modeField,
                (int)saved.AudioFormat, DeckLinkCapture.FormatFourCc((uint)saved.VideoFormat),
                saved.Mode.Width, saved.Mode.Height);
            var log = new QcCaptureLog(device.Name, $"Blackmagic DeckLink API {sdkVersion}", video.Name, audio.Name, saved.Mode.Description,
                recordingStarted, stopped, expected, stats.VideoFrames, stats.NoInputFrames,
                stats.MissingVideoFrames, stats.TimestampRegressions, stats.AudioPackets,
                stats.AudioLevels?.LeftClips, stats.AudioLevels?.RightClips,
                stats.Events.Select(item => new QcEvent(item.Elapsed, item.Description)).ToArray())
            {
                InputModeChanged = stats.RecordingModeChanged
            };
            if (!await RunQcAsync(saved.Path, log))
                closeAfterQc = false;
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            closeAfterQc = false;
            SetStatus("RecordingFailed", () => ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            recordingTransition = false;
            recordingSession = false;
            holdRecordingStatus = true;
            UpdateControls();
            CloseAfterRecordingIfRequested();
        }
    }

    private void CloseAfterRecordingIfRequested()
    {
        if (!closeAfterQc)
            return;

        closeAfterQc = false;
        DispatcherQueue.TryEnqueue(() => Close());
    }

    private async Task RunPreviewAsync()
    {
        if (closing || pickingOutput || captureTask is not null ||
            DevicePicker.SelectedItem is not CaptureDevice device ||
            VideoInputPicker.SelectedItem is not VideoInputSource video ||
            AudioInputPicker.SelectedItem is not AudioInputSource audio ||
            ModePicker.SelectedItem is not ModeOption mode || FormatPicker.SelectedItem is not FormatOption format ||
            AudioFormatPicker.SelectedItem is not AudioFormatOption audioFormat)
            return;

        using var stop = new CancellationTokenSource();
        captureStop = stop;
        var recorder = new PreviewRecorder();
        previewRecorder = recorder;
        holdRecordingStatus = false;
        liveSignal = default;
        var feed = new PreviewFeed();
        ResetPreview(feed);
        ResetMonitoring();
        previewTimer.Start();
        ShowCounts(new CaptureStats());
        SetStatus("StartingPreview", "OpeningInput");
        var dispatcher = DispatcherQueue;
        var request = new DeckLinkCaptureRequest(device.Index, mode.Id, Duration: null)
        {
            Routing = new CaptureRouting(video.Id, audio.Id),
            Preview = feed,
            PixelFormat = format.Id,
            AudioFormat = audioFormat.Id,
            PreviewRecorder = recorder
        };
        var task = Task.Factory.StartNew(() => DeckLink.Run(request,
            update =>
            {
                dispatcher.TryEnqueue(() =>
                {
                    if (!closing && ReferenceEquals(captureStop, stop) && !stop.IsCancellationRequested)
                        ShowCapture(update.Mode, update.Stats);
                });
            }, stop.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        captureTask = task;
        UpdateControls();
        try
        {
            var stats = await task;
            if (closing)
                return;
            ShowCounts(stats);
            if (stats.Error is { } error)
                SetStatus(recordingSession ? "RecordingFailed" : "PreviewFailed", () => error, InfoBarSeverity.Error);
            else if (!recordingTransition)
                SetStatus("PreviewStopped", "LastCounters");
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            if (!closing)
                SetStatus(recordingSession ? "RecordingFailed" : "PreviewFailed", () => ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            previewTimer.Stop();
            if (!closing)
            {
                PresentPreview();
                ShowVideoScopes();
            }
            preview = null;
            previewFrame = null;
            captureTask = null;
            captureStop = null;
            previewReady = false;
            if (!recordingTransition)
            {
                StopRecordingClock();
                recordingSession = false;
                previewRecorder = null;
            }
            if (!closing)
                UpdateControls();
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (qcStop is { } qc)
        {
            e.Cancel = true;
            closeAfterQc = true;
            qc.Cancel();
            SetStatus("CancellingQc", "QcFinishing");
            UpdateControls();
            return;
        }
        if (recordingTransition || recordingSession)
        {
            e.Cancel = true;
            closeAfterQc = true;
            if (!recordingTransition)
                await FinishRecordingAsync();
            return;
        }

        if (closing)
        {
            e.Cancel = captureTask is { IsCompleted: false };
            return;
        }

        SavePendingSettings();
        settingsSaveTimer.Stop();
        storageTimer.Stop();
        detectedModeTimer.Stop();
        closing = true;
        previewTimer.Stop();
        if (captureTask is not { } task)
        {
            bitmapPixels?.Dispose();
            return;
        }

        e.Cancel = true;
        captureStop?.Cancel();
        UpdateControls();
        try
        {
            await task;
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            // Preview is already shutting down.
        }

        bitmapPixels?.Dispose();
        // Complete outside the Closing event, even if the worker task has already finished.
        DispatcherQueue.TryEnqueue(() => Close());
    }

    private async void CheckMovButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckMovButton.IsEnabled)
            return;

        pickingOutput = true;
        UpdateControls();
        PickFileResult? file;
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id) { Title = L("CheckMov") };
            picker.FileTypeFilter.Add(".mov");
            file = await picker.PickSingleFileAsync();
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            if (!closing)
                SetStatus("CheckMovOpenFailed", () => ex.Message, InfoBarSeverity.Error);
            return;
        }
        finally
        {
            pickingOutput = false;
            if (!closing)
                UpdateControls();
        }

        if (file is not null && !closing)
        {
            await RunQcAsync(file.Path, null);
            CloseAfterRecordingIfRequested();
        }
    }

    private async Task<bool> RunQcAsync(string path, QcCaptureLog? log)
    {
        using var cancellation = new CancellationTokenSource();
        qcStop = cancellation;
        statusFilePath = path;
        void SetQcProgress(string stage) => SetStatus(log is null ? "QcChecking" : "RecordingSaved",
            () => log is null ? L(stage) : WithModeChangeNotice(LocalFormat("SavedQcProgress", L(stage))),
            log is { InputModeChanged: true } ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
        string WithModeChangeNotice(string detail) => log is { InputModeChanged: true }
            ? $"{L("QcWarnInputModeChange")} {detail}" : detail;
        string QcDetail(string detail) => log is null ? detail : WithModeChangeNotice(LocalFormat("SavedQcOutcome", detail));
        SetQcProgress("QcStarting");
        UpdateControls();
        var dispatcher = DispatcherQueue;
        try
        {
            var result = await Task.Run(() => QcReports.RunAsync(path, log, cancellation: cancellation.Token, stageChanged: stage =>
            {
                dispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(qcStop, cancellation) && !cancellation.IsCancellationRequested)
                        SetQcProgress($"Qc{stage}");
                });
            }));
            var (title, severity) = result.Status switch
            {
                QcStatus.Passed => ("QcPassed", InfoBarSeverity.Success),
                QcStatus.Cancelled => ("QcCancelled", InfoBarSeverity.Warning),
                QcStatus.ToolsUnavailable => ("QcUnavailable", InfoBarSeverity.Warning),
                _ => ("QcFailed", InfoBarSeverity.Error)
            };
            if (severity == InfoBarSeverity.Success && log is { InputModeChanged: true })
                severity = InfoBarSeverity.Warning;
            ShowQcResult(path, result, log, title, () => DescribeQcResult(result));
            SetStatus(title, () => QcDetail(DescribeQcResult(result)), severity);
            return result.Status is QcStatus.Passed or QcStatus.Cancelled;
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            const string title = "QcWriteFailed";
            ShowQcResult(path, null, log, title, () => ex.Message);
            SetStatus(title, () => QcDetail(ex.Message), InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            qcStop = null;
            if (!closing)
                UpdateControls();
        }
    }

    private void ShowQcResult(string path, QcResult? result, QcCaptureLog? capture, string title, Func<string> summary)
    {
        qcOutputPath = path;
        lastQcResult = result;
        lastQcPresentation = new(capture, title, summary);
        QcFileName.Text = Path.GetFileName(path);
        ToolTipService.SetToolTip(QcFileName, path);
        RenderQcResult();
        QcLogButton.Visibility = result?.CaptureLog is null ? Visibility.Collapsed : Visibility.Visible;
        QcHashButton.Visibility = result?.FrameMd5 is null ? Visibility.Collapsed : Visibility.Visible;
        QcReportButton.Visibility = result?.QcToolsReport is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void QcResultsFlyout_Opened(object sender, object e) => QcOpenStatus.IsOpen = false;

    private async void OpenQcArtifact_Click(object sender, RoutedEventArgs e)
    {
        if (qcOutputPath is not { } output || ((Button)sender).Tag is not string artifact)
            return;

        try
        {
            QcOpenStatus.IsOpen = false;
            var path = artifact switch
            {
                "video" => output,
                "log" => lastQcResult?.CaptureLog,
                "hash" => lastQcResult?.FrameMd5,
                "report" => lastQcResult?.QcToolsReport,
                _ => null
            };
            if (artifact == "folder")
            {
                var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!);
                if (await Windows.System.Launcher.LaunchFolderAsync(folder))
                    return;
            }
            else if (path is not null)
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                if (await Windows.System.Launcher.LaunchFileAsync(file))
                    return;

                var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
                var options = new Windows.System.FolderLauncherOptions();
                WinRT.Interop.InitializeWithWindow.Initialize(options, WinRT.Interop.WindowNative.GetWindowHandle(this));
                options.ItemsToSelect.Add(file);
                if (await Windows.System.Launcher.LaunchFolderAsync(folder, options))
                    return;
            }

            QcOpenStatus.Message = L("QcOpenError");
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            QcOpenStatus.Message = ex.Message;
        }
        QcOpenStatus.IsOpen = true;
    }

    private VideoSignal liveSignal;
    private bool holdRecordingStatus;

    private void UpdateObservedPreviewMode(VideoMode? mode, bool isFallback = false)
    {
        if (observedPreviewMode != mode || observedPreviewModeIsFallback != isFallback)
        {
            observedPreviewMode = mode;
            observedPreviewModeIsFallback = isFallback;
            UpdateConfigurationStatus();
        }

        if (mode is not null)
            UpdatePreviewAspectRatio(mode.Width, mode.Height);
    }

    private void ShowCapture(VideoMode mode, CaptureStats stats)
    {
        var autoMode = ModePicker.SelectedItem is ModeOption { Id: null };
        var recordingAvailabilityChanged = displayedStats?.CanStartRecording != stats.CanStartRecording;
        var recordingInCurrentMode = recordingClock.IsRunning && !stats.RecordingModeChanged;
        var activeMode = !autoMode || stats.CanStartRecording || recordingInCurrentMode ? mode : null;
        UpdateObservedPreviewMode(activeMode, autoMode && activeMode is not null && stats.Signal != VideoSignal.Present);
        ShowCounts(stats);
        if (liveSignal != stats.Signal)
        {
            liveSignal = stats.Signal;
            UpdateLivePreviewStatus();
        }
        if (!previewReady || recordingAvailabilityChanged)
        {
            previewReady = true;
            UpdateControls();
        }

        if (recordingSession && !recordingTransition && stats.RecordingModeChanged)
        {
            _ = FinishRecordingAsync();
            return;
        }

        if (!recordingTransition && !recordingSession && !holdRecordingStatus && qcStop is null)
            ShowLiveSignal();
    }

    private void ShowLiveSignal()
    {
        switch (liveSignal)
        {
            case VideoSignal.NoInput:
                SetStatus("NoInputSignal", ModePicker.SelectedItem is ModeOption { Id: null }
                    ? displayedStats?.CanStartRecording == true ? "AutoNoInputRecordable" : "AutoRecordingHint"
                    : "PreviewingNoInput", InfoBarSeverity.Warning);
                break;
            case VideoSignal.Present:
                if (displayedStats?.CanStartRecording == true)
                    SetStatus("SignalPresent", "ReceivingVideo", InfoBarSeverity.Success);
                else
                    SetStatus("WaitingFrames", "AutoRecordingHint");
                break;
            default:
                SetStatus("WaitingFrames", "WaitingFirstFrame");
                break;
        }
    }

    private void StopRecordingClock()
    {
        if (!recordingClock.IsRunning)
            return;

        recordingClock.Stop();
        UpdateRecordingIndicator(active: false);
        RecordingClockLabel.Text = L("Elapsed");
        UpdateRecordingClock();
    }

    private void UpdateRecordingIndicator(bool active)
    {
        var visual = ElementCompositionPreview.GetElementVisual(RecordingIndicator);
        const string property = nameof(Visual.Opacity);
        visual.StopAnimation(property);
        visual.Opacity = 1;
        if (!active || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            return;

        var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0, 1);
        pulse.InsertKeyFrame(1, 0.45f);
        pulse.Duration = TimeSpan.FromMilliseconds(850);
        pulse.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation(property, pulse);
    }

    private static void RevealWorkspacePanel(UIElement panel)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(panel);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0.7f);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(150);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
    }

    private void UpdateRecordingClock()
    {
        var elapsed = recordingClock.Elapsed;
        var seconds = (long)elapsed.TotalSeconds;
        if (seconds == displayedRecordingSeconds)
            return;

        displayedRecordingSeconds = seconds;
        RecordingElapsed.Text = $"{seconds / 3600:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    private void ResetPreview(PreviewFeed? feed)
    {
        bitmapPixels?.Dispose();
        bitmapPixels = null;
        previewBitmap = null;
        previewFrame = null;
        preview = feed;
        displayedPreviewSkips = 0;
        PreviewImage.Source = null;
        previewCaption = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewDetail.Visibility = Visibility.Collapsed;
    }

    private void PresentPreview()
    {
        if (preview is not { } feed || !feed.TryTakeLatest(ref previewFrame) || previewFrame is not { } frame)
            return;

        if (previewBitmap?.PixelWidth != frame.Width || previewBitmap.PixelHeight != frame.Height)
        {
            bitmapPixels?.Dispose();
            previewBitmap = new WriteableBitmap(frame.Width, frame.Height);
            bitmapPixels = previewBitmap.PixelBuffer.AsStream();
            PreviewImage.Source = previewBitmap;
        }

        UpdatePreviewAspectRatio(frame.SignalWidth, frame.SignalHeight);

        bitmapPixels!.Position = 0;
        bitmapPixels.Write(frame.Pixels, 0, frame.Stride * frame.Height);
        previewBitmap.Invalidate();
        if (PreviewPlaceholder.Visibility == Visibility.Visible)
        {
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewDetail.Visibility = previewFullscreen ? Visibility.Collapsed : Visibility.Visible;
        }
        previewCaption = new(frame.NoInput, frame.SignalWidth, frame.SignalHeight,
            DeckLinkCapture.FormatFourCc((uint)frame.Format), feed.PresentedSequence);
        RenderPreviewCaption();
    }

    private void ShowCounts(CaptureStats stats)
    {
        displayedStats = stats;
        displayedPreviewSkips = preview?.SkippedFrames ?? displayedPreviewSkips;
        RenderCounts(stats);
        ShowMonitoring(stats);
    }

    private void UpdateControls()
    {
        var active = captureTask is not null;
        var idle = settingsReady && !closing && !loading && !pickingOutput && !active && qcStop is null;
        CheckMovButton.IsEnabled = idle && !recordingTransition;
        RefreshButton.IsEnabled = idle;
        DevicePicker.IsEnabled = idle && DevicePicker.Items.Count != 0;
        VideoInputPicker.IsEnabled = idle && VideoInputPicker.Items.Count != 0;
        AudioInputPicker.IsEnabled = idle && AudioInputPicker.Items.Count != 0;
        ModePicker.IsEnabled = idle && ModePicker.Items.Count != 0;
        var canStart = idle && !recordingTransition && DevicePicker.SelectedItem is CaptureDevice &&
            VideoInputPicker.SelectedItem is VideoInputSource && AudioInputPicker.SelectedItem is AudioInputSource &&
            ModePicker.SelectedItem is ModeOption && FormatPicker.SelectedItem is FormatOption &&
            AudioFormatPicker.SelectedItem is AudioFormatOption;
        FormatPicker.IsEnabled = idle;
        AudioFormatPicker.IsEnabled = idle;
        var changingRecording = recordingSession || recordingTransition;
        var canChooseOutput = settingsReady && !closing && !loading && !pickingOutput && !changingRecording;
        OutputPath.IsEnabled = canChooseOutput;
        BrowseButton.IsEnabled = canChooseOutput;
        var previewCanStop = active && !closing && !changingRecording &&
            captureStop is { IsCancellationRequested: false };
        RenderCaptureActions();
        StartButton.IsEnabled = active ? previewCanStop : canStart;
        RecordButton.IsEnabled = qcStop is null && !closing && active && !recordingTransition && (recordingSession ||
                (previewReady && displayedStats?.CanStartRecording == true && previewRecorder is not null && !pickingOutput &&
                    captureStop is { IsCancellationRequested: false } && !string.IsNullOrWhiteSpace(OutputPath.Text)));
        CancelQcButton.IsEnabled = qcStop is { IsCancellationRequested: false };
        CancelQcButton.Visibility = qcStop is null ? Visibility.Collapsed : Visibility.Visible;
        RecordingModeHint.Visibility = ModePicker.SelectedItem is ModeOption { Id: null }
            ? Visibility.Visible : Visibility.Collapsed;
        RecordingClock.Visibility = recordingClock.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        QcResultsButton.Visibility = !changingRecording && qcStop is null && qcOutputPath is not null
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateCaptureFooter();
        UpdateLivePreviewStatus();
        UpdatePreviewForSelection();
        UpdateConfigurationStatus();
    }

    private void UpdateLivePreviewStatus()
    {
        var show = previewReady && (recordingSession || recordingTransition || holdRecordingStatus || qcStop is not null);
        LivePreviewStatus.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            LivePreviewStatus.Text = LocalFormat("LivePreviewStatus", L(liveSignal switch
            {
                VideoSignal.NoInput => "NoInputSignal",
                VideoSignal.Present => "SignalPresent",
                _ => "WaitingFrames"
            }));
            if (recordingClock.IsRunning && liveSignal == VideoSignal.NoInput)
                LivePreviewStatus.Text += " · " + L("RecordingNoInput");
        }
        RenderCompactStatus();
    }

    private void SetStatus(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational) =>
        SetStatus(title, () => L(message), severity);

    private void SetStatus(string title, Func<string> message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        statusTitle = title;
        statusMessage = message;
        SignalStatus.Severity = severity;
        RenderStatus();
    }

    private sealed record ModeOption(string Label, uint? Id);
    private sealed record FormatOption(string Label, string FileTag, CapturePixelFormat Id);
    private sealed record AudioFormatOption(string Label, string FileTag, CaptureAudioFormat Id);

    private static bool IsSessionError(Exception ex) =>
        ex is COMException or InvalidOperationException or InvalidCastException or IOException or
            UnauthorizedAccessException or ArgumentException or OverflowException;
}
