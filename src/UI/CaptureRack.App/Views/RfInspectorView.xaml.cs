using CaptureRack.App.Controls;
using CaptureRack.SignalCapture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace CaptureRack.App.Views;

public sealed partial class RfInspectorView : UserControl
{
    private sealed record Choice(string Key, string Label, RfUsbDevice? Ddd, RtlSdrDevice? Rtl, bool Vga, bool File = false);
    private sealed record Preset(string Label, uint Hertz);
    private sealed record SwitchChoice(string Label, string? Pattern);
    private sealed record RateChoice(string Label, int Hardware, int Output);

    private uint SelectedFrequency => Hertz(FrequencyPreset, Frequency);
    private int SelectedRate => (int)Hertz(RatePreset, CustomRate);

    private static uint Hertz(ComboBox menu, NumberBox custom) =>
        menu.SelectedItem is Preset { Hertz: > 0 } preset ? preset.Hertz :
        double.IsFinite(custom.Value) && custom.Value == Math.Truncate(custom.Value) ? (uint)custom.Value : 0;

    private string? RtlSettingError => SelectedFrequency == 0 || SelectedRate is <= 225000 or > 3200000 or (> 300000 and <= 900000)
        ? "RtlInvalidTuning"
        : RtlMode.SelectedIndex == 0 && (2L * SelectedFrequency <= SelectedRate || 2L * SelectedFrequency + SelectedRate >= 8_000_000)
            ? "RtlUseIq" : null;

    private bool? halfSupported;
    private bool initialized, refreshing, recording, restoring;
    private int vgaIndex = 8;
    private double? lastFifoPercent;
    private RfSettings savedSettings = new();
    private string? activeDeviceKey;
    private bool isFallbackDevice;
    private Func<string, string> localize = key => key;

    internal RfPreviewOptionsBar PreviewOptions { get; } = new();
    public event Action? Changed;
    public event Action? SettingsPersistenceRequested;

    public bool IsPlayback => DeviceBox.SelectedItem is Choice { File: true };
    internal RfPlaybackFile? PlaybackFile { get; private set; }
    public string? DeviceLabel => (DeviceBox.SelectedItem as Choice)?.Label;
    public bool Ready => (!IsPlayback || PlaybackFile is not null) && !refreshing && DeviceBox.SelectedItem is Choice choice && (choice.Rtl is null || RtlSettingError is null);

    public bool HasOpenDropdown =>
        DeviceBox.IsDropDownOpen ||
        Format.IsDropDownOpen ||
        DddRate.IsDropDownOpen ||
        Sw401.IsDropDownOpen ||
        RtlMode.IsDropDownOpen ||
        FrequencyPreset.IsDropDownOpen ||
        RatePreset.IsDropDownOpen ||
        PreviewOptions.HasOpenDropdown;

    public RfInspectorView()
    {
        InitializeComponent();
        FileRate.ValueChanged += (_, _) =>
        {
            if (restoring || PlaybackFile is not { } file || !double.IsFinite(FileRate.Value)) return;
            PlaybackFile = file with { Rate = (int)Math.Round(FileRate.Value * 1e6) };
            Update();
        };
        PreviewOptions.Changed += () =>
        {
            Update();
            NotifyPersist();
        };
        PreviewOptions.DisplayChanged += NotifyPersist;
        DeviceBox.SelectionChanged += (_, _) =>
        {
            if (restoring) return;
            if (DeviceBox.SelectedItem is Choice chosen)
            {
                activeDeviceKey = chosen.Key;
                isFallbackDevice = false;
            }
            Update();
            NotifyPersist();
        };
        Format.SelectionChanged += (_, _) =>
        {
            if (restoring) return;
            Changed?.Invoke();
            NotifyPersist();
        };
        RtlMode.SelectionChanged += (_, _) => UpdateRtlTuning(persist: true);
        TestRamp.Checked += (_, _) => { if (!restoring) { Changed?.Invoke(); NotifyPersist(); } };
        TestRamp.Unchecked += (_, _) => { if (!restoring) { Changed?.Invoke(); NotifyPersist(); } };
        DddRate.SelectionChanged += (_, _) =>
        {
            if (initialized && !restoring)
            {
                Update();
                NotifyPersist();
            }
        };
        Sw401.SelectionChanged += (_, _) =>
        {
            if (initialized && !restoring)
            {
                Changed?.Invoke();
                NotifyPersist();
            }
        };
        FrequencyPreset.SelectionChanged += (_, _) => UpdateRtlTuning(persist: true);
        RatePreset.SelectionChanged += (_, _) => UpdateRtlTuning(persist: true);
        Frequency.ValueChanged += (_, _) => UpdateRtlTuning(persist: true);
        CustomRate.ValueChanged += (_, _) => UpdateRtlTuning(persist: true);
        Vga.ValueChanged += (_, args) =>
        {
            var value = (int)Math.Round(args.NewValue);
            Volatile.Write(ref vgaIndex, value);
            VgaValueText.Text = value.ToString();
            VgaResetButton.IsEnabled = Vga.IsEnabled && value != 8;
            if (!restoring)
                NotifyPersist();
        };
    }

