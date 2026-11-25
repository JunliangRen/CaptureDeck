using System.Globalization;
using CaptureRack.DeckLink;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CaptureRack.App.Views;

public sealed record DeckLinkModeOption(string Label, uint? Id);

public sealed partial class DeckLinkInspectorView : UserControl
{
    private Func<string, string> localize = key => key;
    private CultureInfo culture = CultureInfo.InvariantCulture;
    private bool syncing;
    private bool isIdle = true;

    public CapturePixelFormat SelectedVideoFormat { get; private set; } = CapturePixelFormat.V210;
    public CaptureAudioFormat SelectedAudioFormat { get; private set; } = CaptureAudioFormat.Pcm16;

    public string AnalogNtscIre { get; private set; } = "Driver";
    public string AnalogComponentLevel { get; private set; } = "Driver";
    public string AnalogAudioLevel { get; private set; } = "Driver";
    public bool AnalogAudioGainOverride { get; private set; }
    public double AnalogAudioGainLeftDb { get; private set; }
    public double AnalogAudioGainRightDb { get; private set; }
    public bool AnalogAudioGainLinked { get; private set; } = true;

    public CaptureDevice? SelectedDevice => DevicePicker.SelectedItem as CaptureDevice;
    public VideoInputSource? SelectedVideoSource => VideoInputPicker.SelectedItem as VideoInputSource;
    public AudioInputSource? SelectedAudioSource => AudioInputPicker.SelectedItem as AudioInputSource;
    public DeckLinkModeOption? SelectedMode => ModePicker.SelectedItem as DeckLinkModeOption;

    public bool HasOpenDropdown =>
        DevicePicker.IsDropDownOpen || VideoInputPicker.IsDropDownOpen ||
        AudioInputPicker.IsDropDownOpen || ModePicker.IsDropDownOpen ||
        AnalogIrePicker.IsDropDownOpen || AnalogComponentPicker.IsDropDownOpen ||
        AnalogAudioLevelPicker.IsDropDownOpen;

    public bool HasCompleteSelection =>
        SelectedDevice is not null && SelectedVideoSource is not null &&
        SelectedAudioSource is not null && SelectedMode is not null;

    public event EventHandler? RefreshDevicesRequested;
    public event EventHandler? SelectionChanged;
    public event EventHandler? VideoFormatChanged;
    public event EventHandler? AudioFormatChanged;
    public event EventHandler? AnalogSettingsChanged;

    public DeckLinkInspectorView() => InitializeComponent();

    public AnalogInputOptions BuildAnalogInputOptions() =>
        AnalogInputOptionsBuilder.Build(
            AnalogNtscIre,
            AnalogComponentLevel,
            AnalogAudioLevel,
            AnalogAudioGainOverride,
            AnalogAudioGainLeftDb,
            AnalogAudioGainRightDb);

    public void RestoreAnalogSettings(
        string ntscIre, string componentLevel, string audioLevel,
        bool gainOverride, double leftDb, double rightDb, bool linked)
    {
        AnalogNtscIre = ntscIre;
        AnalogComponentLevel = componentLevel;
        AnalogAudioLevel = audioLevel;
        AnalogAudioGainOverride = gainOverride;
        AnalogAudioGainLeftDb = Math.Clamp(leftDb, -12, 12);
        AnalogAudioGainRightDb = Math.Clamp(rightDb, -12, 12);
        AnalogAudioGainLinked = linked;
        SyncAnalogUiFromState();
    }

    public void SetFormats(uint? videoFormat, uint? audioFormat)
    {
        SelectedVideoFormat = videoFormat == (uint)CapturePixelFormat.Uyvy ? CapturePixelFormat.Uyvy : CapturePixelFormat.V210;
        SelectedAudioFormat = audioFormat == (uint)CaptureAudioFormat.Pcm32 ? CaptureAudioFormat.Pcm32 : CaptureAudioFormat.Pcm16;
        UpdateFormatSegmentVisuals();
    }

