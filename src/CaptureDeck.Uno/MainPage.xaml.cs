using System.ComponentModel;
using System.Diagnostics;
using CaptureDeck.QualityControl;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace CaptureDeck.Uno;

public sealed partial class MainPage : Page
{
    private CancellationTokenSource? runningQc;
    private QcResult? lastResult;

    public MainPage() => InitializeComponent();

    private async void OpenMov_Click(object sender, RoutedEventArgs e)
    {
        OpenMovButton.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".mov");
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                await CheckMovAsync(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or Win32Exception)
        {
            ShowStatus("Could not check MOV", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            OpenMovButton.IsEnabled = true;
        }
    }

    private async Task CheckMovAsync(string path)
    {
        using var cancellation = new CancellationTokenSource();
        runningQc = cancellation;
        lastResult = null;
        InputName.Text = Path.GetFileName(path);
        InputPath.Text = path;
        InputPath.Visibility = Visibility.Visible;
        Results.Visibility = Visibility.Collapsed;
        ReportText.Text = "";
        ReportText.Visibility = Visibility.Collapsed;
        ViewLogButton.Content = "View QC log";
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Visible;
        ShowStatus("Checking MOV", "Starting offline QC.");
        var dispatcher = DispatcherQueue;

        try
        {
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
            Results.Visibility = Visibility.Visible;
            var (title, severity) = result.Status switch
            {
                QcStatus.Passed => ("QC passed", InfoBarSeverity.Success),
                QcStatus.Cancelled => ("QC cancelled", InfoBarSeverity.Warning),
                QcStatus.ToolsUnavailable => ("FFmpeg tools unavailable", InfoBarSeverity.Warning),
                _ => ("QC failed", InfoBarSeverity.Error)
            };
            ShowStatus(title, result.Summary, severity);
        }
        finally
        {
            runningQc = null;
            Progress.IsIndeterminate = false;
            Progress.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        runningQc?.Cancel();
        CancelButton.IsEnabled = false;
        ShowStatus("Cancelling QC", "Finishing the report.");
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
