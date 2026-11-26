using CaptureRack.SignalCapture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App.Views;

public sealed partial class RfInspectorView : UserControl
{
    private sealed record Choice(string Label, RfUsbDevice? Ddd, RtlSdrDevice? Rtl, bool Vga);
    private sealed record Preset(string Label, uint Hertz);
    private uint SelectedFrequency => Hertz(FrequencyPreset, Frequency);
    private int SelectedRate => (int)Hertz(RatePreset, CustomRate);
    private static uint Hertz(ComboBox menu, NumberBox custom) =>
        menu.SelectedItem is Preset { Hertz: > 0 } preset ? preset.Hertz :
        double.IsFinite(custom.Value) && custom.Value == Math.Truncate(custom.Value) ? (uint)custom.Value : 0;

    private string? RtlSettingError => SelectedFrequency == 0 || SelectedRate is <= 225000 or > 3200000 or (> 300000 and <= 900000)
        ? "RtlInvalidTuning"
        : RtlMode.SelectedIndex == 0 && (2L * SelectedFrequency <= SelectedRate || 2L * SelectedFrequency + SelectedRate >= 8_000_000)
            ? "RtlUseIq" : null;

    private void UpdateRtlTuning()
    {
        Frequency.Visibility = FrequencyPreset.SelectedItem is Preset { Hertz: 0 } ? Visibility.Visible : Visibility.Collapsed;
        CustomRate.Visibility = RatePreset.SelectedItem is Preset { Hertz: 0 } ? Visibility.Visible : Visibility.Collapsed;
        var error = RtlSettingError;
        RtlSettingHint.Text = error is null ? "" : localize(error);
        RtlSettingHint.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        Changed?.Invoke();
    }

