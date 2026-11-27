using System.Globalization;
using CaptureRack.QualityControl;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace CaptureRack.App.Views;

public sealed partial class QcCenterView : UserControl
{
    private static readonly string[] SupportedExtensions = [".mov", ".mkv", ".ts", ".ts192", ".m2t", ".dv"];
    private Func<string, string> localize = key => key;
    private CultureInfo culture = CultureInfo.InvariantCulture;
    private QcSessionHistoryEntry? displayedEntry;
    private IReadOnlyList<QcSessionHistoryEntry> historyEntries = [];
    private QcStage? activeStage;
    private bool running;
    private bool syncingHistorySelection;
    private bool dropHighlightActive;

    public ScrollView Scroller => QcCenterScroller;
    public bool CanCheckFile
    {
        get => CheckFileButton.IsEnabled;
        set
        {
            CheckFileButton.IsEnabled = value;
            RecoverMkvButton.IsEnabled = value;
        }
    }
    public bool IsCancelVisible { get => QcCancelButton.Visibility == Visibility.Visible; set => QcCancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public bool IsCancelEnabled { get => QcCancelButton.IsEnabled; set => QcCancelButton.IsEnabled = value; }
    public QcSessionHistoryEntry? DisplayedEntry => displayedEntry;

    public event EventHandler? CheckFileRequested;
    public event EventHandler? RecoverMkvRequested;
    public event EventHandler? CancelQcRequested;
    public event EventHandler<string>? FileDropped;
    public event EventHandler<string>? OpenArtifactRequested;
    public event EventHandler<TsFileReport>? SaveTsReportRequested;
    public event EventHandler<DvFileReport>? SaveDvReportRequested;
    public event EventHandler<Guid>? HistoryEntrySelected;
    public event EventHandler? ClearHistoryRequested;

    public QcCenterView() => InitializeComponent();

    public void ApplyLanguage(Func<string, string> localizer, CultureInfo currentCulture)
    {
        localize = localizer;
        culture = currentCulture;
        QcHeading.Text = localize("QcCenterTitle");
        QcCenterSubtitle.Text = localize("QcCenterSubtitleMulti");
        CheckFileLabel.Text = localize("CheckMediaFile");
        AutomationProperties.SetName(CheckFileButton, CheckFileLabel.Text);
        RecoverMkvLabel.Text = localize("MkvRecoverButton");
        AutomationProperties.SetName(RecoverMkvButton, RecoverMkvLabel.Text);
        QcCancelButton.Content = localize("CancelQc");
        QcStep1Label.Text = localize("QcStepInspect");
        QcStep2Label.Text = localize("QcStepDecode");
        QcStep3Label.Text = localize("QcStepAnalyze");
        QcStreamProgressLabel.Text = localize("QcStreamAnalyzing");
        QcMetricVideoLabel.Text = localize("QcMetricVideo");
        QcMetricAudioLabel.Text = localize("QcMetricAudio");
        QcMetricWarningsLabel.Text = localize("QcWarningsHeading");
        QcWarningsHeading.Text = localize("QcWarningsHeading");
        QcArtifactsHeading.Text = localize("QcArtifactsHeading");
        QcFolderButton.Content = localize("QcFolder");
        TsFolderButton.Content = localize("QcFolder");
        RfRampFolderButton.Content = localize("QcFolder");
        RfRampFifoReportButton.Content = localize("RfOpenFifoReport");
        QcVideoButton.Content = localize(QcPresentationHelper.OpenMediaLabelKey(displayedEntry?.FilePath));
        TsOpenMediaButton.Content = localize(QcPresentationHelper.OpenMediaLabelKey(displayedEntry?.FilePath));
        QcLogButton.Content = localize("QcLog");
        QcRecordingReportButton.Content = localize("DsOpenRecordingReport");
        TsRecordingReportButton.Content = localize("DsOpenRecordingReport");
        QcHashButton.Content = localize("QcHash");
        QcReportButton.Content = localize("QcReport");
        SaveTsJsonButton.Content = localize(QcPresentationHelper.SaveReportLabelKey(displayedEntry?.Kind ?? QcEntryKind.TransportStream));
        TsDecodeNoteText.Text = localize("TsDecodeNote");
        QcHistoryHeading.Text = localize("QcHistoryHeading");
        QcClearHistoryButton.Content = localize("QcClearHistory");
        QcToggleInlineLogLabel.Text = QcInlineLogText.Visibility == Visibility.Visible
            ? localize("QcHideInlineLog") : localize("QcViewInlineLog");

        if (displayedEntry is null && !running)
        {
            QcFileName.Text = localize("QcDropPromptMulti");
            QcFilePathText.Text = localize("QcDropHint");
        }
        else if (displayedEntry is not null && !running)
        {
            RenderEntry(displayedEntry);
        }
        RenderHistory(historyEntries, displayedEntry?.Id);
    }

    public void BeginRunning(string path, QcEntryKind kind, string? statusLabelOverride = null)
    {
        running = true;
        activeStage = null;
        QcHistoryList.IsEnabled = false;
        QcClearHistoryButton.IsEnabled = false;
        QcFileName.Text = Path.GetFileName(path);
        QcFilePathText.Text = path;
        QcInlineLogText.Text = "";
        QcInlineLogText.Visibility = Visibility.Collapsed;
        QcOpenStatus.IsOpen = false;
        TsSaveStatus.IsOpen = false;
        QcOutcomeBanner.Visibility = Visibility.Collapsed;
        MovResultSection.Visibility = Visibility.Collapsed;
        TsResultSection.Visibility = Visibility.Collapsed;
        RfRampResultSection.Visibility = Visibility.Collapsed;
        QcStatusPill.Visibility = Visibility.Collapsed;
        QcPipelineCard.Visibility = kind == QcEntryKind.Mov ? Visibility.Visible : Visibility.Collapsed;
        var showStreamProgress = kind != QcEntryKind.Mov;
        QcStreamProgressCard.Visibility = showStreamProgress ? Visibility.Visible : Visibility.Collapsed;
        QcStreamProgressRing.IsActive = showStreamProgress;
        QcStreamProgressLabel.Text = statusLabelOverride ?? localize(kind == QcEntryKind.Dv ? "DvChecking" : "TsChecking");
        QcProgressBar.Visibility = Visibility.Visible;
        if (kind == QcEntryKind.Mov)
            UpdatePipeline(null, isRunning: true, finalStatus: null);
    }

    public void UpdateMovStage(QcStage? stage)
    {
        activeStage = stage;
        UpdatePipeline(stage, isRunning: true, finalStatus: null);
    }

    public void FinishRunning()
    {
        running = false;
        QcHistoryList.IsEnabled = true;
        QcClearHistoryButton.IsEnabled = true;
        QcProgressBar.Visibility = Visibility.Collapsed;
        QcStreamProgressCard.Visibility = Visibility.Collapsed;
        QcStreamProgressRing.IsActive = false;
    }

    public void ClearDisplayedEntry()
    {
        displayedEntry = null;
        QcFileName.Text = localize("QcDropPromptMulti");
        QcFilePathText.Text = localize("QcDropHint");
        QcInlineLogText.Text = "";
        QcInlineLogText.Visibility = Visibility.Collapsed;
        QcToggleInlineLogLabel.Text = localize("QcViewInlineLog");
        QcOutcomeBanner.Visibility = Visibility.Collapsed;
        QcStatusPill.Visibility = Visibility.Collapsed;
        QcPipelineCard.Visibility = Visibility.Collapsed;
        QcStreamProgressCard.Visibility = Visibility.Collapsed;
        QcStreamProgressRing.IsActive = false;
        MovResultSection.Visibility = Visibility.Collapsed;
        TsResultSection.Visibility = Visibility.Collapsed;
        RfRampResultSection.Visibility = Visibility.Collapsed;
        QcOpenStatus.IsOpen = false;
        TsSaveStatus.IsOpen = false;
    }

    public void RenderEntry(QcSessionHistoryEntry entry)
    {
        displayedEntry = entry;
        QcFileName.Text = entry.FileName;
        QcFilePathText.Text = entry.FilePath;
        QcOpenStatus.IsOpen = false;
        TsSaveStatus.IsOpen = false;
        QcInlineLogText.Visibility = Visibility.Collapsed;
        QcToggleInlineLogLabel.Text = localize("QcViewInlineLog");

        var (toneSoft, toneSolid, glyph) = entry.Severity switch
        {
            TelemetrySeverity.Normal => ("StudioSignalLockSoftBrush", "StudioSignalLockBrush", "\uE73E"),
            TelemetrySeverity.Info => ("StudioInfoSoftBrush", "StudioInfoBrush", "\uE946"),
            TelemetrySeverity.Caution => ("StudioCautionSoftBrush", "StudioCautionBrush", "\uE7BA"),
            _ => ("StudioDangerSoftBrush", "StudioDangerBrush", "\uE783")
        };

        QcOutcomeBanner.Visibility = Visibility.Visible;
        QcOutcomeBanner.Background = StudioBrushes.Resolve(this, toneSoft);
        QcOutcomeBanner.BorderBrush = StudioBrushes.Resolve(this, toneSolid);
        QcOutcomeIcon.Foreground = StudioBrushes.Resolve(this, toneSolid);
        QcOutcomeIcon.Glyph = glyph;
        QcOutcome.Text = localize(entry.TitleKey);
        QcSummary.Text = entry.Summary;

        QcStatusPill.Visibility = Visibility.Visible;
        QcStatusPill.Background = StudioBrushes.Resolve(this, toneSoft);
        QcStatusPillText.Text = localize(entry.TitleKey);
        QcStatusPillText.Foreground = StudioBrushes.Resolve(this, toneSolid);

        QcVideoButton.Content = localize(QcPresentationHelper.OpenMediaLabelKey(entry.FilePath));
        TsOpenMediaButton.Content = localize(QcPresentationHelper.OpenMediaLabelKey(entry.FilePath));
        SaveTsJsonButton.Content = localize(QcPresentationHelper.SaveReportLabelKey(entry.Kind));
        QcStreamProgressCard.Visibility = Visibility.Collapsed;
        QcStreamProgressRing.IsActive = false;
        var hasRecordingReport = File.Exists(NativeCaptureReport.PathFor(entry.FilePath));

        if (entry.Kind == QcEntryKind.Mov)
        {
            RfRampResultSection.Visibility = Visibility.Collapsed;
            QcPipelineCard.Visibility = Visibility.Visible;
            UpdatePipeline(entry.LastStage, isRunning: false, finalStatus: entry.MovResult?.Status ?? QcStatus.Failed);
            MovResultSection.Visibility = Visibility.Visible;
            TsResultSection.Visibility = Visibility.Collapsed;

            QcMetricVideoValue.Text = entry.MovResult?.DecodedVideoFrames?.ToString("N0", culture) ?? "—";
            QcMetricAudioValue.Text = entry.MovResult?.DecodedAudioSampleFrames?.ToString("N0", culture) ?? "—";
            QcMetricWarningsValue.Text = entry.Warnings.Count.ToString("N0", culture);
            QcMetricWarningsValue.Foreground = entry.Warnings.Count > 0
                ? StudioBrushes.Resolve(this, "StudioCautionBrush")
                : StudioBrushes.Resolve(this, "StudioTextPrimaryBrush");

            QcWarningsList.ItemsSource = entry.Warnings;
            QcWarningsPanel.Visibility = entry.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            QcLogButton.Visibility = entry.MovResult?.CaptureLog is null ? Visibility.Collapsed : Visibility.Visible;
            QcRecordingReportButton.Visibility = hasRecordingReport ? Visibility.Visible : Visibility.Collapsed;
            QcToggleInlineLogButton.Visibility = entry.MovResult?.CaptureLog is null ? Visibility.Collapsed : Visibility.Visible;
            QcHashButton.Visibility = entry.MovResult?.FrameMd5 is null ? Visibility.Collapsed : Visibility.Visible;
            QcReportButton.Visibility = entry.MovResult?.QcToolsReport is null ? Visibility.Collapsed : Visibility.Visible;
        }
        else if (entry.TsReport is { } ts)
        {
            RfRampResultSection.Visibility = Visibility.Collapsed;
            QcPipelineCard.Visibility = Visibility.Collapsed;
            MovResultSection.Visibility = Visibility.Collapsed;
            TsResultSection.Visibility = Visibility.Visible;
            TsRecordingReportButton.Visibility = hasRecordingReport ? Visibility.Visible : Visibility.Collapsed;
            TsDecodeNoteText.Text = localize("TsDecodeNote");

            var stats = ts.Analysis;
            TsFormatText.Text = string.Format(culture, "{0} · {1:N0} {2} · {3:N0} B",
                ts.Format.ToUpperInvariant(), stats.Packets, localize("TsPacketsUnit"), stats.InputBytes);
            TsMetricsList.ItemsSource = new TsMetricRow[]
            {
                new(localize("TsMetricSync"), stats.SyncErrors.ToString("N0", culture)),
                new(localize("TsMetricMalformed"), stats.MalformedPackets.ToString("N0", culture)),
                new(localize("TsMetricTei"), stats.Tei.ToString("N0", culture)),
                new(localize("TsMetricContinuity"), stats.ContinuityErrors.ToString("N0", culture)),
                new(localize("TsMetricTrailing"), stats.TrailingBytes.ToString("N0", culture)),
                new(localize("TsMetricPcr"), stats.PcrCount.ToString("N0", culture)),
                new(localize("TsMetricBackwardPcr"), stats.PcrBackwards.ToString("N0", culture)),
                new(localize("TsMetricPts"), stats.PtsCount.ToString("N0", culture))
            };

            var firstErrors = new (string Label, long? Index)[]
            {
                (localize("TsMetricSync"), stats.FirstSyncErrorPacket),
                (localize("TsMetricMalformed"), stats.FirstMalformedPacket),
                (localize("TsMetricTei"), stats.FirstTeiPacket),
                (localize("TsMetricContinuity"), stats.FirstContinuityErrorPacket),
                (localize("TsMetricBackwardPcr"), stats.FirstPcrBackwardsPacket)
            }.Where(item => item.Index.HasValue)
             .Select(item => string.Format(culture, "{0} · #{1:N0}", item.Label, item.Index)).ToArray();

            TsFirstErrorsText.Text = firstErrors.Length > 0
                ? $"{localize("TsFirstAffectedHeading")}\n{string.Join("\n", firstErrors)}" : "";
            TsFirstErrorsCard.Visibility = firstErrors.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (entry.DvReport is { } dv)
        {
            RfRampResultSection.Visibility = Visibility.Collapsed;
            QcPipelineCard.Visibility = MovResultSection.Visibility = TsFirstErrorsCard.Visibility = Visibility.Collapsed;
            TsResultSection.Visibility = Visibility.Visible;
            TsRecordingReportButton.Visibility = hasRecordingReport ? Visibility.Visible : Visibility.Collapsed;
            TsDecodeNoteText.Text = localize(dv.Success ? "DvCheckPassedDetail" : "DvCheckFailedDetail");
            var stats = dv.Analysis;
            var format = stats.LastFrame is { } frame ? $"{frame.Encoding.DisplayName()} · {frame.Width} × {frame.Height} · {frame.FramesPerSecond:0.###} fps" : "DV";
            TsFormatText.Text = $"{format} · {stats.InputBytes:N0} B";
            TsMetricsList.ItemsSource = new TsMetricRow[]
            {
                new(localize("DvInfoFrames"), stats.Frames.ToString("N0", culture)),
                new(localize("DvInfoInvalidFrames"), stats.InvalidFrames.ToString("N0", culture)),
                new(localize("TsMetricTrailing"), stats.TrailingBytes.ToString("N0", culture)),
                new(localize("DvInfoFormatChanges"), stats.FormatChanges.ToString("N0", culture)),
                new(localize("DvInfoTimecode"), $"{stats.FirstFrame?.Timecode ?? "—"} → {stats.LastFrame?.Timecode ?? "—"}"),
                new(localize("DvInfoRecordingDate"), stats.FirstFrame?.RecordingDate ?? "—")
            };
        }
        else if (entry.Kind == QcEntryKind.RfRamp)
        {
            QcPipelineCard.Visibility = MovResultSection.Visibility = TsResultSection.Visibility = Visibility.Collapsed;
            RfRampResultSection.Visibility = Visibility.Visible;
            var ext = Path.GetExtension(entry.FileName).TrimStart('.').ToUpperInvariant();
            RfRampFormatText.Text = string.IsNullOrEmpty(ext) ? "RF" : $"{ext} · {entry.Summary}";
            var fifoPath = RfFifoReportPathFor(entry.FilePath);
            RfRampFifoReportButton.Visibility = File.Exists(fifoPath) ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            QcPipelineCard.Visibility = MovResultSection.Visibility = TsResultSection.Visibility = RfRampResultSection.Visibility = Visibility.Collapsed;
        }
    }

    internal static string RfFifoReportPathFor(string path)
    {
        var basePath = path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
            ? path[..^".partial".Length]
            : path;
        return basePath + ".fifo.json";
    }

    public void RenderHistory(IReadOnlyList<QcSessionHistoryEntry> entries, Guid? selectedId)
    {
        historyEntries = entries;
        var hasHistory = entries.Count > 0;
        QcHistoryPanel.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        UpdateHistoryResponsiveLayout();
        if (!hasHistory)
        {
            QcHistoryList.ItemsSource = null;
            return;
        }

        syncingHistorySelection = true;
        try
        {
            var rows = entries.Select(item => new HistoryRow(
                item.Id,
                item.FileName,
                $"{item.CheckedAt.ToLocalTime():HH:mm:ss} · {localize(item.TitleKey)}",
                item.Kind == QcEntryKind.TransportStream ? "TS" : Path.GetExtension(item.FileName).TrimStart('.').ToUpperInvariant(),
                StudioBrushes.Resolve(this, item.Severity switch
                {
                    TelemetrySeverity.Normal => "StudioSignalLockBrush",
                    TelemetrySeverity.Info => "StudioInfoBrush",
                    TelemetrySeverity.Caution => "StudioCautionBrush",
                    _ => "StudioDangerBrush"
                }))).ToArray();
            QcHistoryList.ItemsSource = rows;
            QcHistoryList.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedId);
        }
        finally
        {
            syncingHistorySelection = false;
        }
    }

    public void ShowArtifactOpenError(string message)
    {
        QcOpenStatus.Message = message;
        QcOpenStatus.IsOpen = true;
    }

    public void ClearArtifactOpenError() => QcOpenStatus.IsOpen = false;

    public void ShowTsSaveStatus(string title, string message, InfoBarSeverity severity)
    {
        TsSaveStatus.Title = title;
        TsSaveStatus.Message = message;
        TsSaveStatus.Severity = severity;
        TsSaveStatus.IsOpen = true;
    }

    public void RefreshTheme()
    {
        if (dropHighlightActive)
            SetDropHighlight(true);
        if (running)
        {
            if (QcPipelineCard.Visibility == Visibility.Visible)
                UpdatePipeline(activeStage, isRunning: true, finalStatus: null);
        }
        else if (displayedEntry is not null)
        {
            RenderEntry(displayedEntry);
        }
        RenderHistory(historyEntries, displayedEntry?.Id);
    }

    private void UpdatePipeline(QcStage? stage, bool isRunning, QcStatus? finalStatus)
    {
        var stepIndex = stage switch
        {
            QcStage.Inspecting => 1,
            QcStage.Decoding => 2,
            QcStage.Analyzing => 3,
            _ => 0
        };

        void StyleStep(Border card, FontIcon icon, int index)
        {
            var completed = finalStatus == QcStatus.Passed || (!isRunning && index < stepIndex) || (isRunning && index < stepIndex);
            var active = isRunning && index == stepIndex;
            var failed = !isRunning && finalStatus is QcStatus.Failed or QcStatus.ToolsUnavailable && index == Math.Max(1, stepIndex);

            if (completed)
            {
                card.Background = StudioBrushes.Resolve(this, "StudioSignalLockSoftBrush");
                card.BorderBrush = StudioBrushes.Resolve(this, "StudioSignalLockBrush");
                icon.Glyph = "\uE73E";
                icon.Foreground = StudioBrushes.Resolve(this, "StudioSignalLockBrush");
            }
            else if (active)
            {
                card.Background = StudioBrushes.Resolve(this, "StudioInfoSoftBrush");
                card.BorderBrush = StudioBrushes.Resolve(this, "StudioInfoBrush");
                icon.Glyph = "\uE895";
                icon.Foreground = StudioBrushes.Resolve(this, "StudioInfoBrush");
            }
            else if (failed)
            {
                card.Background = StudioBrushes.Resolve(this, "StudioDangerSoftBrush");
                card.BorderBrush = StudioBrushes.Resolve(this, "StudioDangerBrush");
                icon.Glyph = "\uE783";
                icon.Foreground = StudioBrushes.Resolve(this, "StudioDangerBrush");
            }
            else
            {
                card.Background = StudioBrushes.Resolve(this, "StudioPanelSecondaryBrush");
                card.BorderBrush = StudioBrushes.Resolve(this, "StudioHairlineBrush");
                icon.Glyph = "\uE91F";
                icon.Foreground = StudioBrushes.Resolve(this, "StudioTextMutedBrush");
            }
        }

        StyleStep(QcStep1Card, QcStep1Icon, 1);
        StyleStep(QcStep2Card, QcStep2Icon, 2);
        StyleStep(QcStep3Card, QcStep3Icon, 3);
    }

    private void CheckFileButton_Click(object sender, RoutedEventArgs e) => CheckFileRequested?.Invoke(this, EventArgs.Empty);

    private void RecoverMkvButton_Click(object sender, RoutedEventArgs e) => RecoverMkvRequested?.Invoke(this, EventArgs.Empty);

    private void CancelQcButton_Click(object sender, RoutedEventArgs e) => CancelQcRequested?.Invoke(this, EventArgs.Empty);

    private void ArtifactButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
            OpenArtifactRequested?.Invoke(this, tag);
    }

