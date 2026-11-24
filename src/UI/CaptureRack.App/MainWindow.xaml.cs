using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.QualityControl;
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
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace CaptureRack.App;

public sealed partial class MainWindow : Window
{
    private enum WorkspaceLayoutMode { Focus, Split, Scopes, Spectrum, Events }
    private enum SplitSecondaryInstrument { Vector, Spectrum, Events }
    private enum ActiveAppPage { Capture, QcCenter, Settings }

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
    private long lastMissingFrames;
    private string? qcOutputPath;
    private string? statusFilePath;
    private QcResult? lastQcResult;
    private QcStage? activeQcStage;
    private PreviewFeed? preview;
    private PreviewFrame? previewFrame;
    private WriteableBitmap? previewBitmap;
    private Stream? bitmapPixels;
    private const double DefaultPreviewAspectRatio = 4d / 3;
    private double previewAspectRatio = DefaultPreviewAspectRatio;
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
    private bool showSafeArea;
    private double lastWindowWidth = 1320;
    private CapturePixelFormat selectedVideoFormat = CapturePixelFormat.V210;
    private CaptureAudioFormat selectedAudioFormat = CaptureAudioFormat.Pcm16;

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
        fullscreenIdleTimer = DispatcherQueue.CreateTimer();
        fullscreenIdleTimer.Interval = TimeSpan.FromSeconds(2);
        fullscreenIdleTimer.IsRepeating = false;
        fullscreenIdleTimer.Tick += (_, _) =>
        {
            fullscreenBarVisible = false;
            UpdateFullscreenMiniBar();
        };

        InitializeComponent();
        ConfigureWindowChrome();
        InitializeSettings();
        InitializeSettingsInput();

        previewTimer = DispatcherQueue.CreateTimer();
        previewTimer.Interval = TimeSpan.FromMilliseconds(1000d / 60);
        previewTimer.Tick += (_, _) =>
        {
            PresentPreview();
            ShowVideoScopes();
            UpdateMeterBallistics();
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
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        var scale = RootShell.XamlRoot?.RasterizationScale ?? 1.0;
        var right = AppWindow.TitleBar.RightInset / scale;
        TitleBarRightInset.Width = new GridLength(Math.Max(136, right));
    }

    private bool IsDarkTheme() => RootShell.RequestedTheme switch
    {
        ElementTheme.Light => false,
        ElementTheme.Dark => true,
        _ => RootShell.ActualTheme switch
        {
            ElementTheme.Light => false,
            ElementTheme.Dark => true,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
        }
    };

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

