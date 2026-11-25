using CaptureRack.DeckLink;
using Microsoft.UI.Xaml;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private enum PreviewAspectMode { Auto, Ratio4x3, Ratio16x9, Square1x1 }

    private PreviewDeinterlaceMode previewDeinterlace = PreviewDeinterlaceMode.BobWeave;
    private PreviewAspectMode previewAspectMode = PreviewAspectMode.Auto;
    private PreviewColorMatrix previewColorMatrix = PreviewColorMatrix.Auto;
    private bool showNtsc480Guide;
    private bool audioMonitorEnabled;
    private double audioMonitorVolume = 0.8;
    private LocalAudioMonitor? activeAudioMonitor;
    private int lastSignalWidth;
    private int lastSignalHeight;
    private bool previewDeinterlaceLocked;
    private bool UsesDoubleRatePreview =>
        !previewDeinterlaceLocked && previewDeinterlace == PreviewDeinterlaceMode.BobWeave2x;

    private void UpdatePreviewScanMode(bool? interlaced)
    {
        var locked = interlaced == false;
        if (previewDeinterlaceLocked == locked)
            return;

        previewDeinterlaceLocked = locked;
        ApplyPreviewOptionsToUi();
    }

    private static PreviewDeinterlaceMode ParsePreviewDeinterlace(string tag) =>
        Enum.TryParse<PreviewDeinterlaceMode>(tag, ignoreCase: true, out var mode) &&
        mode is PreviewDeinterlaceMode.Off or PreviewDeinterlaceMode.BobWeave2x
            ? mode : PreviewDeinterlaceMode.BobWeave;

    private void ApplyPreviewOptionsToUi()
    {
        monitorOptionsBar.SyncState(
            previewDeinterlace.ToString(),
            previewDeinterlaceLocked,
            previewAspectMode.ToString(),
            previewColorMatrix.ToString(),
            showSafeArea,
            showNtsc480Guide,
            audioMonitorEnabled,
            audioMonitorVolume);

        SyncSettingsPageSelections();
        ApplyPreviewSettingsToLiveFeed();
        ApplySafeAreaOverlay();
        RecomputePreviewAspectRatio();
    }

    private void ApplyPreviewSettingsToLiveFeed()
    {
        if (preview is { } feed)
        {
            feed.DeinterlaceMode = previewDeinterlace;
            feed.ColorMatrix = LiveMediaSource is null ? previewColorMatrix : PreviewColorMatrix.Auto;
            feed.ScopeWaveformMode = scopeWaveformMode;
            feed.ScopeIreScale = scopeLumaScale == ScopeLumaScaleMode.Ire;
            var interval = TimeSpan.FromMilliseconds(1000d / (UsesDoubleRatePreview ? 120 : 60));
            if (previewTimer.Interval != interval)
                previewTimer.Interval = interval;
        }
        LiveMediaSource?.SetAudioMonitor(audioMonitorEnabled, audioMonitorVolume);
        if (activeAudioMonitor is { } monitor)
        {
            monitor.Enabled = audioMonitorEnabled;
            monitor.Volume = audioMonitorVolume;
        }
    }

    private void SetPreviewDeinterlace(string tag)
    {
        if (previewDeinterlaceLocked)
            return;

        previewDeinterlace = ParsePreviewDeinterlace(tag);
        ApplyPreviewOptionsToUi();
        SettingsChanged();
    }

    private void SetPreviewAspect(string tag)
    {
        previewAspectMode = Enum.TryParse<PreviewAspectMode>(tag, out var parsed)
            ? parsed : PreviewAspectMode.Auto;
        ApplyPreviewOptionsToUi();
        SettingsChanged();
    }

    private void SetPreviewMatrix(string tag)
    {
        previewColorMatrix = Enum.TryParse<PreviewColorMatrix>(tag, out var parsed)
            ? parsed : PreviewColorMatrix.Auto;
        ApplyPreviewOptionsToUi();
        SettingsChanged();
    }

    private void SetSafeArea(bool enabled)
    {
        showSafeArea = enabled;
        ApplyPreviewOptionsToUi();
        SettingsChanged();
    }

    private void SetNtsc480Guide(bool enabled)
    {
        showNtsc480Guide = enabled;
        ApplyPreviewOptionsToUi();
        SettingsChanged();
    }

    private void ToggleAudioMonitor()
    {
        audioMonitorEnabled = !audioMonitorEnabled;
        if (activeAudioMonitor is { } monitor)
            monitor.Enabled = audioMonitorEnabled;
        ApplyPreviewOptionsToUi();
        SettingsChanged();
    }

    private void SetAudioMonitorVolume(double volume)
    {
        audioMonitorVolume = Math.Clamp(volume, 0.0, 1.0);
        if (activeAudioMonitor is { } monitor)
            monitor.Volume = audioMonitorVolume;
        LiveMediaSource?.SetAudioMonitor(audioMonitorEnabled, audioMonitorVolume);
        SettingsChanged();
    }

    private void RecomputePreviewAspectRatio() =>
        UpdatePreviewAspectRatio(lastSignalWidth, lastSignalHeight);

    private double ComputeEffectivePreviewAspectRatio(int width, int height) => previewAspectMode switch
    {
        PreviewAspectMode.Ratio4x3 => 4d / 3,
        PreviewAspectMode.Ratio16x9 => 16d / 9,
        PreviewAspectMode.Square1x1 when width > 0 && height > 0 => (double)width / height,
        PreviewAspectMode.Square1x1 => 720d / 486,
        _ when decodedDisplayAspectRatio > 0 => decodedDisplayAspectRatio,
        _ => width <= 0 || height <= 0 || (width == 720 && height <= 576)
            ? DefaultPreviewAspectRatio : (double)width / height
    };

    private void UpdateNtsc480GuideLayout()
    {
        var show = LiveMediaSource is null && showNtsc480Guide && PreviewPlaceholder.Visibility == Visibility.Collapsed;
        Ntsc480GuideOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || PreviewSurface.Height <= 0)
            return;

        var h = PreviewSurface.Height;
        Ntsc480TopShade.Height = Math.Max(2, h * 4.0 / 486.0);
        Ntsc480BottomShade.Height = Math.Max(1, h * 2.0 / 486.0);
    }

    private void ApplySignalAndMonitorLanguage()
    {
        monitorOptionsBar.ApplyLanguage(
            L,
            previewDeinterlace.ToString(),
            previewDeinterlaceLocked,
            previewAspectMode.ToString(),
            previewColorMatrix.ToString(),
            showSafeArea,
            showNtsc480Guide,
            audioMonitorEnabled,
            audioMonitorVolume);
    }
}
