using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.QualityControl;
using CaptureRack.Recording;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;

namespace CaptureRack.App;

public sealed partial class MainWindow : Window
{
    private enum WorkspaceLayoutMode { Focus, Split, Scopes, Spectrum, Events }
    private enum SplitSecondaryInstrument { Vector, Spectrum, Events }
    private enum ActiveAppPage { Capture, QcCenter, Settings }

    private static readonly DeckLinkBackend DeckLink = new CaptureBackendRegistry(new DeckLinkBackend())
            .Get<DeckLinkBackend>(DeckLinkBackend.BackendId);
    private readonly WorkspaceModuleRegistry moduleRegistry = new();
    private ICaptureWorkspaceModule activeModule;
    private readonly Views.DeckLinkInspectorView deckLinkInspector = new();
    private readonly Controls.PreviewMonitorOptionsBar monitorOptionsBar = new();
    private readonly Views.PlannedBackendStageView plannedStageView = new();
    private readonly Views.DirectShowCapabilitiesView directShowCapabilitiesView = new();
    private readonly Views.FireWireCaptureView fireWireView = new();
    private readonly Views.TestSignalInspectorView testSignalInspector = new();
    private bool IsFireWireActive => activeModule.Descriptor.BackendId == CaptureRack.DirectShow.Windows.FireWire.MsTape.FireWireCapture.BackendId;
    private bool IsDirectShowActive => activeModule.Descriptor.BackendId == CaptureRack.DirectShow.Windows.Capture.DirectShowBackend.BackendId;
    private readonly Views.PlannedBackendInspectorView plannedInspectorView = new();
    private readonly QcSessionHistory qcHistory = new();
    private bool deckLinkScanned;
    private (string Device, string Driver, string Video, string Audio) recordingInput;
    private bool IsDeckLinkActive => activeModule.Descriptor.BackendId == DeckLinkBackend.BackendId;
    private bool IsTestSignalActive => activeModule.Descriptor.BackendId == TestSignal.BackendId;
    private bool HasLiveCapture => IsDeckLinkActive || IsTestSignalActive || LiveMediaSource is not null;
    private CapturePixelFormat RecordingVideoFormat => IsTestSignalActive ? TestSignal.PixelFormat : deckLinkInspector.SelectedVideoFormat;
    private CaptureAudioFormat RecordingAudioFormat => IsTestSignalActive ? TestSignal.AudioFormat : deckLinkInspector.SelectedAudioFormat;

    private CancellationTokenSource? captureStop;
    private CancellationTokenSource? qcStop;
    private Task<CaptureStats>? captureTask;
    private readonly DispatcherQueueTimer previewTimer;
    private readonly DispatcherQueueTimer detectedModeTimer;
    private bool checkingDetectedMode;
    private readonly Stopwatch recordingClock = new();
    private long displayedRecordingSeconds;
    private long displayedPreviewSkips;
    private long lastMissingFrames;
    private string? statusFilePath;
    private QcSessionHistoryEntry? latestQcEntry;
    private QcStage? activeQcStage;
    private PreviewFeed? preview;
    private PreviewFrame? previewFrame;
    private WriteableBitmap? previewBitmap;
    private Stream? bitmapPixels;
    private const double DefaultPreviewAspectRatio = 4d / 3;
    private double previewAspectRatio = DefaultPreviewAspectRatio;
    private double splitPreviewRatio = 0.63;
    private VideoMode? observedPreviewMode;
    private bool observedPreviewModeIsFallback;
    private (long? DeviceId, int? DeviceIndex, uint? Source, uint? Mode, CapturePixelFormat? Format) previewSelection;
    private double previewBoxWidth;
    private double previewBoxHeight;
    private bool loading;
    private bool closing;
    private bool closeAfterQc;
    private bool recordingSession;
    private bool recordingTransition;
    private bool restartPreviewRequested;
    private bool previewReady;
    private PreviewRecorder? previewRecorder;
    private DateTimeOffset recordingStarted;
    private bool pickingOutput;

    private ActiveAppPage activePage = ActiveAppPage.Capture;
    private ActiveAppPage pageBeforeSettings = ActiveAppPage.Capture;
    private WorkspaceLayoutMode workspaceLayout = WorkspaceLayoutMode.Split;
    private SplitSecondaryInstrument splitSecondary = SplitSecondaryInstrument.Vector;
    private bool inspectorPinned = true;
    private bool inspectorOverlayOpen;
    private bool inspectorClosing;
    private int inspectorAnimationGeneration;
    private CompositionAnimation? inspectorSlideAnimation;
    private CompositionAnimation? inspectorOpacityAnimation;
    private bool IsInspectorOpen => (lastWindowWidth >= 1180 && inspectorPinned) || inspectorOverlayOpen;
    private bool showSafeArea;
    private double lastWindowWidth = 1320;

    public MainWindow()
    {
        activeModule = moduleRegistry.DefaultModule;
        previewHoverTimer = DispatcherQueue.CreateTimer();
        previewHoverTimer.Interval = TimeSpan.FromMilliseconds(200);
        previewHoverTimer.IsRepeating = false;
        previewHoverTimer.Tick += (_, _) =>
        {
            previewHoverReady = true;
            UpdatePreviewFullscreenButton();
        };

        rfAudioTimer = DispatcherQueue.CreateTimer();
        rfAudioTimer.Interval = TimeSpan.FromMilliseconds(33);
        rfAudioTimer.Tick += (_, _) => RenderRfAudio();
        InitializeComponent();
        Activated += MainWindow_Activated;
        Closed += OnWindowClosed;
        rfInspector.PreviewOptions.DisplayChanged += RefreshRfInspection;
        rfInspector.PreviewOptions.DecoderChanged += () =>
        {
            if (settingsReady && !closing && IsRfActive && rfPreviewStop is null) DiscardRfInspection();
            if (settingsReady && !closing && IsRfActive && rfPreviewStop is null && rfInspector.PreviewOptions.PreviewType == RfPreviewType.LaserDisc)
                ConfigureRfAudioMeter(newSession: true);
        };
        rfInspector.Changed += () =>
        {
            if (!IsRfActive || !settingsReady || closing) return;
            if (rfWasPlayback != rfInspector.IsPlayback)
            {
                rfWasPlayback = rfInspector.IsPlayback;
                ResetRfMonitor();
            }
#if REALTIME_RF
            if (heldDecoder is not null && rfPreviewStop is null &&
                (!rfInspector.IsPlayback || heldDecoder.Options.InputSampleRateHz != rfInspector.PlaybackFile?.Rate))
                DiscardRfInspection();
#endif
            ConfigureRfAudioMeter();
            UpdateConfigurationStatus();
            RenderCounts(null);
            UpdateRecordingFilenamePreview();
            UpdateControls();
        };
        WireModularViews();
        ConfigureWindowChrome();
        InitializeSettings();
        InitializeSettingsInput();

        previewTimer = DispatcherQueue.CreateTimer();
        previewTimer.Interval = TimeSpan.FromMilliseconds(1000d / 60);
        var lastMonitorTick = 0L;
        previewTimer.Tick += (_, _) =>
        {
            if (IsFireWireActive && fireWireView.ActivePreview is { } active && !ReferenceEquals(preview, active))
            {
                ResetPreview(active);
                ResetMonitoring();
                ApplyPreviewSettingsToLiveFeed();
            }
            PresentPreview();
            var now = Stopwatch.GetTimestamp();
            if (UsesDoubleRatePreview && Stopwatch.GetElapsedTime(lastMonitorTick, now) < TimeSpan.FromMilliseconds(15))
                return;

            lastMonitorTick = now;
            UpdateDecodedMediaMonitoring();
            if (recordingClock.IsRunning)
                UpdateRecordingClock();
        };
        detectedModeTimer = DispatcherQueue.CreateTimer();
        detectedModeTimer.Interval = TimeSpan.FromSeconds(2);
        detectedModeTimer.Tick += async (_, _) => await RefreshDetectedPreviewModeAsync();

        ResizeWindowForDpi(1320, 840);
        AppWindow.Closing += OnClosing;
        ((FrameworkElement)Content).Loaded += OnLoaded;
    }

    private void WireModularViews()
    {
        directShowCapabilitiesView.StateChanged += DecodedMediaStateChanged;
        directShowCapabilitiesView.SettingsPersistenceRequested += (_, _) => SettingsChanged();
        fireWireView.StateChanged += DecodedMediaStateChanged;
        fireWireView.SettingsPersistenceRequested += (_, _) => SettingsChanged();
        testSignalInspector.ScenarioChanged += (_, _) =>
        {
            if (settingsReady && IsTestSignalActive)
            {
                UpdateRecordingFilenamePreview();
                UpdateConfigurationStatus();
                SettingsChanged();
            }
        };
        QcCenterPage.CheckFileRequested += async (_, _) => await PickAndCheckMediaFileAsync();
        QcCenterPage.CancelQcRequested += (_, _) => CancelActiveQc();
        QcCenterPage.FileDropped += async (_, path) =>
        {
            if (qcStop is null && !closing && captureTask is null && !loading && !pickingOutput && !recordingTransition)
                await RunMediaQcFileAsync(path);
        };
        QcCenterPage.OpenArtifactRequested += async (_, tag) => await OpenQcArtifactAsync(tag);
        QcCenterPage.SaveTsReportRequested += async (_, report) => await SaveStreamReportAsync(report.InputPath, ".ts-qc.json", report.ToJson(indented: true));
        QcCenterPage.SaveDvReportRequested += async (_, report) => await SaveStreamReportAsync(report.InputPath, ".dv-qc.json", report.ToJson());
        QcCenterPage.HistoryEntrySelected += (_, id) =>
        {
            if (qcHistory.Find(id) is { } entry)
                QcCenterPage.RenderEntry(entry);
        };
        QcCenterPage.ClearHistoryRequested += (_, _) =>
        {
            qcHistory.Clear();
            latestQcEntry = null;
            QcCenterPage.ClearDisplayedEntry();
            QcCenterPage.RenderHistory(qcHistory.Entries, null);
            RenderQcSummaryBadge();
            UpdateQcNavBadge();
            UpdateControls();
        };

        SettingsPage.BackRequested += (_, _) => TryBackFromSettings();
        SettingsPage.ThemeChanged += (_, theme) => OnThemeSelectionChanged(theme);
        SettingsPage.LanguageChanged += (_, language) => OnLanguageSelectionChanged(language);
        SettingsPage.DefaultLayoutChanged += (_, tag) =>
            SetWorkspaceLayout(tag == "Focus" ? WorkspaceLayoutMode.Focus : WorkspaceLayoutMode.Split, saveIfDefaultable: true);
        SettingsPage.PreviewDeinterlaceChanged += (_, tag) => SetPreviewDeinterlace(tag);
        SettingsPage.PreviewAspectChanged += (_, tag) => SetPreviewAspect(tag);
        SettingsPage.PreviewMatrixChanged += (_, tag) => SetPreviewMatrix(tag);
        SettingsPage.RefreshToolsRequested += (_, _) => RefreshToolchainDiagnostics();
        SettingsPage.OpenSettingsFolderRequested += async (_, _) => await OpenSettingsFolderAsync();

        deckLinkInspector.RefreshDevicesRequested += async (_, _) => await RefreshDevicesAsync();
        deckLinkInspector.VancScanRequested += async (_, _) => await ScanVancAsync();
        deckLinkInspector.SelectionChanged += (_, _) =>
        {
            deckLinkInspector.ShowVancScanStatus("", scanning: false);
            UpdateControls();
            UpdateRecordingFilenamePreview();
            SettingsChanged();
            if (!loading && captureTask is null && IsDeckLinkActive)
                ShowInputSelectionStatus();
        };
        deckLinkInspector.VideoFormatChanged += (_, _) =>
        {
            deckLinkInspector.ShowVancScanStatus("", scanning: false);
            if (IsDeckLinkActive && captureStop is { IsCancellationRequested: false } stop)
            {
                restartPreviewRequested = true;
                SetStatus("StoppingPreview", "ReleasingInput");
                stop.Cancel();
            }
            UpdateRecordingFormat();
        };
        deckLinkInspector.AudioFormatChanged += (_, _) => UpdateRecordingFormat();
        deckLinkInspector.AudioChannelsChanged += (_, _) => UpdateRecordingFormat();
        deckLinkInspector.AnalogSettingsChanged += (_, _) => SettingsChanged();
        deckLinkInspector.VancSettingsChanged += (_, _) => SettingsChanged();

        monitorOptionsBar.DeinterlaceChanged += (_, tag) => SetPreviewDeinterlace(tag);
        monitorOptionsBar.AspectChanged += (_, tag) => SetPreviewAspect(tag);
        monitorOptionsBar.MatrixChanged += (_, tag) => SetPreviewMatrix(tag);
        monitorOptionsBar.SafeAreaChanged += (_, enabled) => SetSafeArea(enabled);
        monitorOptionsBar.Ntsc480GuideChanged += (_, enabled) => SetNtsc480Guide(enabled);
        monitorOptionsBar.AudioMonitorToggled += (_, _) => ToggleAudioMonitor();
        monitorOptionsBar.AudioMonitorVolumeChanged += (_, volume) => SetAudioMonitorVolume(volume);
        monitorOptionsBar.AudioMonitorRouteChanged += (_, route) => SetAudioMonitorRoute(route);
        StereoMeter.MonitorRouteSelected += (_, route) => SetAudioMonitorRoute(route);

        plannedStageView.SwitchToDeckLinkRequested += (_, _) =>
            SelectBackendModule(DeckLinkBackend.BackendId, persist: true);

        MountActiveModuleSlots();
    }

