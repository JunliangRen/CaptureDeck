using System.Text.Json;
using CaptureRack.DirectShow.Windows.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace CaptureRack.App.Views;

public sealed partial class DirectShowCapabilitiesView
{
    private DirectShowDiagnosticCheck? negotiationCheck;
    private DirectShowDiagnosticCheck? sampleCheck;
    private DirectShowDiagnosticFailure? formatDiscoveryFailure;
    private PreviewDiagnostic? previewDiagnostic;

    private void SaveDiagnosticCheck(bool sample, DirectShowDiagnosticCheck check)
    {
        if (sample)
            sampleCheck = check;
        else
            negotiationCheck = check;
    }

    private static PreviewDiagnostic SnapshotPreview(DirectShowPreviewSession session, string state,
        DirectShowDiagnosticFailure? failure = null) => new(state, session.NegotiatedVideo, session.NegotiatedAudio,
            session.Received, session.Decoded, session.Unreadable, failure);

    private async void CopyDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        if (DevicePicker.SelectedItem is not DirectShowDevice device || IsStopping || IsRecording)
            return;

        await RunAsync(async token =>
        {
            var live = preview;
            var currentPreview = live is null ? previewDiagnostic : SnapshotPreview(live, "active");
            DirectShowDiagnosticDevice? details = null;
            DirectShowDiagnosticFailure? detailsFailure = null;
            try
            {
                details = await DirectShowInspectionClient.RunAsync<DirectShowDiagnosticDevice>(
                    new(DirectShowInspectionOperation.DiagnosticDevice, device.Id, IncludeControls: live is null), token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                detailsFailure = DirectShowDiagnosticFailure.FromException("Read device diagnostics", error);
            }

            var controls = details?.CameraControls;
            var controlFailure = details?.ControlFailure;
            if (live is not null)
            {
                try { controls = await live.AccessCameraControlsAsync(null, token); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    controlFailure = DirectShowDiagnosticFailure.FromException("Read active camera controls", error);
                }
            }

            token.ThrowIfCancellationRequested();
            var report = new
            {
                ReportVersion = 1,
                Backend = "DirectShow",
                CapturedAt = DateTimeOffset.Now,
                ApplicationVersion = typeof(App).Assembly.GetName().Version?.ToString(),
                WindowsVersion = Environment.OSVersion.Version.ToString(),
                Device = new { device.Name, HardwareId = details?.HardwareId, DriverVersion = details?.DriverVersion },
                DeviceQueryFailure = Redact(detailsFailure, device.Id),
                FormatDiscovery = new { AdvertisedFormatCount = capabilities?.Formats.Count,
                    Failure = Redact(formatDiscoveryFailure, device.Id) },
                SelectedFormats = new { Video = VideoFormat, Audio = AudioFormat },
                CameraControls = controls,
                CameraControlFailure = Redact(controlFailure, device.Id),
                Negotiation = ReportCheck(negotiationCheck, device.Id),
                Sample = ReportCheck(sampleCheck, device.Id),
                Preview = currentPreview is null ? null : currentPreview with { Failure = Redact(currentPreview.Failure, device.Id) }
            };
            var clipboard = new DataPackage();
            clipboard.SetText(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Clipboard.SetContent(clipboard);
            ShowNotice("DsDiagnosticsCopied", InfoBarSeverity.Success);
        }, allowPreview: true);
    }

    private static DirectShowDiagnosticFailure? Redact(DirectShowDiagnosticFailure? failure, string deviceId) =>
        failure is null ? null : failure with { Message = failure.Message.Replace(deviceId, "[device]", StringComparison.OrdinalIgnoreCase) };

    private static object? ReportCheck(DirectShowDiagnosticCheck? check, string deviceId) => check is null ? null : new
    {
        check.NegotiatedVideo,
        check.NegotiatedAudio,
        StopReason = check.Result?.StopReason,
        VideoSamples = check.Result is { StopReason: not "negotiated" } result ? result.Video : null,
        AudioSamples = check.Result is { StopReason: not "negotiated" } sampled ? sampled.Audio : null,
        DriverDroppedFrames = check.Result?.DriverDroppedFrames,
        Failure = Redact(check.Failure, deviceId)
    };

    private sealed record PreviewDiagnostic(string State, DirectShowFormat? NegotiatedVideo, DirectShowFormat? NegotiatedAudio,
        long ReceivedFrames, long DecodedFrames, long UnreadableFrames, DirectShowDiagnosticFailure? Failure);
}
