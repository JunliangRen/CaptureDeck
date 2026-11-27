using CaptureRack.Audio;
using CaptureRack.DeckLink;
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
    private int audioChannels = 2;
    private AudioChannelLayout audioLayout = AudioChannelLayout.Stereo;
    private AudioMonitorRoute audioRoute = AudioMonitorRoute.Default;
    private int audioActivePairMask = 1;
    private bool routeItemsDirty;

    public bool HasOpenDropdown =>
        PreviewDeinterlacePicker.IsDropDownOpen ||
        PreviewAspectPicker.IsDropDownOpen ||
        PreviewMatrixPicker.IsDropDownOpen ||
        AudioMonitorRoutePicker.IsDropDownOpen;

    public event EventHandler<string>? DeinterlaceChanged;
    public event EventHandler<string>? AspectChanged;
    public event EventHandler<string>? MatrixChanged;
    public event EventHandler<bool>? SafeAreaChanged;
    public event EventHandler<bool>? Ntsc480GuideChanged;
    public event EventHandler? AudioMonitorToggled;
    public event EventHandler<double>? AudioMonitorVolumeChanged;
    public event EventHandler<AudioMonitorRoute>? AudioMonitorRouteChanged;

    public PreviewMonitorOptionsBar() => InitializeComponent();

    public void ApplyCapabilities(WorkspaceCapabilities caps, bool deinterlace, bool matrix, bool reserveRfControls)
    {
        MonitorOptionsButton.IsEnabled = caps.SupportsPreviewDisplayOptions;
        MonitorOptionsButton.Visibility = reserveRfControls || caps.SupportsPreviewDisplayOptions ? Visibility.Visible : Visibility.Collapsed;
        DeinterlaceOptions.Visibility = deinterlace ? Visibility.Visible : Visibility.Collapsed;
        MatrixOptions.Visibility = matrix ? Visibility.Visible : Visibility.Collapsed;
        AudioMonitorMuteButton.IsEnabled = AudioMonitorVolumeButton.IsEnabled = caps.SupportsLocalAudioMonitor;
        AudioMonitorControlGroup.Visibility = reserveRfControls || caps.SupportsLocalAudioMonitor ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetNtscGuideAvailable(bool available) =>
        Ntsc480GuideCheckBox.Visibility = available ? Visibility.Visible : Visibility.Collapsed;

    public void SyncState(
        string deinterlaceTag,
        bool deinterlaceLocked,
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
            PreviewDeinterlacePicker.IsEnabled = !deinterlaceLocked;
            if (deinterlaceLocked)
                PreviewDeinterlacePicker.IsDropDownOpen = false;
            SelectComboTag(PreviewDeinterlacePicker, deinterlaceLocked ? "Off" : deinterlaceTag, DeinterlaceBobWeaveItem);
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
            (!deinterlaceLocked && !string.Equals(deinterlaceTag, "BobWeave", StringComparison.OrdinalIgnoreCase));
        PreviewOverrideDot.Opacity = hasCustom ? 1 : 0;
        UpdateAudioMonitorVisuals();
    }

    private (int Top, int Bottom)? cropLines = (4, 2);

    internal void SetCropLines((int Top, int Bottom)? lines)
    {
        if (cropLines == lines) return;
        cropLines = lines;
        UpdateCropLabel();
    }

    private void UpdateCropLabel() =>
        Ntsc480GuideCheckBox.Content = cropLines is { } lines
            ? string.Format(localize("Ntsc480Guide"), lines.Top, lines.Bottom)
            : localize("Ntsc480GuideTitle");

    public void ApplyLanguage(
        Func<string, string> localizer,
        string deinterlaceTag,
        bool deinterlaceLocked,
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
        UpdateCropLabel();
        AudioMonitorTitleText.Text = localize("AudioMonitorTitle");
        AudioMonitorRouteLabel.Text = localize("AudioMonitorRouteLabel");
        AutomationProperties.SetName(AudioMonitorRoutePicker, localize("AudioMonitorRouteLabel"));
        AudioMonitorNoticeText.Text = localize("AudioMonitorNotice");
        ToolTipService.SetToolTip(AudioMonitorVolumeButton, localize("AudioMonitorTitle"));

        DeinterlaceBobWeaveItem.Content = localize("DeinterlaceBobWeave");
        DeinterlaceBobWeave2xItem.Content = localize("DeinterlaceBobWeave2x");
        DeinterlaceOffItem.Content = localize("DeinterlaceOff");
        AspectAutoItem.Content = localize("AspectAuto");
        Aspect4x3Item.Content = localize("Aspect4x3");
        Aspect16x9Item.Content = localize("Aspect16x9");
        Aspect1x1Item.Content = localize("Aspect1x1");
        MatrixAutoItem.Content = localize("MatrixAuto");
        Matrix601Item.Content = localize("MatrixBt601");
        Matrix709Item.Content = localize("MatrixBt709");

        SyncState(deinterlaceTag, deinterlaceLocked, aspectTag, matrixTag, safeArea, ntsc480Guide, audioMonitorEnabled, audioMonitorVolume);
        RebuildAudioRouteItems(force: true);
    }

    public void UpdateAudioMonitorRoute(int channels, AudioChannelLayout layout, AudioMonitorRoute route, int activePairMask)
    {
        var safeChannels = Math.Clamp(channels, 1, AudioMonitorRoute.MaxSupportedChannels);
        var clamped = route.ClampForChannels(safeChannels);
        var structureChanged = audioChannels != safeChannels || audioLayout != layout || audioActivePairMask != activePairMask;
        var routeChanged = audioRoute != clamped;
        audioChannels = safeChannels;
        audioLayout = layout;
        audioRoute = clamped;
        audioActivePairMask = activePairMask;
        if (structureChanged || (routeItemsDirty && !AudioMonitorRoutePicker.IsDropDownOpen))
            RebuildAudioRouteItems(force: false);
        else if (routeChanged)
            SelectAudioRouteItem(clamped);
        UpdateAudioRouteBadge();
    }

    private void RebuildAudioRouteItems(bool force)
    {
        if (audioChannels <= 2)
        {
            routeItemsDirty = false;
            AudioMonitorRouteSection.Visibility = Visibility.Collapsed;
            UpdateAudioRouteBadge();
            return;
        }

        AudioMonitorRouteSection.Visibility = Visibility.Visible;
        if (AudioMonitorRoutePicker.IsDropDownOpen && !force)
        {
            routeItemsDirty = true;
            return;
        }

        routeItemsDirty = false;
        syncing = true;
        try
        {
            var pairCount = (audioChannels + 1) / 2;
            var items = new List<ComboBoxItem>(pairCount + 1);
            for (var p = 0; p < pairCount; p++)
            {
                var active = (audioActivePairMask & (1 << p)) != 0;
                var label = $"Ch {AudioChannelLayoutHelper.PairShortLabel(p, audioChannels, audioLayout)}";
                if (active)
                    label += " ●";
                items.Add(new ComboBoxItem { Content = label, Tag = AudioMonitorRoute.Pair(p) });
            }
            items.Add(new ComboBoxItem
            {
                Content = localize("AudioMonitorDownmix"),
                Tag = AudioMonitorRoute.Downmix
            });
            AudioMonitorRoutePicker.ItemsSource = items;
            SelectAudioRouteItem(audioRoute);
        }
        finally
        {
            syncing = false;
        }
        UpdateAudioRouteBadge();
    }

    private void SelectAudioRouteItem(AudioMonitorRoute route)
    {
        if (AudioMonitorRoutePicker.ItemsSource is not IEnumerable<ComboBoxItem> items)
            return;
        var wasSyncing = syncing;
        syncing = true;
        try
        {
            AudioMonitorRoutePicker.SelectedItem = items.FirstOrDefault(item => item.Tag is AudioMonitorRoute r && r == route)
                ?? items.FirstOrDefault();
        }
        finally
        {
            syncing = wasSyncing;
        }
    }

    private void UpdateAudioRouteBadge()
    {
        var showBadge = audioChannels > 2 && audioRoute != AudioMonitorRoute.Default;
        AudioMonitorRouteBadge.Visibility = showBadge ? Visibility.Visible : Visibility.Collapsed;
        if (showBadge)
        {
            AudioMonitorRouteBadgeText.Text = audioRoute.Mode == AudioMonitorMode.Downmix
                ? localize("AudioMonitorDownmixShort")
                : AudioChannelLayoutHelper.PairCompactLabel(audioRoute.PairIndex, audioChannels);
        }
    }

    private void AudioMonitorRoutePicker_DropDownClosed(object? sender, object e)
    {
        if (routeItemsDirty)
            RebuildAudioRouteItems(force: false);
    }

    private void AudioMonitorRoutePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || AudioMonitorRoutePicker.SelectedItem is not ComboBoxItem { Tag: AudioMonitorRoute route })
            return;
        if (audioRoute == route)
            return;
        audioRoute = route;
        UpdateAudioRouteBadge();
        AudioMonitorRouteChanged?.Invoke(this, route);
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
