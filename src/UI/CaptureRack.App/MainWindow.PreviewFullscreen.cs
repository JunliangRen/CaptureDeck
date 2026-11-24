using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private bool previewFullscreen;
    private bool restoreMaximizedWindow;
    private readonly DispatcherQueueTimer previewHoverTimer;
    private readonly DispatcherQueueTimer fullscreenIdleTimer;
    private readonly ScalarTransition previewFullscreenFade = new() { Duration = TimeSpan.FromMilliseconds(160) };
    private bool previewHoverReady;
    private bool previewTouchControlsVisible;
    private bool fullscreenBarVisible;
    private Thickness windowedWorkspacePadding;
    private double windowedWorkspaceColumnSpacing;

    private void PreviewSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
            return;

        previewHoverReady |= previewTouchControlsVisible;
        previewTouchControlsVisible = false;
        if (!previewHoverReady && !previewHoverTimer.IsRunning)
            previewHoverTimer.Start();
        UpdatePreviewFullscreenButton();
        if (previewFullscreen)
            WakeFullscreenMiniBar();
    }

    private void PreviewSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (previewFullscreen && e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
            WakeFullscreenMiniBar();
    }

    private void PreviewSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
            return;

        previewHoverTimer.Stop();
        previewHoverReady = false;
        UpdatePreviewFullscreenButton();
    }

    private void PreviewSurface_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.PointerDeviceType == PointerDeviceType.Mouse)
            return;

        previewTouchControlsVisible = !previewTouchControlsVisible;
        UpdatePreviewFullscreenButton();
        if (previewFullscreen)
        {
            fullscreenBarVisible = previewTouchControlsVisible;
            UpdateFullscreenMiniBar();
        }
    }

    private void PreviewSurface_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SetPreviewFullscreen(!previewFullscreen);
        e.Handled = true;
    }

    private void PreviewFullscreen_Click(object sender, RoutedEventArgs e) =>
        SetPreviewFullscreen(!previewFullscreen);

    private void PreviewFullscreen_FocusChanged(object sender, RoutedEventArgs e) =>
        UpdatePreviewFullscreenButton();

    private void PreviewFullscreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (activePage != ActiveAppPage.Capture ||
            workspaceLayout is not (WorkspaceLayoutMode.Focus or WorkspaceLayoutMode.Split))
            return;

        SetPreviewFullscreen(!previewFullscreen);
        args.Handled = true;
    }

    private void ExitPreviewFullscreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!previewFullscreen)
            return;

        SetPreviewFullscreen(false);
        args.Handled = true;
    }

    private void WakeFullscreenMiniBar()
    {
        fullscreenBarVisible = true;
        UpdateFullscreenMiniBar();
        fullscreenIdleTimer.Stop();
        fullscreenIdleTimer.Start();
    }

    private void UpdateFullscreenMiniBar()
    {
        var visible = previewFullscreen && fullscreenBarVisible;
        FullscreenMiniBar.Visibility = previewFullscreen ? Visibility.Visible : Visibility.Collapsed;
        var opacity = visible ? 1 : 0;
        if (FullscreenMiniBar.Opacity != opacity)
        {
            FullscreenMiniBar.OpacityTransition = new UISettings().AnimationsEnabled
                ? previewFullscreenFade : null;
            FullscreenMiniBar.Opacity = opacity;
        }
        FullscreenMiniBar.IsHitTestVisible = visible;
    }

    private void UpdatePreviewFullscreenButton()
    {
        var visible = !previewFullscreen && (previewHoverReady || previewTouchControlsVisible ||
            PreviewFullscreenButton.FocusState == FocusState.Keyboard);
        var opacity = visible ? 1 : 0;
        if (PreviewFullscreenButton.Opacity != opacity)
        {
            PreviewFullscreenButton.OpacityTransition = new UISettings().AnimationsEnabled
                ? previewFullscreenFade : null;
            PreviewFullscreenButton.Opacity = opacity;
        }
        PreviewFullscreenButton.IsHitTestVisible = visible;
    }

    private void UpdatePreviewFullscreenIcon()
    {
        PreviewFullscreenIcon.Glyph = previewFullscreen ? "\uE73F" : "\uE740";
        AutomationProperties.SetName(PreviewFullscreenButton,
            L(previewFullscreen ? "ExitPreviewFullScreen" : "PreviewFullScreen"));
        ToolTipService.SetToolTip(PreviewFullscreenButton,
            L(previewFullscreen ? "ExitPreviewFullScreen" : "PreviewFullScreen"));
    }

    private void SetPreviewFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            restoreMaximizedWindow = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            windowedWorkspacePadding = WorkspacePage.Padding;
            windowedWorkspaceColumnSpacing = WorkspacePage.ColumnSpacing;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else
        {
            fullscreenIdleTimer.Stop();
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            if (restoreMaximizedWindow && AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Maximized } window)
                window.Maximize();
        }

        previewFullscreen = fullscreen;
        TitleBarGrid.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        StageToolbar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        WorkspacePage.Background = fullscreen ? new SolidColorBrush(Colors.Black) : null;
        WorkspacePage.Padding = fullscreen ? new Thickness(0) : windowedWorkspacePadding;
        WorkspacePage.ColumnSpacing = fullscreen ? 0 : windowedWorkspaceColumnSpacing;
        PreviewMonitorHousing.CornerRadius = fullscreen ? new CornerRadius(0) : new CornerRadius(4);
        PreviewMonitorHousing.BorderThickness = fullscreen ? new Thickness(0) : new Thickness(1.5);
        PreviewRecFrameGlow.Visibility = !fullscreen && recordingClock.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        PreviewSurface.BorderThickness = fullscreen ? new Thickness(0) : new Thickness(1);
        PreviewSurface.CornerRadius = new CornerRadius(0);
        PreviewHudPill.Visibility = !fullscreen && previewCaption is not null ? Visibility.Visible : Visibility.Collapsed;
        StereoMeter.Visibility = !fullscreen && lastWindowWidth >= 680 ? Visibility.Visible : Visibility.Collapsed;

        UpdateInspectorLayout();
        ApplyWorkspaceLayout();
        UpdateCaptureFooter();
        UpdateMonitorLayout();

        previewTouchControlsVisible = false;
        UpdatePreviewFullscreenIcon();
        UpdatePreviewFullscreenButton();
        if (fullscreen)
            WakeFullscreenMiniBar();
        else
        {
            fullscreenBarVisible = false;
            UpdateFullscreenMiniBar();
        }
    }
}