    public void SetDevices(CaptureDevice[]? devices, long? savedDeviceId, string? savedDeviceName,
        uint? savedVideoSource, uint? savedAudioSource, bool savedModeSelected, uint? savedModeId)
    {
        syncing = true;
        try
        {
            DevicePicker.ItemsSource = devices;
            if (devices is null || devices.Length == 0)
            {
                DevicePicker.SelectedItem = null;
                PopulateDeviceInputs(null);
                PopulateModeOptions(null);
                return;
            }

            var matches = devices.Where(device => savedDeviceId is { } id
                ? device.PersistentId == id : savedDeviceName == device.Name).ToArray();
            DevicePicker.SelectedItem = savedDeviceId is null && savedDeviceName is null
                ? devices.FirstOrDefault()
                : matches.Length == 1 ? matches[0] : null;

            PopulateDeviceInputs(SelectedDevice);
            if (SelectedDevice is null)
            {
                PopulateModeOptions(null);
                return;
            }

            VideoInputPicker.SelectedItem = VideoInputPicker.Items.OfType<VideoInputSource>()
                .FirstOrDefault(source => source.Id == savedVideoSource);
            AudioInputPicker.SelectedItem = AudioInputPicker.Items.OfType<AudioInputSource>()
                .FirstOrDefault(source => source.Id == savedAudioSource);
            PopulateModeOptions(savedModeSelected ? new DeckLinkModeOption("", savedModeId) : null, selectFallback: !savedModeSelected);
        }
        finally
        {
            syncing = false;
        }
        UpdateAnalogControlsState(isIdle);
    }

    public void ApplyLanguage(Func<string, string> localizer, CultureInfo currentCulture, bool preserveActiveRouting)
    {
        localize = localizer;
        culture = currentCulture;

        var videoSourceId = SelectedVideoSource?.Id;
        var audioSourceId = SelectedAudioSource?.Id;
        var mode = SelectedMode;

        syncing = true;
        try
        {
            RoutingSectionHeading.Text = localize("RoutingHeading");
            FormatSectionHeading.Text = localize("RecordingFormats");
            DeviceLabel.Text = localize("Device");
            AutomationProperties.SetName(DevicePicker, localize("Device"));
            RefreshLabel.Text = localize("Refresh");
            VideoSourceLabel.Text = localize("VideoSource");
            AutomationProperties.SetName(VideoInputPicker, localize("VideoSource"));
            AudioSourceLabel.Text = localize("AudioSource");
            AutomationProperties.SetName(AudioInputPicker, localize("AudioSource"));
            ModeLabel.Text = localize("Mode");
            AutomationProperties.SetName(ModePicker, localize("Mode"));
            VideoFormatLabel.Text = localize("VideoFormat");
            AudioFormatLabel.Text = localize("AudioFormat");
            RecordingModeHint.Text = localize("AutoRecordingHint");

            VideoFormat10Button.Content = localize("Video10Short");
            VideoFormat8Button.Content = localize("Video8Short");
            AudioFormat16Button.Content = localize("Audio16Short");
            AudioFormat32Button.Content = localize("Audio32Short");
            ToolTipService.SetToolTip(VideoFormat10Button, localize("Video10"));
            ToolTipService.SetToolTip(VideoFormat8Button, localize("Video8"));
            ToolTipService.SetToolTip(AudioFormat16Button, localize("Audio16"));
            ToolTipService.SetToolTip(AudioFormat32Button, localize("Audio32"));

            AnalogSectionHeading.Text = localize("AnalogSectionHeading");
            AnalogIreLabel.Text = localize("AnalogIreLabel");
            AnalogComponentLabel.Text = localize("AnalogComponentLabel");
            AnalogAudioLevelLabel.Text = localize("AnalogAudioLevelLabel");
            AnalogGainOverrideCheckBox.Content = localize("AnalogGainOverride");
            AnalogGainConsumerNote.Text = localize("AnalogGainConsumerNote");
            ToolTipService.SetToolTip(AnalogGainLinkButton, localize("AnalogGainLink"));
            ToolTipService.SetToolTip(AnalogGainResetButton, localize("AnalogGainReset"));

            AnalogIreDriverItem.Content = localize("AnalogDriverDefault");
            AnalogIre0Item.Content = localize("AnalogIre0");
            AnalogIre75Item.Content = localize("AnalogIre75");
            AnalogComponentDriverItem.Content = localize("AnalogDriverDefault");
            AnalogComponentSmpteItem.Content = localize("AnalogComponentSmpte");
            AnalogComponentBetacamItem.Content = localize("AnalogComponentBetacam");
            AnalogAudioDriverItem.Content = localize("AnalogDriverDefault");
            AnalogAudioProItem.Content = localize("AnalogAudioPro");
            AnalogAudioConsumerItem.Content = localize("AnalogAudioConsumer");
            SyncAnalogUiFromState();

            if (preserveActiveRouting && SelectedDevice is not null)
            {
                PopulateDeviceInputs(SelectedDevice);
                VideoInputPicker.SelectedItem = VideoInputPicker.Items.OfType<VideoInputSource>()
                    .FirstOrDefault(source => source.Id == videoSourceId);
                AudioInputPicker.SelectedItem = AudioInputPicker.Items.OfType<AudioInputSource>()
                    .FirstOrDefault(source => source.Id == audioSourceId);
                PopulateModeOptions(mode, selectFallback: false);
            }
            else if (mode is not null)
            {
                PopulateModeOptions(mode, selectFallback: false);
            }
        }
        finally
        {
            syncing = false;
        }

        UpdateFormatSegmentVisuals();
        UpdateAnalogControlsState(isIdle);
    }

