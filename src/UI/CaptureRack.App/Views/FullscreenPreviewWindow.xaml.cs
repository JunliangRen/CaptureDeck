using System.Numerics;
using System.Runtime.InteropServices;
using CaptureRack.App.Controls;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace CaptureRack.App.Views;

internal sealed partial class FullscreenPreviewWindow : Window
{
    private static readonly SolidColorBrush WhiteSpinnerBrush = new(Colors.White);
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    private readonly DispatcherQueueTimer idleTimer;
    private readonly ScalarTransition miniBarFade = new() { Duration = TimeSpan.FromMilliseconds(160) };
    private double aspectRatio = 4d / 3;
    private bool showSafeArea;
    private bool showNtsc480Guide;
    private RfGuideGeometry? cropGuide;
    private bool hasFrame;
    private bool miniBarVisible;
    private bool pointerOverMiniBar;
    private bool focusInsideMiniBar;
    private bool closingProgrammatically;
    private bool exitQueued;
    private bool recAnimationActive;
    private bool pendingRevealAnimation;
    private string? lastSignalDotBrushKey;
    private ScalarKeyFrameAnimation? revealOpacityAnimation;
    private Vector3KeyFrameAnimation? revealScaleAnimation;
    private Point? lastPointerPosition;

    public Image PreviewImageControl => PreviewImage;
    public Microsoft.UI.DisplayId TargetDisplayId { get; private set; }

    public event EventHandler? ExitRequested;
    public event EventHandler? TogglePreviewRequested;
    public event EventHandler? ToggleRecordRequested;
    public event EventHandler? ToggleAudioMonitorRequested;
    public event EventHandler? SwitchDisplayRequested;

    public FullscreenPreviewWindow(DisplayArea targetDisplay)
    {
        InitializeComponent();
        TargetDisplayId = targetDisplay.DisplayId;

        idleTimer = DispatcherQueue.CreateTimer();
        idleTimer.Interval = TimeSpan.FromSeconds(2);
        idleTimer.IsRepeating = false;
        idleTimer.Tick += (_, _) =>
        {
            if (pointerOverMiniBar || focusInsideMiniBar)
                return;
            miniBarVisible = false;
            UpdateMiniBarVisibility();
        };

        ConfigureBorderlessFullscreen(targetDisplay.OuterBounds);
        AppWindow.Closing += OnAppWindowClosing;
    }

    public void ShowWithFade()
    {
        pendingRevealAnimation = StudioFeedback.AnimationsEnabled;
        if (pendingRevealAnimation)
            PreviewSurface.Opacity = 0;
        Activate();
        UpdateSurfaceLayout();
        WakeMiniBar();
    }

    public void MoveToDisplay(DisplayArea targetDisplay)
    {
        TargetDisplayId = targetDisplay.DisplayId;
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        AppWindow.MoveAndResize(targetDisplay.OuterBounds);
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        UpdateSurfaceLayout();
        WakeMiniBar();
    }

    public void CloseCleanly()
    {
        closingProgrammatically = true;
        pendingRevealAnimation = false;
        AppWindow.Closing -= OnAppWindowClosing;
        idleTimer.Stop();
        SetRecordingTally(active: false);
        try
        {
            if (revealOpacityAnimation is not null)
                PreviewSurface.StopAnimation(revealOpacityAnimation);
            if (revealScaleAnimation is not null)
                PreviewSurface.StopAnimation(revealScaleAnimation);
        }
        catch (COMException)
        {
        }
        revealOpacityAnimation = null;
        revealScaleAnimation = null;
        PreviewImage.Source = null;
        try
        {
            Close();
        }
        catch (COMException)
        {
        }
    }

    public void SetPreviewImage(ImageSource? source, bool frameAvailable)
    {
        if (!ReferenceEquals(PreviewImage.Source, source))
            PreviewImage.Source = source;
        if (frameAvailable && PlaceholderSpinner.IsActive)
        {
            PlaceholderSpinner.IsActive = false;
            PlaceholderSpinner.Visibility = Visibility.Collapsed;
            PlaceholderIcon.Visibility = Visibility.Visible;
        }
        if (hasFrame != frameAvailable)
        {
            hasFrame = frameAvailable;
            UpdateOverlayVisibility();
        }
    }

