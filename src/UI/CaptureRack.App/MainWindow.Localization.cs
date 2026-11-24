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
        CaptureModeLabel.Text = L("CaptureModeTab");
        QcModeLabel.Text = L("QcCenterTab");
        LayoutFocusLabel.Text = L("LayoutFocus");
        LayoutSplitLabel.Text = L("LayoutSplit");
        LayoutScopesLabel.Text = L("ScopesTab");
        LayoutSpectrumLabel.Text = L("SpectrumTab");
        LayoutEventsLabel.Text = L("EventsTab");

        SplitVectorBtn.Content = L("VectorShort");
        SplitSpectrumBtn.Content = L("SpectrumTab");
        SplitEventsBtn.Content = L("EventsTab");
        EventsFilterAllBtn.Content = L("FilterAll");
        EventsFilterDropsBtn.Content = L("FilterDrops");
        EventsFilterSignalBtn.Content = L("FilterSignal");
        ToolTipService.SetToolTip(CopyEventsButton, L("CopyEvents"));
        ToolTipService.SetToolTip(SafeAreaToggleButton, L("SafeAreaGuides"));
        ToolTipService.SetToolTip(ToggleInspectorButton, L("Capture"));
        ToolTipService.SetToolTip(PinInspectorButton, L("PinInspector"));
        ToolTipService.SetToolTip(CloseInspectorButton, L("CloseInspector"));

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
        PreviewPlaceholderHint.Text = L("PreviewBeforeRecording");
        PlaceholderStartLabel.Text = L("StartPreview");
        LumaHeading.Text = L("LumaHeading");
        VectorAxes.Text = L("VectorAxes");
        StereoHeading.Text = L("AudioShort");
        ValidFramesLabel.Text = L("MetricValidFrames");
        NoInputFramesLabel.Text = L("MetricNoInput");
        MissingFramesLabel.Text = L("MetricMissing");
        SkippedFramesLabel.Text = L("MetricSkipped");

        RenderCaptureActions();
        CheckMovLabel.Text = L("CheckMov");
        AutomationProperties.SetName(CheckMovButton, CheckMovLabel.Text);
        QcHeading.Text = L("QcCenterTitle");
        QcCenterSubtitle.Text = L("QcCenterSubtitle");
        QcStep1Label.Text = L("QcStepInspect");
        QcStep2Label.Text = L("QcStepDecode");
        QcStep3Label.Text = L("QcStepAnalyze");
        QcMetricVideoLabel.Text = L("QcMetricVideo");
        QcMetricAudioLabel.Text = L("QcMetricAudio");
        QcMetricWarningsLabel.Text = L("QcWarningsHeading");
        QcArtifactsHeading.Text = L("QcArtifactsHeading");
        QcWarningsHeading.Text = L("QcWarningsHeading");
        QcFolderButton.Content = L("QcFolder");
        QcVideoButton.Content = L("OpenRecordedMov");
        QcLogButton.Content = L("QcLog");
        QcHashButton.Content = L("QcHash");
        QcReportButton.Content = L("QcReport");
        QcToggleInlineLogLabel.Text = QcInlineLogText.Visibility == Visibility.Visible
            ? L("QcHideInlineLog") : L("QcViewInlineLog");
        FullscreenExitLabel.Text = L("ExitPreviewFullScreen");

        if (qcOutputPath is null)
        {
            QcFileName.Text = L("QcDropPrompt");
            QcFilePathText.Text = L("QcDropHint");
        }

        if (qcStop is not null)
            FooterQcStageText.Text = activeQcStage is { } stage ? L($"Qc{stage}") : L("QcStarting");

        RenderApiVersion();
        RenderStatus();
        RenderPreviewCaption();
        RenderQcResult();
        RenderCounts(displayedStats);
        RefreshLocalizedMonitoring();
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
        PreviewActionLabel.Text = L(previewActive ? "StopPreview" : "StartPreview");

        RecordActionLabel.Text = L(recordingActive ? "StopRecording" : "StartRecording");
        RecordActionDot.Visibility = recordingActive ? Visibility.Collapsed : Visibility.Visible;
        RecordActionStopIcon.Visibility = recordingActive ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.Style = (Style)Application.Current.Resources[recordingActive
            ? "StudioRecordActiveButtonStyle" : "StudioRecordIdleButtonStyle"];

        FullscreenRecordLabel.Text = RecordActionLabel.Text;
        FullscreenRecordIcon.Glyph = recordingActive ? "\uE71A" : "\uE7C8";
        FullscreenRecordButton.Style = RecordButton.Style;

        ToolTipService.SetToolTip(CancelQcButton, L("CancelQc"));
        AutomationProperties.SetName(CancelQcButton, L("CancelQc"));
        QcCenterCancelButton.Content = L("CancelQc");

        AutomationProperties.SetName(StartButton, PreviewActionLabel.Text);
        AutomationProperties.SetName(RecordButton, RecordActionLabel.Text);
    }

    private void RenderCompactStatus()
    {
        var recordingWithoutInput = recordingClock.IsRunning && liveSignal == VideoSignal.NoInput;
        var severity = recordingWithoutInput ? InfoBarSeverity.Warning : SignalStatus.Severity;
        var dotBrush = severity switch
        {
            InfoBarSeverity.Error => ThemeBrush("StudioDangerBrush"),
            InfoBarSeverity.Warning => ThemeBrush("StudioCautionBrush"),
            InfoBarSeverity.Success => ThemeBrush("StudioSignalLockBrush"),
            _ => ThemeBrush("StudioInfoBrush")
        };

        StatusDot.Fill = dotBrush;
        StatusTitleText.Text = recordingWithoutInput ? L("NoInputSignal") : SignalStatus.Title;
        AutomationProperties.SetName(StatusDetailsButton, StatusTitleText.Text);
        ToolTipService.SetToolTip(StatusDetailsButton, recordingWithoutInput ? L("RecordingNoInput") : SignalStatus.Message);

        // Sync Stage Signal Pill & Fullscreen Signal Indicator
        if (captureTask is not null)
        {
            var isLocked = liveSignal == VideoSignal.Present;
            var signalBrush = isLocked
                ? ThemeBrush("StudioSignalLockBrush")
                : liveSignal == VideoSignal.NoInput
                    ? ThemeBrush("StudioCautionBrush")
                    : ThemeBrush("StudioInfoBrush");
            StageSignalDot.Fill = signalBrush;
            FullscreenSignalDot.Fill = signalBrush;
            StageSignalText.Text = FullscreenStatusText.Text = L(isLocked ? "SignalPresent" :
                liveSignal == VideoSignal.NoInput ? "NoInputSignal" : "WaitingFrames");
            StageSignalBadge.Background = ThemeBrush(isLocked ? "StudioSignalLockSoftBrush" :
                liveSignal == VideoSignal.NoInput ? "StudioCautionSoftBrush" : "StudioNeutralPillBrush");
        }
        else
        {
            StageSignalDot.Fill = ThemeBrush("StudioTextMutedBrush");
            FullscreenSignalDot.Fill = ThemeBrush("StudioTextMutedBrush");
            StageSignalText.Text = FullscreenStatusText.Text = L("Idle");
            StageSignalBadge.Background = ThemeBrush("StudioNeutralPillBrush");
        }
    }

    private void RenderPreviewCaption()
    {
        if (previewCaption is { } caption)
        {
            PreviewDetail.Text = LocalFormat("PreviewFrameDetail",
                L(caption.NoInput ? "PreviewNoInputFrame" : "PreviewLiveFrame"), caption.Width,
                caption.Height, caption.Format, caption.Frames);
            PreviewHudDot.Fill = ThemeBrush(caption.NoInput ? "StudioCautionBrush" : "StudioSignalLockBrush");
        }
    }

    private void RenderCounts(CaptureStats? stats)
    {
        var valid = stats?.VideoFrames ?? 0;
        var noInput = stats?.NoInputFrames ?? 0;
        var audio = stats?.AudioPackets ?? 0;
        var missing = stats?.MissingVideoFrames ?? 0;
        var skipped = displayedPreviewSkips;

        ValidFramesValue.Text = valid.ToString("N0", settingsCulture);
        NoInputFramesValue.Text = noInput.ToString("N0", settingsCulture);
        MissingFramesValue.Text = missing.ToString("N0", settingsCulture);
        SkippedFramesValue.Text = skipped.ToString("N0", settingsCulture);

        ApplyTelemetryChipTone(NoInputFramesChip, NoInputFramesValue, noInput > 0 ? "Caution" : null);
        ApplyTelemetryChipTone(MissingFramesChip, MissingFramesValue, missing > 0 ? "Danger" : null);
        ApplyTelemetryChipTone(SkippedFramesChip, SkippedFramesValue, skipped > 0 ? "Caution" : null);

        var summary = LocalFormat("CaptureCounts", valid, noInput, audio, missing, skipped);
        ToolTipService.SetToolTip(TelemetryStrip, summary);
    }

    private void ApplyTelemetryChipTone(Border chip, TextBlock value, string? tone)
    {
        switch (tone)
        {
            case "Danger":
                chip.Background = ThemeBrush("StudioDangerSoftBrush");
                chip.BorderBrush = ThemeBrush("StudioDangerBrush");
                value.Foreground = ThemeBrush("StudioDangerBrush");
                break;
            case "Caution":
                chip.Background = ThemeBrush("StudioCautionSoftBrush");
                chip.BorderBrush = ThemeBrush("StudioCautionBrush");
                value.Foreground = ThemeBrush("StudioCautionBrush");
                break;
            default:
                chip.Background = ThemeBrush("StudioCardBrush");
                chip.BorderBrush = ThemeBrush("StudioHairlineBrush");
                value.Foreground = ThemeBrush("StudioTextPrimaryBrush");
                break;
        }
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
        QcResultsLabel.Text = L("QcResults");
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

            QcMetricVideoValue.Text = result.DecodedVideoFrames?.ToString("N0", settingsCulture) ?? "—";
            QcMetricAudioValue.Text = result.DecodedAudioSampleFrames?.ToString("N0", settingsCulture) ?? "—";
        }
        else
        {
            QcMetricVideoValue.Text = "—";
            QcMetricAudioValue.Text = "—";
        }

        QcMetricWarningsValue.Text = warnings.Count.ToString("N0", settingsCulture);
        QcMetricWarningsValue.Foreground = warnings.Count > 0
            ? ThemeBrush("StudioCautionBrush") : ThemeBrush("StudioTextPrimaryBrush");

        QcWarningsList.ItemsSource = warnings;
        QcWarningsPanel.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (warnings.Count > 0)
            QcResultsLabel.Text = warnings.Count == 1 ? L("QcOneWarning") : LocalFormat("QcWarningCount", warnings.Count);

        var status = lastQcResult?.Status;
        var (toneSoft, toneSolid, glyph) = status switch
        {
            QcStatus.Passed when warnings.Count == 0 => ("StudioSignalLockSoftBrush", "StudioSignalLockBrush", "\uE73E"),
            QcStatus.Passed or QcStatus.Cancelled or QcStatus.ToolsUnavailable => ("StudioCautionSoftBrush", "StudioCautionBrush", "\uE7BA"),
            _ => ("StudioDangerSoftBrush", "StudioDangerBrush", "\uE783")
        };
        QcOutcomeBanner.Background = ThemeBrush(toneSoft);
        QcOutcomeBanner.BorderBrush = ThemeBrush(toneSolid);
        QcOutcomeIcon.Foreground = ThemeBrush(toneSolid);
        QcOutcomeIcon.Glyph = glyph;

        QcStatusPill.Visibility = Visibility.Visible;
        QcStatusPill.Background = ThemeBrush(toneSoft);
        QcStatusPillText.Text = L(presentation.Title);
        QcStatusPillText.Foreground = ThemeBrush(toneSolid);

        QcResultsDot.Fill = ThemeBrush(toneSolid);
        QcResultsButton.Background = ThemeBrush(toneSoft);
        QcResultsButton.BorderBrush = ThemeBrush(toneSolid);
    }

    private sealed record QcPresentation(QcCaptureLog? Capture, string Title, Func<string> Summary);
    private readonly record struct PreviewCaption(bool NoInput, int Width, int Height, string Format, long Frames);
}