    private sealed record SwitchChoice(string Label, string? Pattern);
    private sealed record RateChoice(string Label, int Hardware, int Output);
    private bool? halfSupported;
    private void BindRates(bool protocolV1)
    {
        int selected = (DddRate.SelectedItem as RateChoice)?.Output ?? 40;
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

    private bool initialized, refreshing, recording;
    private int vgaIndex = 8;
    private Func<string,string> localize = key => key;
    internal RfPreviewType PreviewType => (RfPreviewType)Math.Max(0, PreviewTypeBox.SelectedIndex);
    public event Action? Changed;
    public bool Ready => !refreshing && DeviceBox.SelectedItem is Choice choice && (choice.Rtl is null || RtlSettingError is null);
    public Func<int?> VgaReader()
    {
        bool supported = DeviceBox.SelectedItem is Choice { Vga: true };
        return () => supported ? Volatile.Read(ref vgaIndex) : null;
    }

    public RfInspectorView()
    {
        InitializeComponent();
        PreviewTypeBox.SelectionChanged += (_, _) => Changed?.Invoke();
        DeviceBox.SelectionChanged += (_, _) => Update();
        Format.SelectionChanged += (_, _) => Changed?.Invoke();
        RtlMode.SelectionChanged += (_, _) => UpdateRtlTuning();
        TestRamp.Checked += (_, _) => Changed?.Invoke();
        TestRamp.Unchecked += (_, _) => Changed?.Invoke();
        DddRate.SelectionChanged += (_, _) => Changed?.Invoke();
        FrequencyPreset.SelectionChanged += (_, _) => UpdateRtlTuning();
        RatePreset.SelectionChanged += (_, _) => UpdateRtlTuning();
        Frequency.ValueChanged += (_, _) => UpdateRtlTuning();
        CustomRate.ValueChanged += (_, _) => UpdateRtlTuning();
        Vga.ValueChanged += (_, args) => Volatile.Write(ref vgaIndex, (int)args.NewValue);
    }

    public string? DeviceLabel => (DeviceBox.SelectedItem as Choice)?.Label;

    public RfRecording? Selection => DeviceBox.SelectedItem is Choice device
        ? new(device.Ddd, device.Rtl, device.Ddd is not null && DddRate.SelectedItem is RateChoice { Hardware: 20 },
            SelectedFrequency, RtlMode.SelectedIndex == 0, Format.SelectedIndex switch { 1 => RfFileFormat.Raw, 2 => RfFileFormat.Ldf, _ => RfFileFormat.Flac }, device.Ddd is not null && TestRamp.IsChecked == true,
            device.Ddd is null ? null : (Sw401.SelectedItem as SwitchChoice)?.Pattern,
            device.Ddd is not null && DddRate.SelectedItem is RateChoice rate ? rate.Hardware / rate.Output : 1, device.Rtl is null ? RfRecording.DefaultRtlSampleRate : SelectedRate) : null;

    public void Bind(Func<string,string> labels)
    {
        localize = labels;
        LdfFormat.Content = labels("RfLdfFormat");
        var previewType = PreviewType;
        PreviewTypeBox.Header = labels("RfPreviewType");
        PreviewTypeBox.ItemsSource = new[] { labels("RfOnly"), labels("RfVideoOnly"), "BETAMAX_HIFI", "LD" };
        PreviewTypeBox.SelectedIndex = (int)previewType;
        var pattern = Math.Max(0, Sw401.SelectedIndex);
        Sw401.Header = labels("RfSwitchHeader");
        Sw401.ItemsSource = SwitchChoices(labels("RfSwitchUnknown")).ToArray();
        BindRates((DeviceBox.SelectedItem as Choice)?.Ddd?.VendorId == 0x1209);
        Sw401.SelectedIndex = pattern;
        TestRamp.Content = labels("RfTestRamp");
        DeviceBox.Header = labels("Device");
        RefreshButton.Content = labels("Refresh");
        FrequencyPreset.Header = Frequency.Header = labels("RfFrequency");
        RatePreset.Header = labels("RfMetricSampleRate");
        CustomRate.Header = $"{labels("RfMetricSampleRate")} (Hz)";
        Vga.Header = labels("RtlVgaDefault");
        int freqIndex = Math.Max(0, FrequencyPreset.SelectedIndex);
        uint selectedRate = (RatePreset.SelectedItem as Preset)?.Hertz ?? RfRecording.DefaultRtlSampleRate;
        var custom = new Preset(labels("RtlCustom"), 0);
        FrequencyPreset.ItemsSource = new Preset[] { new("1.500 MHz (NTSC Hi-Fi)", 1500000),
            new("1.600 MHz (PAL Hi-Fi)", 1600000), new("100 MHz (FM)", 100000000),
            new("1090 MHz (ADS-B)", 1090000000), custom };
        RatePreset.ItemsSource = new uint[] { 250000, 1024000, 1200000, 1536000, 2000000, 2048000, 2400000 }
            .Select(rate => new Preset($"{rate / 1e6:F3} MSPS", rate)).Append(custom).ToArray();
        FrequencyPreset.SelectedIndex = freqIndex;
        RatePreset.SelectedItem = ((Preset[])RatePreset.ItemsSource).Single(rate => rate.Hertz == selectedRate);
        GainText.Text = labels("RfManualGain");
        if (!initialized) { initialized = true; RefreshClick(this, new RoutedEventArgs()); }
    }
    public void SetTelemetry(string text)
    {
        TransportReading.Text = text;
        TransportReading.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetRecording(bool active) { recording = active; Update(); }
    private void Update()
    {
        var choice = DeviceBox.SelectedItem as Choice;
        if (choice?.Ddd is null && Format.SelectedIndex == 2) Format.SelectedIndex = 0;
        LdfFormat.Visibility = choice?.Ddd is null ? Visibility.Collapsed : Visibility.Visible;
        LdfFormat.IsEnabled = choice?.Ddd is not null;
        bool edit = !recording && !refreshing;
        DeviceBox.IsEnabled = Format.IsEnabled = RefreshButton.IsEnabled = RtlMode.IsEnabled = FrequencyPreset.IsEnabled = RatePreset.IsEnabled = Frequency.IsEnabled = CustomRate.IsEnabled = edit;
        RtlOptions.Visibility = choice?.Rtl is null ? Visibility.Collapsed : Visibility.Visible;
        DddRate.Visibility = choice?.Ddd is null ? Visibility.Collapsed : Visibility.Visible;
        Sw401.Visibility = TestRamp.Visibility = DddRate.Visibility;
        Sw401.IsEnabled = edit;
        TestRamp.IsEnabled = edit;
        DddRate.IsEnabled = edit;
        bool supportsHalf = choice?.Ddd?.VendorId == 0x1209;
        if (halfSupported != supportsHalf) BindRates(supportsHalf);
        Vga.IsEnabled = choice?.Vga == true;
        Changed?.Invoke();
    }
    private async void RefreshClick(object sender, RoutedEventArgs args)
    {
        if (refreshing || recording) return;
        refreshing = true;
        var previous = DeviceBox.SelectedItem;
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
                    devices.Add(new(version is null ? d.Model : $"{d.Model} {version}", d, null, false));
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
                        devices.Add(new(r.Name, null, r, caps.SupportsManualVga));
                    }
                    catch (Exception ex) { errors.Add(ex.Message); }
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }
            return (devices, errors);
        });
        DeviceBox.ItemsSource = choices;
        DeviceBox.SelectedIndex = choices.Count == 0 ? -1 : Math.Max(0, choices.FindIndex(d => Equals(d, previous)));
        if (choices.Count == 0) messages.Insert(0, localize("RfNoDevice"));
        Diagnostics.Text = string.Join(Environment.NewLine, messages);
        Diagnostics.Visibility = messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        refreshing = false;
        Update();
    }
}