    internal void RestoreSettings(RfSettings rf)
    {
        savedSettings = rf ?? new();
        restoring = true;
        try
        {
            activeDeviceKey = savedSettings.DeviceKey;
            Format.SelectedIndex = Math.Clamp(savedSettings.FormatIndex, 0, 2);
            TestRamp.IsChecked = savedSettings.TestRamp;
            RtlMode.SelectedIndex = Math.Clamp(savedSettings.RtlModeIndex, 0, 1);
            Frequency.Value = Math.Clamp(savedSettings.RtlFrequencyHz, 1u, uint.MaxValue);
            CustomRate.Value = Math.Clamp(savedSettings.RtlSampleRateHz, 1, 3_200_000);
            var vga = Math.Clamp(savedSettings.RtlVgaIndex, 0, 8);
            Volatile.Write(ref vgaIndex, vga);
            Vga.Value = vga;
            VgaValueText.Text = vga.ToString();
            PreviewOptions.RestoreSettings(savedSettings);
            ApplyRestoredSelections();
        }
        finally
        {
            restoring = false;
        }
    }

    internal RfSettings ExportSettings()
    {
        if (!initialized)
            return savedSettings;

        var currentDeviceKey = isFallbackDevice
            ? savedSettings.DeviceKey
            : (DeviceBox.SelectedItem as Choice)?.Key ?? activeDeviceKey ?? savedSettings.DeviceKey;
        var freqCustom = FrequencyPreset.SelectedItem is Preset { Hertz: 0 };
        var rateCustom = RatePreset.SelectedItem is Preset { Hertz: 0 };
        var current = savedSettings with
        {
            DeviceKey = currentDeviceKey,
            FormatIndex = Format.SelectedIndex >= 0 ? Format.SelectedIndex : savedSettings.FormatIndex,
            DddRateMsps = (DddRate.SelectedItem as RateChoice)?.Output ?? savedSettings.DddRateMsps,
            Sw401 = (Sw401.SelectedItem as SwitchChoice)?.Pattern,
            TestRamp = TestRamp.IsChecked == true,
            RtlModeIndex = RtlMode.SelectedIndex >= 0 ? RtlMode.SelectedIndex : savedSettings.RtlModeIndex,
            RtlFrequencyHz = SelectedFrequency > 0 ? SelectedFrequency : savedSettings.RtlFrequencyHz,
            RtlCustomFrequency = freqCustom,
            RtlSampleRateHz = SelectedRate > 0 ? SelectedRate : savedSettings.RtlSampleRateHz,
            RtlCustomRate = rateCustom,
            RtlVgaIndex = Volatile.Read(ref vgaIndex)
        };
        current = PreviewOptions.WriteSettings(current);
        savedSettings = current;
        return current;
    }

