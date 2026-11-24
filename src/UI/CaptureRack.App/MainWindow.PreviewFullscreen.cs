using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private bool previewFullscreen;
    private bool restoreMaximizedWindow;
    private readonly DispatcherQueueTimer previewHoverTimer;
    private readonly ScalarTransition previewFullscreenFade = new() { Duration = TimeSpan.FromMilliseconds(150) };
    private bool previewHoverReady;
    private bool previewTouchControlsVisible;
    private Thickness windowedWorkspacePadding;
    private double windowedWorkspaceRowSpacing;

    private void PreviewSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
            return;

        // Keep the icon visible when switching from a tap to the mouse.
        previewHoverReady |= previewTouchControlsVisible;
        previewTouchControlsVisible = false;
        if (!previewHoverReady && !previewHoverTimer.IsRunning)
            previewHoverTimer.Start();
        UpdatePreviewFullscreenButton();
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
        if (WorkspacePage.Visibility != Visibility.Visible || MonitorSelector.SelectedItem != PreviewTab)
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

    private void UpdatePreviewFullscreenButton()
    {
        var visible = previewHoverReady || previewTouchControlsVisible ||
            PreviewFullscreenButton.FocusState == FocusState.Keyboard;
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
    }

    private void SetPreviewFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            restoreMaximizedWindow = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            windowedWorkspacePadding = WorkspacePage.Padding;
            windowedWorkspaceRowSpacing = WorkspacePage.RowSpacing;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            if (restoreMaximizedWindow && AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Maximized } window)
                window.Maximize();
        }

        previewFullscreen = fullscreen;
        WorkspacePage.Background = fullscreen ? new SolidColorBrush(Colors.Black) : null;
        WorkspacePage.Padding = fullscreen ? new Thickness(0) : windowedWorkspacePadding;
        WorkspacePage.RowSpacing = fullscreen ? 0 : windowedWorkspaceRowSpacing;
        MonitorHeader.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        StereoMeter.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        PreviewDetail.Visibility = !fullscreen && previewCaption is not null ? Visibility.Visible : Visibility.Collapsed;
        PreviewView.VerticalAlignment = fullscreen ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        UpdateCaptureFooter();
        UpdateMonitorLayout();

        PreviewFullscreenButton.Focus(FocusState.Programmatic);
        previewTouchControlsVisible = false;
        UpdatePreviewFullscreenIcon();
        UpdatePreviewFullscreenButton();
    }
}
