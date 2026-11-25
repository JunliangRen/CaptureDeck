using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.DirectShow.Windows.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App.Views;

public sealed partial class DirectShowCapabilitiesView
{
    private DirectShowPreviewSession? preview;
    private CancellationTokenSource? previewStop;
    private Task? previewTask;
    private DirectShowFormat? VideoFormat => (VideoFormatPicker.SelectedItem as FormatOption)?.Format;
    private DirectShowFormat? AudioFormat => (AudioPicker.SelectedItem as FormatOption)?.Format;

    public event EventHandler? StateChanged;
    public bool IsCapturing => previewStop is not null;
    public bool IsStopping => previewStop?.IsCancellationRequested == true;
    public bool CanStart => operation is null && capabilities is not null && (HasPreview || SupportsRecording);
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

    public void StopPreview()
    {
        if (previewStop is not { IsCancellationRequested: false } stop)
            return;
        if (recording is { } writer && !IsFinalizing)
            FinishRecording(writer);
        stop.Cancel();
        RenderEvidence();
    }

    public async Task StopForCloseAsync()
    {
        StopPreview();
        if (previewTask is { } task)
            await task;
        if (recordingTask is { } finishing)
            await finishing;
    }

    public void SetAudioMonitor(bool enabled, double volume) => preview?.SetAudioMonitor(enabled, volume);

    public StereoLevels? ReadAudioLevels() => preview?.ReadAudioLevels();

    private void UpdatePreviewControls()
    {
        PreviewUnavailable.Visibility = VideoFormat is not null && !HasPreview && !SupportsRecording
            ? Visibility.Visible : Visibility.Collapsed;
        PreviewUnavailable.Text = L("DsPreviewUnsupported");
        UpdateRecordingControls();
    }

    public Task StartAsync(PreviewFeed feed)
    {
        if (CanStart && !IsCapturing && !IsRecording && capabilities?.Device is { } device && VideoFormat is { } video)
            return previewTask = RunPreviewAsync(feed, device, video, AudioFormat);
        return Task.CompletedTask;
    }

    private async Task RunPreviewAsync(PreviewFeed feed, DirectShowDevice device, DirectShowFormat video, DirectShowFormat? audio)
    {
        using var stop = new CancellationTokenSource();
        previewStop = stop;
        var session = new DirectShowPreviewSession(feed);
        preview = session;
        Notice.IsOpen = false;
        PreviewFailure = null;
        RenderEvidence();
        try
        {
            var report = await session.RunAsync(new(device.Id, video.Index, audio?.Index, TimeSpan.Zero)
            {
                ExpectedVideo = video,
                ExpectedAudio = audio
            }, stop.Token);
            negotiated = report;
            sampled = report;
            if (report.Error is { } error)
            {
                PreviewFailure = error;
                ShowNotice("PreviewFailed", InfoBarSeverity.Error, error);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            PreviewFailure = error.Message;
            ShowNotice("PreviewFailed", InfoBarSeverity.Error, error.Message);
        }
        finally
        {
            preview = null;
            previewStop = null;
            RenderEvidence();
        }
    }
}