    internal void SetPlaybackFile(RfPlaybackFile file)
    {
        PlaybackFile = file;
        restoring = true;
        try
        {
            FileRate.Value = file.Rate / 1e6;
        }
        finally
        {
            restoring = false;
        }
        Update();
        if (file.SupportsHifi) PreviewOptions.SelectHifiFile(file.IsHifiRecording, file.CenterFrequency);
    }

    public Func<int?> VgaReader()
    {
        bool supported = DeviceBox.SelectedItem is Choice { Vga: true };
        return () => supported ? Volatile.Read(ref vgaIndex) : null;
    }

    public RfRecording? Selection => DeviceBox.SelectedItem is Choice { File: false } device
        ? new(device.Ddd, device.Rtl, device.Ddd is not null && DddRate.SelectedItem is RateChoice { Hardware: 20 },
            SelectedFrequency, RtlMode.SelectedIndex == 0, Format.SelectedIndex switch { 1 => RfFileFormat.Raw, 2 => RfFileFormat.Ldf, _ => RfFileFormat.Flac }, device.Ddd is not null && TestRamp.IsChecked == true,
            device.Ddd is null ? null : (Sw401.SelectedItem as SwitchChoice)?.Pattern,
            device.Ddd is not null && DddRate.SelectedItem is RateChoice rate ? rate.Hardware / rate.Output : 1, device.Rtl is null ? RfRecording.DefaultRtlSampleRate : SelectedRate) : null;

    public void Bind(Func<string, string> labels)
    {
        localize = labels;
        restoring = true;
        try
        {
            SourceSectionHeading.Text = labels("RfSourceHeading");
            CaptureSectionHeading.Text = labels("RfCaptureHeading");
            TelemetrySectionHeading.Text = labels("RfTelemetryHeading");
            DeviceLabelText.Text = labels("Device");
            RefreshLabel.Text = refreshing ? labels("RfScanningDevices") : labels("Refresh");
            ToolTipService.SetToolTip(CopyDiagnosticsButton, labels("RfCopyDiagnostics"));
            AutomationProperties.SetName(CopyDiagnosticsButton, labels("RfCopyDiagnostics"));
            FormatLabel.Text = labels("RfFormatLabel");
            DddRateLabel.Text = labels("RfDddRateLabel");
            Sw401Label.Text = labels("RfSwitchHeader");
            RtlModeLabel.Text = labels("RfRtlModeLabel");
            FrequencyLabel.Text = labels("RfFrequency");
            RateLabel.Text = labels("RfMetricSampleRate");
            FileRateLabel.Text = $"{labels("RfMetricSampleRate")} (MSPS)";
            VgaLabel.Text = labels("RtlVgaDefault");
            VgaResetButton.Content = labels("RfVgaReset");
            LdfFormat.Content = labels("RfLdfFormat");
            TestRamp.Content = labels("RfTestRamp");
            GainText.Text = labels("RfManualGain");
            AutomationProperties.SetName(DeviceBox, labels("Device"));
            AutomationProperties.SetName(Format, labels("RfFormatLabel"));
            AutomationProperties.SetName(DddRate, labels("RfDddRateLabel"));
            AutomationProperties.SetName(Sw401, labels("RfSwitchHeader"));
            AutomationProperties.SetName(RtlMode, labels("RfRtlModeLabel"));
            AutomationProperties.SetName(FrequencyPreset, labels("RfFrequency"));
            AutomationProperties.SetName(RatePreset, labels("RfMetricSampleRate"));

            PreviewOptions.Bind(labels);

            var currentPattern = (Sw401.SelectedItem as SwitchChoice)?.Pattern ?? savedSettings.Sw401;
            var swItems = SwitchChoices(labels("RfSwitchUnknown")).ToArray();
            Sw401.ItemsSource = swItems;
            Sw401.SelectedItem = swItems.FirstOrDefault(s => s.Pattern == currentPattern) ?? swItems[0];

            BindRates((DeviceBox.SelectedItem as Choice)?.Ddd?.VendorId == 0x1209);

            var custom = new Preset(labels("RtlCustom"), 0);
            var freqPresets = new Preset[]
            {
                new("1.500 MHz (NTSC Hi-Fi)", 1_500_000),
                new("1.600 MHz (PAL Hi-Fi)", 1_600_000),
                new("100 MHz (FM)", 100_000_000),
                new("1090 MHz (ADS-B)", 1_090_000_000),
                custom
            };
            var ratePresets = new uint[] { 250_000, 1_024_000, 1_200_000, 1_536_000, 2_000_000, 2_048_000, 2_400_000 }
                .Select(rate => new Preset($"{rate / 1e6:F3} MSPS", rate)).Append(custom).ToArray();

            var prevFreqPreset = FrequencyPreset.SelectedItem as Preset;
            var prevRatePreset = RatePreset.SelectedItem as Preset;
            FrequencyPreset.ItemsSource = freqPresets;
            RatePreset.ItemsSource = ratePresets;

            if (prevFreqPreset is not null)
                FrequencyPreset.SelectedItem = freqPresets.FirstOrDefault(p => p.Hertz == prevFreqPreset.Hertz) ?? freqPresets[0];
            else
                SelectFrequencyFromSaved(freqPresets);

            if (prevRatePreset is not null)
                RatePreset.SelectedItem = ratePresets.FirstOrDefault(p => p.Hertz == prevRatePreset.Hertz) ?? ratePresets[1];
            else
                SelectRateFromSaved(ratePresets);

            // Re-label the Playback entry if devices were already populated.
            if (DeviceBox.ItemsSource is List<Choice> existing && existing.Count > 0)
            {
                var selectedIdx = DeviceBox.SelectedIndex;
                var updated = existing.Select(c => c.File ? c with { Label = labels("RfPlayback") } : c).ToList();
                DeviceBox.ItemsSource = updated;
                DeviceBox.SelectedIndex = Math.Clamp(selectedIdx, 0, updated.Count - 1);
            }
        }
        finally
        {
            restoring = false;
        }

        UpdateRtlTuning(persist: false);
        if (!initialized)
        {
            initialized = true;
            RefreshClick(this, new RoutedEventArgs());
        }
        else
        {
            Update();
        }
    }