    public void UpdateEnabledState(bool idle, bool canReopenPreview, CaptureAudioFormat? previewAudioFormat)
    {
        isIdle = idle;
        RefreshButton.IsEnabled = idle;
        DevicePicker.IsEnabled = idle && DevicePicker.Items.Count != 0;
        VideoInputPicker.IsEnabled = idle && VideoInputPicker.Items.Count != 0;
        AudioInputPicker.IsEnabled = idle && AudioInputPicker.Items.Count != 0;
        ModePicker.IsEnabled = idle && ModePicker.Items.Count != 0;
        VideoFormat10Button.IsEnabled = idle || (canReopenPreview && SupportsSelectedMode(CapturePixelFormat.V210));
        VideoFormat8Button.IsEnabled = idle || (canReopenPreview && SupportsSelectedMode(CapturePixelFormat.Uyvy));
        AudioFormat16Button.IsEnabled = idle || previewAudioFormat is CaptureAudioFormat.Pcm16 or CaptureAudioFormat.Pcm32;
        AudioFormat32Button.IsEnabled = idle || previewAudioFormat == CaptureAudioFormat.Pcm32;
        RecordingModeHint.Visibility = SelectedMode is { Id: null } ? Visibility.Visible : Visibility.Collapsed;
        UpdateAnalogControlsState(idle);
    }

    private bool SupportsSelectedMode(CapturePixelFormat format)
    {
        if (SelectedVideoSource is not { } source || SelectedMode is not { } mode)
            return false;

        var modes = source.ModesFor(format);
        return mode.Id is { } id ? modes.Any(candidate => candidate.Id == id) : modes.Count > 0;
    }

    public void UpdateBitrateBadge(VideoMode? mode)
    {
        if (mode is not null)
        {
            var mbps = DeckLinkCapture.EstimateRecordingBytesPerSecond(mode, SelectedVideoFormat, SelectedAudioFormat) * 8 / 1_000_000d;
            BitrateBadgeText.Text = $"~{mbps:0.#} Mbps";
            BitrateBadge.Visibility = Visibility.Visible;
        }
        else
        {
            BitrateBadge.Visibility = Visibility.Collapsed;
        }
    }

    public void RefreshTheme()
    {
        UpdateFormatSegmentVisuals();
        UpdateAnalogControlsState(isIdle);
    }

    private void PopulateDeviceInputs(CaptureDevice? device)
    {
        VideoInputPicker.ItemsSource = device?.VideoSources
            .Select(source => source with { Name = LocalVideoConnector(source) }).ToArray();
        AudioInputPicker.ItemsSource = device?.AudioSources
            .Select(source => source with { Name = LocalAudioConnector(source) }).ToArray();
        VideoInputPicker.SelectedIndex = -1;
        AudioInputPicker.SelectedIndex = -1;
    }

    private void PopulateModeOptions(DeckLinkModeOption? preferred, bool selectFallback = true)
    {
        List<DeckLinkModeOption> options = [];
        if (SelectedVideoSource is { } video && video.ModesFor(SelectedVideoFormat) is { Count: > 0 } modes)
        {
            if (SelectedDevice is { SupportsFormatDetection: true })
                options.Add(new(localize("Auto"), null));
            options.AddRange(modes.Select(mode => new DeckLinkModeOption(mode.Description, mode.Id)));
        }

        ModePicker.ItemsSource = options;
        ModePicker.SelectedItem = preferred is not null
            ? options.FirstOrDefault(option => option.Id == preferred.Id) ?? (selectFallback ? options.FirstOrDefault() : null)
            : selectFallback ? options.FirstOrDefault() : null;
    }

    private void UpdateFormatSegmentVisuals()
    {
        ApplySegmentState(VideoFormat10Button, SelectedVideoFormat == CapturePixelFormat.V210);
        ApplySegmentState(VideoFormat8Button, SelectedVideoFormat == CapturePixelFormat.Uyvy);
        ApplySegmentState(AudioFormat16Button, SelectedAudioFormat == CaptureAudioFormat.Pcm16);
        ApplySegmentState(AudioFormat32Button, SelectedAudioFormat == CaptureAudioFormat.Pcm32);
    }

