using System.Collections.ObjectModel;
using System.ComponentModel;
using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.QualityControl;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<TelemetryChipItem> telemetryItems = [];
    private string statusTitle = "Idle";
    private string signalDotBrushKey = "StudioTextMutedBrush";
    private Func<string>? statusMessage;
    private string? sdkVersion;
    private CaptureStats? displayedStats;
    private PreviewCaption? previewCaption;
    private CaptureAudioInfo? previewAudio;

    private void ApplyWorkspaceLanguage()
    {
        CaptureModeLabel.Text = L("CaptureModeTab");
        QcModeLabel.Text = L("QcCenterTab");
        SourceRackHeading.Text = L("SourceRackHeading");
        LayoutFocusLabel.Text = L("LayoutFocus");
        LayoutSplitLabel.Text = L("LayoutSplit");
        LayoutScopesLabel.Text = L("ScopesTab");
        LayoutSpectrumLabel.Text = L("SpectrumTab");
        LayoutEventsLabel.Text = L("EventsTab");

        ScopeScaleCodeBtn.Content = L("ScopeScaleCode");
        ToolTipService.SetToolTip(ScopeLumaBtn, L("LumaHeading"));
        ToolTipService.SetToolTip(ScopeParadeBtn, L("RgbParadeHeading"));
        ToolTipService.SetToolTip(SplitResizeHandle, L("SplitResizeTooltip"));

        SplitVectorBtn.Content = L("VectorShort");
        SplitSpectrumBtn.Content = L("SpectrumTab");
        SplitEventsBtn.Content = L("EventsTab");
        EventsFilterAllBtn.Content = L("FilterAll");
        EventsFilterDropsBtn.Content = L("FilterDrops");
        EventsFilterSignalBtn.Content = L("FilterSignal");
        ToolTipService.SetToolTip(CopyEventsButton, L("CopyEvents"));
        ToolTipService.SetToolTip(ToggleInspectorButton, L("Capture"));
        ToolTipService.SetToolTip(PinInspectorButton, L("PinInspector"));
        ToolTipService.SetToolTip(CloseInspectorButton, L("CloseInspector"));

        AutomationProperties.SetName(PreviewImage, L("PreviewImage"));
        UpdatePreviewFullscreenIcon();
        AutomationProperties.SetName(VectorPlot, L("VectorPlot"));
        AutomationProperties.SetName(SpectrumPlot, L("AudioSpectrum"));
        AutomationProperties.SetName(EventList, L("EventTimeline"));
        ToolTipService.SetToolTip(RecordingElapsed, L("ElapsedHelp"));

        PreviewPlaceholderText.Text = L(LiveMediaSource?.PreviewHint ?? "WaitingPreview");
        PreviewPlaceholderHint.Text = LiveMediaSource is null ? L("PreviewBeforeRecording") : "";
        PlaceholderStartLabel.Text = L("StartPreview");
        VectorAxes.Text = L("VectorAxes");

        RenderCaptureActions();
        RelocalizeQcHistory();
        QcCenterPage.ApplyLanguage(L, settingsCulture);
        RenderSourceRackList();

        if (qcStop is not null)
            FooterQcStageText.Text = activeQcStage is { } stage ? L($"Qc{stage}") : L("QcStarting");

        RenderApiVersion();
        RenderStatus();
        RenderPreviewCaption();
        RenderPreviewAudioCaption();
        RenderQcSummaryBadge();
        RenderCounts(HasLiveCapture ? displayedStats : null);
        RefreshLocalizedMonitoring();
        SyncFullscreenPreviewWindow();
    }

    private void RelocalizeQcHistory()
    {
        if (qcHistory.Entries.Count == 0)
            return;

        qcHistory.TransformAll(entry =>
        {
            if (entry.Kind == QcEntryKind.Mov)
            {
                var summary = entry.MovResult is { } movResult ? DescribeQcResult(movResult) : entry.Summary;
                var warnings = BuildMovWarnings(entry.CaptureLog, entry.MovResult);
                return entry with { Summary = summary, Warnings = warnings };
            }

            if (entry.SummaryKey is { } summaryKey)
                return entry with { Summary = L(summaryKey) };

            return entry;
        });

        if (latestQcEntry is not null && qcHistory.Find(latestQcEntry.Id) is { } updatedLatest)
            latestQcEntry = updatedLatest;

        var displayedId = QcCenterPage.DisplayedEntry?.Id;
        if (displayedId is { } id && qcHistory.Find(id) is { } updatedDisplayed)
            QcCenterPage.RenderEntry(updatedDisplayed);
        QcCenterPage.RenderHistory(qcHistory.Entries, displayedId);
    }

    private void RenderStatus()
    {
        SignalStatus.Title = L(statusTitle);
        SignalStatus.Message = statusMessage?.Invoke() ?? L("SelectInputs");
        StatusFileName.Text = Path.GetFileName(statusFilePath) ?? "";
        StatusFileName.Visibility = statusFilePath is not null &&
            QcPresentationHelper.ShouldShowStatusFileName(IsCaptureRecording, qcStop is not null, statusTitle)
            ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(StatusFileName, statusFilePath);
        RenderCompactStatus();
    }

    private void RenderCaptureActions()
    {
        var previewActive = IsCaptureInputActive;
        var recordingActive = IsCaptureRecording && qcStop is null;
        var finalizing = (recordingTransition && qcStop is null) || LiveMediaSource?.IsFinalizing == true;

        PreviewActionIcon.Glyph = previewActive && IsRfActive && rfInspector.IsPlayback ? "\uE769" : previewActive ? "\uE71A" : "\uE768";
        var captureOnly = LiveMediaSource is { HasPreview: false };
        PreviewActionLabel.Text = L(IsRfActive && rfInspector.IsPlayback ? previewActive ? "RfPause" : "RfPlay" : captureOnly
            ? previewActive ? "DsStopInput" : "DsStartInput"
            : previewActive ? "StopPreview" : "StartPreview");
        PlaceholderStartLabel.Text = PreviewActionLabel.Text;

        RecordActionLabel.Text = L(recordingActive ? "StopRecording" : "StartRecording");
        RecordActionDot.Visibility = !recordingActive && !finalizing ? Visibility.Visible : Visibility.Collapsed;
        RecordActionStopIcon.Visibility = recordingActive && !finalizing ? Visibility.Visible : Visibility.Collapsed;
        RecordActionSpinner.Visibility = finalizing ? Visibility.Visible : Visibility.Collapsed;
        RecordActionSpinner.IsActive = finalizing;
        RecordActionSpinner.Foreground = recordingActive
            ? new SolidColorBrush(Microsoft.UI.Colors.White)
            : ThemeBrush("StudioRecBrush");
        RecordButton.Style = (Style)Application.Current.Resources[recordingActive
            ? "StudioRecordActiveButtonStyle" : "StudioRecordIdleButtonStyle"];

        FooterFinalizingPill.Visibility = finalizing && qcStop is null ? Visibility.Visible : Visibility.Collapsed;
        FooterFinalizingSpinner.IsActive = FooterFinalizingPill.Visibility == Visibility.Visible;
        if (FooterFinalizingPill.Visibility == Visibility.Visible)
        {
            FooterFinalizingText.Text = LiveMediaSource is { IsFinalizing: true } media && !string.IsNullOrWhiteSpace(media.RecordingDetail)
                ? media.RecordingDetail!
                : L(statusTitle is "StartingRecording" or "FinalizingMov" or "StopRecording" ? statusTitle : "QcFinishing");
        }

        ToolTipService.SetToolTip(CancelQcButton, L("CancelQc"));
        AutomationProperties.SetName(CancelQcButton, L("CancelQc"));

        AutomationProperties.SetName(StartButton, PreviewActionLabel.Text);
        AutomationProperties.SetName(RecordButton, RecordActionLabel.Text);
        SyncFullscreenPreviewWindow();
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

        if (IsCaptureInputActive)
        {
            var isLocked = liveSignal == VideoSignal.Present;
            signalDotBrushKey = isLocked
                ? "StudioSignalLockBrush"
                : liveSignal == VideoSignal.NoInput
                    ? "StudioCautionBrush"
                    : "StudioInfoBrush";
            StageSignalDot.Fill = ThemeBrush(signalDotBrushKey);
            StageSignalText.Text = L(isLocked ? "VideoLocked" :
                liveSignal == VideoSignal.NoInput ? "VideoUnlocked" : "WaitingFrames");
            StageSignalBadge.Background = ThemeBrush(isLocked ? "StudioSignalLockSoftBrush" :
                liveSignal == VideoSignal.NoInput ? "StudioCautionSoftBrush" : "StudioNeutralPillBrush");
        }
        else
        {
            signalDotBrushKey = "StudioTextMutedBrush";
            StageSignalDot.Fill = ThemeBrush(signalDotBrushKey);
            StageSignalText.Text = L("Idle");
            StageSignalBadge.Background = ThemeBrush("StudioNeutralPillBrush");
        }
        SyncFullscreenPreviewWindow();
    }

    private void RenderPreviewCaption()
    {
        if (previewCaption is { } caption)
        {
            var scanLabel = L(caption.ScanMode switch
            {
                PreviewScanMode.Progressive => "DsProgressive",
                PreviewScanMode.TopFirst => "PreviewTopFirst",
                PreviewScanMode.BottomFirst => "PreviewBottomFirst",
                PreviewScanMode.ProgressiveSegmented => "PreviewPsf",
                _ => "DsFieldUnknown"
            });
            PreviewDetail.Text = LocalFormat("PreviewFrameDetail",
                L(caption.NoInput ? "PreviewNoInputFrame" : "PreviewLiveFrame"), caption.Width,
                caption.Height, caption.Format, scanLabel, caption.Frames);
            PreviewHudDot.Fill = ThemeBrush(caption.NoInput ? "StudioCautionBrush" : "StudioSignalLockBrush");
        }
    }

    private void SetPreviewAudio(CaptureAudioInfo? audio)
    {
        if (audio == previewAudio)
            return;
        previewAudio = audio;
        RenderPreviewAudioCaption();
    }

    private void RenderPreviewAudioCaption()
    {
        PreviewAudioDetail.Visibility = previewAudio is null ? Visibility.Collapsed : Visibility.Visible;
        if (previewAudio is not { } audio)
            return;
        var depth = audio.BitDepth is { } bits ? LocalFormat("PreviewAudioBitDepth", bits) : LocalFormat("PreviewAudioBitDepth", "—");
        var rate = audio.SampleRateHz is { } hz ? LocalFormat("PreviewAudioSampleRate", (hz / 1000d).ToString("0.###", settingsCulture))
            : LocalFormat("PreviewAudioSampleRate", "—");
        var channels = LocalFormat("PreviewAudioChannels", audio.Channels?.ToString(settingsCulture) ?? "—");
        var bitrate = audio.BitRate is { } bps ? (audio.BitRateEstimated ? "≈" : "") + (bps / 1000d).ToString("N0", settingsCulture)
            : "—";
        PreviewAudioDetail.Text = LocalFormat("PreviewAudioDetail", L("PreviewAudioHeading"), audio.Codec, depth, rate,
            channels, LocalFormat("PreviewAudioBitRate", bitrate));
    }

    private void RenderCounts(CaptureStats? stats)
    {
        var metrics = IsRfActive ? RfTelemetry() : LiveMediaSource is { } media ? BuildDecodedMediaTelemetry(media) :
            activeModule.BuildTelemetry(stats, displayedPreviewSkips, L, settingsCulture);
        if (!ReferenceEquals(TelemetryStrip.ItemsSource, telemetryItems))
            TelemetryStrip.ItemsSource = telemetryItems;

        while (telemetryItems.Count > metrics.Count)
            telemetryItems.RemoveAt(telemetryItems.Count - 1);

        for (var i = 0; i < metrics.Count; i++)
        {
            var metric = metrics[i];
            var (bgKey, borderKey, fgKey) = metric.Severity switch
            {
                TelemetrySeverity.Danger => ("StudioDangerSoftBrush", "StudioDangerBrush", "StudioDangerBrush"),
                TelemetrySeverity.Caution => ("StudioCautionSoftBrush", "StudioCautionBrush", "StudioCautionBrush"),
                TelemetrySeverity.Info => ("StudioInfoSoftBrush", "StudioInfoBrush", "StudioInfoBrush"),
                _ => ("StudioCardBrush", "StudioHairlineBrush", "StudioTextPrimaryBrush")
            };
            var bg = ThemeBrush(bgKey);
            var border = ThemeBrush(borderKey);
            var fg = ThemeBrush(fgKey);

            if (i < telemetryItems.Count)
                telemetryItems[i].Update(metric.Label, metric.Value, metric.Tooltip ?? "", bg, border, fg);
            else
                telemetryItems.Add(new TelemetryChipItem(metric.Label, metric.Value, metric.Tooltip ?? "", bg, border, fg));
        }
    }

    private string DescribeQcResult(QcResult result) => result.Status switch
    {
        QcStatus.Passed => LocalFormat("QcDecoded", result.DecodedVideoFrames ?? 0, result.DecodedAudioSampleFrames ?? 0),
        QcStatus.Cancelled => L("QcCancelledDetail"),
        QcStatus.ToolsUnavailable => L("QcToolsDetail"),
        _ => L("QcLogDetail")
    };

    private void RenderQcSummaryBadge()
    {
        QcResultsLabel.Text = L("QcResults");
        if (latestQcEntry is not { } entry)
            return;

        if (entry.Warnings.Count > 0)
            QcResultsLabel.Text = entry.Warnings.Count == 1 ? L("QcOneWarning") : LocalFormat("QcWarningCount", entry.Warnings.Count);
        else
            QcResultsLabel.Text = L(entry.TitleKey);

        var (toneSoft, toneSolid) = entry.Severity switch
        {
            TelemetrySeverity.Normal => ("StudioSignalLockSoftBrush", "StudioSignalLockBrush"),
            TelemetrySeverity.Caution or TelemetrySeverity.Info => ("StudioCautionSoftBrush", "StudioCautionBrush"),
            _ => ("StudioDangerSoftBrush", "StudioDangerBrush")
        };

        QcResultsDot.Fill = ThemeBrush(toneSolid);
        QcResultsButton.Background = ThemeBrush(toneSoft);
        QcResultsButton.BorderBrush = ThemeBrush(toneSolid);
    }

    private sealed class TelemetryChipItem(string label, string value, string tooltip,
        Brush chipBackground, Brush chipBorder, Brush valueForeground) : INotifyPropertyChanged
    {
        public string Label { get; private set; } = label;
        public string Value { get; private set; } = value;
        public string Tooltip { get; private set; } = tooltip;
        public Brush ChipBackground { get; private set; } = chipBackground;
        public Brush ChipBorder { get; private set; } = chipBorder;
        public Brush ValueForeground { get; private set; } = valueForeground;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(string label, string value, string tooltip, Brush bg, Brush border, Brush fg)
        {
            if (Label != label) { Label = label; Notify(nameof(Label)); }
            if (Value != value) { Value = value; Notify(nameof(Value)); }
            if (Tooltip != tooltip) { Tooltip = tooltip; Notify(nameof(Tooltip)); }
            if (!ReferenceEquals(ChipBackground, bg)) { ChipBackground = bg; Notify(nameof(ChipBackground)); }
            if (!ReferenceEquals(ChipBorder, border)) { ChipBorder = border; Notify(nameof(ChipBorder)); }
            if (!ReferenceEquals(ValueForeground, fg)) { ValueForeground = fg; Notify(nameof(ValueForeground)); }
        }

        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly record struct PreviewCaption(bool NoInput, int Width, int Height, string Format, PreviewScanMode ScanMode, long Frames);
}