    public void SetTelemetry(string text, double? fifoUsedPercent = null)
    {
        lastFifoPercent = fifoUsedPercent;
        TransportReading.Text = text;
        var hasText = !string.IsNullOrWhiteSpace(text);
        var hasFifo = fifoUsedPercent.HasValue;
        TelemetryCard.Visibility = hasText || hasFifo ? Visibility.Visible : Visibility.Collapsed;
        FifoProgressBar.Visibility = FifoPercentText.Visibility = hasFifo ? Visibility.Visible : Visibility.Collapsed;
        if (fifoUsedPercent is { } pct)
        {
            var clamped = Math.Clamp(pct, 0, 100);
            FifoProgressBar.Value = clamped;
            FifoPercentText.Text = $"{clamped:F1}%";
            ApplyFifoBarTheme(clamped);
        }
        CopyDiagnosticsButton.Visibility = !string.IsNullOrWhiteSpace(Diagnostics.Text) || hasText
            ? Visibility.Visible : Visibility.Collapsed;
    }

    public void RefreshTheme()
    {
        if (lastFifoPercent is { } pct)
            ApplyFifoBarTheme(pct);
    }

    public void SetRecording(bool active)
    {
        recording = active;
        Update();
    }

    private void ApplyFifoBarTheme(double percent)
    {
        var brushKey = percent >= 85
            ? "StudioDangerBrush"
            : percent >= 60
                ? "StudioCautionBrush"
                : "StudioAccentBrush";
        var brush = StudioBrushes.Resolve(this, brushKey);
        FifoProgressBar.Foreground = brush;
        FifoPercentText.Foreground = percent >= 60 ? brush : StudioBrushes.Resolve(this, "StudioTextSecondaryBrush");
    }

