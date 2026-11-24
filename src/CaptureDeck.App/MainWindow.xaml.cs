using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using CaptureDeck.Core;
using CaptureDeck.DeckLink;
using CaptureDeck.QualityControl;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace CaptureDeck.App;

public sealed partial class MainWindow : Window
{
    private static readonly ICaptureBackend<CaptureDevice, DeckLinkCaptureRequest, DeckLinkProgress, CaptureStats>
        DeckLink = new CaptureBackendRegistry(new DeckLinkBackend())
            .Get<DeckLinkBackend>(DeckLinkBackend.BackendId);
    private CancellationTokenSource? captureStop;
    private CancellationTokenSource? qcStop;
    private Task<CaptureStats>? captureTask;
    private readonly DispatcherQueueTimer previewTimer;
    private readonly DispatcherQueueTimer detectedModeTimer;
    private bool checkingDetectedMode;
    private readonly Stopwatch recordingClock = new();
    private long displayedRecordingSeconds;
    private string? qcOutputPath;
    private QcResult? lastQcResult;
    private PreviewFeed? preview;
    private PreviewFrame? previewFrame;
    private WriteableBitmap? previewBitmap;
    private Stream? bitmapPixels;
    private const double DefaultPreviewAspectRatio = 4d / 3;
    private const double StereoMeterGap = 12;
    private double previewAspectRatio = DefaultPreviewAspectRatio;
    private VideoMode? observedPreviewMode;
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
        InitializeComponent();
        MonitorSelector.SelectedItem = PreviewTab;
        InitializeSettings();
        previewTimer = DispatcherQueue.CreateTimer();
        previewTimer.Interval = TimeSpan.FromMilliseconds(1000d / 60);
        previewTimer.Tick += (_, _) =>
        {
            PresentPreview();
            ShowVideoScopes();
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
        var narrow = availableWidth < 600;
        SettingsContent.Width = Math.Min(e.NewSize.Width, 960);
        SettingsRightColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SettingsSections.ColumnSpacing = narrow ? 0 : 32;
        SettingsSections.RowSpacing = narrow ? 20 : 0;
        Grid.SetColumn(OutputAndAppearance, narrow ? 0 : 1);
        Grid.SetRow(OutputAndAppearance, narrow ? 1 : 0);
        var stackFooter = availableWidth < 860;
        var stackQcButton = availableWidth < 425;
        FooterActionsColumn.Width = stackFooter ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        FooterStatusColumn.Width = stackFooter ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FooterLayout.ColumnSpacing = stackFooter ? 0 : 12;
        Grid.SetColumn(FooterStatusPanel, stackFooter ? 0 : 1);
        Grid.SetRow(FooterStatusPanel, stackFooter ? 1 : 0);
        Grid.SetColumn(QcResultsButton, stackQcButton ? 0 : 1);
        Grid.SetRow(QcResultsButton, stackQcButton ? 1 : 0);
        StartActions.Orientation = availableWidth < 260 ? Orientation.Vertical : Orientation.Horizontal;

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

        var meterSpace = StereoMeter.Width + StereoMeterGap;
        const double border = 2;
        var videoSpace = Math.Max(0, previewAvailableWidth - 2 * meterSpace - border);
        var height = Math.Min(videoSpace / previewAspectRatio, Math.Max(0, previewHeightLimit - 28 - border));
        PreviewView.Width = PreviewSurface.Width = height * previewAspectRatio + border;
        PreviewSurface.Height = height + border;

        var rightMargin = Math.Max(0, (previewAvailableWidth - PreviewSurface.Width) / 2 - meterSpace);
        StereoMeter.Margin = new Thickness(0, 0, rightMargin, 0);
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
            if (id is not null && !closing && captureTask is null && previewSelection == selection &&
                source.ModesFor(format.Id).FirstOrDefault(candidate => candidate.Id == id) is { } mode)
                UpdateObservedPreviewMode(mode);
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
        StereoMeter.Visibility = PreviewView.Visibility;
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
        if (loading || captureTask is not null)
            return;

        SavePendingSettings();
        loading = true;
        UpdateControls();
        SetStatus("CheckingDevices", "CheckingDeckLink");
        try
        {
            var result = await Task.Run(() => (Api: DeckLinkCapture.GetApiVersion(),
                Devices: DeckLink.GetDevices(includeCapabilities: true).Where(device => device.HasVideoInput).ToArray()));
            if (closing)
                return;

            sdkVersion = result.Api;
            RenderApiVersion();
            DevicePicker.ItemsSource = result.Devices;
            RestoreDeviceSelections(result.Devices);
            loading = false;
            UpdateControls();
            if (result.Devices.Length == 0)
                SetStatus("NoDevice", "ConnectDevice", InfoBarSeverity.Warning);
            else
                ShowInputSelectionStatus();
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
        if (qcStop is { } stop)
        {
            stop.Cancel();
            SetStatus("CancellingQc", "MovSaved");
            UpdateControls();
            return;
        }

        if (recordingSession)
            await FinishRecordingAsync();
        else
            await BeginRecordingAsync();
    }

    private async Task BeginRecordingAsync()
    {
        if (!previewReady || previewRecorder is not { } recorder || recordingTransition ||
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
            var prefix = $"CaptureDeck_{timestamp}";
            var formats = $"{videoFormat.FileTag}_{audioFormat.FileTag}";
            recordingStarted = await Task.Run(() =>
            {
                var path = Path.Combine(directory, $"{prefix}_{formats}.mov");
                for (var suffix = 2; File.Exists(path); suffix++)
                    path = Path.Combine(directory, $"{prefix}_{suffix}_{formats}.mov");
                return recorder.Start(path);
            });
            if (captureTask is null)
                throw new InvalidOperationException(L("PreviewStoppedOpening"));
            qcOutputPath = null;
            lastQcResult = null;
            recordingSession = true;
            displayedRecordingSeconds = 0;
            RecordingElapsed.Text = "00:00:00";
            RecordingClockLabel.Text = L("RecordingClock");
            recordingClock.Restart();
            ShowLiveSignal();
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
                stats.Events.Select(item => new QcEvent(item.Elapsed, item.Description)).ToArray());
            if (!await RunRecordingQcAsync(saved.Path, log))
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
        var recorder = mode.Id is null ? null : new PreviewRecorder();
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
            SetStatus("CancellingQc", "MovSaved");
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

    private async Task<bool> RunRecordingQcAsync(string path, QcCaptureLog log)
    {
        using var cancellation = new CancellationTokenSource();
        qcStop = cancellation;
        SetStatus("QcChecking", "QcStarting");
        UpdateControls();
        var dispatcher = DispatcherQueue;
        try
        {
            var result = await Task.Run(() => QcReports.RunAsync(path, log, cancellation: cancellation.Token, stageChanged: stage =>
            {
                dispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(qcStop, cancellation) && !cancellation.IsCancellationRequested)
                        SetStatus("QcChecking", $"Qc{stage}");
                });
            }));
            var (title, severity) = result.Status switch
            {
                QcStatus.Passed => ("QcPassed", InfoBarSeverity.Success),
                QcStatus.Cancelled => ("QcCancelled", InfoBarSeverity.Warning),
                QcStatus.ToolsUnavailable => ("QcUnavailable", InfoBarSeverity.Warning),
                _ => ("QcFailed", InfoBarSeverity.Error)
            };
            ShowQcResult(path, result, log, title, () => DescribeQcResult(result));
            SetStatus(title, () => DescribeQcResult(result), severity);
            return result.Status is QcStatus.Passed or QcStatus.Cancelled;
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            const string title = "QcWriteFailed";
            ShowQcResult(path, null, log, title, () => ex.Message);
            SetStatus(title, () => ex.Message, InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            qcStop = null;
        }
    }

