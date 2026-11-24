using CaptureRack.DeckLink;
using CaptureRack.QualityControl;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private string statusTitle = "Idle";
    private Func<string>? statusMessage;
    private string? sdkVersion;
    private CaptureStats? displayedStats;
    private PreviewCaption? previewCaption;
    private QcPresentation? lastQcPresentation;

    private void ApplyWorkspaceLanguage()
    {
        PreviewTab.Text = L("PreviewTab");
        ScopesTab.Text = L("ScopesTab");
        SpectrumTab.Text = L("SpectrumTab");
        EventsTab.Text = L("EventsTab");
        ToolTipService.SetToolTip(ScopesTab, L("VideoScopes"));
        ToolTipService.SetToolTip(SpectrumTab, L("AudioSpectrum"));
        AutomationProperties.SetName(MonitorSelector, L("Monitoring"));
        AutomationProperties.SetName(PreviewImage, L("PreviewImage"));
        AutomationProperties.SetName(VectorPlot, L("VectorPlot"));
        AutomationProperties.SetName(SpectrumPlot, L("AudioSpectrum"));
        AutomationProperties.SetName(EventList, L("EventTimeline"));
        AutomationProperties.SetName(StereoMeterGrid, L("StereoMeter"));
        AutomationProperties.SetName(LeftChannel, L("LeftChannel"));
        AutomationProperties.SetName(RightChannel, L("RightChannel"));
        ToolTipService.SetToolTip(LeftLevel, L("LeftPeak"));
        ToolTipService.SetToolTip(RightLevel, L("RightPeak"));
        ToolTipService.SetToolTip(PhaseLevel, L("PhaseHelp"));
        ToolTipService.SetToolTip(RecordingElapsed, L("ElapsedHelp"));

        PreviewPlaceholder.Text = L("WaitingPreview");
        LumaHeading.Text = L("LumaHeading");
        VectorHeading.Text = L("VectorHeading");
        VectorAxes.Text = L("VectorAxes");
        StereoHeading.Text = L("StereoHeading");
        StartButton.Content = L(captureTask is not null ? "StopPreview" : "StartPreview");
        RecordButton.Content = L(qcStop is not null ? "CancelQc" : recordingSession ? "StopRecording" : "StartRecording");
        RecordingClockLabel.Text = L(recordingClock.IsRunning ? "RecordingClock" : "Elapsed");
        CheckMovButton.Content = L("CheckMov");
        QcHeading.Text = L("QcHeading");
        QcWarningsHeading.Text = L("QcWarningsHeading");
        QcFolderButton.Content = L("QcFolder");
        QcLogButton.Content = L("QcLog");
        QcHashButton.Content = L("QcHash");
        QcReportButton.Content = L("QcReport");

        RenderApiVersion();
        RenderStatus();
        RenderPreviewCaption();
        RenderQcResult();
        RenderCounts(displayedStats);
        RefreshLocalizedMonitoring();
    }

    private void RenderApiVersion()
    {
        ApiVersion.Text = sdkVersion switch
        {
            null => L("ApiWaiting"),
            "" => L("ApiUnavailable"),
            _ => $"Blackmagic DeckLink API {sdkVersion}"
        };
    }

    private void RenderStatus()
    {
        SignalStatus.Title = L(statusTitle);
        SignalStatus.Message = statusMessage?.Invoke() ?? L("SelectInputs");
    }

    private void RenderPreviewCaption()
    {
        if (previewCaption is { } caption)
            PreviewDetail.Text = LocalFormat("PreviewFrameDetail",
                L(caption.NoInput ? "PreviewNoInputFrame" : "PreviewLiveFrame"), caption.Width,
                caption.Height, caption.Format, caption.Frames);
    }

    private void RenderCounts(CaptureStats? stats)
    {
        var text = LocalFormat("CaptureCounts", stats?.VideoFrames ?? 0, stats?.NoInputFrames ?? 0,
            stats?.AudioPackets ?? 0, stats?.MissingVideoFrames ?? 0);
        CaptureCountsStatus.Text = text;
        ToolTipService.SetToolTip(CaptureCountsStatus, text);
    }

    private string DescribeQcResult(QcResult result) => result.Status switch
    {
        QcStatus.Passed => LocalFormat("QcDecoded", result.DecodedVideoFrames ?? 0, result.DecodedAudioSampleFrames ?? 0),
        QcStatus.Cancelled => L("QcCancelledDetail"),
        QcStatus.ToolsUnavailable => L("QcToolsDetail"),
        _ => L("QcLogDetail")
    };

    private void RenderQcResult()
    {
        QcResultsButton.Content = L("QcResults");
        if (qcOutputPath is null || lastQcPresentation is not { } presentation)
            return;

        QcOutcome.Text = L(presentation.Title);
        QcSummary.Text = presentation.Summary();
        var capture = presentation.Capture;
        List<string> warnings = [];
        if (lastQcResult is { } result)
        {
            if (capture is { NoInputFrames: > 0 })
                warnings.Add(LocalFormat("QcWarnNoInput", capture.NoInputFrames));
            if (capture is { MissingVideoFrames: > 0 })
                warnings.Add(LocalFormat("QcWarnMissing", capture.MissingVideoFrames));
            if (capture is { TimestampRegressions: > 0 })
                warnings.Add(LocalFormat("QcWarnTimestamp", capture.TimestampRegressions));
            if (capture is { LeftClips: > 0 } or { RightClips: > 0 })
                warnings.Add(L("QcWarnClipping"));
            if (result.VideoFramesWithoutFieldFlags > 0)
                warnings.Add(LocalFormat("QcWarnFieldFlags", result.VideoFramesWithoutFieldFlags));
        }

        QcWarningsList.ItemsSource = warnings;
        QcWarningsPanel.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (warnings.Count > 0)
            QcResultsButton.Content = warnings.Count == 1 ? L("QcOneWarning") : LocalFormat("QcWarningCount", warnings.Count);
    }

    private sealed record QcPresentation(QcCaptureLog? Capture, string Title, Func<string> Summary);
    private readonly record struct PreviewCaption(bool NoInput, int Width, int Height, string Format, long Frames);
}