    private void ResizeWindowForDpi(int logicalWidth, int logicalHeight)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var margin = (int)Math.Round(48 * scale);
        var width = Math.Min((int)Math.Round(logicalWidth * scale), Math.Max(800, workArea.Width - margin));
        var height = Math.Min((int)Math.Round(logicalHeight * scale), Math.Max(600, workArea.Height - margin));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint hwnd);

    private void ConfigureWindowChrome()
    {
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        else
            RootShell.Background = ThemeBrush("StudioWindowBackgroundBrush");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        TitleBarBrand.SizeChanged += (_, _) => UpdateSourceSummaryWidth();
        TitleBarModeSwitcher.SizeChanged += (_, _) => UpdateSourceSummaryWidth();
        TitleBarActions.SizeChanged += (_, _) => UpdateSourceSummaryWidth();
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var bar = AppWindow.TitleBar;
            bar.PreferredHeightOption = TitleBarHeightOption.Standard;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        UpdateTitleBarInsets();
    }

    private void UpdateTitleBarInsets()
    {
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var scale = RootShell.XamlRoot?.RasterizationScale ?? 1.0;
            var right = AppWindow.TitleBar.RightInset / scale;
            TitleBarRightInset.Width = new GridLength(Math.Max(136, right));
        }
        UpdateSourceSummaryWidth();
    }

    private void UpdateSourceSummaryWidth()
    {
        const double buttonChrome = 74, draggableSpace = 48, leftSpacing = 20;
        var reserved = TitleBarGrid.Padding.Left + TitleBarBrand.ActualWidth + TitleBarBrand.Margin.Right +
            TitleBarModeSwitcher.ActualWidth + leftSpacing + TitleBarActions.ActualWidth +
            TitleBarActions.Margin.Right + TitleBarRightInset.Width.Value + buttonChrome + draggableSpace;
        ConfigurationSummary.MaxWidth = Math.Clamp(lastWindowWidth - reserved, 0, 520);
    }

    private bool IsDarkTheme() => StudioBrushes.IsDark(RootShell);

    private void UpdateTitleBarTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        var dark = IsDarkTheme();
        var bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 232, 236, 244) : Windows.UI.Color.FromArgb(255, 24, 29, 40);
        bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(28, 255, 255, 255) : Windows.UI.Color.FromArgb(20, 0, 0, 0);
        bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(44, 255, 255, 255) : Windows.UI.Color.FromArgb(32, 0, 0, 0);
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(160, 180, 190, 206) : Windows.UI.Color.FromArgb(160, 82, 93, 113);
        if (!MicaController.IsSupported())
            RootShell.Background = ThemeBrush("StudioWindowBackgroundBrush");
    }

    private Brush ThemeBrush(string key) => StudioBrushes.Resolve(RootShell, key);

    private void RefreshDynamicThemeBrushes()
    {
        UpdateTitleBarTheme();
        UpdateNavigationStyles();
        ApplyWorkspaceLayout();
        deckLinkInspector.RefreshTheme();
        directShowCapabilitiesView.RefreshTheme();
        fireWireView.RefreshTheme();
        monitorOptionsBar.RefreshTheme();
        RefreshMonitoringTheme();
        UpdateScopeModeButtons();
        UpdateInspectorLayout();
        ApplySafeAreaOverlay();
        RenderSourceRackList();
        RenderCaptureActions();
        RenderCounts(HasLiveCapture ? displayedStats : null);
        RenderCompactStatus();
        RenderQcSummaryBadge();
        UpdateQcNavBadge();
        UpdateConfigurationStatus();
        RefreshToolchainDiagnostics();
        StereoMeter.RefreshTheme();
        QcCenterPage.RefreshTheme();
        SplitResizeGrip.Background = ThemeBrush("StudioHairlineBrush");
        UpdateRecordingIndicator(recordingClock.IsRunning);
    }

    private void MountActiveModuleSlots()
    {
        InspectorSlot.Content = LiveMediaSource as UserControl ?? (IsDeckLinkActive ? deckLinkInspector :
            IsTestSignalActive ? testSignalInspector : IsRfActive ? rfInspector : plannedInspectorView);
        RfOptionsSlot.Content = IsRfActive ? rfInspector.PreviewOptions : null;
        MonitorBarSlot.Content = IsRfActive || HasLiveCapture ? monitorOptionsBar : null;
        MonitorBarSlot.Visibility = IsRfActive || HasLiveCapture ? Visibility.Visible : Visibility.Collapsed;
        MonitorViewport.Visibility = HasLiveCapture || IsRfActive ? Visibility.Visible : Visibility.Collapsed;
        StageSlot.Content = HasLiveCapture ? null : plannedStageView;
        RfStage.Visibility = IsRfActive ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = Visibility.Visible;
        StageSlot.Visibility = HasLiveCapture || IsRfActive ? Visibility.Collapsed : Visibility.Visible;

        if (IsDirectShowActive)
        {
            directShowCapabilitiesView.SetRecordingTarget(() => OutputPath.Text.Trim(), GetRecordingLabel,
                () => ReadRecordingEncoding(), () => SelectedRecordingOutput);
            directShowCapabilitiesView.Bind(L);
            directShowCapabilitiesView.RefreshTheme();
        }
        else if (IsFireWireActive)
        {
            fireWireView.Bind(L, () => OutputPath.Text.Trim(), GetRecordingLabel);
            fireWireView.RefreshTheme();
        }
        else if (IsDeckLinkActive)
        {
            deckLinkInspector.RefreshTheme();
        }
        else if (IsRfActive)
        {
            rfInspector.Bind(L);
            RfHeading.Text = L("RfWaveform");
        }
        else if (!HasLiveCapture)
        {
            plannedInspectorView.Bind(activeModule.Descriptor, L);
            plannedStageView.Bind(activeModule.Descriptor, L);
        }

        if (LiveMediaSource is null)
        {
            PreviewPlaceholderText.Text = L("WaitingPreview");
            PreviewPlaceholderHint.Text = L("PreviewBeforeRecording");
        }
        else
        {
            PreviewPlaceholderText.Text = L(LiveMediaSource.PreviewHint ?? "WaitingPreview");
            PreviewPlaceholderHint.Text = "";
        }
    }

    private void SelectBackendModule(string backendId, bool persist)
    {
        if (rfPreviewStop is not null || rfStop is not null || LiveMediaSource is { IsCapturing: true } or { IsRecording: true } ||
            !WorkspaceModuleRegistry.CanSwitchBackend(captureTask is not null || vancScanTask is not null || restartPreviewRequested,
            recordingSession, recordingTransition, qcStop is not null))
            return;

        var target = moduleRegistry.Resolve(backendId);
        var changed = target.Descriptor.BackendId != activeModule.Descriptor.BackendId;
        activeModule = target;
        if (changed)
        {
            DiscardRfInspection();
            if (previewFullscreen)
                SetPreviewFullscreen(false);
            previewSelection = default;
            displayedStats = null;
            observedPreviewMode = null;
            observedPreviewModeIsFallback = false;
            appliedDecodedCapabilities = null;
            decodedInputReady = decodedFieldOrderAvailable = false;
            ResetPreview(null);
            UpdatePreviewAspectRatio(0, 0);
            ResetMonitoring();
        }

        MountActiveModuleSlots();
        ApplyModuleCapabilities();
        RenderSourceRackList();
        RenderCounts(HasLiveCapture ? displayedStats : null);
        UpdateControls();
        UpdateConfigurationStatus();

        if (changed && !loading && captureTask is null)
            ShowInputSelectionStatus();

        if (persist && changed)
            SettingsChanged();
        if (changed && IsDeckLinkActive && !deckLinkScanned)
            _ = RefreshDevicesAsync();
    }

    private void SourceRackFlyout_Opening(object sender, object e) => RenderSourceRackList();

    private void SourceRackItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string backendId })
            return;

        SourceRackFlyout.Hide();
        if (activePage != ActiveAppPage.Capture)
            NavigateToPage(ActiveAppPage.Capture);
        SelectBackendModule(backendId, persist: true);
    }

    private void StageToolbar_SizeChanged(object sender, SizeChangedEventArgs args) => ArrangeStageOptions();

    private void ArrangeStageOptions()
    {
        bool wrap = IsRfActive && StageToolbar.ActualWidth < 980;
        Grid.SetRow(StageOptionsScroll, wrap ? 1 : 0);
        Grid.SetColumn(StageOptionsScroll, wrap ? 0 : 1);
        Grid.SetColumnSpan(StageOptionsScroll, wrap ? 2 : 1);
        StageOptionsScroll.HorizontalAlignment = wrap ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
    }

    private void ApplyModuleCapabilities()
    {
        var caps = ActiveWorkspaceCapabilities;
        ArrangeStageOptions();
        var liveStage = HasLiveCapture;
        LayoutSegmentTrack.Visibility = liveStage && (caps.SupportsVideoPreview || caps.SupportsAudioSpectrum || caps.SupportsEvents)
            ? Visibility.Visible : Visibility.Collapsed;
        StageSignalBadge.Visibility = liveStage ? Visibility.Visible : Visibility.Collapsed;
        LayoutFocusBtn.Visibility = liveStage && caps.SupportsVideoPreview ? Visibility.Visible : Visibility.Collapsed;
        LayoutSplitBtn.Visibility = liveStage && caps.SupportsVideoPreview && caps.SupportsVideoScopes ? Visibility.Visible : Visibility.Collapsed;
        LayoutScopesBtn.Visibility = liveStage && caps.SupportsVideoScopes ? Visibility.Visible : Visibility.Collapsed;
        LayoutSpectrumBtn.Visibility = liveStage && caps.SupportsAudioSpectrum ? Visibility.Visible : Visibility.Collapsed;
        LayoutEventsBtn.Visibility = liveStage && caps.SupportsEvents ? Visibility.Visible : Visibility.Collapsed;
        if (IsRfActive)
            LayoutSegmentTrack.Visibility = LayoutFocusBtn.Visibility = LayoutSplitBtn.Visibility = LayoutScopesBtn.Visibility = LayoutSpectrumBtn.Visibility = Visibility.Visible;
        monitorOptionsBar.ApplyCapabilities(caps, LiveMediaSource?.HasDeinterlace ?? true,
            matrix: !IsRfActive && LiveMediaSource is null, ntscGuide: LiveMediaSource is null);
        SplitSpectrumBtn.Visibility = IsRfActive || caps.SupportsAudioSpectrum ? Visibility.Visible : Visibility.Collapsed;
        SplitEventsBtn.Visibility = caps.SupportsEvents ? Visibility.Visible : Visibility.Collapsed;
        ApplyWorkspaceLayout();
        RfMeasureButton.Visibility = IsRfActive ? Visibility.Visible : Visibility.Collapsed;
        UpdateResponsiveFooter(lastWindowWidth);
        ConfigureRfAudioMeter();
        UpdateRecordingFilenamePreview();
    }

    private bool rackShowsRecording;
    private bool CapturingToFile => rfStop is { IsCancellationRequested: false } && rfWriterActive ||
        recordingSession && !recordingTransition || LiveMediaSource is { IsRecording: true, IsFinalizing: false };

    private void RenderSourceRackList()
    {
        var canSwitch = rfPreviewStop is null && rfStop is null && LiveMediaSource is not ({ IsCapturing: true } or { IsRecording: true }) && WorkspaceModuleRegistry.CanSwitchBackend(
            captureTask is not null || vancScanTask is not null || restartPreviewRequested, recordingSession, recordingTransition, qcStop is not null);
        SourceRackLockNotice.Text = L("SourceRackLockedNotice");
        SourceRackLockNotice.Visibility = canSwitch ? Visibility.Collapsed : Visibility.Visible;

        rackShowsRecording = CapturingToFile;
        SourceRackList.ItemsSource = moduleRegistry.Modules.Select(module =>
        {
            var source = module.Descriptor;
            var selected = source.BackendId == activeModule.Descriptor.BackendId;
            bool writing = selected && rackShowsRecording;
            return new SourceRackRow(
                source.BackendId,
                source.IconGlyph,
                L(source.DisplayNameKey),
                L(source.SubtitleKey),
                L(writing ? "RecordingClock" : "SourceStatusPlanned"),
                writing || !source.IsImplementedInUi ? Visibility.Visible : Visibility.Collapsed,
                ThemeBrush(writing ? "StudioRecSoftBrush" : "StudioCardBrush"),
                ThemeBrush(writing ? "StudioRecTextBrush" : "StudioTextMutedBrush"),
                canSwitch,
                ThemeBrush(selected ? "StudioAccentSoftBrush" : "StudioPanelSecondaryBrush"),
                ThemeBrush(selected ? "StudioAccentBrush" : "StudioHairlineBrush"),
                ThemeBrush(selected ? "StudioAccentBrush" : "StudioTextSecondaryBrush"));
        }).ToArray();
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        lastWindowWidth = e.NewSize.Width;
        UpdateTitleBarInsets();
        UpdateInspectorLayout();
        UpdateResponsiveFooter(e.NewSize.Width);
        UpdateMonitorLayout();
    }

    private void UpdateResponsiveFooter(double width)
    {
        var compact = width < (IsRfActive ? 1280 : 1040);
        var narrow = width < 760;
        FooterCenterColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FooterLayout.RowSpacing = compact ? 6 : 0;
        Grid.SetRow(FooterTelemetry, compact ? 1 : 0);
        Grid.SetColumn(FooterTelemetry, compact ? 0 : 1);
        Grid.SetColumnSpan(FooterTelemetry, compact ? 3 : 1);
        FooterTelemetry.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        FooterStorageButton.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        ConfigurationSummary.Visibility = width < 820 ? Visibility.Collapsed : Visibility.Visible;
        UpdateAudioMeterVisibility(width);
    }

    private void StopInspectorAnimations()
    {
        inspectorAnimationGeneration++;
        inspectorClosing = false;
        if (inspectorSlideAnimation is not null)
        {
            InspectorPanel.StopAnimation(inspectorSlideAnimation);
            inspectorSlideAnimation = null;
        }
        if (inspectorOpacityAnimation is not null)
        {
            InspectorPanel.StopAnimation(inspectorOpacityAnimation);
            inspectorOpacityAnimation = null;
        }
        InspectorPanel.Translation = Vector3.Zero;
        InspectorPanel.Opacity = 1;
        var scrimVisual = ElementCompositionPreview.GetElementVisual(InspectorScrim);
        scrimVisual.StopAnimation(nameof(Visual.Opacity));
        scrimVisual.Opacity = 1f;
    }

    private void UpdateInspectorLayout()
    {
        if (inspectorClosing || inspectorSlideAnimation is not null || inspectorOpacityAnimation is not null)
            StopInspectorAnimations();

        var canDock = lastWindowWidth >= 1180;
        if (canDock && inspectorPinned)
        {
            InspectorDockColumn.Width = GridLength.Auto;
            Grid.SetColumn(InspectorPanel, 2);
            Grid.SetColumnSpan(InspectorPanel, 1);
            InspectorPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            InspectorPanel.Margin = new Thickness(0);
            InspectorPanel.Visibility = Visibility.Visible;
            InspectorScrim.Visibility = Visibility.Collapsed;
            inspectorOverlayOpen = false;
        }
        else if (inspectorOverlayOpen)
        {
            InspectorDockColumn.Width = new GridLength(0);
            Grid.SetColumn(InspectorPanel, 0);
            Grid.SetColumnSpan(InspectorPanel, 3);
            InspectorPanel.HorizontalAlignment = HorizontalAlignment.Right;
            InspectorPanel.Margin = new Thickness(0);
            InspectorPanel.Visibility = Visibility.Visible;
            InspectorScrim.Visibility = Visibility.Visible;
        }
        else
        {
            InspectorDockColumn.Width = new GridLength(0);
            InspectorPanel.Visibility = Visibility.Collapsed;
            InspectorScrim.Visibility = Visibility.Collapsed;
        }

        UpdateInspectorButtonsVisuals(canDock);
        UpdateWheelFallbackState();
    }

    private void UpdateInspectorButtonsVisuals(bool canDock)
    {
        var open = InspectorPanel.Visibility == Visibility.Visible;
        PinInspectorButton.Visibility = canDock ? Visibility.Visible : Visibility.Collapsed;
        PinInspectorIcon.Glyph = inspectorPinned ? "\uE840" : "\uE718";
        ToggleInspectorButton.Background = open
            ? ThemeBrush("StudioSegmentActiveBrush")
            : new SolidColorBrush(Colors.Transparent);
    }

    private void ToggleInspector()
    {
        if (activePage != ActiveAppPage.Capture)
            NavigateToPage(ActiveAppPage.Capture);

        var wasOpen = IsInspectorOpen;
        var canDock = lastWindowWidth >= 1180;
        if (canDock)
        {
            inspectorPinned = !inspectorPinned;
            inspectorOverlayOpen = false;
            SettingsChanged();
        }
        else
        {
            inspectorOverlayOpen = !inspectorOverlayOpen;
        }

        var willBeOpen = IsInspectorOpen;
        if (wasOpen && !willBeOpen)
        {
            AnimateInspectorCloseAndHide();
            return;
        }

        inspectorClosing = false;
        inspectorAnimationGeneration++;
        UpdateInspectorLayout();
        if (InspectorPanel.Visibility == Visibility.Visible)
            AnimateInspectorSlideIn();
    }

    private void AnimateInspectorCloseAndHide()
    {
        if (inspectorClosing)
            return;

        if (!Controls.StudioFeedback.AnimationsEnabled || InspectorPanel.Visibility != Visibility.Visible)
        {
            StopInspectorAnimations();
            UpdateInspectorLayout();
            return;
        }

        StopInspectorAnimations();
        inspectorClosing = true;
        var generation = ++inspectorAnimationGeneration;
        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0.0f), new Vector2(1.0f, 1.0f));
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = nameof(UIElement.Translation);
        slide.InsertKeyFrame(0f, Vector3.Zero);
        slide.InsertKeyFrame(1f, new Vector3(24f, 0f, 0f), easing);
        slide.Duration = TimeSpan.FromMilliseconds(140);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = nameof(UIElement.Opacity);
        opacity.InsertKeyFrame(0f, 1f);
        opacity.InsertKeyFrame(1f, 0f, easing);
        opacity.Duration = TimeSpan.FromMilliseconds(140);

        inspectorSlideAnimation = slide;
        inspectorOpacityAnimation = opacity;
        InspectorPanel.StartAnimation(slide);
        InspectorPanel.StartAnimation(opacity);

        if (InspectorScrim.Visibility == Visibility.Visible)
        {
            var scrimVisual = ElementCompositionPreview.GetElementVisual(InspectorScrim);
            var scrimFade = compositor.CreateScalarKeyFrameAnimation();
            scrimFade.InsertKeyFrame(0f, 1f);
            scrimFade.InsertKeyFrame(1f, 0f, easing);
            scrimFade.Duration = TimeSpan.FromMilliseconds(140);
            scrimVisual.StartAnimation(nameof(Visual.Opacity), scrimFade);
        }

        batch.Completed += (_, _) =>
        {
            if (closing || generation != inspectorAnimationGeneration)
                return;
            StopInspectorAnimations();
            UpdateInspectorLayout();
        };
        batch.End();
    }

    private void AnimateInspectorSlideIn()
    {
        StopInspectorAnimations();
        if (!Controls.StudioFeedback.AnimationsEnabled)
            return;

        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = nameof(UIElement.Translation);
        slide.InsertKeyFrame(0f, new Vector3(28f, 0f, 0f));
        slide.InsertKeyFrame(1f, Vector3.Zero, easing);
        slide.Duration = TimeSpan.FromMilliseconds(210);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = nameof(UIElement.Opacity);
        opacity.InsertKeyFrame(0f, 0.2f);
        opacity.InsertKeyFrame(1f, 1f, easing);
        opacity.Duration = TimeSpan.FromMilliseconds(180);

        inspectorSlideAnimation = slide;
        inspectorOpacityAnimation = opacity;
        InspectorPanel.StartAnimation(slide);
        InspectorPanel.StartAnimation(opacity);

        if (InspectorScrim.Visibility == Visibility.Visible)
        {
            var scrimVisual = ElementCompositionPreview.GetElementVisual(InspectorScrim);
            var scrimFade = compositor.CreateScalarKeyFrameAnimation();
            scrimFade.InsertKeyFrame(0f, 0f);
            scrimFade.InsertKeyFrame(1f, 1f, easing);
            scrimFade.Duration = TimeSpan.FromMilliseconds(180);
            scrimVisual.StartAnimation(nameof(Visual.Opacity), scrimFade);
        }
    }

    private void OpenInspectorFromFooter_Click(object sender, RoutedEventArgs e)
    {
        if (activePage != ActiveAppPage.Capture)
            NavigateToPage(ActiveAppPage.Capture);

        if (!IsInspectorOpen)
            ToggleInspector();
    }

    private void ToggleInspectorButton_Click(object sender, RoutedEventArgs e) => ToggleInspector();

    private void PinInspectorButton_Click(object sender, RoutedEventArgs e)
    {
        inspectorPinned = !inspectorPinned;
        inspectorOverlayOpen = !inspectorPinned;
        SettingsChanged();
        UpdateInspectorLayout();
    }

    private void CloseInspectorButton_Click(object sender, RoutedEventArgs e)
    {
        inspectorPinned = false;
        inspectorOverlayOpen = false;
        SettingsChanged();
        AnimateInspectorCloseAndHide();
    }

    private void InspectorScrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        inspectorOverlayOpen = false;
        AnimateInspectorCloseAndHide();
    }

    private void CaptureModeButton_Click(object sender, RoutedEventArgs e) => NavigateToPage(ActiveAppPage.Capture);

    private void QcModeButton_Click(object sender, RoutedEventArgs e) => NavigateToPage(ActiveAppPage.QcCenter);

    private void QcResultsButton_Click(object sender, RoutedEventArgs e) => NavigateToPage(ActiveAppPage.QcCenter);

    private void NavigateToPage(ActiveAppPage target)
    {
        if (activePage == target || LiveMediaSource?.IsRecording == true)
            return;

        if (target != ActiveAppPage.Capture && previewFullscreen)
            SetPreviewFullscreen(false);

        if (activePage == ActiveAppPage.Settings)
            SavePendingSettings();
        if (target == ActiveAppPage.Settings)
            pageBeforeSettings = activePage;

        activePage = target;
        WorkspacePage.Visibility = target == ActiveAppPage.Capture ? Visibility.Visible : Visibility.Collapsed;
        QcCenterPage.Visibility = target == ActiveAppPage.QcCenter ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = target == ActiveAppPage.Settings ? Visibility.Visible : Visibility.Collapsed;

        if (target == ActiveAppPage.Settings)
        {
            RefreshToolchainDiagnostics();
            RevealWorkspacePanel(SettingsPage);
            SettingsPage.FocusBack();
        }
        else if (target == ActiveAppPage.QcCenter)
        {
            RevealWorkspacePanel(QcCenterPage);
        }
        else
        {
            RevealWorkspacePanel(WorkspacePage);
            UpdateMonitorLayout();
        }

        UpdateNavigationStyles();
        UpdateCaptureFooter();
        UpdateWheelFallbackState();
    }

    private void UpdateNavigationStyles()
    {
        var locked = LiveMediaSource?.IsRecording == true;
        QcModeButton.IsEnabled = OpenSettingsButton.IsEnabled = !locked;
        ToolTipService.SetToolTip(QcModeButton, locked ? L("NavigationLockedRecording") : null);
        ToolTipService.SetToolTip(OpenSettingsButton, locked ? L("NavigationLockedRecording") : L("Settings"));
        ApplySegmentState(CaptureModeButton, activePage == ActiveAppPage.Capture);
        ApplySegmentState(QcModeButton, activePage == ActiveAppPage.QcCenter);
        OpenSettingsButton.Background = activePage == ActiveAppPage.Settings
            ? ThemeBrush("StudioSegmentActiveBrush")
            : new SolidColorBrush(Colors.Transparent);
    }

    private void ApplySegmentState(Button button, bool active)
    {
        button.Background = active ? ThemeBrush("StudioSegmentActiveBrush") : new SolidColorBrush(Colors.Transparent);
        button.BorderBrush = active ? ThemeBrush("StudioHairlineBrush") : new SolidColorBrush(Colors.Transparent);
        button.Foreground = active ? ThemeBrush("StudioTextPrimaryBrush") : ThemeBrush("StudioTextSecondaryBrush");
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void LayoutModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<WorkspaceLayoutMode>(tag, out var mode))
            SetWorkspaceLayout(mode, saveIfDefaultable: true);
    }

    private void SetWorkspaceLayout(WorkspaceLayoutMode mode, bool saveIfDefaultable)
    {
        workspaceLayout = mode;
        ApplyWorkspaceLayout();
        SyncSettingsPageSelections();
        if (saveIfDefaultable && mode is WorkspaceLayoutMode.Split or WorkspaceLayoutMode.Focus)
            SettingsChanged();
    }

    private void ApplyWorkspaceLayout()
    {
        var layout = EffectiveWorkspaceLayout;
        ApplySegmentState(LayoutFocusBtn, layout == WorkspaceLayoutMode.Focus);
        ApplySegmentState(LayoutSplitBtn, layout == WorkspaceLayoutMode.Split);
        ApplySegmentState(LayoutScopesBtn, layout == WorkspaceLayoutMode.Scopes);
        ApplySegmentState(LayoutSpectrumBtn, layout == WorkspaceLayoutMode.Spectrum);
        ApplySegmentState(LayoutEventsBtn, layout == WorkspaceLayoutMode.Events);

        RfMeterRack.Visibility = IsRfActive ? Visibility.Visible : Visibility.Collapsed;
        RfAdcRow.Height = IsRfActive && layout is WorkspaceLayoutMode.Split or WorkspaceLayoutMode.Spectrum or WorkspaceLayoutMode.Scopes ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RfStage.Visibility = RfAdcRow.Height.Value > 0 ? Visibility.Visible : Visibility.Collapsed;
        var showPreview = layout is WorkspaceLayoutMode.Focus or WorkspaceLayoutMode.Split;
        var showInstruments = layout != WorkspaceLayoutMode.Focus;
        var showSplitDivider = layout == WorkspaceLayoutMode.Split;

        PreviewMonitorHousing.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        SplitResizeHandle.Visibility = showSplitDivider ? Visibility.Visible : Visibility.Collapsed;
        InstrumentsHost.Visibility = showInstruments ? Visibility.Visible : Visibility.Collapsed;

        if (layout == WorkspaceLayoutMode.Focus)
        {
            PreviewStageRow.Height = new GridLength(1, GridUnitType.Star);
            SplitDividerRow.Height = new GridLength(0);
            InstrumentsStageRow.Height = new GridLength(0);
        }
        else if (layout == WorkspaceLayoutMode.Split)
        {
            var ratio = Math.Clamp(splitPreviewRatio, 0.35, 0.82);
            PreviewStageRow.Height = new GridLength(ratio, GridUnitType.Star);
            SplitDividerRow.Height = GridLength.Auto;
            InstrumentsStageRow.Height = new GridLength(1.0 - ratio, GridUnitType.Star);
        }
        else
        {
            PreviewStageRow.Height = new GridLength(0);
            SplitDividerRow.Height = new GridLength(0);
            InstrumentsStageRow.Height = new GridLength(1, GridUnitType.Star);
        }

        if (showInstruments)
        {
            var twoColumns = layout is WorkspaceLayoutMode.Split or WorkspaceLayoutMode.Scopes;
            LumaCard.Visibility = twoColumns ? Visibility.Visible : Visibility.Collapsed;
            InstrumentCol0.Width = twoColumns ? new GridLength(56, GridUnitType.Star) : new GridLength(0);
            InstrumentCol1.Width = twoColumns ? new GridLength(44, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
            InstrumentsHost.ColumnSpacing = twoColumns ? 8 : 0;

            SplitRightSwitcher.Visibility = layout == WorkspaceLayoutMode.Split
                ? Visibility.Visible : Visibility.Collapsed;

            var secondary = layout switch
            {
                WorkspaceLayoutMode.Scopes => SplitSecondaryInstrument.Vector,
                WorkspaceLayoutMode.Spectrum => SplitSecondaryInstrument.Spectrum,
                WorkspaceLayoutMode.Events => SplitSecondaryInstrument.Events,
                _ => EffectiveSplitInstrument
            };
            ApplySecondaryInstrument(secondary);
        }

        UpdateMonitorLayout();
    }

    private void SplitResizeHandle_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (workspaceLayout != WorkspaceLayoutMode.Split)
            return;

        var totalHeight = MonitorViewport.ActualHeight - SplitResizeHandle.ActualHeight;
        if (totalHeight <= 100)
            return;

        var currentPreviewHeight = splitPreviewRatio * totalHeight;
        var updated = Math.Clamp((currentPreviewHeight + e.Delta.Translation.Y) / totalHeight, 0.35, 0.82);
        if (Math.Abs(updated - splitPreviewRatio) < 0.002)
            return;

        splitPreviewRatio = updated;
        PreviewStageRow.Height = new GridLength(splitPreviewRatio, GridUnitType.Star);
        InstrumentsStageRow.Height = new GridLength(1.0 - splitPreviewRatio, GridUnitType.Star);
    }

    private void SplitResizeHandle_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e) =>
        SettingsChanged();

    private void SplitResizeHandle_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        splitPreviewRatio = 0.63;
        ApplyWorkspaceLayout();
        SettingsChanged();
    }

    private void SplitResizeHandle_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SplitResizeGrip.Background = ThemeBrush("StudioAccentBrush");

    private void SplitResizeHandle_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SplitResizeGrip.Background = ThemeBrush("StudioHairlineBrush");

    private void SplitSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<SplitSecondaryInstrument>(tag, out var instrument))
        {
            splitSecondary = instrument;
            ApplySecondaryInstrument(instrument);
        }
    }

    private void ApplySecondaryInstrument(SplitSecondaryInstrument instrument)
    {
        var previousVisible = VectorBody.Visibility == Visibility.Visible ? SplitSecondaryInstrument.Vector
            : SpectrumBody.Visibility == Visibility.Visible ? SplitSecondaryInstrument.Spectrum
            : SplitSecondaryInstrument.Events;

        ApplySegmentState(SplitVectorBtn, instrument == SplitSecondaryInstrument.Vector);
        ApplySegmentState(SplitSpectrumBtn, instrument == SplitSecondaryInstrument.Spectrum);
        ApplySegmentState(SplitEventsBtn, instrument == SplitSecondaryInstrument.Events);

        VectorBody.Visibility = instrument == SplitSecondaryInstrument.Vector ? Visibility.Visible : Visibility.Collapsed;
        SpectrumBody.Visibility = instrument == SplitSecondaryInstrument.Spectrum ? Visibility.Visible : Visibility.Collapsed;
        UpdateRfSpectrumSource();
        EventsBody.Visibility = instrument == SplitSecondaryInstrument.Events ? Visibility.Visible : Visibility.Collapsed;
        EventsToolbar.Visibility = instrument == SplitSecondaryInstrument.Events && EffectiveWorkspaceLayout == WorkspaceLayoutMode.Events
            ? Visibility.Visible : Visibility.Collapsed;

        if (previousVisible != instrument)
        {
            UIElement activeBody = instrument switch
            {
                SplitSecondaryInstrument.Spectrum => SpectrumBody,
                SplitSecondaryInstrument.Events => EventsBody,
                _ => VectorBody
            };
            Controls.StudioFeedback.RevealSubtle(activeBody, offsetY: 4f, durationMs: 130);
        }

        UpdateSecondaryInstrumentHeader(instrument);
    }

    private void ApplySafeAreaOverlay()
    {
        SafeAreaOverlay.Visibility = showSafeArea && (!IsRfActive || previewBitmap is not null) && PreviewPlaceholder.Visibility == Visibility.Collapsed
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateNtsc480GuideLayout();
        SyncFullscreenPreviewWindow();
    }

    private void MonitorViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateMonitorLayout();

    private void PreviewMonitorHousing_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        previewBoxWidth = e.NewSize.Width;
        previewBoxHeight = e.NewSize.Height;
        UpdateMonitorLayout();
    }

    private void UpdateMonitorLayout()
    {
        var (width, height) = FullscreenRenderPolicy.ComputePreviewSurfaceSize(
            previewBoxWidth, previewBoxHeight, previewAspectRatio, padding: 14);
        if (width > 0 && height > 0)
        {
            PreviewSurface.Width = width;
            PreviewSurface.Height = height;
            UpdateNtsc480GuideLayout();
        }
        fullscreenWindow?.UpdateGeometry(previewAspectRatio, showSafeArea, NtscGuideEnabled, IsRfActive ? rfGuide : null);
    }

    private void UpdatePreviewAspectRatio(int width, int height)
    {
        lastSignalWidth = width;
        lastSignalHeight = height;
        var aspectRatio = ComputeEffectivePreviewAspectRatio(width, height);
        if (Math.Abs(previewAspectRatio - aspectRatio) < 0.001)
            return;

        previewAspectRatio = aspectRatio;
        UpdateMonitorLayout();
    }

    private void UpdatePreviewForSelection()
    {
        if (captureTask is not null || loading || !settingsReady)
            return;
        if (IsTestSignalActive)
        {
            UpdatePreviewAspectRatio(TestSignal.Mode.Width, TestSignal.Mode.Height);
            return;
        }
        if (!IsDeckLinkActive)
            return;

        var device = deckLinkInspector.SelectedDevice;
        var source = deckLinkInspector.SelectedVideoSource;
        var selectedMode = deckLinkInspector.SelectedMode;
        var selection = (device?.PersistentId, device?.Index, source?.Id, selectedMode?.Id, deckLinkInspector.SelectedVideoFormat);
        if (previewSelection == selection)
            return;

        previewSelection = selection;
        observedPreviewMode = null;
        var mode = selectedMode?.Id is { } id
            ? source?.ModesFor(deckLinkInspector.SelectedVideoFormat).FirstOrDefault(candidate => candidate.Id == id) : null;
        ResetPreview(null);
        UpdatePreviewScanMode(mode?.IsInterlaced);
        UpdatePreviewAspectRatio(mode?.Width ?? 0, mode?.Height ?? 0);
        if (selectedMode is { Id: null })
            _ = RefreshDetectedPreviewModeAsync();
    }

    private async Task RefreshDetectedPreviewModeAsync()
    {
        if (checkingDetectedMode || captureTask is not null || vancScanTask is not null || loading || closing || !IsDeckLinkActive ||
            deckLinkInspector.SelectedMode is not { Id: null } ||
            deckLinkInspector.SelectedDevice is not { } device ||
            deckLinkInspector.SelectedVideoSource is not { } source)
            return;

        var selection = previewSelection;
        checkingDetectedMode = true;
        try
        {
            var id = await Task.Run(() => DeckLinkCapture.GetLockedVideoInputMode(device, source.Id));
            if (!closing && captureTask is null && vancScanTask is null && previewSelection == selection)
                UpdateObservedPreviewMode(id is null ? null :
                    source.ModesFor(deckLinkInspector.SelectedVideoFormat).FirstOrDefault(candidate => candidate.Id == id));
        }
        catch (COMException)
        {
        }
        finally
        {
            checkingDetectedMode = false;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)sender).Loaded -= OnLoaded;
        RootShell.ActualThemeChanged += (_, _) => RefreshDynamicThemeBrushes();
        await RestoreSettingsAsync();
        if (closing)
            return;
        MountActiveModuleSlots();
        ApplyModuleCapabilities();
        UpdateTitleBarTheme();
        UpdateNavigationStyles();
        ApplyWorkspaceLayout();
        UpdateInspectorLayout();
        ApplySafeAreaOverlay();
        ApplyPreviewOptionsToUi();
        UpdateScopeModeButtons();
        RenderSourceRackList();
        RefreshToolchainDiagnostics();
        if (IsDeckLinkActive)
            await RefreshDevicesAsync();
        else
        {
            UpdateControls();
            ShowInputSelectionStatus();
        }
        if (closing)
            return;
        storageTimer.Start();
        detectedModeTimer.Start();
        await RefreshStorageAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        if (loading || pickingOutput || captureTask is not null || vancScanTask is not null || qcStop is not null)
            return;

        SavePendingSettings();
        loading = true;
        UpdateControls();
        if (IsDeckLinkActive)
            SetStatus("CheckingDevices", "CheckingDeckLink");
        try
        {
            var result = await Task.Run(() => DeckLink.ScanDevices(includeCapabilities: true));
            deckLinkScanned = true;
            if (closing)
                return;

            sdkVersion = result.ApiVersion ?? "";
            RenderApiVersion();
            var devices = result.Devices.Where(device => device.HasVideoInput).ToArray();
            deckLinkInspector.SetDevices(
                devices,
                settings.DeckLink.DeviceId,
                settings.DeckLink.DeviceName,
                settings.DeckLink.VideoSource,
                settings.DeckLink.AudioSource,
                settings.DeckLink.ModeSelected,
                settings.DeckLink.ModeId);
            loading = false;
            UpdateControls();
            UpdateRecordingFilenamePreview();
            if (!IsDeckLinkActive)
                return;

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
            deckLinkInspector.SetDevices(null, null, null, null, null, false, null);
            if (IsDeckLinkActive)
                SetStatus("ScanFailed", () => ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            loading = false;
            if (!closing)
                UpdateControls();
        }
    }

    private void OutputPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        LiveMediaSource?.NotifyOutputFolderChanged();
        UpdateRecordingFilenamePreview();
        UpdateControls();
        SettingsChanged();
    }

    private void RecordingLabelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateRecordingFilenamePreview();
        SettingsChanged();
    }

    private void UpdateRecordingFormat()
    {
        UpdateRecordingFilenamePreview();
        UpdateControls();
        SettingsChanged();
        if (!loading && captureTask is null && IsDeckLinkActive)
            ShowInputSelectionStatus();
    }

    private void UpdateRecordingFilenamePreview()
    {
        var label = GetRecordingLabel();
        UpdateRecordingEncodingControls();
        if (LiveMediaSource is { } media)
        {
            RecordingFilenamePreview.Text = media.BuildFilenamePreview(label)
                ?? (RecordingFileNaming.SanitizeLabel(label) is { Length: > 0 } sanitizedLabel
                    ? $"{sanitizedLabel}_CaptureRack_YYYYMMDD_HHmmss"
                    : "CaptureRack_YYYYMMDD_HHmmss");
            return;
        }

        var sanitized = RecordingFileNaming.SanitizeLabel(label);
        var samplePrefix = sanitized.Length == 0 ? "CaptureRack_YYYYMMDD_HHmmss" : $"{sanitized}_CaptureRack_YYYYMMDD_HHmmss";
        if (IsRfActive)
        {
            RecordingFilenamePreview.Text = rfInspector.IsPlayback ? "" : samplePrefix + rfInspector.Selection?.Suffix;
            return;
        }
        if (SupportsSoftwareOutput && SelectedRecordingOutput != RecordingOutput.Native)
        {
            var tag = new RecordingEncoding(SelectedRecordingOutput).FileTagForAudioChannels((int)ActiveDeckLinkAudioChannels);
            RecordingFilenamePreview.Text = $"{samplePrefix}_{tag}.mkv";
            return;
        }
        if (!HasLiveCapture)
        {
            RecordingFilenamePreview.Text = samplePrefix;
            return;
        }
        var videoTag = RecordingVideoFormat == CapturePixelFormat.Uyvy ? "8bit-Uncompressed" : "10bit-Uncompressed";
        var channels = ActiveDeckLinkAudioChannels;
        var audioTag = BuildPcmAudioTag(RecordingAudioFormat, channels);
        RecordingFilenamePreview.Text = $"{samplePrefix}_{videoTag}_{audioTag}.mov";
    }

    private static string BuildPcmAudioTag(CaptureAudioFormat format, uint channels)
    {
        var bits = format == CaptureAudioFormat.Pcm32 ? "32bit" : "16bit";
        return channels > 2 ? $"{bits}-{channels}ch-PCM" : $"{bits}-PCM";
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e) => await BrowseOutputFolderAsync();

    private async Task BrowseOutputFolderAsync()
    {
        if (pickingOutput || closing)
            return;
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
        if (IsTestSignalActive)
        {
            SetStatus("Idle", "PreviewBeforeRecording");
            return;
        }

        if (LiveMediaSource is { } media)
        {
            SetStatus("Idle", media.CanStart ? "PreviewBeforeRecording" : "SelectInputs");
            return;
        }
        if (IsRfActive)
        {
            SetStatus("Idle", "RfReadyHint");
            return;
        }
        if (!IsDeckLinkActive)
        {
            SetStatus("BackendPlannedStatus", "PlannedBackendStageNotice", InfoBarSeverity.Informational);
            return;
        }

        if (deckLinkInspector.SelectedDevice is { } device &&
            (device.VideoSources.Count == 0 || device.AudioSources.Count == 0))
        {
            SetStatus("InputsUnavailable", "NoInputRoutes", InfoBarSeverity.Warning);
        }
        else if (deckLinkInspector.SelectedVideoSource is { } video &&
            video.ModesFor(deckLinkInspector.SelectedVideoFormat).Count == 0)
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
        if (IsRfActive)
        {
            if (rfPreviewStop is not null) rfPreviewStop.Cancel();
            else if (StartButton.IsEnabled && rfInspector.IsPlayback && rfInspector.PlaybackFile is { } file)
                await (rfPreviewTask = PreviewRfAsync(null, file));
            else if (StartButton.IsEnabled && rfInspector.Selection is { } request) await (rfPreviewTask = PreviewRfAsync(request));
            return;
        }
        if (LiveMediaSource is { } media)
        {
            if (media.IsCapturing)
                media.StopPreview();
            else
                await StartDecodedMediaAsync(media);
            return;
        }
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
        if (IsRfActive)
        {
            if (rfInspector.IsPlayback) return;
            if (rfStop is not null) { rfStop.Cancel(); UpdateControls(); }
            else if (rfInspector.Selection is { } selected) await (rfTask = RecordRfAsync(selected));
            return;
        }
        if (LiveMediaSource is { } media)
        {
            await RecordDecodedMediaAsync(media);
            return;
        }

        if (recordingSession)
            await FinishRecordingAsync();
        else
            await BeginRecordingAsync();
    }

    private void CancelQcButton_Click(object sender, RoutedEventArgs e) => CancelActiveQc();

    private void CancelActiveQc()
    {
        if (qcStop is { IsCancellationRequested: false } stop)
        {
            stop.Cancel();
            SetStatus("CancellingQc", "QcFinishing");
            UpdateControls();
        }
    }

    private void TogglePreview_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (activePage == ActiveAppPage.Capture && StartButton.IsEnabled)
        {
            StartButton_Click(StartButton, new RoutedEventArgs());
            args.Handled = true;
        }
    }

    private void ToggleRecord_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (activePage == ActiveAppPage.Capture && RecordButton.IsEnabled)
        {
            RecordButton_Click(RecordButton, new RoutedEventArgs());
            args.Handled = true;
        }
    }

    private void LayoutFocus_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (activePage == ActiveAppPage.Capture && !previewFullscreen)
        {
            SetWorkspaceLayout(WorkspaceLayoutMode.Focus, saveIfDefaultable: true);
            args.Handled = true;
        }
    }

    private void LayoutSplit_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (activePage == ActiveAppPage.Capture && !previewFullscreen)
        {
            SetWorkspaceLayout(WorkspaceLayoutMode.Split, saveIfDefaultable: true);
            args.Handled = true;
        }
    }

    private void LayoutScopes_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (activePage == ActiveAppPage.Capture && !previewFullscreen && ActiveWorkspaceCapabilities.SupportsVideoScopes)
        {
            SetWorkspaceLayout(WorkspaceLayoutMode.Scopes, saveIfDefaultable: false);
            args.Handled = true;
        }
    }

    private void LayoutSpectrum_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (activePage == ActiveAppPage.Capture && !previewFullscreen && ActiveWorkspaceCapabilities.SupportsAudioSpectrum)
        {
            SetWorkspaceLayout(WorkspaceLayoutMode.Spectrum, saveIfDefaultable: false);
            args.Handled = true;
        }
    }

    private void LayoutEvents_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (activePage == ActiveAppPage.Capture && !previewFullscreen && ActiveWorkspaceCapabilities.SupportsEvents)
        {
            SetWorkspaceLayout(WorkspaceLayoutMode.Events, saveIfDefaultable: false);
            args.Handled = true;
        }
    }

    private void ToggleAudioMonitor_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (activePage != ActiveAppPage.Capture || (!HasLiveCapture && !RfHasAudio) || !ActiveWorkspaceCapabilities.SupportsLocalAudioMonitor || AnyDropdownOpen())
            return;

        if (RootShell.XamlRoot is { } root)
        {
            if (VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => popup.Child is not ToolTip))
                return;
            var focused = FocusManager.GetFocusedElement(root);
            if (focused is TextBox or NumberBox or RichEditBox or PasswordBox or AutoSuggestBox or ComboBox or ComboBoxItem)
                return;
        }

        ToggleAudioMonitor();
        args.Handled = true;
    }

    private void ToggleInspector_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!previewFullscreen)
        {
            ToggleInspector();
            args.Handled = true;
        }
    }

    private async Task BeginRecordingAsync()
    {
        if (!previewReady || displayedStats?.CanStartRecording != true || !CanRecordSelectedAudio ||
            captureStop is not { IsCancellationRequested: false } ||
            previewRecorder is not { } recorder || recordingTransition)
            return;

        var directory = OutputPath.Text.Trim();
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
        {
            if (previewFullscreen)
                SetPreviewFullscreen(false);
            holdRecordingStatus = true;
            UpdateLivePreviewStatus();
            SetStatus("ChooseOutput", "ExistingOutput", InfoBarSeverity.Warning);
            if (!IsInspectorOpen)
                ToggleInspector();
            return;
        }

        recordingTransition = true;
        SetStatus("StartingRecording", SelectedRecordingOutput == RecordingOutput.Native ? "OpeningMov" : "EncodingLive");
        UpdateControls();
        try
        {
            var encoding = ReadRecordingEncoding();
            var videoTag = RecordingVideoFormat == CapturePixelFormat.Uyvy ? "8bit-Uncompressed" : "10bit-Uncompressed";
            var audioFormat = RecordingAudioFormat;
            var audioChannels = displayedStats?.AudioChannels ?? ActiveDeckLinkAudioChannels;
            var audioTag = BuildPcmAudioTag(audioFormat, audioChannels);
            var label = GetRecordingLabel();
            var source = IsTestSignalActive
                ? ("CaptureRack test signal", "Built-in", SelectedTestScenario.ToString(),
                    audioChannels > 2 ? $"{audioChannels}-channel test tone" : "Stereo test tone")
                : (deckLinkInspector.SelectedDevice!.Name, $"Blackmagic DeckLink API {sdkVersion}",
                    deckLinkInspector.SelectedVideoSource!.Name, deckLinkInspector.SelectedAudioSource!.Name);
            var now = DateTime.Now;
            var opened = await Task.Run(() =>
            {
                var path = encoding.IsSoftware
                    ? Path.Combine(directory, $"{RecordingFileNaming.BuildPrefix(label, now)}_{encoding.FileTagForAudioChannels((int)audioChannels)}.mkv")
                    : RecordingFileNaming.BuildMovPath(directory, label, now, videoTag, audioTag);
                return (Path: path, Started: recorder.Start(path, audioFormat, encoding));
            });
            if (captureTask is null)
                throw new InvalidOperationException(L("PreviewStoppedOpening"));
            recordingStarted = opened.Started;
            recordingInput = source;
            statusFilePath = opened.Path;
            recordingSession = true;
            displayedRecordingSeconds = 0;
            RecordingElapsed.Text = "00:00:00";
            recordingClock.Restart();
            UpdateRecordingIndicator(active: true);
            SetStatus("RecordingActive", encoding.IsSoftware ? "EncodingLive" : "WritingMov");
            SyncFullscreenPreviewWindow();
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

    private async Task FinishRecordingAsync(string? captureError = null)
    {
        if (!recordingSession || recordingTransition || previewRecorder is not { } recorder)
            return;

        recordingTransition = true;
        StopRecordingClock();
        var stopped = DateTimeOffset.UtcNow;
        if (Path.GetExtension(statusFilePath) == ".mkv")
            SetStatus("StopRecording", "QcFinishing");
        else
            SetStatus("FinalizingMov", "WritingIndex");
        UpdateControls();
        try
        {
            var stats = await Task.Run(() => recorder.Finish(captureError));
            var log = CaptureQcLog.Create(stats, recordingStarted, stopped, recordingInput.Device,
                recordingInput.Driver, recordingInput.Video, recordingInput.Audio, captureError);
            if (!await RunMovQcAsync(stats.Recording!.Path, log))
                closeAfterQc = false;
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            closeAfterQc = false;
            if (statusFilePath is { } target && Path.GetExtension(target) == ".mkv" &&
                !File.Exists(target) && File.Exists(target + ".partial.mkv"))
                statusFilePath = target + ".partial.mkv";
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
        do
        {
            restartPreviewRequested = false;
            await RunPreviewSessionAsync();
        }
        while (restartPreviewRequested && !closing);
    }

    private async Task RunPreviewSessionAsync()
    {
        if (closing || pickingOutput || captureTask is not null || vancScanTask is not null || !HasLiveCapture ||
            (IsDeckLinkActive && !deckLinkInspector.HasCompleteSelection))
            return;

        using var stop = new CancellationTokenSource();
        captureStop = stop;
        var recorder = new PreviewRecorder { ReadAudioFromUi = true };
        previewRecorder = recorder;
        holdRecordingStatus = false;
        liveSignal = default;
        lastMissingFrames = 0;
        var feed = new PreviewFeed
        {
            DeinterlaceMode = previewDeinterlace,
            ColorMatrix = previewColorMatrix,
            ScopeWaveformMode = scopeWaveformMode
        };
        using var audioMonitor = new LocalAudioMonitor
        {
            Enabled = audioMonitorEnabled,
            Volume = audioMonitorVolume
        };
        activeAudioMonitor = audioMonitor;
        ResetPreview(feed);
        ResetMonitoring();
        ApplyPreviewSettingsToLiveFeed();
        StartPreviewClock();
        ShowCounts(new CaptureStats());
        SetStatus("StartingPreview", "OpeningInput");
        var dispatcher = DispatcherQueue;
        void Publish(DeckLinkProgress update) => dispatcher.TryEnqueue(() =>
        {
            if (!closing && ReferenceEquals(captureStop, stop) && !stop.IsCancellationRequested)
                ShowCapture(update.Mode, update.Stats);
        });

        var run = CreatePreviewRunner(feed, recorder, audioMonitor, Publish, stop.Token);
        var task = Task.Factory.StartNew(run,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        captureTask = task;
        UpdateControls();
        try
        {
            var stats = await task;
            if (closing)
                return;
            ShowCounts(stats);
            if (recordingTransition)
                return;
            if (recordingSession)
                await FinishRecordingAsync();
            else if (stats.Error is { } error)
            {
                restartPreviewRequested = false;
                SetStatus("PreviewFailed", () => error, InfoBarSeverity.Error);
            }
            else if (!restartPreviewRequested)
                SetStatus("PreviewStopped", "LastCounters");
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            restartPreviewRequested = false;
            if (!closing && !recordingTransition)
            {
                if (recordingSession)
                    await FinishRecordingAsync(ex.Message);
                else
                    SetStatus("PreviewFailed", () => ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            StopPreviewClock();
            if (!closing)
            {
                PresentPreview();
                ShowVideoScopes();
            }
            preview = null;
            previewFrame = null;
            activeAudioMonitor = null;
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
            {
                ResetAudioMonitorRoute();
                UpdateControls();
            }
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        rfAudioTimer.Stop();
        closing = true;
        CloseFullscreenPreviewWindow(suppressRestore: true);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (rfStop is not null || rfPreviewStop is not null)
        {
            e.Cancel = true;
            if (!finishingMediaForClose)
            {
                finishingMediaForClose = true;
                rfStop?.Cancel();
                if (rfTask is not null) await rfTask;
                rfPreviewStop?.Cancel();
                if (rfPreviewTask is not null) await rfPreviewTask;
                DispatcherQueue.TryEnqueue(() => Close());
            }
            return;
        }
        if (previewFullscreen)
        {
            var willDeferClose = LiveMediaSource is { IsCapturing: true } or { IsRecording: true } ||
                qcStop is not null || recordingTransition || recordingSession || closing;
            CloseFullscreenPreviewWindow(suppressRestore: !willDeferClose);
        }

        if (LiveMediaSource is { IsCapturing: true } or { IsRecording: true })
        {
            e.Cancel = true;
            if (!finishingMediaForClose)
            {
                finishingMediaForClose = true;
                await LiveMediaSource!.StopForCloseAsync();
                DispatcherQueue.TryEnqueue(() => Close());
            }
            return;
        }

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
            e.Cancel = !rfQcTail.IsCompleted || captureTask is { IsCompleted: false } || vancScanTask is { IsCompleted: false };
            return;
        }

        SavePendingSettings();
        settingsSaveTimer.Stop();
        storageTimer.Stop();
        detectedModeTimer.Stop();
        closing = true;
        StopPreviewClock();
        var task = (Task?)captureTask ?? vancScanTask;
        if (!rfQcTail.IsCompleted) task = Task.WhenAll(task ?? Task.CompletedTask, rfQcTail);
        if (task is null)
        {
            bitmapPixels?.Dispose();
            return;
        }

        e.Cancel = true;
        captureStop?.Cancel();
        vancScanStop?.Cancel();
        UpdateControls();
        try
        {
            await task;
        }
        catch (Exception ex) when (IsSessionError(ex) || ex is OperationCanceledException)
        {
        }

        bitmapPixels?.Dispose();
        DispatcherQueue.TryEnqueue(() => Close());
    }

    private async Task PickAndCheckMediaFileAsync()
    {
        if (!QcCenterPage.CanCheckFile)
            return;

        pickingOutput = true;
        UpdateControls();
        PickFileResult? file;
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id) { Title = L("CheckMediaFile") };
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".mkv");
            picker.FileTypeFilter.Add(".ts");
            picker.FileTypeFilter.Add(".ts192");
            picker.FileTypeFilter.Add(".m2t");
            picker.FileTypeFilter.Add(".dv");
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
            NavigateToPage(ActiveAppPage.QcCenter);
            await RunMediaQcFileAsync(file.Path);
            CloseAfterRecordingIfRequested();
        }
    }

    private async Task RunMediaQcFileAsync(string path)
    {
        if (QcReports.SupportsFile(path))
            await RunMovQcAsync(path, null);
        else
            await RunStreamQcAsync(path);
    }

    private async Task RunStreamQcAsync(string path)
    {
        var dv = Path.GetExtension(path).Equals(".dv", StringComparison.OrdinalIgnoreCase);
        var kind = dv ? QcEntryKind.Dv : QcEntryKind.TransportStream;
        var checking = dv ? "DvChecking" : "TsChecking";
        var checkFailed = dv ? "DvCheckFailed" : "TsCheckFailed";
        var checkPassed = dv ? "DvCheckPassed" : "TsCheckPassed";
        using var cancellation = new CancellationTokenSource();
        qcStop = cancellation;
        activeQcStage = null;
        statusFilePath = path;
        QcCenterPage.BeginRunning(path, kind);
        FooterQcStageText.Text = L(checking);
        SetStatus(checking, checking + "Detail");
        UpdateControls();

        try
        {
            var dvReport = dv ? await Task.Run(() => DvFile.Analyze(path, cancellation.Token), cancellation.Token) : null;
            var report = !dv ? await Task.Run(() => TsFile.Analyze(path, cancellation: cancellation.Token), cancellation.Token) : null;
            var (titleKey, detailKey, severity, barSeverity) = !(dvReport?.Analysis.Success ?? report!.Success)
                ? (checkFailed, checkFailed + "Detail", TelemetrySeverity.Danger, InfoBarSeverity.Error)
                : report?.Analysis.HasTransportWarnings == true
                    ? ("TsCheckWarnings", "TsCheckWarningsDetail", TelemetrySeverity.Caution, InfoBarSeverity.Warning)
                    : (checkPassed, checkPassed + "Detail", TelemetrySeverity.Normal, InfoBarSeverity.Success);
            var entry = new QcSessionHistoryEntry(
                Guid.NewGuid(), path, Path.GetFileName(path), kind,
                DateTimeOffset.UtcNow, titleKey, L(detailKey), severity, [], SummaryKey: detailKey, TsReport: report, DvReport: dvReport);
            RecordAndDisplayQcEntry(entry);
            SetStatus(titleKey, detailKey, barSeverity);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var entry = new QcSessionHistoryEntry(
                Guid.NewGuid(), path, Path.GetFileName(path), kind,
                DateTimeOffset.UtcNow, "QcCancelled", L("QcCancelledDetail"), TelemetrySeverity.Caution, [], SummaryKey: "QcCancelledDetail");
            RecordAndDisplayQcEntry(entry);
            SetStatus("QcCancelled", "QcCancelledDetail", InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (IsSessionError(ex) || ex is InvalidDataException)
        {
            var entry = new QcSessionHistoryEntry(
                Guid.NewGuid(), path, Path.GetFileName(path), kind,
                DateTimeOffset.UtcNow, checkFailed, ex.Message, TelemetrySeverity.Danger, []);
            RecordAndDisplayQcEntry(entry);
            SetStatus(checkFailed, () => ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            qcStop = null;
            QcCenterPage.FinishRunning();
            if (!closing)
                UpdateControls();
        }
    }

    private async Task<bool> RunMovQcAsync(string path, QcCaptureLog? log)
    {
        using var cancellation = new CancellationTokenSource();
        qcStop = cancellation;
        activeQcStage = null;
        statusFilePath = path;
        QcCenterPage.BeginRunning(path, QcEntryKind.Mov);

        void SetQcProgress(string stageKey, QcStage? stage)
        {
            activeQcStage = stage;
            FooterQcStageText.Text = L(stageKey);
            QcCenterPage.UpdateMovStage(stage);
            SetStatus(log is null ? "QcChecking" : "RecordingSaved",
                () => log is null ? L(stageKey) : WithModeChangeNotice(LocalFormat("SavedQcProgress", L(stageKey))),
                log is { InputModeChanged: true } ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
        }
        string WithModeChangeNotice(string detail) => log is { InputModeChanged: true }
            ? $"{L("QcWarnInputModeChange")} {detail}" : detail;
        string QcDetail(string detail) => log is null ? detail : WithModeChangeNotice(LocalFormat("SavedQcOutcome", detail));

        SetQcProgress("QcStarting", null);
        UpdateControls();
        var dispatcher = DispatcherQueue;
        try
        {
            var result = await Task.Run(() => QcReports.RunAsync(path, log, cancellation: cancellation.Token, stageChanged: stage =>
            {
                dispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(qcStop, cancellation) && !cancellation.IsCancellationRequested)
                        SetQcProgress($"Qc{stage}", stage);
                });
            }));
            var warnings = BuildMovWarnings(log, result);
            var (title, barSeverity, entrySeverity) = result.Status switch
            {
                QcStatus.Passed when warnings.Count == 0 => ("QcPassed", InfoBarSeverity.Success, TelemetrySeverity.Normal),
                QcStatus.Passed => ("QcPassed", InfoBarSeverity.Warning, TelemetrySeverity.Caution),
                QcStatus.Cancelled => ("QcCancelled", InfoBarSeverity.Warning, TelemetrySeverity.Caution),
                QcStatus.ToolsUnavailable => ("QcUnavailable", InfoBarSeverity.Warning, TelemetrySeverity.Caution),
                _ => ("QcFailed", InfoBarSeverity.Error, TelemetrySeverity.Danger)
            };
            var entry = new QcSessionHistoryEntry(
                Guid.NewGuid(), path, Path.GetFileName(path), QcEntryKind.Mov,
                DateTimeOffset.UtcNow, title, DescribeQcResult(result), entrySeverity, warnings,
                LastStage: activeQcStage, MovResult: result, CaptureLog: log);
            RecordAndDisplayQcEntry(entry);
            SetStatus(title, () => QcDetail(DescribeQcResult(result)), barSeverity);
            return result.Status is QcStatus.Passed or QcStatus.Cancelled;
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            const string title = "QcWriteFailed";
            var entry = new QcSessionHistoryEntry(
                Guid.NewGuid(), path, Path.GetFileName(path), QcEntryKind.Mov,
                DateTimeOffset.UtcNow, title, ex.Message, TelemetrySeverity.Danger,
                BuildMovWarnings(log, null), LastStage: activeQcStage, CaptureLog: log);
            RecordAndDisplayQcEntry(entry);
            SetStatus(title, () => QcDetail(ex.Message), InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            qcStop = null;
            QcCenterPage.FinishRunning();
            if (!closing)
                UpdateControls();
        }
    }

    private void RecordAndDisplayQcEntry(QcSessionHistoryEntry entry)
    {
        latestQcEntry = entry;
        qcHistory.Record(entry);
        QcCenterPage.RenderEntry(entry);
        QcCenterPage.RenderHistory(qcHistory.Entries, entry.Id);
        RenderQcSummaryBadge();
    }

    private IReadOnlyList<string> BuildMovWarnings(QcCaptureLog? capture, QcResult? result) =>
        QcWarningFormatter.BuildMovWarnings(capture, result, L, settingsCulture);

    private async Task OpenQcArtifactAsync(string artifact)
    {
        if (QcCenterPage.DisplayedEntry is not { } entry)
            return;

        try
        {
            QcCenterPage.ClearArtifactOpenError();
            var output = entry.FilePath;
            var path = artifact switch
            {
                "video" => output,
                "log" => entry.MovResult?.CaptureLog,
                "recording-report" => NativeCaptureReport.PathFor(output),
                "hash" => entry.MovResult?.FrameMd5,
                "report" => entry.MovResult?.QcToolsReport,
                _ => null
            };
            if (artifact == "folder")
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!);
                if (await Windows.System.Launcher.LaunchFolderAsync(folder))
                    return;
            }
            else if (path is not null)
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                if (await Windows.System.Launcher.LaunchFileAsync(file))
                    return;

                var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
                var options = new Windows.System.FolderLauncherOptions();
                WinRT.Interop.InitializeWithWindow.Initialize(options, WinRT.Interop.WindowNative.GetWindowHandle(this));
                options.ItemsToSelect.Add(file);
                if (await Windows.System.Launcher.LaunchFolderAsync(folder, options))
                    return;
            }

            QcCenterPage.ShowArtifactOpenError(L("QcOpenError"));
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            QcCenterPage.ShowArtifactOpenError(ex.Message);
        }
    }

    private async Task SaveStreamReportAsync(string inputPath, string suffix, string json)
    {
        pickingOutput = true;
        UpdateControls();
        try
        {
            var picker = new FileSavePicker(AppWindow.Id)
            {
                SuggestedFileName = Path.GetFileNameWithoutExtension(inputPath) + suffix
            };
            picker.FileTypeChoices.Add("JSON report", [".json"]);
            var file = await picker.PickSaveFileAsync();
            if (file is null || closing)
                return;
            if (string.Equals(Path.GetFullPath(file.Path), inputPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException(L("TsSaveDifferentFile"));

            var temporary = Path.Combine(Path.GetDirectoryName(file.Path)!, $".CaptureRack-{Guid.NewGuid():N}.json.tmp");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await using (var writer = new StreamWriter(output))
                    await writer.WriteAsync(json);
                File.Move(temporary, file.Path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            QcCenterPage.ShowTsSaveStatus(L("TsJsonSaved"), file.Path, InfoBarSeverity.Success);
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            QcCenterPage.ShowTsSaveStatus(L("TsJsonSaveFailed"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            pickingOutput = false;
            if (!closing)
                UpdateControls();
        }
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

        if (previewFrame is null)
            UpdatePreviewScanMode(mode?.IsInterlaced);
        if (mode is not null)
            UpdatePreviewAspectRatio(mode.Width, mode.Height);
    }

    private void ShowCapture(VideoMode mode, CaptureStats stats)
    {
        var autoMode = IsDeckLinkActive && deckLinkInspector.SelectedMode is { Id: null };
        var recordingAvailabilityChanged = displayedStats?.CanStartRecording != stats.CanStartRecording ||
            displayedStats?.InputAudioFormat != stats.InputAudioFormat;
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

        if (recordingSession && !recordingTransition && (stats.RecordingModeChanged || stats.RecordingError is not null))
        {
            _ = FinishRecordingAsync(stats.RecordingError);
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
                SetStatus("NoInputSignal", IsTestSignalActive ? "TestSignalSimulatedNoInput" :
                    deckLinkInspector.SelectedMode is { Id: null }
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
        UpdateRecordingClock();
    }

    private void RecordingSweepTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (recordingClock.IsRunning)
            StartRecordingSweepAnimation();
    }

    private void StartRecordingSweepAnimation()
    {
        var beamVisual = ElementCompositionPreview.GetElementVisual(RecordingSweepBeam);
        beamVisual.StopAnimation(nameof(Visual.Offset));
        if (!recordingClock.IsRunning || !Controls.StudioFeedback.AnimationsEnabled ||
            !FullscreenRenderPolicy.ShouldRunMainWindowAnimations(PauseMainWindowRendering))
        {
            beamVisual.Offset = Vector3.Zero;
            return;
        }

        var trackWidth = (float)Math.Max(300, RecordingSweepTrack.ActualWidth);
        var beamWidth = (float)RecordingSweepBeam.Width;
        var compositor = beamVisual.Compositor;
        var linear = compositor.CreateLinearEasingFunction();
        var sweep = compositor.CreateVector3KeyFrameAnimation();
        sweep.InsertKeyFrame(0f, new Vector3(-beamWidth, 0f, 0f));
        sweep.InsertKeyFrame(1f, new Vector3(trackWidth, 0f, 0f), linear);
        sweep.Duration = TimeSpan.FromMilliseconds(2100);
        sweep.IterationBehavior = AnimationIterationBehavior.Forever;
        beamVisual.StartAnimation(nameof(Visual.Offset), sweep);
    }

    private void UpdateRecordingIndicator(bool active)
    {
        var animationsEnabled = Controls.StudioFeedback.AnimationsEnabled &&
            FullscreenRenderPolicy.ShouldRunMainWindowAnimations(PauseMainWindowRendering);

        foreach (var dot in new UIElement[] { RecordingIndicator, PreviewRecTallyDot })
        {
            var visual = ElementCompositionPreview.GetElementVisual(dot);
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = 1;
            if (!active || !animationsEnabled)
                continue;

            var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
            pulse.InsertKeyFrame(0, 1f);
            pulse.InsertKeyFrame(1, 0.30f);
            pulse.Duration = TimeSpan.FromMilliseconds(850);
            pulse.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation(nameof(Visual.Opacity), pulse);
        }

        var reelVisual = ElementCompositionPreview.GetElementVisual(RecordingReelRotor);
        reelVisual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
        reelVisual.CenterPoint = new Vector3(7.5f, 7.5f, 0f);
        reelVisual.RotationAngleInDegrees = 0f;
        if (active && animationsEnabled)
        {
            var compositor = reelVisual.Compositor;
            var spin = compositor.CreateScalarKeyFrameAnimation();
            spin.InsertKeyFrame(0f, 0f);
            spin.InsertKeyFrame(1f, 360f, compositor.CreateLinearEasingFunction());
            spin.Duration = TimeSpan.FromMilliseconds(2200);
            spin.IterationBehavior = AnimationIterationBehavior.Forever;
            reelVisual.StartAnimation(nameof(Visual.RotationAngleInDegrees), spin);
        }

        var bars = new[] { (RecBar1, 460, 0), (RecBar2, 580, 150), (RecBar3, 410, 290) };
        foreach (var (bar, durationMs, delayMs) in bars)
        {
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.StopAnimation("Scale.Y");
            visual.CenterPoint = new Vector3(1f, 10f, 0f);
            visual.Scale = Vector3.One;
            if (!active || !animationsEnabled)
                continue;

            var bounce = visual.Compositor.CreateScalarKeyFrameAnimation();
            bounce.InsertKeyFrame(0f, 0.25f);
            bounce.InsertKeyFrame(0.5f, 1.0f);
            bounce.InsertKeyFrame(1f, 0.35f);
            bounce.Duration = TimeSpan.FromMilliseconds(durationMs);
            bounce.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            bounce.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Scale.Y", bounce);
        }

        PreviewRecFrameGlow.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        var glowVisual = ElementCompositionPreview.GetElementVisual(PreviewRecFrameGlow);
        glowVisual.StopAnimation(nameof(Visual.Opacity));
        glowVisual.Opacity = 1f;
        if (active && animationsEnabled)
        {
            var breathe = glowVisual.Compositor.CreateScalarKeyFrameAnimation();
            breathe.InsertKeyFrame(0f, 0.95f);
            breathe.InsertKeyFrame(1f, 0.42f);
            breathe.Duration = TimeSpan.FromMilliseconds(1300);
            breathe.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
            breathe.IterationBehavior = AnimationIterationBehavior.Forever;
            glowVisual.StartAnimation(nameof(Visual.Opacity), breathe);
        }

        RecordingSweepTrack.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        StartRecordingSweepAnimation();

        PreviewRecTally.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PreviewMonitorHousing.BorderBrush = active
            ? ThemeBrush("StudioRecBrush") : ThemeBrush("StudioMonitorBezelBrush");
        SyncFullscreenPreviewWindow();
    }

    private static void RevealWorkspacePanel(UIElement panel)
    {
        if (!Controls.StudioFeedback.AnimationsEnabled)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(panel);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0.4f);
        fade.InsertKeyFrame(1, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(180);

        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0, new Vector3(0f, 10f, 0f));
        offset.InsertKeyFrame(1, Vector3.Zero, easing);
        offset.Duration = TimeSpan.FromMilliseconds(180);

        visual.StartAnimation(nameof(Visual.Opacity), fade);
        visual.StartAnimation(nameof(Visual.Offset), offset);
    }

    private static void PulseWarningChip(UIElement element)
    {
        if (!Controls.StudioFeedback.AnimationsEnabled)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0f, 1f);
        pulse.InsertKeyFrame(0.35f, 0.55f);
        pulse.InsertKeyFrame(1f, 1f);
        pulse.Duration = TimeSpan.FromMilliseconds(340);
        visual.StartAnimation(nameof(Visual.Opacity), pulse);
    }

    private void UpdateRecordingClock()
    {
        var elapsed = recordingClock.Elapsed;
        var seconds = (long)elapsed.TotalSeconds;
        if (seconds == displayedRecordingSeconds)
            return;

        displayedRecordingSeconds = seconds;
        var text = $"{seconds / 3600:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        RecordingElapsed.Text = text;
        SyncFullscreenPreviewWindow();
    }

    private void ResetPreview(PreviewFeed? feed)
    {
        bitmapPixels?.Dispose();
        bitmapPixels = null;
        previewBitmap = null;
        previewFrame = null;
        preview = feed;
        if (feed is not null && PauseMainWindowRendering)
            feed.RenderScopes = false;
        displayedPreviewSkips = 0;
        PreviewImage.Source = null;
        previewCaption = null;
        SetPreviewAudio(null);
        PreviewTapeDetail.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewHudPill.Visibility = Visibility.Collapsed;
        SafeAreaOverlay.Visibility = Visibility.Collapsed;
        Ntsc480GuideOverlay.Visibility = Visibility.Collapsed;
        decodedDisplayAspectRatio = 0;
        if (feed is null)
            UpdatePreviewScanMode(null);
        SyncFullscreenPreviewWindow();
    }

    private void PresentPreview() => PresentPreview(preview);

    private void PresentPreview(PreviewFeed? feed)
    {
        if (feed is null || !feed.TryTakeLatest(ref previewFrame) || previewFrame is not { } frame)
            return;

        UpdatePreviewScanMode(frame.IsInterlaced);
        if (previewBitmap?.PixelWidth != frame.Width || previewBitmap.PixelHeight != frame.Height)
        {
            bitmapPixels?.Dispose();
            previewBitmap = new WriteableBitmap(frame.Width, frame.Height);
            bitmapPixels = previewBitmap.PixelBuffer.AsStream();
            if (!PauseMainWindowRendering)
                PreviewImage.Source = previewBitmap;
            fullscreenWindow?.SetPreviewImage(previewBitmap, frameAvailable: true);
        }
        else if (!PauseMainWindowRendering && !ReferenceEquals(PreviewImage.Source, previewBitmap))
        {
            PreviewImage.Source = previewBitmap;
        }

        decodedDisplayAspectRatio = frame.DisplayAspectRatio;
        UpdatePreviewAspectRatio(frame.SignalWidth, frame.SignalHeight);

        bitmapPixels!.Position = 0;
        bitmapPixels.Write(frame.Pixels, 0, frame.Stride * frame.Height);
        previewBitmap.Invalidate();
        previewCaption = new(frame.NoInput, frame.SignalWidth, frame.SignalHeight,
            frame.SignalFormat ?? (frame.Format == CapturePixelFormat.Bgra ? "—" : DeckLinkCapture.FormatFourCc((uint)frame.Format)),
            frame.SignalScanMode, feed.PresentedSequence);
        if (PreviewPlaceholder.Visibility == Visibility.Visible || PreviewHudPill.Visibility == Visibility.Collapsed)
        {
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewPlaceholderSpinner.IsActive = false;
            PreviewPlaceholderSpinner.Visibility = Visibility.Collapsed;
            PreviewPlaceholderIcon.Visibility = Visibility.Visible;
            PreviewHudPill.Visibility = Visibility.Visible;
            ApplySafeAreaOverlay();
        }
        if (!PauseMainWindowRendering)
            RenderPreviewCaption();
        fullscreenWindow?.SetPreviewImage(previewBitmap, frameAvailable: true);
    }

    private void ShowCounts(CaptureStats stats)
    {
        displayedStats = stats;
        displayedPreviewSkips = preview?.SkippedFrames ?? displayedPreviewSkips;
        if (stats.MissingVideoFrames > lastMissingFrames)
            PulseWarningChip(TelemetryStrip);
        lastMissingFrames = stats.MissingVideoFrames;
        RenderCounts(stats);
        ShowMonitoring(stats);
        SetPreviewAudio(stats.AudioInfo);
    }

    private bool CanRecordSelectedAudio => displayedStats?.SupportsRecordingAudioFormat(RecordingAudioFormat) == true;

    private void UpdateControls()
    {
        if (rackShowsRecording != CapturingToFile) RenderSourceRackList();
        UpdateRecordingEncodingControls();
        var media = LiveMediaSource;
        var active = IsCaptureInputActive;
        var idle = settingsReady && !closing && !loading && !pickingOutput && !active && vancScanTask is null &&
            !restartPreviewRequested && !recordingTransition && !IsCaptureRecording && qcStop is null;
        var canChangePreviewFormat = IsDeckLinkActive && active && previewReady && !closing && !loading &&
            !pickingOutput && !recordingSession && !recordingTransition && qcStop is null &&
            captureStop is { IsCancellationRequested: false };
        QcCenterPage.CanCheckFile = settingsReady && !closing && !loading && !pickingOutput &&
            !IsCaptureRecording && !recordingTransition && qcStop is null && (idle || media is not null);
        deckLinkInspector.UpdateEnabledState(idle && IsDeckLinkActive, canChangePreviewFormat,
            canChangePreviewFormat ? displayedStats?.InputAudioFormat : null);
        testSignalInspector.SetEnabled(idle && !recordingTransition);
        var canStart = HasLiveCapture && idle && !recordingTransition &&
            (media?.CanStart ?? (IsTestSignalActive || deckLinkInspector.HasCompleteSelection));
        var changingRecording = IsCaptureRecording || recordingTransition;
        var canChooseOutput = settingsReady && !closing && !loading && !pickingOutput &&
            !changingRecording && !restartPreviewRequested;
        bool fileMode = IsRfActive && rfInspector.IsPlayback;
        canChooseOutput &= !fileMode;
        UpdateRfPlaybackBar(fileMode);
        OutputPath.IsEnabled = canChooseOutput;
        RecordingLabelBox.IsEnabled = canChooseOutput;
        BrowseButton.IsEnabled = canChooseOutput;
        var previewCanStop = active && !closing && !changingRecording &&
            (media is null ? captureStop is { IsCancellationRequested: false } : !media.IsStopping);
        RenderCaptureActions();
        StartButton.IsEnabled = IsRfActive
            ? !closing && !changingRecording && (rfPreviewStop is { IsCancellationRequested: false } || idle && !inspectingField && !openingPlayback && rfInspector.Ready)
            : active ? previewCanStop : canStart;
        PlaceholderStartButton.IsEnabled = canStart;
        PlaceholderStartButton.Visibility = HasLiveCapture && !active && canStart ? Visibility.Visible : Visibility.Collapsed;
        if (IsRfActive && previewBitmap is null)
            PreviewPlaceholder.Visibility = rfInspector.PreviewOptions.PreviewType is RfPreviewType.RfOnly or RfPreviewType.VhsHifi ? Visibility.Collapsed : Visibility.Visible;
        var waitingForFirstFrame = active && PreviewPlaceholder.Visibility == Visibility.Visible && (media?.HasPreview ?? true);
        PreviewPlaceholderSpinner.IsActive = waitingForFirstFrame;
        PreviewPlaceholderSpinner.Visibility = waitingForFirstFrame ? Visibility.Visible : Visibility.Collapsed;
        PreviewPlaceholderIcon.Visibility = waitingForFirstFrame ? Visibility.Collapsed : Visibility.Visible;
        var recordEnabled = media is not null
            ? qcStop is null && !closing && !finishingMediaForClose && (media.IsRecording
                ? !media.IsFinalizing : !pickingOutput && media.CanRecord && !string.IsNullOrWhiteSpace(OutputPath.Text))
            : HasLiveCapture && qcStop is null && !closing && active && !recordingTransition && (recordingSession ||
            (previewReady && displayedStats?.CanStartRecording == true && CanRecordSelectedAudio &&
                previewRecorder is not null && !pickingOutput &&
                captureStop is { IsCancellationRequested: false } && !string.IsNullOrWhiteSpace(OutputPath.Text)));
        RecordButton.Visibility = media?.SupportsRecording == false ? Visibility.Collapsed : Visibility.Visible;
        RecordButton.IsEnabled = IsRfActive
            ? !fileMode && (rfStop is { IsCancellationRequested: false } || ((idle || rfPreviewReady && rfPreviewStop is { IsCancellationRequested: false }) && rfStop is null && !closing && qcStop is null && rfInspector.Ready && Directory.Exists(OutputPath.Text.Trim())))
            : recordEnabled;
        CancelQcButton.IsEnabled = qcStop is { IsCancellationRequested: false };
        QcCenterPage.IsCancelEnabled = CancelQcButton.IsEnabled;
        FooterQcActivePill.Visibility = qcStop is null ? Visibility.Collapsed : Visibility.Visible;
        QcCenterPage.IsCancelVisible = qcStop is not null;
        RecordingClock.Visibility = recordingClock.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        QcResultsButton.Visibility = !changingRecording && qcStop is null && latestQcEntry is not null
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateQcNavBadge();
        UpdateCaptureFooter();
        UpdateLivePreviewStatus();
        UpdatePreviewForSelection();
        UpdateConfigurationStatus();
        SyncFullscreenPreviewWindow();
    }

    private void UpdateQcNavBadge()
    {
        if (qcStop is not null)
        {
            QcNavBadgeDot.Visibility = Visibility.Visible;
            QcNavBadgeDot.Fill = ThemeBrush("StudioInfoBrush");
        }
        else if (latestQcEntry is { Severity: TelemetrySeverity.Danger })
        {
            QcNavBadgeDot.Visibility = Visibility.Visible;
            QcNavBadgeDot.Fill = ThemeBrush("StudioDangerBrush");
        }
        else if (latestQcEntry is { Severity: TelemetrySeverity.Caution })
        {
            QcNavBadgeDot.Visibility = Visibility.Visible;
            QcNavBadgeDot.Fill = ThemeBrush("StudioCautionBrush");
        }
        else if (latestQcEntry is { Severity: TelemetrySeverity.Normal })
        {
            QcNavBadgeDot.Visibility = Visibility.Visible;
            QcNavBadgeDot.Fill = ThemeBrush("StudioSignalLockBrush");
        }
        else
        {
            QcNavBadgeDot.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateLivePreviewStatus()
    {
        var show = (previewReady || LiveMediaSource?.HasInput == true) && (IsCaptureRecording || recordingTransition || holdRecordingStatus || qcStop is not null);
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

    private sealed record SourceRackRow(
        string BackendId,
        string IconGlyph,
        string Name,
        string Subtitle,
        string BadgeText,
        Visibility BadgeVisibility,
        Brush BadgeBackground,
        Brush BadgeForeground,
        bool IsSelectable,
        Brush CardBackground,
        Brush CardBorder,
        Brush IconForeground);

    private static bool IsSessionError(Exception ex) =>
        ex is COMException or InvalidOperationException or InvalidCastException or IOException or
            UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException or System.ComponentModel.Win32Exception;
}