    private void ApplySegmentState(Button button, bool active)
    {
        button.Background = active ? StudioBrushes.Resolve(this, "StudioSegmentActiveBrush") : new SolidColorBrush(Colors.Transparent);
        button.BorderBrush = active ? StudioBrushes.Resolve(this, "StudioHairlineBrush") : new SolidColorBrush(Colors.Transparent);
        button.Foreground = active ? StudioBrushes.Resolve(this, "StudioTextPrimaryBrush") : StudioBrushes.Resolve(this, "StudioTextSecondaryBrush");
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void UpdateAnalogControlsState(bool idle)
    {
        var device = SelectedDevice;
        var videoId = SelectedVideoSource?.Id ?? 0;
        var audioId = SelectedAudioSource?.Id ?? 0;
        var caps = device?.AnalogCapabilities ?? new AnalogInputCapabilities(false, false, false);

        var showIre = videoId is 16 or 32;
        var showComponent = videoId == 8;
        var isAnalogAudio = audioId is 4 or 8 or 16;
        var showAudioLevel = isAnalogAudio;
        var showGain = isAnalogAudio && (caps.SupportsAnalogAudioGain || device is null || AnalogAudioGainOverride);
        var consumerLocked = AnalogAudioLevel == "Consumer";

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
        AnalogGainSlidersPanel.Visibility = AnalogAudioGainOverride && !consumerLocked
            ? Visibility.Visible : Visibility.Collapsed;
        AnalogGainConsumerNote.Visibility = consumerLocked ? Visibility.Visible : Visibility.Collapsed;
        AnalogGainLeftSlider.IsEnabled = canAdjustGain && AnalogAudioGainOverride;
        AnalogGainRightSlider.IsEnabled = canAdjustGain && AnalogAudioGainOverride;
        AnalogGainLinkButton.IsEnabled = canAdjustGain && AnalogAudioGainOverride;
        AnalogGainResetButton.IsEnabled = canAdjustGain && AnalogAudioGainOverride;
        AnalogGainLinkButton.Background = AnalogAudioGainLinked
            ? StudioBrushes.Resolve(this, "StudioSegmentActiveBrush") : new SolidColorBrush(Colors.Transparent);
    }

    private void SyncAnalogUiFromState()
    {
        var wasSyncing = syncing;
        syncing = true;
        try
        {
            SelectComboTag(AnalogIrePicker, AnalogNtscIre, AnalogIreDriverItem);
            SelectComboTag(AnalogComponentPicker, AnalogComponentLevel, AnalogComponentDriverItem);
            SelectComboTag(AnalogAudioLevelPicker, AnalogAudioLevel, AnalogAudioDriverItem);
            AnalogGainOverrideCheckBox.IsChecked = AnalogAudioGainOverride;
            AnalogGainLeftSlider.Value = Math.Clamp(AnalogAudioGainLeftDb, -12, 12);
            AnalogGainRightSlider.Value = Math.Clamp(AnalogAudioGainRightDb, -12, 12);
            RenderAnalogGainLabels();
        }
        finally
        {
            syncing = wasSyncing;
        }
    }

    private void RenderAnalogGainLabels()
    {
        AnalogGainLeftText.Text = FormatDb(AnalogAudioGainLeftDb);
        AnalogGainRightText.Text = FormatDb(AnalogAudioGainRightDb);
    }

    private string FormatDb(double db) =>
        db.ToString("+0.0;-0.0;0.0", culture) + " dB";

    private string LocalVideoConnector(VideoInputSource source) => source.Id switch
    {
        4 => localize("ConnectorOpticalSdi"),
        8 => localize("ConnectorComponent"),
        16 => localize("ConnectorComposite"),
        64 => localize("ConnectorEthernet"),
        128 => localize("ConnectorOpticalEthernet"),
        256 => localize("ConnectorInternal"),
        _ => source.Name
    };

    private string LocalAudioConnector(AudioInputSource source) => source.Id switch
    {
        1 => localize("ConnectorEmbedded"),
        4 => localize("ConnectorAnalog"),
        8 => localize("ConnectorXlr"),
        16 => localize("ConnectorRca"),
        32 => localize("ConnectorMicrophone"),
        64 => localize("ConnectorHeadphones"),
        _ => source.Name
    };

    private static void SelectComboTag(ComboBox picker, string tag, ComboBoxItem fallback)
    {
        picker.SelectedIndex = -1;
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            ?? fallback;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshDevicesRequested?.Invoke(this, EventArgs.Empty);

    private void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing)
            return;

        syncing = true;
        try
        {
            PopulateDeviceInputs(SelectedDevice);
            PopulateModeOptions(null);
        }
        finally
        {
            syncing = false;
        }
        UpdateAnalogControlsState(isIdle);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void VideoInputPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing)
            return;

