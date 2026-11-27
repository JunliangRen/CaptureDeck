using CaptureRack.DirectShow.Windows.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App.Views;

public sealed partial class DirectShowCapabilitiesView : UserControl, ILiveMediaSource, ILiveMediaInspector
{
    private Func<string, string> localize = key => key;
    private DirectShowDeviceCapabilities? capabilities;
    private DirectShowCaptureResult? negotiated;
    private DirectShowCaptureResult? sampled;
    private CancellationTokenSource? operation;
    private DirectShowSettings savedSelection = new();
    private bool deviceSelectionIsFallback;
    private InfoBarSeverity? activeNoticeSeverity;
    private bool populated;
    private bool updatingPickers;
    private bool cameraControlsExpanded;
    private bool detailsExpanded;

    public bool HasOpenDropdown =>
        DevicePicker.IsDropDownOpen ||
        ResolutionPicker.IsDropDownOpen ||
        FrameRatePicker.IsDropDownOpen ||
        ColorSpacePicker.IsDropDownOpen ||
        CameraControlPicker.IsDropDownOpen ||
        CameraControlChoices.IsDropDownOpen ||
        VideoStreamPicker.IsDropDownOpen ||
        VideoFormatPicker.IsDropDownOpen ||
        AudioPicker.IsDropDownOpen;

    public event EventHandler? SettingsPersistenceRequested;

