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
    private static readonly string[] SupportedExtensions = [".mov", ".ts", ".ts192", ".m2t"];
    private Func<string, string> localize = key => key;
    private CultureInfo culture = CultureInfo.InvariantCulture;
    private QcSessionHistoryEntry? displayedEntry;
    private IReadOnlyList<QcSessionHistoryEntry> historyEntries = [];
    private QcStage? activeStage;
    private bool running;
    private bool syncingHistorySelection;

    public ScrollView Scroller => QcCenterScroller;
    public bool CanCheckFile { get => CheckFileButton.IsEnabled; set => CheckFileButton.IsEnabled = value; }
    public bool IsCancelVisible { get => QcCancelButton.Visibility == Visibility.Visible; set => QcCancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public bool IsCancelEnabled { get => QcCancelButton.IsEnabled; set => QcCancelButton.IsEnabled = value; }
    public QcSessionHistoryEntry? DisplayedEntry => displayedEntry;

    public event EventHandler? CheckFileRequested;
    public event EventHandler? CancelQcRequested;
    public event EventHandler<string>? FileDropped;
    public event EventHandler<string>? OpenArtifactRequested;
    public event EventHandler<TsFileReport>? SaveTsReportRequested;
    public event EventHandler<Guid>? HistoryEntrySelected;

    public QcCenterView() => InitializeComponent();

    public void ApplyLanguage(Func<string, string> localizer, CultureInfo currentCulture)
    {
        localize = localizer;
        culture = currentCulture;
        QcHeading.Text = localize("QcCenterTitle");
        QcCenterSubtitle.Text = localize("QcCenterSubtitleMulti");
        CheckFileLabel.Text = localize("CheckMediaFile");
        AutomationProperties.SetName(CheckFileButton, CheckFileLabel.Text);
        QcCancelButton.Content = localize("CancelQc");
        QcStep1Label.Text = localize("QcStepInspect");
        QcStep2Label.Text = localize("QcStepDecode");
        QcStep3Label.Text = localize("QcStepAnalyze");
        QcMetricVideoLabel.Text = localize("QcMetricVideo");
        QcMetricAudioLabel.Text = localize("QcMetricAudio");
        QcMetricWarningsLabel.Text = localize("QcWarningsHeading");
        QcWarningsHeading.Text = localize("QcWarningsHeading");
        QcArtifactsHeading.Text = localize("QcArtifactsHeading");
        QcFolderButton.Content = localize("QcFolder");
        QcVideoButton.Content = localize("OpenRecordedMov");
        QcLogButton.Content = localize("QcLog");
        QcHashButton.Content = localize("QcHash");
        QcReportButton.Content = localize("QcReport");
        SaveTsJsonButton.Content = localize("SaveTsJson");
        TsDecodeNoteText.Text = localize("TsDecodeNote");
        QcHistoryHeading.Text = localize("QcHistoryHeading");
        QcToggleInlineLogLabel.Text = QcInlineLogText.Visibility == Visibility.Visible
            ? localize("QcHideInlineLog") : localize("QcViewInlineLog");

        if (displayedEntry is null && !running)
        {
            QcFileName.Text = localize("QcDropPromptMulti");
            QcFilePathText.Text = localize("QcDropHint");
        }
        else if (displayedEntry is not null)
        {
            RenderEntry(displayedEntry);
        }
        RenderHistory(historyEntries, displayedEntry?.Id);
    }

    public void BeginRunning(string path, QcEntryKind kind)
    {
        running = true;
        activeStage = null;
        QcHistoryList.IsEnabled = false;
        QcFileName.Text = Path.GetFileName(path);
        QcFilePathText.Text = path;
        QcInlineLogText.Text = "";
        QcInlineLogText.Visibility = Visibility.Collapsed;
        QcOpenStatus.IsOpen = false;
        TsSaveStatus.IsOpen = false;
        QcOutcomeBanner.Visibility = Visibility.Collapsed;
        MovResultSection.Visibility = Visibility.Collapsed;
        TsResultSection.Visibility = Visibility.Collapsed;
        QcStatusPill.Visibility = Visibility.Collapsed;
        QcPipelineCard.Visibility = kind == QcEntryKind.Mov ? Visibility.Visible : Visibility.Collapsed;
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
        QcProgressBar.Visibility = Visibility.Collapsed;
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
            TelemetrySeverity.Caution or TelemetrySeverity.Info => ("StudioCautionSoftBrush", "StudioCautionBrush", "\uE7BA"),
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

        if (entry.Kind == QcEntryKind.Mov)
        {
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
            QcToggleInlineLogButton.Visibility = entry.MovResult?.CaptureLog is null ? Visibility.Collapsed : Visibility.Visible;
            QcHashButton.Visibility = entry.MovResult?.FrameMd5 is null ? Visibility.Collapsed : Visibility.Visible;
            QcReportButton.Visibility = entry.MovResult?.QcToolsReport is null ? Visibility.Collapsed : Visibility.Visible;
        }
        else if (entry.TsReport is { } ts)
        {
            QcPipelineCard.Visibility = Visibility.Collapsed;
            MovResultSection.Visibility = Visibility.Collapsed;
            TsResultSection.Visibility = Visibility.Visible;

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
    }

    public void RenderHistory(IReadOnlyList<QcSessionHistoryEntry> entries, Guid? selectedId)
    {
        historyEntries = entries;
        var hasHistory = entries.Count > 0;
        QcHistoryPanel.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        HistoryColumn.Width = hasHistory ? new GridLength(250) : new GridLength(0);
        if (!hasHistory)
            return;

        syncingHistorySelection = true;
        try
        {
            var rows = entries.Select(item => new HistoryRow(
                item.Id,
                item.FileName,
                $"{item.CheckedAt.ToLocalTime():HH:mm:ss} · {localize(item.TitleKey)}",
                item.Kind == QcEntryKind.Mov ? "MOV" : "TS",
                StudioBrushes.Resolve(this, item.Severity switch
                {
                    TelemetrySeverity.Normal => "StudioSignalLockBrush",
                    TelemetrySeverity.Caution or TelemetrySeverity.Info => "StudioCautionBrush",
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
        if (displayedEntry is not null)
            RenderEntry(displayedEntry);
        else if (running && QcPipelineCard.Visibility == Visibility.Visible)
            UpdatePipeline(activeStage, isRunning: true, finalStatus: null);
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
    }

    private void QcHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncingHistorySelection && QcHistoryList.SelectedItem is HistoryRow row)
            HistoryEntrySelected?.Invoke(this, row.Id);
    }

    private async void ToggleInlineLog_Click(object sender, RoutedEventArgs e)
    {
        if (displayedEntry?.MovResult?.CaptureLog is not { } logPath)
            return;

        if (QcInlineLogText.Visibility == Visibility.Visible)
        {
            QcInlineLogText.Visibility = Visibility.Collapsed;
            QcToggleInlineLogLabel.Text = localize("QcViewInlineLog");
            return;
        }

        try
        {
            QcInlineLogText.Text = await File.ReadAllTextAsync(logPath);
            QcInlineLogText.Visibility = Visibility.Visible;
            QcToggleInlineLogLabel.Text = localize("QcHideInlineLog");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowArtifactOpenError(ex.Message);
        }
    }

    private void QcDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = CheckFileButton.IsEnabled && e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy : DataPackageOperation.None;
    }

    private async void QcDropZone_Drop(object sender, DragEventArgs e)
    {
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