    private Brush ThemeBrush(string key)
    {
        var themeKey = IsDarkTheme() ? "Dark" : "Light";
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(themeKey, out var obj) &&
                obj is ResourceDictionary dict && dict.TryGetValue(key, out var themed) && themed is Brush themedBrush)
                return themedBrush;
        }
        return Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush : new SolidColorBrush(Colors.Transparent);
    }

    private void RefreshDynamicThemeBrushes()
    {
        UpdateTitleBarTheme();
        UpdateNavigationStyles();
        ApplyWorkspaceLayout();
        UpdateFormatSegmentVisuals();
        UpdateInspectorLayout();
        ApplySafeAreaOverlay();
        UpdateAudioMonitorVisuals();
        RenderCaptureActions();
        RenderCounts(displayedStats);
        RenderCompactStatus();
        RenderQcResult();
        UpdateQcNavBadge();
        UpdateConfigurationStatus();
        RefreshToolchainDiagnostics();
        UpdatePhaseBar(displayedPhase);
        UpdateClipBadge(LeftClipBadge, LeftChannel, "L", visibleAudio?.LeftClips ?? 0);
        UpdateClipBadge(RightClipBadge, RightChannel, "R", visibleAudio?.RightClips ?? 0);
        UpdateRecordingIndicator(recordingClock.IsRunning);
        if (QcPipelineCard.Visibility == Visibility.Visible)
            UpdateQcPipeline(activeQcStage, qcStop is not null, lastQcResult?.Status);
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
        var compact = width < 1040;
        var narrow = width < 760;
        FooterCenterColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FooterLayout.RowSpacing = compact ? 6 : 0;
        Grid.SetRow(TelemetryStrip, compact ? 1 : 0);
        Grid.SetColumn(TelemetryStrip, compact ? 0 : 1);
        Grid.SetColumnSpan(TelemetryStrip, compact ? 3 : 1);
        TelemetryStrip.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        FooterStorageButton.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        QuickSourceButton.Visibility = width < 820 ? Visibility.Collapsed : Visibility.Visible;
        StereoMeter.Visibility = !previewFullscreen && width >= 680 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateInspectorLayout()
    {
        if (previewFullscreen)
        {
            InspectorPanel.Visibility = Visibility.Collapsed;
            InspectorScrim.Visibility = Visibility.Collapsed;
            InspectorDockColumn.Width = new GridLength(0);
            return;
        }

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

        var canDock = lastWindowWidth >= 1180;
        if (canDock)
        {
            if (inspectorPinned)
            {
                inspectorPinned = false;
                inspectorOverlayOpen = false;
            }
            else
            {
                inspectorPinned = true;
                inspectorOverlayOpen = false;
                AnimateInspectorSlideIn();
            }
            SettingsChanged();
        }
        else
        {
            inspectorOverlayOpen = !inspectorOverlayOpen;
            if (inspectorOverlayOpen)
                AnimateInspectorSlideIn();
        }
        UpdateInspectorLayout();
    }

    private void AnimateInspectorSlideIn()
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(InspectorPanel);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0f, new Vector3(28f, 0f, 0f));
        offset.InsertKeyFrame(1f, Vector3.Zero, easing);
        offset.Duration = TimeSpan.FromMilliseconds(210);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0.2f);
        opacity.InsertKeyFrame(1f, 1f, easing);
        opacity.Duration = TimeSpan.FromMilliseconds(180);

        visual.StartAnimation(nameof(Visual.Offset), offset);
        visual.StartAnimation(nameof(Visual.Opacity), opacity);
    }

    private void QuickSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (activePage != ActiveAppPage.Capture)
            NavigateToPage(ActiveAppPage.Capture);

        if (InspectorPanel.Visibility != Visibility.Visible)
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
        UpdateInspectorLayout();
    }

    private void InspectorScrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        inspectorOverlayOpen = false;
        UpdateInspectorLayout();
    }

    private void CaptureModeButton_Click(object sender, RoutedEventArgs e) => NavigateToPage(ActiveAppPage.Capture);

    private void QcModeButton_Click(object sender, RoutedEventArgs e) => NavigateToPage(ActiveAppPage.QcCenter);

    private void QcResultsButton_Click(object sender, RoutedEventArgs e) => NavigateToPage(ActiveAppPage.QcCenter);

    private void NavigateToPage(ActiveAppPage target)
    {
        if (activePage == target)
            return;

        if (activePage == ActiveAppPage.Settings)
        {
            SavePendingSettings();
            StopSettingsWheelFallback();
        }
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
            BackButton.Focus(FocusState.Programmatic);
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
        if (saveIfDefaultable && mode is WorkspaceLayoutMode.Split or WorkspaceLayoutMode.Focus)
            SettingsChanged();
    }

    private void ApplyWorkspaceLayout()
    {
        ApplySegmentState(LayoutFocusBtn, workspaceLayout == WorkspaceLayoutMode.Focus);
        ApplySegmentState(LayoutSplitBtn, workspaceLayout == WorkspaceLayoutMode.Split);
        ApplySegmentState(LayoutScopesBtn, workspaceLayout == WorkspaceLayoutMode.Scopes);
        ApplySegmentState(LayoutSpectrumBtn, workspaceLayout == WorkspaceLayoutMode.Spectrum);
        ApplySegmentState(LayoutEventsBtn, workspaceLayout == WorkspaceLayoutMode.Events);

        var showPreview = previewFullscreen || workspaceLayout is WorkspaceLayoutMode.Focus or WorkspaceLayoutMode.Split;
        var showInstruments = !previewFullscreen && workspaceLayout != WorkspaceLayoutMode.Focus;

        PreviewMonitorHousing.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        InstrumentsHost.Visibility = showInstruments ? Visibility.Visible : Visibility.Collapsed;

        if (previewFullscreen || workspaceLayout == WorkspaceLayoutMode.Focus)
        {
            PreviewStageRow.Height = new GridLength(1, GridUnitType.Star);
            InstrumentsStageRow.Height = new GridLength(0);
            MonitorViewport.RowSpacing = 0;
        }
        else if (workspaceLayout == WorkspaceLayoutMode.Split)
        {
            PreviewStageRow.Height = new GridLength(63, GridUnitType.Star);
            InstrumentsStageRow.Height = new GridLength(37, GridUnitType.Star);
            MonitorViewport.RowSpacing = 10;
        }
        else
        {
            PreviewStageRow.Height = new GridLength(0);
            InstrumentsStageRow.Height = new GridLength(1, GridUnitType.Star);
            MonitorViewport.RowSpacing = 0;
        }

        if (showInstruments)
        {
            var twoColumns = workspaceLayout is WorkspaceLayoutMode.Split or WorkspaceLayoutMode.Scopes;
            LumaCard.Visibility = twoColumns ? Visibility.Visible : Visibility.Collapsed;
            InstrumentCol0.Width = twoColumns ? new GridLength(56, GridUnitType.Star) : new GridLength(0);
            InstrumentCol1.Width = twoColumns ? new GridLength(44, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
            InstrumentsHost.ColumnSpacing = twoColumns ? 10 : 0;

            SplitRightSwitcher.Visibility = workspaceLayout == WorkspaceLayoutMode.Split
                ? Visibility.Visible : Visibility.Collapsed;

            var secondary = workspaceLayout switch
            {
                WorkspaceLayoutMode.Scopes => SplitSecondaryInstrument.Vector,
                WorkspaceLayoutMode.Spectrum => SplitSecondaryInstrument.Spectrum,
                WorkspaceLayoutMode.Events => SplitSecondaryInstrument.Events,
                _ => splitSecondary
            };
            ApplySecondaryInstrument(secondary);
        }

        UpdateMonitorLayout();
    }

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
        ApplySegmentState(SplitVectorBtn, instrument == SplitSecondaryInstrument.Vector);
        ApplySegmentState(SplitSpectrumBtn, instrument == SplitSecondaryInstrument.Spectrum);
        ApplySegmentState(SplitEventsBtn, instrument == SplitSecondaryInstrument.Events);

        VectorBody.Visibility = instrument == SplitSecondaryInstrument.Vector ? Visibility.Visible : Visibility.Collapsed;
        SpectrumBody.Visibility = instrument == SplitSecondaryInstrument.Spectrum ? Visibility.Visible : Visibility.Collapsed;
        EventsBody.Visibility = instrument == SplitSecondaryInstrument.Events ? Visibility.Visible : Visibility.Collapsed;
        EventsToolbar.Visibility = instrument == SplitSecondaryInstrument.Events && workspaceLayout == WorkspaceLayoutMode.Events
            ? Visibility.Visible : Visibility.Collapsed;

        UpdateSecondaryInstrumentHeader(instrument);
    }

    private void SafeAreaToggleButton_Click(object sender, RoutedEventArgs e)
    {
        showSafeArea = !showSafeArea;
        ApplySafeAreaOverlay();
        SettingsChanged();
    }

    private void ApplySafeAreaOverlay()
    {
        SafeAreaOverlay.Visibility = showSafeArea && PreviewPlaceholder.Visibility == Visibility.Collapsed
            ? Visibility.Visible : Visibility.Collapsed;
        SafeAreaToggleButton.Background = showSafeArea
            ? ThemeBrush("StudioSegmentActiveBrush") : new SolidColorBrush(Colors.Transparent);
        SafeAreaCheckBox.IsChecked = showSafeArea;
        Ntsc480GuideCheckBox.IsChecked = showNtsc480Guide;
        UpdateNtsc480GuideLayout();
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
        if (previewBoxWidth <= 0 || previewBoxHeight <= 0)
            return;

        var pad = previewFullscreen ? 0 : 14;
        var availW = Math.Max(0, previewBoxWidth - pad * 2);
        var availH = Math.Max(0, previewBoxHeight - pad * 2);
        var height = Math.Min(availW / previewAspectRatio, availH);
        var width = height * previewAspectRatio;
        PreviewSurface.Width = Math.Max(0, width);
        PreviewSurface.Height = Math.Max(0, height);
        UpdateNtsc480GuideLayout();
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

        var device = DevicePicker.SelectedItem as CaptureDevice;
        var source = VideoInputPicker.SelectedItem as VideoInputSource;
        var selectedMode = ModePicker.SelectedItem as ModeOption;
        var selection = (device?.PersistentId, device?.Index, source?.Id, selectedMode?.Id, selectedVideoFormat);
        if (previewSelection == selection)
            return;

        previewSelection = selection;
        observedPreviewMode = null;
        var mode = selectedMode?.Id is { } id
            ? source?.ModesFor(selectedVideoFormat).FirstOrDefault(candidate => candidate.Id == id) : null;
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
            VideoInputPicker.SelectedItem is not VideoInputSource source)
            return;

        var selection = previewSelection;
        checkingDetectedMode = true;
        try
        {
            var id = await Task.Run(() => DeckLinkCapture.GetLockedVideoInputMode(device, source.Id));
            if (!closing && captureTask is null && previewSelection == selection)
                UpdateObservedPreviewMode(id is null ? null :
                    source.ModesFor(selectedVideoFormat).FirstOrDefault(candidate => candidate.Id == id));
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
        UpdateTitleBarTheme();
        UpdateNavigationStyles();
        ApplyWorkspaceLayout();
        UpdateInspectorLayout();
        ApplySafeAreaOverlay();
        ApplyPreviewOptionsToUi();
        SyncAnalogUiFromState();
        UpdateAudioMonitorVisuals();
        RefreshToolchainDiagnostics();
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
        if (VideoInputPicker.SelectedItem is VideoInputSource video &&
            video.ModesFor(selectedVideoFormat) is { Count: > 0 } modes)
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

    private void VideoFormatSegment_Click(object sender, RoutedEventArgs e)
    {
        if (captureTask is not null || sender is not Button { Tag: string tag })
            return;

        var format = tag == "Uyvy" ? CapturePixelFormat.Uyvy : CapturePixelFormat.V210;
        if (selectedVideoFormat == format)
            return;

        selectedVideoFormat = format;
        UpdateFormatSegmentVisuals();
        UpdateModeOptions();
        SettingsChanged();
        if (!loading && captureTask is null)
            ShowInputSelectionStatus();
    }

    private void AudioFormatSegment_Click(object sender, RoutedEventArgs e)
    {
        if (captureTask is not null || sender is not Button { Tag: string tag })
            return;

        var format = tag == "Pcm32" ? CaptureAudioFormat.Pcm32 : CaptureAudioFormat.Pcm16;
        if (selectedAudioFormat == format)
            return;

        selectedAudioFormat = format;
        UpdateFormatSegmentVisuals();
        UpdateControls();
        SettingsChanged();
        if (!loading && captureTask is null)
            ShowInputSelectionStatus();
    }

    private void UpdateFormatSegmentVisuals()
    {
        ApplySegmentState(VideoFormat10Button, selectedVideoFormat == CapturePixelFormat.V210);
        ApplySegmentState(VideoFormat8Button, selectedVideoFormat == CapturePixelFormat.Uyvy);
        ApplySegmentState(AudioFormat16Button, selectedAudioFormat == CaptureAudioFormat.Pcm16);
        ApplySegmentState(AudioFormat32Button, selectedAudioFormat == CaptureAudioFormat.Pcm32);
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
            SetStatus("InputsUnavailable", "NoInputRoutes", InfoBarSeverity.Warning);
        }
        else if (VideoInputPicker.SelectedItem is VideoInputSource video &&
            video.ModesFor(selectedVideoFormat).Count == 0)
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
        if (activePage == ActiveAppPage.Capture && !previewFullscreen)
        {
            SetWorkspaceLayout(WorkspaceLayoutMode.Scopes, saveIfDefaultable: false);
            args.Handled = true;
        }
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
        if (!previewReady || displayedStats?.CanStartRecording != true ||
            previewRecorder is not { } recorder || recordingTransition)
            return;

        var directory = OutputPath.Text.Trim();
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
        {
            holdRecordingStatus = true;
            UpdateLivePreviewStatus();
            SetStatus("ChooseOutput", "ExistingOutput", InfoBarSeverity.Warning);
            if (InspectorPanel.Visibility != Visibility.Visible)
                ToggleInspector();
            return;
        }

        recordingTransition = true;
        SetStatus("StartingRecording", "OpeningMov");
        UpdateControls();
        try
        {
            var videoTag = selectedVideoFormat == CapturePixelFormat.Uyvy ? "8bit-Uncompressed" : "10bit-Uncompressed";
            var audioTag = selectedAudioFormat == CaptureAudioFormat.Pcm32 ? "32bit-PCM" : "16bit-PCM";
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var prefix = $"CaptureRack_{timestamp}";
            var formats = $"{videoTag}_{audioTag}";
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
            RecordingElapsed.Text = FullscreenClockText.Text = "00:00:00";
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
            ModePicker.SelectedItem is not ModeOption mode)
            return;

        using var stop = new CancellationTokenSource();
        captureStop = stop;
        var recorder = new PreviewRecorder();
        previewRecorder = recorder;
        holdRecordingStatus = false;
        liveSignal = default;
        lastMissingFrames = 0;
        var feed = new PreviewFeed
        {
            DeinterlaceMode = previewDeinterlace,
            ColorMatrix = previewColorMatrix
        };
        using var audioMonitor = new LocalAudioMonitor
        {
            Enabled = audioMonitorEnabled,
            Volume = audioMonitorVolume
        };
        activeAudioMonitor = audioMonitor;
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
            PixelFormat = selectedVideoFormat,
            AudioFormat = selectedAudioFormat,
            PreviewRecorder = recorder,
            AnalogOptions = BuildAnalogInputOptions(),
            LocalAudioMonitor = audioMonitor
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
        }

        bitmapPixels?.Dispose();
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
            NavigateToPage(ActiveAppPage.QcCenter);
            await RunQcAsync(file.Path, null);
            CloseAfterRecordingIfRequested();
        }
    }

    private void QcDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (CheckMovButton.IsEnabled && e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
        else
            e.AcceptedOperation = DataPackageOperation.None;
    }

    private async void QcDropZone_Drop(object sender, DragEventArgs e)
    {
        if (!CheckMovButton.IsEnabled || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var mov = items.OfType<StorageFile>().FirstOrDefault(item =>
                string.Equals(Path.GetExtension(item.Path), ".mov", StringComparison.OrdinalIgnoreCase));
            if (mov is not null && !closing)
            {
                await RunQcAsync(mov.Path, null);
                CloseAfterRecordingIfRequested();
            }
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            if (!closing)
                SetStatus("CheckMovOpenFailed", () => ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task<bool> RunQcAsync(string path, QcCaptureLog? log)
    {
        using var cancellation = new CancellationTokenSource();
        qcStop = cancellation;
        activeQcStage = null;
        statusFilePath = path;
        qcOutputPath = path;
        lastQcResult = null;
        QcFileName.Text = Path.GetFileName(path);
        QcFilePathText.Text = path;
        QcInlineLogText.Text = "";
        QcInlineLogText.Visibility = Visibility.Collapsed;
        QcResultSection.Visibility = Visibility.Collapsed;
        QcPipelineCard.Visibility = Visibility.Visible;
        QcProgressBar.Visibility = Visibility.Visible;
        UpdateQcPipeline(null, running: true, finalStatus: null);

        void SetQcProgress(string stageKey, QcStage? stage)
        {
            activeQcStage = stage;
            FooterQcStageText.Text = L(stageKey);
            UpdateQcPipeline(stage, running: true, finalStatus: null);
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
            var (title, severity) = result.Status switch
            {
                QcStatus.Passed => ("QcPassed", InfoBarSeverity.Success),
                QcStatus.Cancelled => ("QcCancelled", InfoBarSeverity.Warning),
                QcStatus.ToolsUnavailable => ("QcUnavailable", InfoBarSeverity.Warning),
                _ => ("QcFailed", InfoBarSeverity.Error)
            };
            if (severity == InfoBarSeverity.Success && log is { InputModeChanged: true })
                severity = InfoBarSeverity.Warning;
            UpdateQcPipeline(activeQcStage, running: false, finalStatus: result.Status);
            ShowQcResult(path, result, log, title, () => DescribeQcResult(result));
            SetStatus(title, () => QcDetail(DescribeQcResult(result)), severity);
            return result.Status is QcStatus.Passed or QcStatus.Cancelled;
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            const string title = "QcWriteFailed";
            UpdateQcPipeline(activeQcStage, running: false, finalStatus: QcStatus.Failed);
            ShowQcResult(path, null, log, title, () => ex.Message);
            SetStatus(title, () => QcDetail(ex.Message), InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            qcStop = null;
            QcProgressBar.Visibility = Visibility.Collapsed;
            if (!closing)
                UpdateControls();
        }
    }

    private void UpdateQcPipeline(QcStage? stage, bool running, QcStatus? finalStatus)
    {
        var stepIndex = stage switch
        {
            QcStage.Inspecting => 1,
            QcStage.Decoding => 2,
            QcStage.Analyzing => 3,
            _ => 0
        };

        void StyleStep(Border card, FontIcon icon, int index)
        {
            var completed = finalStatus == QcStatus.Passed || (!running && index < stepIndex) || (running && index < stepIndex);
            var active = running && index == stepIndex;
            var failed = !running && finalStatus is QcStatus.Failed or QcStatus.ToolsUnavailable && index == Math.Max(1, stepIndex);

            if (completed)
            {
                card.Background = ThemeBrush("StudioSignalLockSoftBrush");
                card.BorderBrush = ThemeBrush("StudioSignalLockBrush");
                icon.Glyph = "\uE73E";
                icon.Foreground = ThemeBrush("StudioSignalLockBrush");
            }
            else if (active)
            {
                card.Background = ThemeBrush("StudioInfoSoftBrush");
                card.BorderBrush = ThemeBrush("StudioInfoBrush");
                icon.Glyph = "\uE895";
                icon.Foreground = ThemeBrush("StudioInfoBrush");
            }
            else if (failed)
            {
                card.Background = ThemeBrush("StudioDangerSoftBrush");
                card.BorderBrush = ThemeBrush("StudioDangerBrush");
                icon.Glyph = "\uE783";
                icon.Foreground = ThemeBrush("StudioDangerBrush");
            }
            else
            {
                card.Background = ThemeBrush("StudioPanelSecondaryBrush");
                card.BorderBrush = ThemeBrush("StudioHairlineBrush");
                icon.Glyph = "\uE91F";
                icon.Foreground = ThemeBrush("StudioTextMutedBrush");
            }
        }

        StyleStep(QcStep1Card, QcStep1Icon, 1);
        StyleStep(QcStep2Card, QcStep2Icon, 2);
        StyleStep(QcStep3Card, QcStep3Icon, 3);
    }

    private void ShowQcResult(string path, QcResult? result, QcCaptureLog? capture, string title, Func<string> summary)
    {
        qcOutputPath = path;
        lastQcResult = result;
        lastQcPresentation = new(capture, title, summary);
        QcFileName.Text = Path.GetFileName(path);
        QcFilePathText.Text = path;
        QcResultSection.Visibility = Visibility.Visible;
        QcLogButton.Visibility = result?.CaptureLog is null ? Visibility.Collapsed : Visibility.Visible;
        QcToggleInlineLogButton.Visibility = result?.CaptureLog is null ? Visibility.Collapsed : Visibility.Visible;
        QcHashButton.Visibility = result?.FrameMd5 is null ? Visibility.Collapsed : Visibility.Visible;
        QcReportButton.Visibility = result?.QcToolsReport is null ? Visibility.Collapsed : Visibility.Visible;
        RenderQcResult();
    }

    private async void QcToggleInlineLog_Click(object sender, RoutedEventArgs e)
    {
        if (lastQcResult?.CaptureLog is not { } logPath)
            return;

        if (QcInlineLogText.Visibility == Visibility.Visible)
        {
            QcInlineLogText.Visibility = Visibility.Collapsed;
            QcToggleInlineLogLabel.Text = L("QcViewInlineLog");
            return;
        }

        try
        {
            QcInlineLogText.Text = await File.ReadAllTextAsync(logPath);
            QcInlineLogText.Visibility = Visibility.Visible;
            QcToggleInlineLogLabel.Text = L("QcHideInlineLog");
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            QcOpenStatus.Message = ex.Message;
            QcOpenStatus.IsOpen = true;
        }
    }

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
        if (!recordingClock.IsRunning || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
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
        var animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

        // 1. Pulsing REC tally dots
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

        // 2. Rotating precision tape reel next to timecode
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

        // 3. 3-bar live write-activity equalizer inside top-right REC tally tag
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

        // 4. Breathing crimson Tally border on Video Monitor
        PreviewRecFrameGlow.Visibility = active && !previewFullscreen ? Visibility.Visible : Visibility.Collapsed;
        var glowVisual = ElementCompositionPreview.GetElementVisual(PreviewRecFrameGlow);
        glowVisual.StopAnimation(nameof(Visual.Opacity));
        glowVisual.Opacity = 1f;
        if (active && !previewFullscreen && animationsEnabled)
        {
            var breathe = glowVisual.Compositor.CreateScalarKeyFrameAnimation();
            breathe.InsertKeyFrame(0f, 0.95f);
            breathe.InsertKeyFrame(1f, 0.42f);
            breathe.Duration = TimeSpan.FromMilliseconds(1300);
            breathe.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
            breathe.IterationBehavior = AnimationIterationBehavior.Forever;
            glowVisual.StartAnimation(nameof(Visual.Opacity), breathe);
        }

        // 5. Continuous 2px crimson stream sweep bar along top of Footer
        RecordingSweepTrack.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        StartRecordingSweepAnimation();

        PreviewRecTally.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PreviewMonitorHousing.BorderBrush = active
            ? ThemeBrush("StudioRecBrush") : ThemeBrush("StudioMonitorBezelBrush");
    }

    private static void RevealWorkspacePanel(UIElement panel)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
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

    private static void PulseWarningChip(UIElement chip)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(chip);
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
        FullscreenClockText.Text = text;
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
        PreviewHudPill.Visibility = Visibility.Collapsed;
        SafeAreaOverlay.Visibility = Visibility.Collapsed;
        Ntsc480GuideOverlay.Visibility = Visibility.Collapsed;
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
            PreviewHudPill.Visibility = previewFullscreen ? Visibility.Collapsed : Visibility.Visible;
            ApplySafeAreaOverlay();
        }
        previewCaption = new(frame.NoInput, frame.SignalWidth, frame.SignalHeight,
            DeckLinkCapture.FormatFourCc((uint)frame.Format), feed.PresentedSequence);
        RenderPreviewCaption();
    }

    private void ShowCounts(CaptureStats stats)
    {
        displayedStats = stats;
        displayedPreviewSkips = preview?.SkippedFrames ?? displayedPreviewSkips;
        if (stats.MissingVideoFrames > lastMissingFrames)
            PulseWarningChip(MissingFramesChip);
        lastMissingFrames = stats.MissingVideoFrames;
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
        VideoFormat10Button.IsEnabled = idle;
        VideoFormat8Button.IsEnabled = idle;
        AudioFormat16Button.IsEnabled = idle;
        AudioFormat32Button.IsEnabled = idle;
        UpdateAnalogControlsState(idle);
        var canStart = idle && !recordingTransition && DevicePicker.SelectedItem is CaptureDevice &&
            VideoInputPicker.SelectedItem is VideoInputSource && AudioInputPicker.SelectedItem is AudioInputSource &&
            ModePicker.SelectedItem is ModeOption;
        var changingRecording = recordingSession || recordingTransition;
        var canChooseOutput = settingsReady && !closing && !loading && !pickingOutput && !changingRecording;
        OutputPath.IsEnabled = canChooseOutput;
        BrowseButton.IsEnabled = canChooseOutput;
        var previewCanStop = active && !closing && !changingRecording &&
            captureStop is { IsCancellationRequested: false };
        RenderCaptureActions();
        StartButton.IsEnabled = active ? previewCanStop : canStart;
        PlaceholderStartButton.IsEnabled = canStart;
        PlaceholderStartButton.Visibility = !active && canStart ? Visibility.Visible : Visibility.Collapsed;
        var recordEnabled = qcStop is null && !closing && active && !recordingTransition && (recordingSession ||
            (previewReady && displayedStats?.CanStartRecording == true && previewRecorder is not null && !pickingOutput &&
                captureStop is { IsCancellationRequested: false } && !string.IsNullOrWhiteSpace(OutputPath.Text)));
        RecordButton.IsEnabled = recordEnabled;
        FullscreenRecordButton.IsEnabled = recordEnabled;
        CancelQcButton.IsEnabled = qcStop is { IsCancellationRequested: false };
        QcCenterCancelButton.IsEnabled = CancelQcButton.IsEnabled;
        FooterQcActivePill.Visibility = qcStop is null ? Visibility.Collapsed : Visibility.Visible;
        QcCenterCancelButton.Visibility = qcStop is null ? Visibility.Collapsed : Visibility.Visible;
        RecordingModeHint.Visibility = ModePicker.SelectedItem is ModeOption { Id: null }
            ? Visibility.Visible : Visibility.Collapsed;
        RecordingClock.Visibility = recordingClock.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        FullscreenClockText.Visibility = recordingClock.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        QcResultsButton.Visibility = !changingRecording && qcStop is null && lastQcPresentation is not null
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateQcNavBadge();
        UpdateCaptureFooter();
        UpdateLivePreviewStatus();
        UpdatePreviewForSelection();
        UpdateConfigurationStatus();
    }

    private void UpdateQcNavBadge()
    {
        if (qcStop is not null)
        {
            QcNavBadgeDot.Visibility = Visibility.Visible;
            QcNavBadgeDot.Fill = ThemeBrush("StudioInfoBrush");
        }
        else if (lastQcResult is { Status: QcStatus.Failed })
        {
            QcNavBadgeDot.Visibility = Visibility.Visible;
            QcNavBadgeDot.Fill = ThemeBrush("StudioDangerBrush");
        }
        else if (lastQcResult is not null && QcWarningsPanel.Visibility == Visibility.Visible)
        {
            QcNavBadgeDot.Visibility = Visibility.Visible;
            QcNavBadgeDot.Fill = ThemeBrush("StudioCautionBrush");
        }
        else if (lastQcResult is { Status: QcStatus.Passed })
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

    private static bool IsSessionError(Exception ex) =>
        ex is COMException or InvalidOperationException or InvalidCastException or IOException or
            UnauthorizedAccessException or ArgumentException or OverflowException;
}