    public DirectShowCapabilitiesView()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => RefreshTheme();
        Loaded += (_, _) =>
        {
            RefreshTheme();
            if (!populated)
                _ = RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            operation?.Cancel();
            StopPreview();
        };
    }

    internal void RestoreSettings(DirectShowSettings settings) =>
        savedSelection = settings ?? new();

    internal DirectShowSettings ExportSettings()
    {
        var device = DevicePicker.SelectedItem as DirectShowDevice;
        var video = VideoFormat;
        var snapshot = video is null ? null : new DirectShowFormatSnapshot(
            video.PinId,
            video.IsInfrared,
            video.Width,
            video.Height,
            FrameRate(video),
            Encoding(video),
            video.Index);
        return DirectShowSettingsResolver.Export(
            savedSelection,
            device?.Id,
            deviceSelectionIsFallback,
            capabilities?.Device.Id,
            snapshot,
            AudioFormat?.Index);
    }

    internal void Bind(Func<string, string> translate)
    {
        localize = translate;
        DeviceSectionHeading.Text = L("DsDeviceSectionHeading");
        DeviceLabel.Text = L("Device");
        AutomationProperties.SetName(DevicePicker, L("Device"));
        DevicePicker.PlaceholderText = L("DsSelectDevice");
        ResolutionLabel.Text = L("DsResolution");
        AutomationProperties.SetName(ResolutionPicker, L("DsResolution"));
        FrameRateLabel.Text = L("DsFrameRate");
        AutomationProperties.SetName(FrameRatePicker, L("DsFrameRate"));
        ColorSpaceLabel.Text = L("DsColorSpace");
        AutomationProperties.SetName(ColorSpacePicker, L("DsColorSpace"));
        DetailsHeading.Text = L("DsFormatDetails");
        VideoStreamLabel.Text = L("DsVideoStream");
        AutomationProperties.SetName(VideoStreamPicker, L("DsVideoStream"));
        VideoFormatLabel.Text = L("DsVideoFormat");
        AutomationProperties.SetName(VideoFormatPicker, L("DsVideoFormat"));
        AudioLabel.Text = L("DsAudioFormat");
        AutomationProperties.SetName(AudioPicker, L("DsAudioFormat"));
        RefreshLabel.Text = L("DsRefresh");
        AutomationProperties.SetName(RefreshButton, L("DsRefresh"));
        NegotiateButton.Content = L("DsNegotiate");
        SampleButton.Content = L("DsSample");
        AdvertisedHeading.Text = L("DsDriverReported");
        NegotiatedHeading.Text = L("DsNegotiated");
        SampleHeading.Text = L("DsSampleResult");
        CopyDiagnosticsLabel.Text = L("DsCopyDiagnostics");
        AutomationProperties.SetName(CopyDiagnosticsButton, L("DsCopyDiagnostics"));
        BindCameraControls();
        if (capabilities is not null)
            PopulateFormats();
        RenderEvidence();
    }

    internal void RefreshTheme()
    {
        UpdateCameraControlState();
        if (NoticeBanner.Visibility == Visibility.Visible && activeNoticeSeverity is { } severity)
            ApplyNoticeTheme(severity);
    }

    private void DetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        detailsExpanded = !detailsExpanded;
        DetailsBody.Visibility = detailsExpanded ? Visibility.Visible : Visibility.Collapsed;
        DetailsChevron.Glyph = detailsExpanded ? "\uE70E" : "\uE70D";
        if (detailsExpanded)
            Controls.StudioFeedback.RevealSubtle(DetailsBody, offsetY: 4f, durationMs: 120);
    }

    private string L(string key) => localize(key);

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        await RunAsync(async token =>
        {
            var wasFallback = deviceSelectionIsFallback;
            var previous = DirectShowSettingsResolver.ResolveRefreshTargetDeviceId(
                savedSelection, (DevicePicker.SelectedItem as DirectShowDevice)?.Id, wasFallback);
            var video = wasFallback ? null : VideoFormat;
            var audio = wasFallback ? null : AudioFormat;
            var devices = await DirectShowInspectionClient.RunAsync<DirectShowDevice[]>(
                new(DirectShowInspectionOperation.List), token);
            token.ThrowIfCancellationRequested();
            ResetDeviceEvidence();
            var matched = string.IsNullOrWhiteSpace(previous)
                ? null
                : devices.FirstOrDefault(device => string.Equals(device.Id, previous, StringComparison.OrdinalIgnoreCase));
            deviceSelectionIsFallback = matched is null && !string.IsNullOrWhiteSpace(previous);
            updatingPickers = true;
            DevicePicker.ItemsSource = devices;
            DevicePicker.SelectedItem = matched ?? devices.FirstOrDefault();
            updatingPickers = false;
            populated = true;
            if (DevicePicker.SelectedItem is DirectShowDevice selected)
                await InspectAsync(selected, token, video, audio);
            else if (devices.Length == 0)
                ShowNotice("DsNoDevices", InfoBarSeverity.Informational);
        });
    }

    private async void DeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPickers || DevicePicker.SelectedItem is not DirectShowDevice selected)
            return;
        deviceSelectionIsFallback = false;
        ResetDeviceEvidence();
        await RunAsync(token => InspectAsync(selected, token));
        savedSelection = ExportSettings();
        SettingsPersistenceRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task InspectAsync(DirectShowDevice device, CancellationToken token,
        DirectShowFormat? video = null, DirectShowFormat? audio = null)
    {
        DirectShowDeviceCapabilities report;
        try
        {
            report = await DirectShowInspectionClient.RunAsync<DirectShowDeviceCapabilities>(
                new(DirectShowInspectionOperation.Probe, device.Id), token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            formatDiscoveryFailure = DirectShowDiagnosticFailure.FromException("Read advertised media types", error);
            throw;
        }
        token.ThrowIfCancellationRequested();
        capabilities = report;
        PopulateFormats(video, audio);
        if (VideoStreamPicker.Items.Count == 0)
            ShowNotice("DsNoVideo", InfoBarSeverity.Warning);
    }

    private void PopulateFormats() => PopulateFormats(VideoFormat, AudioFormat);

    private void PopulateFormats(DirectShowFormat? previousVideo, DirectShowFormat? previousAudio)
    {
        updatingPickers = true;
        var formats = capabilities!.Formats;
        var matchesSaved = string.Equals(capabilities.Device.Id, savedSelection.DeviceId, StringComparison.OrdinalIgnoreCase);
        if (previousVideo is null && matchesSaved)
        {
            var videoCandidates = formats.Where(f => f.Kind == "video").ToArray();
            var snapshots = videoCandidates.Select(f => new DirectShowFormatSnapshot(
                f.PinId, f.IsInfrared, f.Width, f.Height, FrameRate(f), Encoding(f), f.Index)).ToArray();
            var matchedIndex = DirectShowSettingsResolver.FindMatchingVideoFormatIndex(savedSelection, snapshots);
            if (matchedIndex is { } idx)
                previousVideo = videoCandidates.FirstOrDefault(f => f.Index == idx);
        }
        if (previousAudio is null && matchesSaved && savedSelection.AudioFormatIndex is { } savedAudioIdx)
        {
            previousAudio = formats.FirstOrDefault(f => f.Kind == "audio" && f.Index == savedAudioIdx);
        }

        var video = formats.Where(format => format.Kind == "video")
            .GroupBy(format => new { format.PinId, format.IsInfrared })
            .Select(group => new FormatGroup<(string PinId, bool IsInfrared)>((group.Key.PinId, group.Key.IsInfrared), group.ToArray(),
                $"{L(group.Key.IsInfrared ? "DsInfrared" : "DsVideo")} · {Pin(group.First())}"))
            .ToArray();
        VideoStreamPicker.ItemsSource = video;
        VideoStreamPicker.SelectedItem = video.FirstOrDefault(stream => stream.Key == (previousVideo?.PinId, previousVideo?.IsInfrared))
            ?? video.FirstOrDefault();
        PopulateVideoFormats(previousVideo);

        var audio = new List<FormatOption> { new(L("DsWithoutAudio"), null) };
        audio.AddRange(formats.Where(format => format.Kind == "audio")
            .Select(format => new FormatOption($"{Pin(format)} · {Describe(format)}", format)));
        AudioPicker.ItemsSource = audio;
        AudioPicker.SelectedItem = audio.FirstOrDefault(item => item.Format == previousAudio) ?? audio[0];
        updatingPickers = false;
    }

    private void PopulateVideoFormats(DirectShowFormat? previous, ComboBox? changed = null)
    {
        var formats = (VideoStreamPicker.SelectedItem as FormatGroup<(string, bool)>)?.Formats ?? [];
        var preference = previous ?? formats.FirstOrDefault();
        var resolution = SelectFormatGroup(ResolutionPicker, formats, Resolution,
            key => key is (int width, int height) ? $"{width} × {height}" : L("DsUnspecified"),
            preference is null ? default : Resolution(preference), refresh: changed != ResolutionPicker &&
                changed != FrameRatePicker && changed != ColorSpacePicker);
        var frameRate = SelectFormatGroup(FrameRatePicker, resolution, FrameRate,
            fps => fps is { } value ? $"{value:0.###} fps" : L("DsUnspecified"),
            preference is null ? null : FrameRate(preference), refresh: changed != FrameRatePicker && changed != ColorSpacePicker);
        var color = SelectFormatGroup(ColorSpacePicker, frameRate, Encoding, name => name ?? L("DsUnspecified"),
            preference is null ? null : Encoding(preference), refresh: changed != ColorSpacePicker);
        var options = color.Select(format => new FormatOption($"{L(ScanOrder(format))} · #{format.Index}", format)).ToArray();
        VideoFormatPicker.ItemsSource = options;
        VideoFormatPicker.SelectedItem = options.FirstOrDefault(option => option.Format == previous)
            ?? options.FirstOrDefault(option => option.Format?.FieldOrder is not null) ?? options.FirstOrDefault();
    }

    private static DirectShowFormat[] SelectFormatGroup<TKey>(ComboBox picker, DirectShowFormat[] formats,
        Func<DirectShowFormat, TKey> key, Func<TKey, string> label, TKey preferred, bool refresh)
    {
        if (refresh)
        {
            var groups = formats.GroupBy(key).Select(group => new FormatGroup<TKey>(group.Key, group.ToArray(), label(group.Key))).ToArray();
            picker.ItemsSource = groups;
            picker.SelectedItem = groups.FirstOrDefault(group => EqualityComparer<TKey>.Default.Equals(group.Key, preferred))
                ?? groups.FirstOrDefault();
        }
        return (picker.SelectedItem as FormatGroup<TKey>)?.Formats ?? [];
    }

    private static (int? Width, int? Height) Resolution(DirectShowFormat format) => (format.Width, format.Height);
    private static double? FrameRate(DirectShowFormat format) => format.FramesPerSecond is { } fps ? Math.Round(fps, 3) : null;
    private static string Encoding(DirectShowFormat format) => DirectShowPreviewVideo.Encoding(format)
        ?? (format.IsMpeg2Video ? "MPEG-2" : format.FourCc ?? format.Subtype.ToString());

    private static string ScanOrder(DirectShowFormat format) => format.FieldOrder switch
    {
        "progressive" => "DsProgressive",
        "top-first" => "DsTopFirst",
        "bottom-first" => "DsBottomFirst",
        _ => "DsFieldUnknown"
    };

    private string Pin(DirectShowFormat format) => string.Format(L("DsPin"), format.PinName, format.PinId);

    private string Describe(DirectShowFormat format)
    {
        if (format.Kind == "audio" && format.SampleRate is { } sampleRate)
        {
            return string.Format(L("DsAudioDescription"), format.IsPcm ? "PCM" : format.Subtype.ToString(),
                sampleRate, format.Channels, format.BitsPerSample);
        }
        var encoding = Encoding(format);
        if (format.Width is null)
            return encoding;
        return $"{encoding} · {format.Width} × {format.Height} · {format.FramesPerSecond:0.###} fps · {L(ScanOrder(format))}";
    }

    private void VideoOptionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPickers)
            return;
        deviceSelectionIsFallback = false;
        updatingPickers = true;
        PopulateVideoFormats(VideoFormat, (ComboBox)sender);
        updatingPickers = false;
        ClearTests();
        savedSelection = ExportSettings();
        SettingsPersistenceRequested?.Invoke(this, EventArgs.Empty);
        RenderEvidence();
    }

    private void FormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPickers)
            return;
        deviceSelectionIsFallback = false;
        ClearTests();
        savedSelection = ExportSettings();
        SettingsPersistenceRequested?.Invoke(this, EventArgs.Empty);
        RenderEvidence();
    }

    private async void NegotiateClicked(object sender, RoutedEventArgs e) => await VerifyAsync(false);
    private async void SampleClicked(object sender, RoutedEventArgs e) => await VerifyAsync(true);

    private async Task VerifyAsync(bool sample)
    {
        if (capabilities?.Device is not { } device || VideoFormat is not { } video)
            return;
        var audio = AudioFormat;
        if (sample)
            sampled = null;
        else
            negotiated = null;
        await RunAsync(async token =>
        {
            DirectShowDiagnosticCheck check;
            try
            {
                check = await DirectShowInspectionClient.RunAsync<DirectShowDiagnosticCheck>(
                    new(DirectShowInspectionOperation.DiagnosticCheck, device.Id, video, audio, Sample: sample), token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                SaveDiagnosticCheck(sample, new(null, null, null,
                    DirectShowDiagnosticFailure.FromException("Run inspection worker", error)));
                throw;
            }
            token.ThrowIfCancellationRequested();
            SaveDiagnosticCheck(sample, check);
            negotiated = check.Result;
            if (sample)
                sampled = check.Result;
            if (check.Failure is { } failure)
                ShowNotice("DsFailed", InfoBarSeverity.Error, failure.Message);
        });
    }

    private void ResetDeviceEvidence()
    {
        updatingPickers = true;
        VideoStreamPicker.ItemsSource = null;
        ResolutionPicker.ItemsSource = null;
        FrameRatePicker.ItemsSource = null;
        ColorSpacePicker.ItemsSource = null;
        VideoFormatPicker.ItemsSource = null;
        AudioPicker.ItemsSource = null;
        updatingPickers = false;
        capabilities = null;
        formatDiscoveryFailure = null;
        ResetCameraControls();
        ClearTests();
    }

    private void ClearTests()
    {
        negotiated = null;
        sampled = null;
        negotiationCheck = sampleCheck = null;
        previewDiagnostic = null;
        HideNotice();
    }

    private void RenderEvidence()
    {
        var video = VideoFormat;
        var audio = AudioFormat;
        var visible = video is not null;
        FormatPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DetailsCard.Visibility = DevicePicker.SelectedItem is DirectShowDevice ? Visibility.Visible : Visibility.Collapsed;
        VideoStreamGroup.Visibility = VideoStreamPicker.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        VideoFormatGroup.Visibility = VideoFormatPicker.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        AudioGroup.Visibility = capabilities?.Formats.Any(format => format.Kind == "audio") == true
            ? Visibility.Visible : Visibility.Collapsed;
        ResultPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var idle = operation is null && !IsCapturing && !IsRecording;
        RefreshButton.IsEnabled = idle;
        DevicePicker.IsEnabled = idle && DevicePicker.Items.Count > 0;
        VideoStreamPicker.IsEnabled = idle && VideoStreamPicker.Items.Count > 0;
        ResolutionPicker.IsEnabled = idle && visible;
        FrameRatePicker.IsEnabled = idle && visible;
        ColorSpacePicker.IsEnabled = idle && visible;
        VideoFormatPicker.IsEnabled = idle && visible;
        AudioPicker.IsEnabled = idle && visible;
        NegotiateButton.IsEnabled = idle && visible;
        SampleButton.IsEnabled = idle && visible;
        BusyBar.Visibility = operation is null ? Visibility.Collapsed : Visibility.Visible;
        UpdatePreviewControls();
        StateChanged?.Invoke(this, EventArgs.Empty);

        if (video is null)
            return;
        AdvertisedText.Text = DescribeSelection(video, audio);
        NegotiatedText.Text = negotiated is null ? L("DsNotTested") :
            DescribeSelection(negotiated.VideoFormat, negotiated.AudioFormat);
        SampleText.Text = sampled is null ? L("DsNotTested") :
            string.Join("\n", new[]
            {
                string.Format(L("DsSamples"), L("DsVideo"), sampled.Video.Samples),
                sampled.Audio is { } stats ? string.Format(L("DsSamples"), L("DsAudio"), stats.Samples) : null,
                sampled.DriverDroppedFrames is { } drops ? string.Format(L("DsDriverDrops"), drops) : null
            }.OfType<string>());
    }

    private string DescribeSelection(DirectShowFormat video, DirectShowFormat? audio) =>
        audio is null ? Describe(video) : Describe(video) + "\n" + Describe(audio);

    private async Task RunAsync(Func<CancellationToken, Task> action, bool allowPreview = false)
    {
        if (operation is not null || (!allowPreview && IsCapturing))
            return;
        using var stop = new CancellationTokenSource();
        operation = stop;
        HideNotice();
        RenderEvidence();
        try { await action(stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (FileNotFoundException) { ShowNotice("DsProbeMissing", InfoBarSeverity.Error); }
        catch (TimeoutException) { ShowNotice("DsTimedOut", InfoBarSeverity.Error); }
        catch (Exception error)
        {
            ShowNotice("DsFailed", InfoBarSeverity.Error, error.Message);
        }
        finally
        {
            operation = null;
            RenderEvidence();
        }
    }

    private void HideNotice()
    {
        activeNoticeSeverity = null;
        NoticeBanner.Visibility = Visibility.Collapsed;
    }

    private void ShowNotice(string key, InfoBarSeverity severity, string? detail = null)
    {
        activeNoticeSeverity = severity;
        NoticeTitle.Text = L(key);
        NoticeMessage.Text = detail ?? "";
        NoticeMessage.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        ApplyNoticeTheme(severity);
        NoticeBanner.Visibility = Visibility.Visible;
    }

    private void ApplyNoticeTheme(InfoBarSeverity severity)
    {
        var (bgKey, borderKey, glyph) = severity switch
        {
            InfoBarSeverity.Error => ("StudioDangerSoftBrush", "StudioDangerBrush", "\uE783"),
            InfoBarSeverity.Warning => ("StudioCautionSoftBrush", "StudioCautionBrush", "\uE7BA"),
            InfoBarSeverity.Success => ("StudioSignalLockSoftBrush", "StudioSignalLockBrush", "\uE73E"),
            _ => ("StudioInfoSoftBrush", "StudioInfoBrush", "\uE946")
        };
        NoticeBanner.Background = StudioBrushes.Resolve(this, bgKey);
        NoticeBanner.BorderBrush = StudioBrushes.Resolve(this, borderKey);
        NoticeIcon.Foreground = StudioBrushes.Resolve(this, borderKey);
        NoticeIcon.Glyph = glyph;
    }

    public sealed record FormatGroup<TKey>(TKey Key, DirectShowFormat[] Formats, string Label);
    public sealed record FormatOption(string Label, DirectShowFormat? Format);
}