    private void ApplyRestoredSelections()
    {
        if (Sw401.ItemsSource is SwitchChoice[] swItems)
            Sw401.SelectedItem = swItems.FirstOrDefault(s => s.Pattern == savedSettings.Sw401) ?? swItems[0];
        if (DddRate.ItemsSource is RateChoice[] rateItems)
            DddRate.SelectedItem = rateItems.FirstOrDefault(r => r.Output == savedSettings.DddRateMsps) ?? rateItems[0];
        if (FrequencyPreset.ItemsSource is Preset[] freqPresets)
            SelectFrequencyFromSaved(freqPresets);
        if (RatePreset.ItemsSource is Preset[] ratePresets)
            SelectRateFromSaved(ratePresets);
    }

    private void SelectFrequencyFromSaved(Preset[] presets)
    {
        if (savedSettings.RtlCustomFrequency)
        {
            FrequencyPreset.SelectedItem = presets[^1];
            Frequency.Value = Math.Clamp(savedSettings.RtlFrequencyHz, 1u, uint.MaxValue);
            return;
        }
        var match = presets.FirstOrDefault(p => p.Hertz == savedSettings.RtlFrequencyHz && p.Hertz > 0);
        if (match is not null)
        {
            FrequencyPreset.SelectedItem = match;
        }
        else if (savedSettings.RtlFrequencyHz > 0)
        {
            FrequencyPreset.SelectedItem = presets[^1];
            Frequency.Value = savedSettings.RtlFrequencyHz;
        }
        else
        {
            FrequencyPreset.SelectedIndex = 0;
        }
    }

    private void SelectRateFromSaved(Preset[] presets)
    {
        if (savedSettings.RtlCustomRate)
        {
            RatePreset.SelectedItem = presets[^1];
            CustomRate.Value = Math.Clamp(savedSettings.RtlSampleRateHz, 1, 3_200_000);
            return;
        }
        var match = presets.FirstOrDefault(p => p.Hertz == (uint)Math.Max(0, savedSettings.RtlSampleRateHz) && p.Hertz > 0);
        if (match is not null)
        {
            RatePreset.SelectedItem = match;
        }
        else if (savedSettings.RtlSampleRateHz > 0)
        {
            RatePreset.SelectedItem = presets[^1];
            CustomRate.Value = savedSettings.RtlSampleRateHz;
        }
        else
        {
            RatePreset.SelectedItem = presets.FirstOrDefault(p => p.Hertz == RfRecording.DefaultRtlSampleRate) ?? presets[0];
        }
    }

    private void BindRates(bool protocolV1)
    {
        int selected = (DddRate.SelectedItem as RateChoice)?.Output ?? savedSettings.DddRateMsps;
        if (selected is not (40 or 20 or 10 or 5))
            selected = 40;
        var rates = new[] { 40, 20, 10, 5 }.Select(output =>
        {
            int hardware = protocolV1 && output <= 20 ? 20 : 40;
            string mode = localize(hardware == output ? "RfHardwareRate" : "RfSoftwareRate");
            return new RateChoice($"{output} MSPS · {mode}", hardware, output);
        }).ToArray();
        halfSupported = protocolV1;
        DddRate.ItemsSource = rates;
        DddRate.SelectedItem = rates.Single(rate => rate.Output == selected);
    }

    private static IEnumerable<SwitchChoice> SwitchChoices(string unknown)
    {
        yield return new(unknown, null);
        double[] ohms = [1500, 1000, 680, 560];
        var choices = Enumerable.Range(1, 15).Select(mask =>
        {
            double conductance = 0;
            for (int i = 0; i < ohms.Length; i++)
                if ((mask & (8 >> i)) != 0) conductance += 1 / ohms[i];
            return (Pattern: Convert.ToString(mask, 2).PadLeft(4, '0'), Gain: 1 + 1 / (200 * conductance));
        });
        foreach (var choice in choices.OrderByDescending(c => c.Gain))
            yield return new($"{choice.Pattern} · ×{choice.Gain:F2}", choice.Pattern);
    }

