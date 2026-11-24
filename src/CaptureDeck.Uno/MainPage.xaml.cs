using System.ComponentModel;
using System.Diagnostics;
using CaptureDeck.QualityControl;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace CaptureDeck.Uno;

public sealed partial class MainPage : Page
{
    private static readonly string[] SupportedExtensions = [".mov", ".ts", ".ts192", ".m2t"];
    private CancellationTokenSource? runningQc;
    private QcResult? lastResult;
    private TsFileReport? lastTsReport;

    public MainPage() => InitializeComponent();

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        OpenFileButton.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker();
            // macOS pickers use Uniform Type Identifiers. Let the app validate
            // extensions such as .ts192, which lack a standard registration.
            foreach (var extension in OperatingSystem.IsMacOS() ? ["*"] : SupportedExtensions)
                picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                await CheckFileAsync(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or Win32Exception or InvalidDataException)
        {
            ShowStatus("Could not check file", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            OpenFileButton.IsEnabled = true;
        }
    }

    private async Task CheckFileAsync(string path)
    {
        var extension = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a .mov, .ts, .ts192, or .m2t file.");
        var isMov = string.Equals(extension, ".mov", StringComparison.OrdinalIgnoreCase);
        using var cancellation = new CancellationTokenSource();
        runningQc = cancellation;
        lastResult = null;
        lastTsReport = null;
        InputName.Text = Path.GetFileName(path);
        InputPath.Text = path;
        InputPath.Visibility = Visibility.Visible;
        MovResults.Visibility = Visibility.Collapsed;
        TsResults.Visibility = Visibility.Collapsed;
        TsSaveStatus.IsOpen = false;
        ReportText.Text = "";
        ReportText.Visibility = Visibility.Collapsed;
        ViewLogButton.Content = "View QC log";
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Visible;
        ShowStatus(isMov ? "Checking MOV" : "Checking transport stream", "Starting offline QC.");

        try
        {
            if (isMov)
                await CheckMovAsync(path, cancellation);
            else
                ShowTsReport(await Task.Run(() => TsFile.Analyze(path, cancellation: cancellation.Token), cancellation.Token));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowStatus("QC cancelled", "Open the file again to restart the check.", InfoBarSeverity.Warning);
        }
        finally
        {
            runningQc = null;
            Progress.IsIndeterminate = false;
            Progress.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private async Task CheckMovAsync(string path, CancellationTokenSource cancellation)
    {
        var dispatcher = DispatcherQueue;
        var result = await Task.Run(() => QcReports.RunAsync(path, cancellation: cancellation.Token,
            progress: message => dispatcher.TryEnqueue(() =>
            {
                if (ReferenceEquals(runningQc, cancellation) && !cancellation.IsCancellationRequested)
                    ShowStatus("Checking MOV", message);
            })));

        lastResult = result;
        Warnings.ItemsSource = result.Warnings;
        Warnings.Visibility = VisibleWhen(result.Warnings.Count > 0);
        LogName.Text = Path.GetFileName(result.CaptureLog);
        SetReportName(FrameMd5Name, result.FrameMd5);
        SetReportName(QctoolsName, result.QcToolsReport);
        MovResults.Visibility = Visibility.Visible;
        var (title, severity) = result.Status switch
        {
            QcStatus.Passed => ("QC passed", InfoBarSeverity.Success),
            QcStatus.Cancelled => ("QC cancelled", InfoBarSeverity.Warning),
            QcStatus.ToolsUnavailable => ("FFmpeg tools unavailable", InfoBarSeverity.Warning),
            _ => ("QC failed", InfoBarSeverity.Error)
        };
        ShowStatus(title, result.Summary, severity);
    }

    private sealed record Metric(string Label, string Value);

    private void ShowTsReport(TsFileReport report)
    {
        lastTsReport = report;
        var stats = report.Analysis;
        TsFormat.Text = $"{report.Format.ToUpperInvariant()} · {stats.Packets:N0} packets · {stats.InputBytes:N0} bytes";
        Metric[] metrics =
        [
            new("Sync errors", $"{stats.SyncErrors:N0}"),
            new("Malformed packets", $"{stats.MalformedPackets:N0}"),
            new("Transport error indicator (TEI)", $"{stats.Tei:N0}"),
            new("Continuity errors", $"{stats.ContinuityErrors:N0}"),
            new("Trailing bytes", $"{stats.TrailingBytes:N0}"),
            new("PCR timestamps", $"{stats.PcrCount:N0}"),
            new("Backward PCR timestamps", $"{stats.PcrBackwards:N0}"),
            new("PTS timestamps", $"{stats.PtsCount:N0}")
        ];
        TsMetrics.ItemsSource = metrics;
        (string Label, long? Index)[] firstErrors =
        [
            ("Sync", stats.FirstSyncErrorPacket),
            ("Malformed", stats.FirstMalformedPacket),
            ("TEI", stats.FirstTeiPacket),
            ("Continuity", stats.FirstContinuityErrorPacket),
            ("Backward PCR", stats.FirstPcrBackwardsPacket)
        ];
        var positions = firstErrors.Where(error => error.Index.HasValue)
            .Select(error => $"{error.Label} · packet {error.Index:N0}").ToArray();
        TsFirstErrors.Text = $"First affected packets (zero-based)\n{string.Join("\n", positions)}";
        TsFirstErrors.Visibility = VisibleWhen(positions.Length > 0);
        TsResults.Visibility = Visibility.Visible;

        if (!report.Success)
            ShowStatus("TS structure check failed", "Review the packet errors or trailing bytes below.", InfoBarSeverity.Error);
        else if (stats.HasTransportWarnings)
            ShowStatus("TS structure passed with warnings", "Review the transport warnings below.", InfoBarSeverity.Warning);
        else
            ShowStatus("TS structure passed", "No transport errors detected.", InfoBarSeverity.Success);
    }

    private async void SaveTs_Click(object sender, RoutedEventArgs e)
    {
        if (lastTsReport is not { } report)
            return;

        SaveTsButton.IsEnabled = false;
        OpenFileButton.IsEnabled = false;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = Path.GetFileNameWithoutExtension(report.InputPath) + ".ts-qc.json"
            };
            picker.FileTypeChoices.Add("JSON report", [".json"]);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;
            if (string.Equals(Path.GetFullPath(file.Path), report.InputPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a report filename different from the source file.");

            await FileIO.WriteTextAsync(file, report.ToJson(indented: true));
            ShowTsSaveStatus("JSON saved", file.Path, InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or Win32Exception)
        {
            ShowTsSaveStatus("Could not save JSON", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SaveTsButton.IsEnabled = true;
            OpenFileButton.IsEnabled = true;
        }
    }

    private void ShowTsSaveStatus(string title, string message, InfoBarSeverity severity)
    {
        TsSaveStatus.Title = title;
        TsSaveStatus.Message = message;
        TsSaveStatus.Severity = severity;
        TsSaveStatus.IsOpen = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        runningQc?.Cancel();
        CancelButton.IsEnabled = false;
        ShowStatus("Cancelling QC", "Stopping the current check.");
    }

    private async void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        if (lastResult is not { } result)
            return;

        if (ReportText.Visibility == Visibility.Visible)
        {
            ReportText.Visibility = Visibility.Collapsed;
            ViewLogButton.Content = "View QC log";
            return;
        }

        ViewLogButton.IsEnabled = false;
        try
        {
            var text = await File.ReadAllTextAsync(result.CaptureLog);
            if (ReferenceEquals(lastResult, result))
            {
                ReportText.Text = text;
                ReportText.Visibility = Visibility.Visible;
                ViewLogButton.Content = "Hide QC log";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(lastResult, result))
                ShowStatus("Could not open QC log", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            ViewLogButton.IsEnabled = true;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (lastResult is not { } result)
            return;

        var folder = Path.GetDirectoryName(result.CaptureLog)!;
        try
        {
            var startInfo = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("/usr/bin/open") { ArgumentList = { folder } }
                : new ProcessStartInfo(folder) { UseShellExecute = true };
            using var process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            ShowStatus("Could not open report folder", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Status.Title = title;
        Status.Message = message;
        Status.Severity = severity;
    }

    private static void SetReportName(TextBlock label, string? path)
    {
        label.Text = Path.GetFileName(path) ?? "";
        label.Visibility = VisibleWhen(path is not null);
    }

    private static Visibility VisibleWhen(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
}