    public void UpdatePlaceholder(string text, bool waitingSpinner)
    {
        if (PlaceholderText.Text != text)
            PlaceholderText.Text = text;
        var showSpinner = waitingSpinner && !hasFrame;
        PlaceholderSpinner.IsActive = showSpinner;
        PlaceholderSpinner.Visibility = showSpinner ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderIcon.Visibility = showSpinner ? Visibility.Collapsed : Visibility.Visible;
        UpdateOverlayVisibility();
    }

    public void UpdateGeometry(double newAspectRatio, bool safeArea, bool ntsc480Guide, RfGuideGeometry? guide = null)
    {
        var aspectChanged = Math.Abs(aspectRatio - newAspectRatio) > 0.0001;
        aspectRatio = newAspectRatio;
        showSafeArea = safeArea;
        showNtsc480Guide = ntsc480Guide;
        cropGuide = guide;
        if (aspectChanged || double.IsNaN(PreviewSurface.Width) || PreviewSurface.Width <= 0)
            UpdateSurfaceLayout();
        UpdateOverlayVisibility();
    }

    public void UpdateChrome(
        string previewAccessibleName,
        string statusText,
        string signalDotBrushKey,
        bool recordingActive,
        bool finalizing,
        string clockText,
        bool showClock,
        bool showRecordButton,
        bool recordEnabled,
        string recordLabel,
        Style recordStyle,
        string exitLabel,
        bool showAudioMute = false,
        bool audioMonitorActive = false,
        string audioMuteTooltip = "",
        string? audioRouteBadge = null,
        bool showPlaybackButton = false,
        bool playbackPlaying = false,
        bool playbackEnabled = false,
        string playbackLabel = "",
        bool showSwitchDisplay = false,
        string switchDisplayTooltip = "")
    {
        AutomationProperties.SetName(PreviewImage, previewAccessibleName);
        if (StatusText.Text != statusText)
            StatusText.Text = statusText;
        if (lastSignalDotBrushKey != signalDotBrushKey)
        {
            lastSignalDotBrushKey = signalDotBrushKey;
            SignalDot.Fill = StudioBrushes.Resolve(RootCanvas, signalDotBrushKey);
        }
        if (ClockText.Text != clockText)
            ClockText.Text = clockText;
        ClockText.Visibility = showClock ? Visibility.Visible : Visibility.Collapsed;

        PlaybackButton.Visibility = showPlaybackButton ? Visibility.Visible : Visibility.Collapsed;
        PlaybackButton.IsEnabled = playbackEnabled;
        PlaybackIcon.Glyph = playbackPlaying ? "\uE769" : "\uE768";
        if (PlaybackLabel.Text != playbackLabel)
            PlaybackLabel.Text = playbackLabel;
        AutomationProperties.SetName(PlaybackButton, playbackLabel);

        AudioMuteButton.Visibility = showAudioMute ? Visibility.Visible : Visibility.Collapsed;
        AudioMuteIcon.Glyph = audioMonitorActive ? "\uE767" : "\uE74F";
        AudioMuteIcon.Foreground = StudioBrushes.Resolve(RootCanvas,
            audioMonitorActive ? "StudioAccentBrush" : "StudioTextSecondaryBrush");
        AudioMuteButton.Background = audioMonitorActive
            ? StudioBrushes.Resolve(RootCanvas, "StudioAccentSoftBrush")
            : TransparentBrush;
        if (!string.IsNullOrEmpty(audioMuteTooltip))
        {
            ToolTipService.SetToolTip(AudioMuteButton, audioMuteTooltip);
            AutomationProperties.SetName(AudioMuteButton, audioMuteTooltip);
        }
        var hasRoute = !string.IsNullOrEmpty(audioRouteBadge);
        AudioRouteText.Visibility = hasRoute ? Visibility.Visible : Visibility.Collapsed;
        if (hasRoute && AudioRouteText.Text != audioRouteBadge)
            AudioRouteText.Text = audioRouteBadge;

        RecordButton.Visibility = showRecordButton ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.IsEnabled = recordEnabled;
        if (!ReferenceEquals(RecordButton.Style, recordStyle))
            RecordButton.Style = recordStyle;
        if (RecordLabel.Text != recordLabel)
            RecordLabel.Text = recordLabel;
        AutomationProperties.SetName(RecordButton, recordLabel);
        RecordIcon.Glyph = recordingActive ? "\uE71A" : "\uE7C8";
        RecordIcon.Visibility = finalizing ? Visibility.Collapsed : Visibility.Visible;
        RecordSpinner.Visibility = finalizing ? Visibility.Visible : Visibility.Collapsed;
        RecordSpinner.IsActive = finalizing;
        if (finalizing)
        {
            RecordSpinner.Foreground = recordingActive
                ? WhiteSpinnerBrush
                : StudioBrushes.Resolve(RootCanvas, "StudioRecBrush");
        }

        SwitchDisplayButton.Visibility = showSwitchDisplay ? Visibility.Visible : Visibility.Collapsed;
        if (showSwitchDisplay && !string.IsNullOrEmpty(switchDisplayTooltip))
        {
            ToolTipService.SetToolTip(SwitchDisplayButton, switchDisplayTooltip);
            AutomationProperties.SetName(SwitchDisplayButton, switchDisplayTooltip);
        }

        if (ExitLabel.Text != exitLabel)
            ExitLabel.Text = exitLabel;
        AutomationProperties.SetName(ExitButton, exitLabel);
        SetRecordingTally(recordingActive);
    }