    private void SaveTsJsonButton_Click(object sender, RoutedEventArgs e)
    {
        if (displayedEntry?.TsReport is { } report)
            SaveTsReportRequested?.Invoke(this, report);
        else if (displayedEntry?.DvReport is { } dv)
            SaveDvReportRequested?.Invoke(this, dv);
    }

    private void QcHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncingHistorySelection && QcHistoryList.SelectedItem is HistoryRow row)
            HistoryEntrySelected?.Invoke(this, row.Id);
    }

    private async void ToggleInlineLog_Click(object sender, RoutedEventArgs e)
    {
        if (running || displayedEntry is not { MovResult.CaptureLog: { } logPath } entry)
            return;

        if (QcInlineLogText.Visibility == Visibility.Visible)
        {
            QcInlineLogText.Visibility = Visibility.Collapsed;
            QcToggleInlineLogLabel.Text = localize("QcViewInlineLog");
            return;
        }

        var entryId = entry.Id;
        try
        {
            var text = await File.ReadAllTextAsync(logPath);
            if (running || displayedEntry?.Id != entryId)
                return;
            QcInlineLogText.Text = text;
            QcInlineLogText.Visibility = Visibility.Visible;
            QcToggleInlineLogLabel.Text = localize("QcHideInlineLog");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!running && displayedEntry?.Id == entryId)
                ShowArtifactOpenError(ex.Message);
        }
    }

    private void QcRootGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateHistoryResponsiveLayout();

    private void UpdateHistoryResponsiveLayout()
    {
        var hasHistory = historyEntries.Count > 0;
        if (!hasHistory)
        {
            HistoryColumn.Width = new GridLength(0);
            return;
        }

        var narrow = QcRootGrid.ActualWidth > 0 && QcRootGrid.ActualWidth < 860;
        if (narrow)
        {
            HistoryColumn.Width = new GridLength(0);
            Grid.SetColumn(QcHistoryPanel, 0);
            Grid.SetRow(QcHistoryPanel, 1);
            QcHistoryList.MaxHeight = 240;
        }
        else
        {
            HistoryColumn.Width = new GridLength(250);
            Grid.SetColumn(QcHistoryPanel, 1);
            Grid.SetRow(QcHistoryPanel, 0);
            QcHistoryList.MaxHeight = 520;
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!running)
            ClearHistoryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetDropHighlight(bool active)
    {
        dropHighlightActive = active;
        if (active)
        {
            QcFileCard.BorderBrush = StudioBrushes.Resolve(this, "StudioAccentBrush");
            QcFileCard.Background = StudioBrushes.Resolve(this, "StudioAccentSoftBrush");
            if (displayedEntry is null && !running)
                QcFilePathText.Text = localize("QcDropReleaseHint");
        }
        else
        {
            QcFileCard.ClearValue(Border.BorderBrushProperty);
            QcFileCard.ClearValue(Border.BackgroundProperty);
            if (displayedEntry is null && !running)
                QcFilePathText.Text = localize("QcDropHint");
        }
    }

    private void QcDropZone_DragOver(object sender, DragEventArgs e)
    {
        var accept = CheckFileButton.IsEnabled && e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = accept ? DataPackageOperation.Copy : DataPackageOperation.None;
        SetDropHighlight(accept);
    }

    private void QcDropZone_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    private async void QcDropZone_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (!CheckFileButton.IsEnabled || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var media = items.OfType<StorageFile>().FirstOrDefault(item =>
                SupportedExtensions.Contains(Path.GetExtension(item.Path), StringComparer.OrdinalIgnoreCase));
            if (media is not null)
                FileDropped?.Invoke(this, media.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowArtifactOpenError(ex.Message);
        }
    }

    private sealed record TsMetricRow(string Label, string Value);
    private sealed record HistoryRow(Guid Id, string FileName, string Subtitle, string KindTag, Brush StatusBrush);
}
