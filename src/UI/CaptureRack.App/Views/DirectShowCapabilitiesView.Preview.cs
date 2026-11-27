using CaptureRack.Audio;
using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.DirectShow.Windows.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App.Views;

public sealed partial class DirectShowCapabilitiesView
{
    private DirectShowPreviewSession? preview;
    private CaptureOperation? previewOperation;
    private bool closing;
    private DirectShowFormat? VideoFormat => (VideoFormatPicker.SelectedItem as FormatOption)?.Format;
    private DirectShowFormat? AudioFormat => (AudioPicker.SelectedItem as FormatOption)?.Format;

    public event EventHandler? StateChanged;
    public bool IsCapturing => previewOperation is not null;
    public bool IsStopping => previewOperation?.IsStopping == true;
    public bool CanStart => !closing && operation is null && capabilities is not null && (HasPreview || SupportsRecording);
    public bool HasInput => ReceivedUnits > 0;
    public long ReceivedUnits => preview?.Received ?? 0;
    public long DecodedFrames => preview?.Decoded ?? 0;
    private DirectShowFormat? ActiveVideo => preview?.NegotiatedVideo ?? VideoFormat;
    private DirectShowFormat? ActiveAudio => preview is { NegotiatedVideo: not null } active ? active.NegotiatedAudio : AudioFormat;

    public bool HasPreview => ActiveVideo is { } video && DirectShowPreviewVideo.Encoding(video) is not null;
    public CaptureAudioInfo? AudioInfo => preview?.NegotiatedAudio?.DescribeAudio();
    public bool HasAudio => DirectShowPreviewAudio.Supports(ActiveAudio) && ActiveAudio?.SampleRate is > 0 and <= 384000;
    public bool HasDeinterlace => HasPreview && ActiveVideo is { FieldOrder: "top-first" or "bottom-first" } video &&
        !DirectShowPreviewVideo.IsJpeg(DirectShowPreviewVideo.Encoding(video)!);
    public string? PreviewFailure { get; private set; }
    public string? PreviewHint => !IsCapturing ? null : VideoFormat?.IsMpeg2Video == true ? "DsMpeg2RecordOnly" :
        preview is { Decoded: 0, Unreadable: > 0 } ? "DsPreviewUnreadable" : "DsWaitingForFrames";

    public string? BuildFilenamePreview(string label)
    {
        if (ActiveVideo is not { } video)
            return null;
        if (recordingOutput() is var output && output != CaptureRack.Recording.RecordingOutput.Native)
        {
            var sanitized = RecordingFileNaming.SanitizeLabel(label);
            var prefix = sanitized.Length == 0 ? "CaptureRack" : sanitized + "_CaptureRack";
            var tag = new CaptureRack.Recording.RecordingEncoding(output).FileTagForAudioChannels(ActiveAudio?.Channels);
            return $"{prefix}_YYYYMMDD_HHmmss_DirectShow_{tag}.mkv";
        }
        var videoTag = video.IsMpeg2Video ? "MPEG2" : DirectShowPreviewVideo.Encoding(video) ?? "Video";
        var extensions = DirectShowRecording.OutputExtensions(video);
        return RecordingFileNaming.BuildDirectShowPreviewName(label, videoTag, ActiveAudio?.BitsPerSample, extensions);
    }

    public IReadOnlyList<TelemetryMetric> BuildLiveTelemetry(long previewSkippedFrames,
        Func<string, string> localize, IFormatProvider formatProvider) =>
        preview is { NegotiatedVideo: not null } source
            ? DirectShowWorkspaceModule.BuildLiveTelemetry(source.Received, source.Decoded, source.Unreadable,
                source.SkippedBeforeDecode + previewSkippedFrames, localize, formatProvider, recording?.RejectedPackets ?? lastRejectedPackets)
            : DirectShowWorkspaceModule.IdleMetrics(localize);

    public void NotifyOutputFolderChanged() => UpdateRecordingControls();

    public void StopPreview()
    {
        if (previewOperation is not { IsStopping: false } stop)
            return;
        if (recording is { } writer && !IsFinalizing)
            FinishRecording(writer);
        operation?.Cancel();
        stop.RequestStop();
        RenderEvidence();
    }

    public async Task StopForCloseAsync()
    {
        closing = true;
        var source = previewOperation;
        var clip = recordingOperation;
        StopPreview();
        if (source is not null) await source.Completion;
        if (clip is not null) await clip.Completion;
    }

    public void SetAudioMonitor(bool enabled, double volume) => preview?.SetAudioMonitor(enabled, volume);

    public void SetAudioMonitorRoute(AudioMonitorRoute route) => preview?.SetAudioMonitorRoute(route);

    public StereoLevels? ReadAudioLevels() => preview?.ReadAudioLevels();

    private void UpdatePreviewControls()
    {
        PreviewUnavailableBanner.Visibility = VideoFormat is not null && !HasPreview && !SupportsRecording
            ? Visibility.Visible : Visibility.Collapsed;
        PreviewUnavailable.Text = L("DsPreviewUnsupported");
        UpdateRecordingControls();
    }

    public Task StartAsync(PreviewFeed feed)
    {
        if (CanStart && !IsCapturing && !IsRecording && capabilities?.Device is { } device && VideoFormat is { } video)
            return RunPreviewAsync(feed, device, video, AudioFormat);
        return Task.CompletedTask;
    }

    private async Task RunPreviewAsync(PreviewFeed feed, DirectShowDevice device, DirectShowFormat video, DirectShowFormat? audio)
    {
        var session = new DirectShowPreviewSession(feed);
        using var stop = previewOperation = new CaptureOperation();
        preview = session;
        lastRejectedPackets = null;
        previewDiagnostic = null;
        try
        {
            HideNotice();
            PreviewFailure = null;
            RenderEvidence();
            var report = await session.RunAsync(new(device.Id, video.Index, audio?.Index, TimeSpan.Zero)
            {
                ExpectedVideo = video,
                ExpectedAudio = audio
            }, stop.Token);
            negotiated = report;
            sampled = report;
            previewDiagnostic = SnapshotPreview(session, report.StopReason,
                report.Error is { } captureError ? DirectShowDiagnosticFailure.FromCaptureError(captureError) : null);
            if (report.Error is { } error)
            {
                PreviewFailure = error;
                ShowNotice("PreviewFailed", InfoBarSeverity.Error, error);
            }
        }
        catch (OperationCanceledException) when (stop.IsStopping) { }
        catch (Exception error)
        {
            previewDiagnostic = SnapshotPreview(session, "error",
                DirectShowDiagnosticFailure.FromException(session.DiagnosticStage, error));
            PreviewFailure = error.Message;
            ShowNotice("PreviewFailed", InfoBarSeverity.Error, error.Message);
        }
        finally
        {
            preview = null;
            previewOperation = null;
            RenderEvidence();
        }
    }
}