    private void ShowQcResult(string path, QcResult? result, QcCaptureLog capture, string title, Func<string> summary)
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

    private void UpdateObservedPreviewMode(VideoMode mode)
    {
        if (observedPreviewMode != mode)
        {
            observedPreviewMode = mode;
            UpdateConfigurationStatus();
        }

        UpdatePreviewAspectRatio(mode.Width, mode.Height);
    }

    private void ShowCapture(VideoMode mode, CaptureStats stats)
    {
        UpdateObservedPreviewMode(mode);
        ShowCounts(stats);
        if (liveSignal != stats.Signal)
        {
            liveSignal = stats.Signal;
            UpdateLivePreviewStatus();
        }
        if (!previewReady)
        {
            previewReady = true;
            UpdateControls();
        }

        if (recordingClock.IsRunning)
            UpdateRecordingClock();
        if (!recordingTransition && (recordingSession || !holdRecordingStatus))
            ShowLiveSignal();
    }

    private void ShowLiveSignal()
    {
        switch (liveSignal)
        {
            case VideoSignal.NoInput:
                SetStatus("NoInputSignal", recordingSession
                    ? "RecordingNoInput"
                    : "PreviewingNoInput", InfoBarSeverity.Warning);
                break;
            case VideoSignal.Present:
                SetStatus("SignalPresent", "ReceivingVideo", InfoBarSeverity.Success);
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
        RecordingClockLabel.Text = L("Elapsed");
        UpdateRecordingClock();
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
            PreviewDetail.Visibility = Visibility.Visible;
        }
        previewCaption = new(frame.NoInput, frame.SignalWidth, frame.SignalHeight,
            DeckLinkCapture.FormatFourCc((uint)frame.Format), feed.PresentedSequence);
        RenderPreviewCaption();
    }

    private void ShowCounts(CaptureStats stats)
    {
        displayedStats = stats;
        RenderCounts(stats);
        ShowMonitoring(stats);
    }

    private void UpdateControls()
    {
        var active = captureTask is not null;
        var idle = settingsReady && !closing && !loading && !pickingOutput && !active;
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
        StartButton.Content = L(active ? "StopPreview" : "StartPreview");
        StartButton.IsEnabled = active ? previewCanStop : canStart;
        RecordButton.Content = L(qcStop is not null ? "CancelQc" : recordingSession ? "StopRecording" : "StartRecording");
        RecordButton.IsEnabled = qcStop is { } qc ? !qc.IsCancellationRequested :
            !closing && active && !recordingTransition && (recordingSession ||
                (previewReady && previewRecorder is not null && !pickingOutput &&
                    captureStop is { IsCancellationRequested: false } && !string.IsNullOrWhiteSpace(OutputPath.Text)));
        RecordingModeHint.Visibility = ModePicker.SelectedItem is ModeOption { Id: null }
            ? Visibility.Visible : Visibility.Collapsed;
        RecordingClock.Visibility = recordingSession ? Visibility.Visible : Visibility.Collapsed;
        QcResultsButton.Visibility = !changingRecording && qcOutputPath is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateLivePreviewStatus();
        UpdatePreviewForSelection();
        UpdateConfigurationStatus();
    }

    private void UpdateLivePreviewStatus()
    {
        var show = previewReady && (recordingTransition || (holdRecordingStatus && !recordingSession));
        LivePreviewStatus.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            LivePreviewStatus.Text = LocalFormat("LivePreviewStatus", L(liveSignal switch
            {
                VideoSignal.NoInput => "NoInputSignal",
                VideoSignal.Present => "SignalPresent",
                _ => "WaitingFrames"
            }));
        }
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
