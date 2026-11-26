using CaptureRack.App.Controls;
using CaptureRack.App.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private bool previewFullscreen;
    private bool fullscreenCoversMainDisplay;
    private bool enteringFullscreen;
    private FullscreenPreviewWindow? fullscreenWindow;
    private readonly DispatcherQueueTimer previewHoverTimer;
    private readonly ScalarTransition previewFullscreenFade = new() { Duration = TimeSpan.FromMilliseconds(160) };
    private bool previewHoverReady;
    private bool previewTouchControlsVisible;

    private bool PauseMainWindowRendering =>
        FullscreenRenderPolicy.ShouldPauseMainWindowRendering(previewFullscreen, fullscreenCoversMainDisplay);

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated &&
            !enteringFullscreen && previewFullscreen && fullscreenCoversMainDisplay)
            SetPreviewFullscreen(false);
    }

    private void PreviewSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
            return;

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
        if (previewFullscreen)
        {
            SetPreviewFullscreen(false);
            args.Handled = true;
            return;
        }

        if (activePage != ActiveAppPage.Capture || !ActiveWorkspaceCapabilities.SupportsVideoPreview ||
            EffectiveWorkspaceLayout is not (WorkspaceLayoutMode.Focus or WorkspaceLayoutMode.Split))
            return;

        SetPreviewFullscreen(true);
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
        var visible = !previewFullscreen && (previewHoverReady || previewTouchControlsVisible ||
            PreviewFullscreenButton.FocusState == FocusState.Keyboard);
        var opacity = visible ? 1 : 0;
        if (PreviewFullscreenButton.Opacity != opacity)
        {
            PreviewFullscreenButton.OpacityTransition = StudioFeedback.AnimationsEnabled
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

    private void CloseFullscreenPreviewWindow(bool suppressRestore) =>
        SetPreviewFullscreen(false, suppressRestore);

    private void SetPreviewFullscreen(bool fullscreen, bool suppressRestore = false)
    {
        if (previewFullscreen == fullscreen)
            return;

        if (fullscreen)
        {
            if (closing || closeAfterQc || finishingMediaForClose ||
                !HasLiveCapture || !ActiveWorkspaceCapabilities.SupportsVideoPreview)
                return;

            enteringFullscreen = true;
            try
            {
                var mainDisplay = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                var window = new FullscreenPreviewWindow(mainDisplay);
                window.ExitRequested += FullscreenWindow_ExitRequested;
                window.TogglePreviewRequested += FullscreenWindow_TogglePreviewRequested;
                window.ToggleRecordRequested += FullscreenWindow_ToggleRecordRequested;
                window.ToggleAudioMonitorRequested += FullscreenWindow_ToggleAudioMonitorRequested;

                fullscreenWindow = window;
                previewFullscreen = true;
                fullscreenCoversMainDisplay = window.TargetDisplayId == mainDisplay.DisplayId;

                // Pause redundant main-window preview, scope, and recording-indicator animations when covered on the same display.
                if (PauseMainWindowRendering)
                {
                    PreviewImage.Source = null;
                    if (preview is { } feed)
                        feed.RenderScopes = false;
                    UpdateRecordingIndicator(recordingClock.IsRunning);
                }

                SyncFullscreenPreviewWindow();
                previewTouchControlsVisible = false;
                UpdatePreviewFullscreenIcon();
                UpdatePreviewFullscreenButton();
                window.ShowWithFade();
            }
            finally
            {
                enteringFullscreen = false;
            }
        }
        else
        {
            previewFullscreen = false;
            fullscreenCoversMainDisplay = false;
            var window = fullscreenWindow;
            fullscreenWindow = null;

            if (window is not null)
            {
                window.ExitRequested -= FullscreenWindow_ExitRequested;
                window.TogglePreviewRequested -= FullscreenWindow_TogglePreviewRequested;
                window.ToggleRecordRequested -= FullscreenWindow_ToggleRecordRequested;
                window.ToggleAudioMonitorRequested -= FullscreenWindow_ToggleAudioMonitorRequested;
            }

            // Re-attach the live preview bitmap and resume scopes BEFORE closing the overlay window
            // so the untouched main window underneath appears immediately with zero black flash.
            var restoreMainWindow = !suppressRestore && !closing;
            if (restoreMainWindow)
            {
                PreviewImage.Source = previewBitmap;
                if (preview is { } feed)
                {
                    feed.RenderScopes = FullscreenRenderPolicy.ShouldRenderVideoScopes(
                        pauseMainWindowRendering: false,
                        EffectiveWorkspaceLayout is WorkspaceLayoutMode.Split or WorkspaceLayoutMode.Scopes);
                }
                nextMonitorFrame = 0;
                RenderPreviewCaption();
                UpdateRecordingIndicator(recordingClock.IsRunning);
                ApplySafeAreaOverlay();
                UpdateMonitorLayout();
            }

            window?.CloseCleanly();

            previewTouchControlsVisible = false;
            if (restoreMainWindow)
            {
                UpdatePreviewFullscreenIcon();
                UpdatePreviewFullscreenButton();
                Activate();
            }
        }
    }

    private void SyncFullscreenPreviewWindow()
    {
        if (fullscreenWindow is not { } window)
            return;

        var hasFrame = previewCaption is not null && previewBitmap is not null;
        window.SetPreviewImage(previewBitmap, hasFrame);
        window.UpdatePlaceholder(
            PreviewPlaceholderText.Text,
            IsCaptureInputActive && !hasFrame && (LiveMediaSource?.HasPreview ?? true));
        window.UpdateGeometry(
            previewAspectRatio,
            showSafeArea,
            LiveMediaSource is null && showNtsc480Guide);

        var recordingActive = IsCaptureRecording && qcStop is null;
        var finalizing = (recordingTransition && qcStop is null) || LiveMediaSource?.IsFinalizing == true;
        window.UpdateChrome(
            L("PreviewImage"),
            StageSignalText.Text,
            signalDotBrushKey,
            recordingActive,
            finalizing,
            RecordingElapsed.Text,
            recordingClock.IsRunning,
            RecordButton.Visibility == Visibility.Visible,
            RecordButton.IsEnabled,
            RecordActionLabel.Text,
            RecordButton.Style,
            L("ExitPreviewFullScreen"));
    }

    private void FullscreenWindow_ExitRequested(object? sender, EventArgs e) =>
        SetPreviewFullscreen(false);

    private void FullscreenWindow_TogglePreviewRequested(object? sender, EventArgs e)
    {
        if (activePage == ActiveAppPage.Capture && StartButton.IsEnabled)
            StartButton_Click(StartButton, new RoutedEventArgs());
    }

    private void FullscreenWindow_ToggleRecordRequested(object? sender, EventArgs e)
    {
        if (activePage == ActiveAppPage.Capture && RecordButton.IsEnabled)
            RecordButton_Click(RecordButton, new RoutedEventArgs());
    }

    private void FullscreenWindow_ToggleAudioMonitorRequested(object? sender, EventArgs e)
    {
        if (activePage == ActiveAppPage.Capture && HasLiveCapture && ActiveWorkspaceCapabilities.SupportsLocalAudioMonitor)
            ToggleAudioMonitor();
    }
}
