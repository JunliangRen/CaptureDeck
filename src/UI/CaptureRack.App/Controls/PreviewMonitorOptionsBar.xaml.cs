using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CaptureRack.App.Controls;

public sealed partial class PreviewMonitorOptionsBar : UserControl
{
    private bool syncing;
    private Func<string, string> localize = key => key;
    private bool audioEnabled;
    private double audioVolume = 0.8;

    public bool HasOpenDropdown =>
        PreviewDeinterlacePicker.IsDropDownOpen ||
        PreviewAspectPicker.IsDropDownOpen ||
        PreviewMatrixPicker.IsDropDownOpen;

    public event EventHandler<string>? DeinterlaceChanged;
    public event EventHandler<string>? AspectChanged;
    public event EventHandler<string>? MatrixChanged;
    public event EventHandler<bool>? SafeAreaChanged;
    public event EventHandler<bool>? Ntsc480GuideChanged;
    public event EventHandler? AudioMonitorToggled;
    public event EventHandler<double>? AudioMonitorVolumeChanged;

    public PreviewMonitorOptionsBar() => InitializeComponent();

    public void ApplyCapabilities(WorkspaceCapabilities caps)
    {
        MonitorOptionsButton.Visibility = caps.SupportsPreviewDisplayOptions ? Visibility.Visible : Visibility.Collapsed;
        AudioMonitorControlGroup.Visibility = caps.SupportsLocalAudioMonitor ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SyncState(
        string deinterlaceTag,
        string aspectTag,
        string matrixTag,
        bool safeArea,
        bool ntsc480Guide,
        bool audioMonitorEnabled,
        double audioMonitorVolume)
    {
        syncing = true;
        try
        {
            SelectComboTag(PreviewDeinterlacePicker, deinterlaceTag, DeinterlaceBobWeaveItem);
            SelectComboTag(PreviewAspectPicker, aspectTag, AspectAutoItem);
            SelectComboTag(PreviewMatrixPicker, matrixTag, MatrixAutoItem);
            SafeAreaCheckBox.IsChecked = safeArea;
            Ntsc480GuideCheckBox.IsChecked = ntsc480Guide;
            audioEnabled = audioMonitorEnabled;
            audioVolume = Math.Clamp(audioMonitorVolume, 0.0, 1.0);
            AudioMonitorVolumeSlider.Value = Math.Round(audioVolume * 100);
            AudioMonitorVolumeText.Text = $"{(int)Math.Round(audioVolume * 100)}%";
        }
        finally
        {
            syncing = false;
        }

        var hasCustom = safeArea || ntsc480Guide ||
            !string.Equals(aspectTag, "Auto", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(matrixTag, "Auto", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(deinterlaceTag, "BobWeave", StringComparison.OrdinalIgnoreCase);
        PreviewOverrideDot.Visibility = hasCustom ? Visibility.Visible : Visibility.Collapsed;
        UpdateAudioMonitorVisuals();
    }

    public void ApplyLanguage(
        Func<string, string> localizer,
        string deinterlaceTag,
        string aspectTag,
        string matrixTag,
        bool safeArea,
        bool ntsc480Guide,
        bool audioMonitorEnabled,
        double audioMonitorVolume)
    {
        localize = localizer;
        MonitorOptionsLabel.Text = localize("PreviewOptionsButton");
        PreviewOnlyNoticeText.Text = localize("PreviewOnlyNotice");
        PreviewDeinterlaceLabel.Text = localize("PreviewDeinterlaceLabel");
        PreviewAspectLabel.Text = localize("PreviewAspectLabel");
        PreviewMatrixLabel.Text = localize("PreviewMatrixLabel");
        SafeAreaCheckBox.Content = localize("SafeAreaGuides");
        Ntsc480GuideCheckBox.Content = localize("Ntsc480Guide");
        AudioMonitorTitleText.Text = localize("AudioMonitorTitle");
        AudioMonitorNoticeText.Text = localize("AudioMonitorNotice");
        ToolTipService.SetToolTip(AudioMonitorVolumeButton, localize("AudioMonitorTitle"));

        DeinterlaceBobWeaveItem.Content = localize("DeinterlaceBobWeave");
        DeinterlaceFieldItem.Content = localize("DeinterlaceField");
        DeinterlaceOffItem.Content = localize("DeinterlaceOff");
        AspectAutoItem.Content = localize("AspectAuto");
        Aspect4x3Item.Content = localize("Aspect4x3");
        Aspect16x9Item.Content = localize("Aspect16x9");
        Aspect1x1Item.Content = localize("Aspect1x1");
        MatrixAutoItem.Content = localize("MatrixAuto");
        Matrix601Item.Content = localize("MatrixBt601");
        Matrix709Item.Content = localize("MatrixBt709");

        SyncState(deinterlaceTag, aspectTag, matrixTag, safeArea, ntsc480Guide, audioMonitorEnabled, audioMonitorVolume);
    }

    public void RefreshTheme() => UpdateAudioMonitorVisuals();

    private void UpdateAudioMonitorVisuals()
    {
        var active = audioEnabled && audioVolume > 0.005;
        AudioMonitorIcon.Glyph = !active
            ? "\uE74F"
            : audioVolume < 0.35
                ? "\uE993"
                : audioVolume < 0.70
                    ? "\uE994"
                    : "\uE767";
        AudioMonitorMuteButton.Background = active
            ? StudioBrushes.Resolve(this, "StudioAccentSoftBrush")
            : new SolidColorBrush(Colors.Transparent);
        AudioMonitorIcon.Foreground = active
            ? StudioBrushes.Resolve(this, "StudioAccentBrush")
            : StudioBrushes.Resolve(this, "StudioTextSecondaryBrush");
        var tip = localize(active ? "AudioMonitorOnTooltip" : "AudioMonitorOffTooltip");
        ToolTipService.SetToolTip(AudioMonitorMuteButton, tip);
        AutomationProperties.SetName(AudioMonitorMuteButton, tip);
    }

    private static void SelectComboTag(ComboBox picker, string tag, ComboBoxItem fallback)
    {
        picker.SelectedIndex = -1;
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            ?? fallback;
    }

    private void PreviewDeinterlacePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing && PreviewDeinterlacePicker.SelectedItem is ComboBoxItem { Tag: string tag })
            DeinterlaceChanged?.Invoke(this, tag);
    }

    private void PreviewAspectPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing && PreviewAspectPicker.SelectedItem is ComboBoxItem { Tag: string tag })
            AspectChanged?.Invoke(this, tag);
    }

    private void PreviewMatrixPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing && PreviewMatrixPicker.SelectedItem is ComboBoxItem { Tag: string tag })
            MatrixChanged?.Invoke(this, tag);
    }

    private void SafeAreaCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!syncing)
            SafeAreaChanged?.Invoke(this, SafeAreaCheckBox.IsChecked == true);
    }

    private void Ntsc480GuideCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!syncing)
            Ntsc480GuideChanged?.Invoke(this, Ntsc480GuideCheckBox.IsChecked == true);
    }

    private void AudioMonitorMute_Click(object sender, RoutedEventArgs e) =>
        AudioMonitorToggled?.Invoke(this, EventArgs.Empty);

    private void AudioMonitorVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (syncing)
            return;

        audioVolume = Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
        AudioMonitorVolumeText.Text = $"{(int)Math.Round(audioVolume * 100)}%";
        UpdateAudioMonitorVisuals();
        AudioMonitorVolumeChanged?.Invoke(this, audioVolume);
    }
}