        var currentMode = SelectedMode;
        syncing = true;
        try
        {
            PopulateModeOptions(currentMode);
        }
        finally
        {
            syncing = false;
        }
        UpdateAnalogControlsState(isIdle);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InputPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing)
            return;

        UpdateAnalogControlsState(isIdle);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void VideoFormatSegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { IsEnabled: true, Tag: string tag })
            return;

        var format = tag == "Uyvy" ? CapturePixelFormat.Uyvy : CapturePixelFormat.V210;
        if (SelectedVideoFormat == format)
            return;

        SelectedVideoFormat = format;
        var currentMode = SelectedMode;
        syncing = true;
        try
        {
            PopulateModeOptions(currentMode, selectFallback: isIdle);
        }
        finally
        {
            syncing = false;
        }
        UpdateFormatSegmentVisuals();
        VideoFormatChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AudioFormatSegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { IsEnabled: true, Tag: string tag })
            return;

        var format = tag == "Pcm32" ? CaptureAudioFormat.Pcm32 : CaptureAudioFormat.Pcm16;
        if (SelectedAudioFormat == format)
            return;

        SelectedAudioFormat = format;
        UpdateFormatSegmentVisuals();
        AudioFormatChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AnalogSetting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing)
            return;

        if (AnalogIrePicker.SelectedItem is ComboBoxItem { Tag: string ire })
            AnalogNtscIre = ire;
        if (AnalogComponentPicker.SelectedItem is ComboBoxItem { Tag: string comp })
            AnalogComponentLevel = comp;
        AnalogSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AnalogAudioLevelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing)
            return;

        if (AnalogAudioLevelPicker.SelectedItem is ComboBoxItem { Tag: string level })
            AnalogAudioLevel = level;
        UpdateAnalogControlsState(isIdle);
        AnalogSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AnalogGainOverride_Click(object sender, RoutedEventArgs e)
    {
        if (syncing)
            return;

        AnalogAudioGainOverride = AnalogGainOverrideCheckBox.IsChecked == true;
        UpdateAnalogControlsState(isIdle);
        AnalogSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AnalogGainLink_Click(object sender, RoutedEventArgs e)
    {
        AnalogAudioGainLinked = !AnalogAudioGainLinked;
        if (AnalogAudioGainLinked && Math.Abs(AnalogAudioGainLeftDb - AnalogAudioGainRightDb) > 0.01)
        {
            syncing = true;
            AnalogAudioGainRightDb = AnalogAudioGainLeftDb;
            AnalogGainRightSlider.Value = AnalogAudioGainRightDb;
            RenderAnalogGainLabels();
            syncing = false;
        }
        UpdateAnalogControlsState(isIdle);
        AnalogSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AnalogGainReset_Click(object sender, RoutedEventArgs e)
    {
        syncing = true;
        AnalogAudioGainLeftDb = 0;
        AnalogAudioGainRightDb = 0;
        AnalogGainLeftSlider.Value = 0;
        AnalogGainRightSlider.Value = 0;
        RenderAnalogGainLabels();
        syncing = false;
        AnalogSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AnalogGainLeft_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (syncing)
            return;

        AnalogAudioGainLeftDb = Math.Round(e.NewValue * 2, MidpointRounding.AwayFromZero) / 2;
        if (AnalogAudioGainLinked)
        {
            syncing = true;
            AnalogAudioGainRightDb = AnalogAudioGainLeftDb;
            AnalogGainRightSlider.Value = AnalogAudioGainRightDb;
            syncing = false;
        }
        RenderAnalogGainLabels();
        AnalogSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AnalogGainRight_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (syncing)
            return;

        AnalogAudioGainRightDb = Math.Round(e.NewValue * 2, MidpointRounding.AwayFromZero) / 2;
        if (AnalogAudioGainLinked)
        {
            syncing = true;
            AnalogAudioGainLeftDb = AnalogAudioGainRightDb;
            AnalogGainLeftSlider.Value = AnalogAudioGainLeftDb;
            syncing = false;
        }
        RenderAnalogGainLabels();
        AnalogSettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
