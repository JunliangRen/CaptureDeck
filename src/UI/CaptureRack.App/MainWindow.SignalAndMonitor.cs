using System.Globalization;
using CaptureRack.DeckLink;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

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

    private string analogNtscIre = "Driver";
    private string analogComponentLevel = "Driver";
    private string analogAudioLevel = "Driver";
    private bool analogAudioGainOverride;
    private double analogAudioGainLeftDb;
    private double analogAudioGainRightDb;
    private bool analogAudioGainLinked = true;
    private bool syncingAnalogSliders;
    private bool syncingPreviewPickers;
    private int lastSignalWidth;
    private int lastSignalHeight;

    private AnalogInputOptions BuildAnalogInputOptions() => new()
    {
        NtscIre = analogNtscIre switch
        {
            "Ire0" => AnalogNtscIreMode.Ire0,
            "Ire75" => AnalogNtscIreMode.Ire75,
            _ => AnalogNtscIreMode.Driver
        },
        ComponentLevel = analogComponentLevel switch
        {
            "Smpte" => AnalogComponentLevelMode.Smpte,
            "Betacam" => AnalogComponentLevelMode.Betacam,
            _ => AnalogComponentLevelMode.Driver
        },
        AudioLevel = analogAudioLevel switch
        {
            "Pro" => AnalogAudioLevelMode.Pro,
            "Consumer" => AnalogAudioLevelMode.Consumer,
            _ => AnalogAudioLevelMode.Driver
        },
        GainLeftDb = analogAudioGainOverride ? analogAudioGainLeftDb : null,
        GainRightDb = analogAudioGainOverride ? analogAudioGainRightDb : null
    };

    private void UpdateAnalogControlsState(bool idle)
    {
        var device = DevicePicker.SelectedItem as CaptureDevice;
        var videoId = (VideoInputPicker.SelectedItem as VideoInputSource)?.Id ?? 0;
        var audioId = (AudioInputPicker.SelectedItem as AudioInputSource)?.Id ?? 0;
        var caps = device?.AnalogCapabilities ?? new AnalogInputCapabilities(false, false, false);

        var showIre = videoId is 16 or 32; // Composite or S-Video
        var showComponent = videoId == 8;  // Component
        var isAnalogAudio = audioId is 4 or 8 or 16; // Analog, XLR, RCA
        var showAudioLevel = isAnalogAudio;
        var showGain = isAnalogAudio && (caps.SupportsAnalogAudioGain || device is null || analogAudioGainOverride);
        var consumerLocked = analogAudioLevel == "Consumer";

        AnalogIreGroup.Visibility = showIre ? Visibility.Visible : Visibility.Collapsed;
        AnalogComponentGroup.Visibility = showComponent ? Visibility.Visible : Visibility.Collapsed;
        AnalogAudioLevelGroup.Visibility = showAudioLevel ? Visibility.Visible : Visibility.Collapsed;
        AnalogAudioGainGroup.Visibility = showGain ? Visibility.Visible : Visibility.Collapsed;
        AnalogInputCard.Visibility = showIre || showComponent || showAudioLevel || showGain
            ? Visibility.Visible : Visibility.Collapsed;

        AnalogIrePicker.IsEnabled = idle && (caps.SupportsAnalogVideoFlags || device is null);
        AnalogComponentPicker.IsEnabled = idle && (caps.SupportsAnalogVideoFlags || device is null);
        AnalogAudioLevelPicker.IsEnabled = idle && (caps.SupportsConsumerAudioLevels || device is null);

        var canAdjustGain = idle && !consumerLocked && (caps.SupportsAnalogAudioGain || device is null);
        AnalogGainOverrideCheckBox.IsEnabled = canAdjustGain;
        AnalogGainSlidersPanel.Visibility = analogAudioGainOverride && !consumerLocked
            ? Visibility.Visible : Visibility.Collapsed;
        AnalogGainConsumerNote.Visibility = consumerLocked ? Visibility.Visible : Visibility.Collapsed;
        AnalogGainLeftSlider.IsEnabled = canAdjustGain && analogAudioGainOverride;
        AnalogGainRightSlider.IsEnabled = canAdjustGain && analogAudioGainOverride;
        AnalogGainLinkButton.IsEnabled = canAdjustGain && analogAudioGainOverride;
        AnalogGainResetButton.IsEnabled = canAdjustGain && analogAudioGainOverride;
        AnalogGainLinkButton.Background = analogAudioGainLinked
            ? ThemeBrush("StudioSegmentActiveBrush") : new SolidColorBrush(Colors.Transparent);
    }

    private void SyncAnalogUiFromState()
    {
        syncingAnalogSliders = true;
        try
        {
            SelectComboTag(AnalogIrePicker, analogNtscIre, AnalogIreDriverItem);
            SelectComboTag(AnalogComponentPicker, analogComponentLevel, AnalogComponentDriverItem);
            SelectComboTag(AnalogAudioLevelPicker, analogAudioLevel, AnalogAudioDriverItem);
            AnalogGainOverrideCheckBox.IsChecked = analogAudioGainOverride;
            AnalogGainLeftSlider.Value = Math.Clamp(analogAudioGainLeftDb, -12, 12);
            AnalogGainRightSlider.Value = Math.Clamp(analogAudioGainRightDb, -12, 12);
            RenderAnalogGainLabels();
        }
        finally
        {
            syncingAnalogSliders = false;
        }
    }

    private void RenderAnalogGainLabels()
    {
        AnalogGainLeftText.Text = FormatDb(analogAudioGainLeftDb);
        AnalogGainRightText.Text = FormatDb(analogAudioGainRightDb);
    }

    private string FormatDb(double db) =>
        db.ToString("+0.0;-0.0;0.0", settingsCulture) + " dB";

    private void AnalogSetting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingAnalogSliders || !settingsReady || loading)
            return;

        if (AnalogIrePicker.SelectedItem is ComboBoxItem { Tag: string ire })
            analogNtscIre = ire;
        if (AnalogComponentPicker.SelectedItem is ComboBoxItem { Tag: string comp })
            analogComponentLevel = comp;
        SettingsChanged();
    }

    private void AnalogAudioLevelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingAnalogSliders || !settingsReady || loading)
            return;

        if (AnalogAudioLevelPicker.SelectedItem is ComboBoxItem { Tag: string level })
            analogAudioLevel = level;
        UpdateControls();
        SettingsChanged();
    }

    private void AnalogGainOverride_Click(object sender, RoutedEventArgs e)
    {
        if (!settingsReady || loading)
            return;

        analogAudioGainOverride = AnalogGainOverrideCheckBox.IsChecked == true;
        UpdateControls();
        SettingsChanged();
    }

    private void AnalogGainLink_Click(object sender, RoutedEventArgs e)
    {
        analogAudioGainLinked = !analogAudioGainLinked;
        if (analogAudioGainLinked && Math.Abs(analogAudioGainLeftDb - analogAudioGainRightDb) > 0.01)
        {
            syncingAnalogSliders = true;
            analogAudioGainRightDb = analogAudioGainLeftDb;
            AnalogGainRightSlider.Value = analogAudioGainRightDb;
            RenderAnalogGainLabels();
            syncingAnalogSliders = false;
        }
        UpdateControls();
        SettingsChanged();
    }

    private void AnalogGainReset_Click(object sender, RoutedEventArgs e)
    {
        syncingAnalogSliders = true;
        analogAudioGainLeftDb = 0;
        analogAudioGainRightDb = 0;
        AnalogGainLeftSlider.Value = 0;
        AnalogGainRightSlider.Value = 0;
        RenderAnalogGainLabels();
        syncingAnalogSliders = false;
        SettingsChanged();
    }

    private void AnalogGainLeft_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (syncingAnalogSliders || !settingsReady || loading)
            return;

        analogAudioGainLeftDb = Math.Round(e.NewValue * 2, MidpointRounding.AwayFromZero) / 2;
        if (analogAudioGainLinked)
        {
            syncingAnalogSliders = true;
            analogAudioGainRightDb = analogAudioGainLeftDb;
            AnalogGainRightSlider.Value = analogAudioGainRightDb;
            syncingAnalogSliders = false;
        }
        RenderAnalogGainLabels();
        SettingsChanged();
    }

    private void AnalogGainRight_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (syncingAnalogSliders || !settingsReady || loading)
            return;

        analogAudioGainRightDb = Math.Round(e.NewValue * 2, MidpointRounding.AwayFromZero) / 2;
        if (analogAudioGainLinked)
        {
            syncingAnalogSliders = true;
            analogAudioGainLeftDb = analogAudioGainRightDb;
            AnalogGainLeftSlider.Value = analogAudioGainLeftDb;
            syncingAnalogSliders = false;
        }
        RenderAnalogGainLabels();
        SettingsChanged();
    }

    // --- Tier 3: Preview-Only Display & Local Audio Monitor ---

    private void ApplyPreviewOptionsToUi()
    {
        syncingPreviewPickers = true;
        try
        {
            var deintTag = previewDeinterlace.ToString();
            SelectComboTag(PreviewDeinterlacePicker, deintTag, DeinterlaceBobWeaveItem);
            SelectComboTag(SettingsDeinterlacePicker, deintTag, SettingsDeinterlaceBobWeaveItem);

            var aspectTag = previewAspectMode.ToString();
            SelectComboTag(PreviewAspectPicker, aspectTag, AspectAutoItem);
            SelectComboTag(SettingsAspectPicker, aspectTag, SettingsAspectAutoItem);

            var matrixTag = previewColorMatrix.ToString();
            SelectComboTag(PreviewMatrixPicker, matrixTag, MatrixAutoItem);
            SelectComboTag(SettingsMatrixPicker, matrixTag, SettingsMatrixAutoItem);

            SafeAreaCheckBox.IsChecked = showSafeArea;
            Ntsc480GuideCheckBox.IsChecked = showNtsc480Guide;
            AudioMonitorVolumeSlider.Value = Math.Round(audioMonitorVolume * 100);
            AudioMonitorVolumeText.Text = $"{(int)Math.Round(audioMonitorVolume * 100)}%";
        }
        finally
        {
            syncingPreviewPickers = false;
        }

        ApplyPreviewSettingsToLiveFeed();
        UpdateAudioMonitorVisuals();
        ApplySafeAreaOverlay();
        RecomputePreviewAspectRatio();
    }

    private void ApplyPreviewSettingsToLiveFeed()
    {
        if (preview is { } feed)
        {
            feed.DeinterlaceMode = previewDeinterlace;
            feed.ColorMatrix = previewColorMatrix;
        }
        if (activeAudioMonitor is { } monitor)
        {
            monitor.Enabled = audioMonitorEnabled;
            monitor.Volume = audioMonitorVolume;
        }
    }

    private void SetPreviewDeinterlace(string tag)
    {
        previewDeinterlace = Enum.TryParse<PreviewDeinterlaceMode>(tag, out var parsed)
            ? parsed : PreviewDeinterlaceMode.BobWeave;
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

    private void PreviewDeinterlacePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncingPreviewPickers && settingsReady && !loading &&
            PreviewDeinterlacePicker.SelectedItem is ComboBoxItem { Tag: string tag })
            SetPreviewDeinterlace(tag);
    }

    private void SettingsDeinterlacePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncingPreviewPickers && settingsReady && !loading &&
            SettingsDeinterlacePicker.SelectedItem is ComboBoxItem { Tag: string tag })
            SetPreviewDeinterlace(tag);
    }

    private void PreviewAspectPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncingPreviewPickers && settingsReady && !loading &&
            PreviewAspectPicker.SelectedItem is ComboBoxItem { Tag: string tag })
            SetPreviewAspect(tag);
    }

    private void SettingsAspectPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncingPreviewPickers && settingsReady && !loading &&
            SettingsAspectPicker.SelectedItem is ComboBoxItem { Tag: string tag })
            SetPreviewAspect(tag);
    }

    private void PreviewMatrixPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncingPreviewPickers && settingsReady && !loading &&
            PreviewMatrixPicker.SelectedItem is ComboBoxItem { Tag: string tag })
            SetPreviewMatrix(tag);
    }

    private void SettingsMatrixPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncingPreviewPickers && settingsReady && !loading &&
            SettingsMatrixPicker.SelectedItem is ComboBoxItem { Tag: string tag })
            SetPreviewMatrix(tag);
    }

    private void SafeAreaCheckBox_Click(object sender, RoutedEventArgs e)
    {
        showSafeArea = SafeAreaCheckBox.IsChecked == true;
        ApplySafeAreaOverlay();
        SettingsChanged();
    }

    private void Ntsc480GuideCheckBox_Click(object sender, RoutedEventArgs e)
    {
        showNtsc480Guide = Ntsc480GuideCheckBox.IsChecked == true;
        ApplySafeAreaOverlay();
        SettingsChanged();
    }

    private void AudioMonitorMute_Click(object sender, RoutedEventArgs e)
    {
        audioMonitorEnabled = !audioMonitorEnabled;
        if (activeAudioMonitor is { } monitor)
            monitor.Enabled = audioMonitorEnabled;
        UpdateAudioMonitorVisuals();
        SettingsChanged();
    }

    private void AudioMonitorVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (syncingPreviewPickers || !settingsReady || loading)
            return;

        audioMonitorVolume = Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
        AudioMonitorVolumeText.Text = $"{(int)Math.Round(audioMonitorVolume * 100)}%";
        if (activeAudioMonitor is { } monitor)
            monitor.Volume = audioMonitorVolume;
        UpdateAudioMonitorVisuals();
        SettingsChanged();
    }

    private void UpdateAudioMonitorVisuals()
    {
        var active = audioMonitorEnabled && audioMonitorVolume > 0.005;
        AudioMonitorIcon.Glyph = !active
            ? "\uE74F" // Mute
            : audioMonitorVolume < 0.35
                ? "\uE993"
                : audioMonitorVolume < 0.70
                    ? "\uE994"
                    : "\uE767";
        AudioMonitorMuteButton.Background = active
            ? ThemeBrush("StudioAccentSoftBrush") : new SolidColorBrush(Colors.Transparent);
        AudioMonitorIcon.Foreground = active
            ? ThemeBrush("StudioAccentBrush") : ThemeBrush("StudioTextSecondaryBrush");
        var tip = L(active ? "AudioMonitorOnTooltip" : "AudioMonitorOffTooltip");
        ToolTipService.SetToolTip(AudioMonitorMuteButton, tip);
        AutomationProperties.SetName(AudioMonitorMuteButton, tip);
    }

    private void RecomputePreviewAspectRatio() =>
        UpdatePreviewAspectRatio(lastSignalWidth, lastSignalHeight);

    private double ComputeEffectivePreviewAspectRatio(int width, int height) => previewAspectMode switch
    {
        PreviewAspectMode.Ratio4x3 => 4d / 3,
        PreviewAspectMode.Ratio16x9 => 16d / 9,
        PreviewAspectMode.Square1x1 when width > 0 && height > 0 => (double)width / height,
        PreviewAspectMode.Square1x1 => 720d / 486,
        _ => width <= 0 || height <= 0 || (width == 720 && height <= 576)
            ? DefaultPreviewAspectRatio : (double)width / height
    };

    private void UpdateNtsc480GuideLayout()
    {
        var show = showNtsc480Guide && PreviewPlaceholder.Visibility == Visibility.Collapsed;
        Ntsc480GuideOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || PreviewSurface.Height <= 0)
            return;

        // SMPTE 259M 486 -> 480 standard crop: top 4 lines, bottom 2 lines.
        var h = PreviewSurface.Height;
        Ntsc480TopShade.Height = Math.Max(2, h * 4.0 / 486.0);
        Ntsc480BottomShade.Height = Math.Max(1, h * 2.0 / 486.0);
    }

    private static void SelectComboTag(ComboBox picker, string tag, ComboBoxItem fallback)
    {
        picker.SelectedIndex = -1;
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            ?? fallback;
    }

    private void ApplySignalAndMonitorLanguage()
    {
        // Tier 1 Analog Hardware Controls
        AnalogSectionHeading.Text = L("AnalogSectionHeading");
        AnalogIreLabel.Text = L("AnalogIreLabel");
        AnalogComponentLabel.Text = L("AnalogComponentLabel");
        AnalogAudioLevelLabel.Text = L("AnalogAudioLevelLabel");
        AnalogGainOverrideCheckBox.Content = L("AnalogGainOverride");
        AnalogGainConsumerNote.Text = L("AnalogGainConsumerNote");
        ToolTipService.SetToolTip(AnalogGainLinkButton, L("AnalogGainLink"));
        ToolTipService.SetToolTip(AnalogGainResetButton, L("AnalogGainReset"));

        AnalogIreDriverItem.Content = L("AnalogDriverDefault");
        AnalogIre0Item.Content = L("AnalogIre0");
        AnalogIre75Item.Content = L("AnalogIre75");
        AnalogComponentDriverItem.Content = L("AnalogDriverDefault");
        AnalogComponentSmpteItem.Content = L("AnalogComponentSmpte");
        AnalogComponentBetacamItem.Content = L("AnalogComponentBetacam");
        AnalogAudioDriverItem.Content = L("AnalogDriverDefault");
        AnalogAudioProItem.Content = L("AnalogAudioPro");
        AnalogAudioConsumerItem.Content = L("AnalogAudioConsumer");
        SyncAnalogUiFromState();

        // Tier 3 Preview-Only Controls
        MonitorOptionsLabel.Text = L("PreviewOptionsButton");
        PreviewOnlyNoticeText.Text = L("PreviewOnlyNotice");
        PreviewDeinterlaceLabel.Text = L("PreviewDeinterlaceLabel");
        PreviewAspectLabel.Text = L("PreviewAspectLabel");
        PreviewMatrixLabel.Text = L("PreviewMatrixLabel");
        SafeAreaCheckBox.Content = L("SafeAreaGuides");
        Ntsc480GuideCheckBox.Content = L("Ntsc480Guide");
        AudioMonitorTitleText.Text = L("AudioMonitorTitle");
        AudioMonitorNoticeText.Text = L("AudioMonitorNotice");
        ToolTipService.SetToolTip(AudioMonitorVolumeButton, L("AudioMonitorTitle"));

        PreviewDefaultsHeading.Text = L("PreviewDefaultsHeading");
        PreviewDefaultsSubtitle.Text = L("PreviewOnlyNotice");
        SettingsDeinterlaceCard.Header = L("PreviewDeinterlaceLabel");
        SettingsDeinterlaceCard.Description = L("PreviewDeinterlaceDesc");
        SettingsAspectCard.Header = L("PreviewAspectLabel");
        SettingsAspectCard.Description = L("PreviewAspectDesc");
        SettingsMatrixCard.Header = L("PreviewMatrixLabel");
        SettingsMatrixCard.Description = L("PreviewMatrixDesc");

        DeinterlaceBobWeaveItem.Content = SettingsDeinterlaceBobWeaveItem.Content = L("DeinterlaceBobWeave");
        DeinterlaceFieldItem.Content = SettingsDeinterlaceFieldItem.Content = L("DeinterlaceField");
        DeinterlaceOffItem.Content = SettingsDeinterlaceOffItem.Content = L("DeinterlaceOff");
        AspectAutoItem.Content = SettingsAspectAutoItem.Content = L("AspectAuto");
        Aspect4x3Item.Content = SettingsAspect4x3Item.Content = L("Aspect4x3");
        Aspect16x9Item.Content = SettingsAspect16x9Item.Content = L("Aspect16x9");
        Aspect1x1Item.Content = SettingsAspect1x1Item.Content = L("Aspect1x1");
        MatrixAutoItem.Content = SettingsMatrixAutoItem.Content = L("MatrixAuto");
        Matrix601Item.Content = SettingsMatrix601Item.Content = L("MatrixBt601");
        Matrix709Item.Content = SettingsMatrix709Item.Content = L("MatrixBt709");
        ApplyPreviewOptionsToUi();
    }
}
