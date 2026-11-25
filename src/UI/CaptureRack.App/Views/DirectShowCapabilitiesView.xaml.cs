using CaptureRack.DirectShow.Windows.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App.Views;

public sealed partial class DirectShowCapabilitiesView : UserControl
{
    private Func<string, string> localize = key => key;
    private DirectShowDeviceCapabilities? capabilities;
    private DirectShowCaptureResult? negotiated;
    private DirectShowCaptureResult? sampled;
    private CancellationTokenSource? operation;
    private bool populated;
    private bool updatingPickers;

    public DirectShowCapabilitiesView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!populated)
                _ = RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            operation?.Cancel();
            StopPreview();
        };
    }

    internal void Bind(Func<string, string> translate)
    {
        localize = translate;
        PageTitle.Text = L("DsCapabilities");
        PageDescription.Text = L("DsCapabilitiesHelp");
        DevicePicker.Header = L("Device");
        DevicePicker.PlaceholderText = L("DsSelectDevice");
        VideoStreamPicker.Header = L("DsVideoStream");
        VideoFormatPicker.Header = L("DsVideoFormat");
        AudioPicker.Header = L("DsAudioFormat");
        RefreshButton.Content = L("DsRefresh");
        NegotiateButton.Content = L("DsNegotiate");
        SampleButton.Content = L("DsSample");
        AdvertisedHeading.Text = L("DsDriverReported");
        NegotiatedHeading.Text = L("DsNegotiated");
        SampleHeading.Text = L("DsSampleResult");
        BindPreview();
        if (capabilities is not null)
            PopulateFormats();
        RenderEvidence();
    }

    private string L(string key) => localize(key);

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        await RunAsync(async token =>
        {
            var previous = (DevicePicker.SelectedItem as DirectShowDevice)?.Id;
            var devices = await DirectShowInspectionClient.RunAsync<DirectShowDevice[]>(
                new(DirectShowInspectionOperation.List), token);
            token.ThrowIfCancellationRequested();
            ResetDeviceEvidence();
            updatingPickers = true;
            DevicePicker.ItemsSource = devices;
            DevicePicker.SelectedItem = devices.FirstOrDefault(device => device.Id == previous);
            updatingPickers = false;
            populated = true;
            if (DevicePicker.SelectedItem is DirectShowDevice selected)
                await InspectAsync(selected, token);
            else if (devices.Length == 0)
                ShowNotice("DsNoDevices", InfoBarSeverity.Informational);
        });
    }

    private async void DeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPickers || DevicePicker.SelectedItem is not DirectShowDevice selected)
            return;
        ResetDeviceEvidence();
        await RunAsync(token => InspectAsync(selected, token));
    }

    private async Task InspectAsync(DirectShowDevice device, CancellationToken token)
    {
        var report = await DirectShowInspectionClient.RunAsync<DirectShowDeviceCapabilities>(
            new(DirectShowInspectionOperation.Probe, device.Id), token);
        token.ThrowIfCancellationRequested();
        capabilities = report;
        PopulateFormats();
        if (VideoStreamPicker.Items.Count == 0)
            ShowNotice("DsNoVideo", InfoBarSeverity.Warning);
    }

    private void PopulateFormats()
    {
        updatingPickers = true;
        var previousPin = (VideoStreamPicker.SelectedItem as VideoStream)?.PinId;
        var previousVideo = (VideoFormatPicker.SelectedItem as FormatOption)?.Format?.Index;
        var previousAudio = (AudioPicker.SelectedItem as FormatOption)?.Format?.Index;
        var formats = capabilities!.Formats;
        var video = formats.Where(format => format.Kind == "video")
            .GroupBy(format => new { format.PinId, format.IsInfrared })
            .Select(group => new VideoStream(group.Key.PinId, group.ToArray(),
                $"{L(group.Key.IsInfrared ? "DsInfrared" : "DsVideo")} · {Pin(group.First())}"))
            .ToArray();
        VideoStreamPicker.ItemsSource = video;
        VideoStreamPicker.SelectedItem = video.FirstOrDefault(stream => stream.Formats.Any(format => format.Index == previousVideo))
            ?? video.FirstOrDefault(stream => stream.PinId == previousPin) ?? video.FirstOrDefault();
        PopulateVideoFormats(previousVideo);

        var audio = new List<FormatOption> { new(L("DsWithoutAudio"), null) };
        audio.AddRange(formats.Where(format => format.Kind == "audio")
            .Select(format => new FormatOption($"{Pin(format)} · {Describe(format)}", format)));
        AudioPicker.ItemsSource = audio;
        AudioPicker.SelectedItem = audio.FirstOrDefault(item => item.Format?.Index == previousAudio) ?? audio[0];
        updatingPickers = false;
    }

    private void PopulateVideoFormats(int? previous)
    {
        var options = (VideoStreamPicker.SelectedItem as VideoStream)?.Formats
            .Select(format => new FormatOption(Describe(format), format)).ToArray() ?? [];
        VideoFormatPicker.ItemsSource = options;
        VideoFormatPicker.SelectedItem = options.FirstOrDefault(option => option.Format?.Index == previous)
            ?? options.FirstOrDefault();
    }

    private string Pin(DirectShowFormat format) => string.Format(L("DsPin"), format.PinName, format.PinId);

    private string Describe(DirectShowFormat format)
    {
        if (format.Kind == "audio" && format.SampleRate is { } sampleRate)
        {
            return string.Format(L("DsAudioDescription"), format.IsPcm ? "PCM" : format.Subtype.ToString(),
                sampleRate, format.Channels, format.BitsPerSample);
        }
        var encoding = format.InfraredEncoding ?? format.FourCc ?? format.Subtype.ToString();
        if (format.Width is null)
            return encoding;
        var order = format.FieldOrder switch
        {
            "progressive" => "DsProgressive",
            "top-first" => "DsTopFirst",
            "bottom-first" => "DsBottomFirst",
            _ => "DsFieldUnknown"
        };
        return $"{encoding} · {format.Width} × {format.Height} · {format.FramesPerSecond:0.###} fps · {L(order)}";
    }

    private void VideoStreamChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPickers)
            return;
        updatingPickers = true;
        PopulateVideoFormats(null);
        updatingPickers = false;
        ClearTests();
        RenderEvidence();
    }

    private void FormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPickers)
            return;
        ClearTests();
        RenderEvidence();
    }

    private async void NegotiateClicked(object sender, RoutedEventArgs e) => await VerifyAsync(false);
    private async void SampleClicked(object sender, RoutedEventArgs e) => await VerifyAsync(true);

    private async Task VerifyAsync(bool sample)
    {
        if (capabilities?.Device is not { } device ||
            (VideoFormatPicker.SelectedItem as FormatOption)?.Format is not { } video)
            return;
        var audio = (AudioPicker.SelectedItem as FormatOption)?.Format;
        if (sample)
            sampled = null;
        else
            negotiated = null;
        await RunAsync(async token =>
        {
            var report = await DirectShowInspectionClient.RunAsync<DirectShowCaptureResult>(
                new(sample ? DirectShowInspectionOperation.Sample : DirectShowInspectionOperation.Negotiate,
                    device.Id, video, audio), token);
            token.ThrowIfCancellationRequested();
            negotiated = report;
            if (sample)
                sampled = report;
            if (report.Error is { } error)
                ShowNotice("DsFailed", InfoBarSeverity.Error, error);
        });
    }

    private void ResetDeviceEvidence()
    {
        updatingPickers = true;
        VideoStreamPicker.ItemsSource = null;
        VideoFormatPicker.ItemsSource = null;
        AudioPicker.ItemsSource = null;
        updatingPickers = false;
        capabilities = null;
        PreviewPanel.Visibility = Visibility.Collapsed;
        ClearTests();
    }

    private void ClearTests()
    {
        negotiated = null;
        sampled = null;
        Notice.IsOpen = false;
        PreviewPicture.Source = null;
        previewBitmap = null;
        PreviewPanel.Visibility = Visibility.Collapsed;
    }

    private void RenderEvidence()
    {
        var video = capabilities is null ? null : (VideoFormatPicker.SelectedItem as FormatOption)?.Format;
        var audio = (AudioPicker.SelectedItem as FormatOption)?.Format;
        var visible = video is not null;
        FormatPanel.Visibility = capabilities is null ? Visibility.Collapsed : Visibility.Visible;
        AudioPicker.Visibility = capabilities?.Formats.Any(format => format.Kind == "audio") == true
            ? Visibility.Visible : Visibility.Collapsed;
        ResultPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var idle = operation is null && previewStop is null;
        RefreshButton.IsEnabled = idle;
        DevicePicker.IsEnabled = idle && DevicePicker.Items.Count > 0;
        VideoStreamPicker.IsEnabled = idle && VideoStreamPicker.Items.Count > 0;
        VideoFormatPicker.IsEnabled = idle && visible;
        AudioPicker.IsEnabled = idle && visible;
        NegotiateButton.IsEnabled = idle && visible;
        SampleButton.IsEnabled = idle && visible;
        BusyBar.Visibility = operation is null ? Visibility.Collapsed : Visibility.Visible;
        UpdatePreviewControls();

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

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null || previewStop is not null)
            return;
        using var stop = new CancellationTokenSource();
        operation = stop;
        Notice.IsOpen = false;
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

    private void ShowNotice(string key, InfoBarSeverity severity, string? detail = null)
    {
        Notice.Title = L(key);
        Notice.Message = detail ?? "";
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    public sealed record VideoStream(string PinId, DirectShowFormat[] Formats, string Label);
    public sealed record FormatOption(string Label, DirectShowFormat? Format);
}