    private void UpdateRtlTuning(bool persist)
    {
        if (restoring) return;
        var customFreq = FrequencyPreset.SelectedItem is Preset { Hertz: 0 };
        var customRate = RatePreset.SelectedItem is Preset { Hertz: 0 };
        if (customFreq && Frequency.Visibility == Visibility.Collapsed)
        {
            Frequency.Visibility = Visibility.Visible;
            StudioFeedback.RevealSubtle(Frequency, offsetY: 4f, durationMs: 120);
        }
        else if (!customFreq)
        {
            Frequency.Visibility = Visibility.Collapsed;
        }
        if (customRate && CustomRate.Visibility == Visibility.Collapsed)
        {
            CustomRate.Visibility = Visibility.Visible;
            StudioFeedback.RevealSubtle(CustomRate, offsetY: 4f, durationMs: 120);
        }
        else if (!customRate)
        {
            CustomRate.Visibility = Visibility.Collapsed;
        }

        var error = RtlSettingError;
        RtlSettingHint.Text = error is null ? "" : localize(error);
        RtlErrorBanner.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        Changed?.Invoke();
        if (persist)
            NotifyPersist();
    }

    private void Update()
    {
        var choice = DeviceBox.SelectedItem as Choice;
        if (choice?.Ddd is null && Format.SelectedIndex == 2) Format.SelectedIndex = 0;
        LdfFormat.Visibility = choice?.Ddd is null ? Visibility.Collapsed : Visibility.Visible;
        LdfFormat.IsEnabled = choice?.Ddd is not null;
        bool edit = !recording && !refreshing;
        bool playback = IsPlayback;
#if REALTIME_RF
        bool hifi = playback && PlaybackFile?.SupportsHifi == true;
        bool canDecode = hifi || choice?.Ddd is not null || playback && PlaybackFile?.SupportsVideo == true;
#else
        bool hifi = false;
        bool canDecode = false;
#endif
        PreviewOptions.Update(canDecode, !refreshing, hifi, playback ? PlaybackFile?.Rate == 20_000_000 : DddRate.SelectedItem is RateChoice { Hardware: 20 });

        RefreshIcon.Visibility = refreshing ? Visibility.Collapsed : Visibility.Visible;
        RefreshRing.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;
        RefreshRing.IsActive = refreshing;
        RefreshLabel.Text = localize(refreshing ? "RfScanningDevices" : "Refresh");

        DeviceBox.IsEnabled = Format.IsEnabled = RefreshButton.IsEnabled = RtlMode.IsEnabled =
            FrequencyPreset.IsEnabled = RatePreset.IsEnabled = Frequency.IsEnabled = CustomRate.IsEnabled = edit;
        FileRatePanel.Visibility = playback && PlaybackFile is not null ? Visibility.Visible : Visibility.Collapsed;
        FileRate.IsEnabled = edit;

        // Collapse capture/tuning controls cleanly during file playback instead of leaving disabled boxes.
        CaptureTuningCard.Visibility = playback || choice is null ? Visibility.Collapsed : Visibility.Visible;
        FormatPanel.Visibility = playback ? Visibility.Collapsed : Visibility.Visible;
        Format.IsEnabled = edit && !playback;
        RtlOptions.Visibility = choice?.Rtl is null ? Visibility.Collapsed : Visibility.Visible;
        DddOptions.Visibility = choice?.Ddd is null ? Visibility.Collapsed : Visibility.Visible;
        Sw401.IsEnabled = TestRamp.IsEnabled = DddRate.IsEnabled = edit;
        bool supportsHalf = choice?.Ddd?.VendorId == 0x1209;
        if (halfSupported != supportsHalf) BindRates(supportsHalf);
        Vga.IsEnabled = choice?.Vga == true;
        VgaResetButton.IsEnabled = choice?.Vga == true && Volatile.Read(ref vgaIndex) != 8;
        CopyDiagnosticsButton.Visibility = !string.IsNullOrWhiteSpace(Diagnostics.Text) || !string.IsNullOrWhiteSpace(TransportReading.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        Changed?.Invoke();
    }

    private void NotifyPersist()
    {
        if (!restoring && initialized)
            SettingsPersistenceRequested?.Invoke();
    }

    private void VgaResetClick(object sender, RoutedEventArgs args)
    {
        if (Vga.IsEnabled)
            Vga.Value = 8;
    }

    private void CopyDiagnosticsClick(object sender, RoutedEventArgs args)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(DeviceLabel))
            lines.Add($"Source: {DeviceLabel}");
        if (!string.IsNullOrWhiteSpace(Diagnostics.Text))
            lines.Add(Diagnostics.Text);
        if (!string.IsNullOrWhiteSpace(TransportReading.Text))
            lines.Add(TransportReading.Text);
        if (lines.Count == 0)
            return;

        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, lines));
        Clipboard.SetContent(package);
        StudioFeedback.FlashCopied(CopyDiagnosticsIcon, "\uE8C8", themeRoot: this);
    }

    private async void RefreshClick(object sender, RoutedEventArgs args)
    {
        if (refreshing || recording) return;
        refreshing = true;
        var preferredKey = isFallbackDevice ? savedSettings.DeviceKey : (DeviceBox.SelectedItem as Choice)?.Key ?? activeDeviceKey;
        Update();
        var (choices, messages) = await Task.Run(() =>
        {
            List<Choice> devices = [];
            List<string> errors = [];
            try
            {
                foreach (var d in WinUsbRfProbe.ListDevices())
                {
                    if (!d.WinUsbBound)
                    {
                        errors.Add($"{d.UsbId} · WinUSB required ({d.DriverService})");
                        continue;
                    }
                    string? version = d.VendorId == 0x1d50 ? "Legacy" : null;
                    try
                    {
                        var probe = WinUsbRfProbe.Probe(d.InstanceId);
                        version ??= probe.FirmwareRelease;
                        if (probe.RecommendUsb3) errors.Add($"{d.UsbId} · {probe.LinkSpeedHint} · {localize("RfUsbSpeedWarning")}");
                    }
                    catch (IOException ex) { errors.Add(ex.Message); }
                    catch (InvalidDataException ex) { errors.Add(ex.Message); }
                    devices.Add(new(RfDeviceSelectionHelper.BuildDddKey(d.InstanceId),
                        version is null ? d.Model : $"{d.Model} {version}", d, null, false));
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }
            try
            {
                foreach (var r in RtlSdrProbe.List())
                {
                    try
                    {
                        var caps = RtlSdrProbe.Probe(r.Index);
                        devices.Add(new(RfDeviceSelectionHelper.BuildRtlKey(r.Index, r.Name),
                            r.Name, null, r, caps.SupportsManualVga));
                    }
                    catch (Exception ex) { errors.Add(ex.Message); }
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }
            return (devices, errors);
        });
        if (choices.Count == 0) messages.Insert(0, localize("RfNoDevice"));
        choices.Add(new(RfDeviceSelectionHelper.PlaybackDeviceKey, localize("RfPlayback"), null, null, false, true));

        restoring = true;
        try
        {
            DeviceBox.ItemsSource = choices;
            var targetIndex = RfDeviceSelectionHelper.ResolveSelectionIndex(
                choices.Select(c => c.Key).ToArray(),
                preferredKey,
                savedSettings.DeviceKey);
            DeviceBox.SelectedIndex = targetIndex;
            var chosen = targetIndex >= 0 && targetIndex < choices.Count ? choices[targetIndex] : null;
            isFallbackDevice = chosen is not null && !string.IsNullOrWhiteSpace(savedSettings.DeviceKey) &&
                !string.Equals(chosen.Key, savedSettings.DeviceKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(preferredKey, savedSettings.DeviceKey, StringComparison.OrdinalIgnoreCase);
            if (!isFallbackDevice && chosen is not null)
                activeDeviceKey = chosen.Key;
        }
        finally
        {
            restoring = false;
        }

        Diagnostics.Text = string.Join(Environment.NewLine, messages);
        DiagnosticsBanner.Visibility = messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        refreshing = false;
        Update();
    }
}
