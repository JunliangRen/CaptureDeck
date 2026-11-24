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
        UpdatePreviewFullscreenIcon();
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

        PreviewPlaceholderText.Text = L("WaitingPreview");
        LumaHeading.Text = L("LumaHeading");
        VectorHeading.Text = L("VectorHeading");
        VectorAxes.Text = L("VectorAxes");
        StereoHeading.Text = L("StereoHeading");
        RenderCaptureActions();
        RecordingClockLabel.Text = L(recordingClock.IsRunning ? "RecordingClock" : "Elapsed");
        CheckMovLabel.Text = L("CheckMov");
        AutomationProperties.SetName(CheckMovButton, CheckMovLabel.Text);
        QcHeading.Text = L("QcHeading");
        QcWarningsHeading.Text = L("QcWarningsHeading");
        QcFolderButton.Content = L("QcFolder");
        QcVideoButton.Content = L("OpenRecordedMov");
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
        StatusFileName.Text = Path.GetFileName(statusFilePath) ?? "";
        StatusFileName.Visibility = statusFilePath is not null &&
            (recordingSession || qcStop is not null || statusTitle.StartsWith("Qc", StringComparison.Ordinal))
            ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(StatusFileName, statusFilePath);
        RenderCompactStatus();
    }

    private void RenderCaptureActions()
    {
        var previewActive = captureTask is not null;
        var recordingActive = recordingSession && qcStop is null;
        PreviewActionIcon.Glyph = previewActive ? "\uE71A" : "\uE768";
        RecordActionIcon.Glyph = recordingActive ? "\uE71A" : "\uE7C8";
        PreviewActionLabel.Text = L(previewActive ? "StopPreview" : "StartPreview");
        RecordActionLabel.Text = L(recordingActive ? "StopRecording" : "StartRecording");
        CancelQcButton.Content = L("CancelQc");
        AutomationProperties.SetName(StartButton, PreviewActionLabel.Text);
        AutomationProperties.SetName(RecordButton, RecordActionLabel.Text);
    }

    private void RenderCompactStatus()
    {
        var recordingWithoutInput = recordingClock.IsRunning && liveSignal == VideoSignal.NoInput;
        var severity = recordingWithoutInput ? InfoBarSeverity.Warning : SignalStatus.Severity;
        StatusGlyph.Glyph = severity switch
        {
            InfoBarSeverity.Error => "\uE783",
            InfoBarSeverity.Warning => "\uE7BA",
            InfoBarSeverity.Success => "\uE73E",
            _ => "\uE946"
        };
        StatusTitleText.Text = recordingWithoutInput ? L("NoInputSignal") : SignalStatus.Title;
        AutomationProperties.SetName(StatusDetailsButton, StatusTitleText.Text);
        ToolTipService.SetToolTip(StatusDetailsButton, recordingWithoutInput ? L("RecordingNoInput") : SignalStatus.Message);
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
            stats?.AudioPackets ?? 0, stats?.MissingVideoFrames ?? 0, displayedPreviewSkips);
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
        if (capture is { InputModeChanged: true })
            warnings.Add(L("QcWarnInputModeChange"));
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