    private void ConfigureBorderlessFullscreen(RectInt32 bounds)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        const int dwmwaTransitionsForceDisabled = 3;
        var disableTransitions = 1;
        _ = DwmSetWindowAttribute(hwnd, dwmwaTransitionsForceDisabled, ref disableTransitions, sizeof(int));

        AppWindow.MoveAndResize(bounds);
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closingProgrammatically)
            return;

        args.Cancel = true;
        if (exitQueued)
            return;
        exitQueued = true;
        DispatcherQueue.TryEnqueue(() => ExitRequested?.Invoke(this, EventArgs.Empty));
    }

    private void UpdateSurfaceLayout()
    {
        var (w, h) = FullscreenRenderPolicy.ComputePreviewSurfaceSize(
            RootCanvas.ActualWidth, RootCanvas.ActualHeight, aspectRatio, padding: 0);
        if (w <= 0 || h <= 0)
            return;

        PreviewSurface.Width = w;
        PreviewSurface.Height = h;
        PreviewSurface.CenterPoint = new Vector3((float)(w / 2), (float)(h / 2), 0f);
        UpdateNtsc480GuideLayout();

        if (pendingRevealAnimation)
        {
            pendingRevealAnimation = false;
            StartRevealAnimation();
        }
    }

    private void StartRevealAnimation()
    {
        PreviewSurface.Opacity = 1;
        if (!StudioFeedback.AnimationsEnabled)
            return;

        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = nameof(UIElement.Opacity);
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, easing);
        opacity.Duration = TimeSpan.FromMilliseconds(150);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Target = nameof(UIElement.Scale);
        scale.InsertKeyFrame(0f, new Vector3(0.985f, 0.985f, 1f));
        scale.InsertKeyFrame(1f, Vector3.One, easing);
        scale.Duration = TimeSpan.FromMilliseconds(170);

        revealOpacityAnimation = opacity;
        revealScaleAnimation = scale;
        PreviewSurface.StartAnimation(opacity);
        PreviewSurface.StartAnimation(scale);
    }

    private void UpdateOverlayVisibility()
    {
        PreviewPlaceholder.Visibility = hasFrame ? Visibility.Collapsed : Visibility.Visible;
        SafeAreaOverlay.Visibility = hasFrame && showSafeArea ? Visibility.Visible : Visibility.Collapsed;
        UpdateNtsc480GuideLayout();
    }

    private void UpdateNtsc480GuideLayout()
    {
        var geometry = cropGuide;
        double width = PreviewSurface.Width, height = PreviewSurface.Height;
        SafeAreaOverlay.Margin = geometry is not null && double.IsFinite(width + height)
            ? new Thickness(width * geometry.Left / geometry.Width, height * geometry.Top / geometry.Height,
                width * geometry.Right / geometry.Width, height * geometry.Bottom / geometry.Height)
            : default;

        var show = hasFrame && showNtsc480Guide;
        Ntsc480GuideOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;

        var (top, bottom) = geometry?.Crop is { } crop ? (height * crop.Top / geometry.Height, height * crop.Bottom / geometry.Height)
            : FullscreenRenderPolicy.ComputeNtsc480GuideHeights(PreviewSurface.Height);
        if (!double.IsFinite(top + bottom))
            return;

        Ntsc480TopShade.Height = top;
        Ntsc480BottomShade.Height = bottom;
    }

    private void SetRecordingTally(bool active)
    {
        RecTally.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (recAnimationActive == active)
            return;
        recAnimationActive = active;

        var animationsEnabled = StudioFeedback.AnimationsEnabled;
        var dotVisual = ElementCompositionPreview.GetElementVisual(RecTallyDot);
        dotVisual.StopAnimation(nameof(Visual.Opacity));
        dotVisual.Opacity = 1f;
        if (active && animationsEnabled)
        {
            var pulse = dotVisual.Compositor.CreateScalarKeyFrameAnimation();
            pulse.InsertKeyFrame(0f, 1f);
            pulse.InsertKeyFrame(1f, 0.30f);
            pulse.Duration = TimeSpan.FromMilliseconds(850);
            pulse.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;
            dotVisual.StartAnimation(nameof(Visual.Opacity), pulse);
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
    }

    private void WakeMiniBar()
    {
        miniBarVisible = true;
        UpdateMiniBarVisibility();
        idleTimer.Stop();
        if (!pointerOverMiniBar && !focusInsideMiniBar)
            idleTimer.Start();
    }

    private void UpdateMiniBarVisibility()
    {
        var opacity = miniBarVisible ? 1 : 0;
        if (MiniBar.Opacity != opacity)
        {
            MiniBar.OpacityTransition = StudioFeedback.AnimationsEnabled ? miniBarFade : null;
            MiniBar.Opacity = opacity;
        }
        MiniBar.IsHitTestVisible = miniBarVisible;
    }

    private void RootCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSurfaceLayout();

    private void RootCanvas_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
            return;
        lastPointerPosition = e.GetCurrentPoint(RootCanvas).Position;
        WakeMiniBar();
    }

    private void RootCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
            return;
        var pos = e.GetCurrentPoint(RootCanvas).Position;
        if (lastPointerPosition is { } prev && Math.Abs(pos.X - prev.X) < 3 && Math.Abs(pos.Y - prev.Y) < 3)
            return;
        lastPointerPosition = pos;
        WakeMiniBar();
    }

    private void RootCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (pointerOverMiniBar)
            return;

        focusInsideMiniBar = false;
        if (miniBarVisible && e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
            idleTimer.Start();
    }

    private void RootCanvas_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.PointerDeviceType == PointerDeviceType.Mouse)
            return;
        focusInsideMiniBar = false;
        miniBarVisible = !miniBarVisible;
        idleTimer.Stop();
        UpdateMiniBarVisibility();
    }

    private void RootCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void MiniBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        pointerOverMiniBar = true;
        idleTimer.Stop();
    }

    private void MiniBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        pointerOverMiniBar = false;
        if (miniBarVisible && !focusInsideMiniBar)
            idleTimer.Start();
    }

    private void MiniBar_GotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Control { FocusState: FocusState.Keyboard })
            return;

        focusInsideMiniBar = true;
        WakeMiniBar();
    }

    private void MiniBar_LostFocus(object sender, RoutedEventArgs e)
    {
        focusInsideMiniBar = RootCanvas.XamlRoot is { } xamlRoot &&
            FocusManager.GetFocusedElement(xamlRoot) is Control { FocusState: FocusState.Keyboard } focused &&
            IsDescendantOf(focused, MiniBar);
        if (!focusInsideMiniBar && miniBarVisible && !pointerOverMiniBar)
            idleTimer.Start();
    }

    private void MiniBar_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void MiniBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject ancestor)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, ancestor))
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e) => ToggleRecordRequested?.Invoke(this, EventArgs.Empty);

    private void PlaybackButton_Click(object sender, RoutedEventArgs e) => TogglePreviewRequested?.Invoke(this, EventArgs.Empty);

    private void AudioMuteButton_Click(object sender, RoutedEventArgs e) => ToggleAudioMonitorRequested?.Invoke(this, EventArgs.Empty);

    private void SwitchDisplayButton_Click(object sender, RoutedEventArgs e) => SwitchDisplayRequested?.Invoke(this, EventArgs.Empty);

    private void ExitButton_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void SpacePlayPause_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (RootCanvas.XamlRoot is { } xamlRoot && FocusManager.GetFocusedElement(xamlRoot) is Button)
            return;
        if (PlaybackButton.Visibility == Visibility.Visible && PlaybackButton.IsEnabled)
        {
            TogglePreviewRequested?.Invoke(this, EventArgs.Empty);
            args.Handled = true;
        }
    }

    private void ExitFullscreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private void TogglePreview_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        TogglePreviewRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private void ToggleRecord_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleRecordRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    private void ToggleAudioMonitor_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleAudioMonitorRequested?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
