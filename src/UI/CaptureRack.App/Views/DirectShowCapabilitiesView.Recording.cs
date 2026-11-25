using CaptureRack.DirectShow.Windows.Capture;
using Microsoft.UI.Xaml;
using CaptureRack.QualityControl;
using Windows.Storage;
using Windows.System;

namespace CaptureRack.App.Views;

public sealed partial class DirectShowCapabilitiesView
{
    private Func<string> outputDirectory = () => "";
    private Func<string> recordingLabel = () => "";
    private DirectShowRecording? recording;
    private bool finalizingRecording;
    private string? savedMediaPath, savedReportPath;
    private int recordGeneration;

    internal bool IsRecording => recording is not null;
    internal event EventHandler? RecordingChanged;
    internal event EventHandler<string>? CheckRecordingRequested;

    internal void SetRecordingTarget(Func<string> directory, Func<string> label)
    {
        outputDirectory = directory;
        recordingLabel = label;
        UpdateRecordingControls();
    }

    internal void UpdateRecordingControls()
    {
        RecordButton.Content = L(IsRecording ? "StopRecording" : "StartRecording");
        CheckRecordingButton.Content = L("DsCheckRecording");
        OpenRecordingReportButton.Content = L("DsOpenRecordingReport");
        CheckRecordingButton.Visibility = !IsRecording && savedMediaPath is { } file && QcReports.SupportsFile(file)
            ? Visibility.Visible : Visibility.Collapsed;
        OpenRecordingReportButton.Visibility = !IsRecording && savedReportPath is not null
            ? Visibility.Visible : Visibility.Collapsed;
        RecordingActions.Visibility = CheckRecordingButton.Visibility == Visibility.Visible ||
            OpenRecordingReportButton.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        var video = (VideoFormatPicker.SelectedItem as FormatOption)?.Format;
        var audio = (AudioPicker.SelectedItem as FormatOption)?.Format;
        var supported = video is not null && DirectShowRecording.Supports(video, audio);
        var hasFolder = Path.IsPathFullyQualified(outputDirectory());
        RecordButton.IsEnabled = IsRecording ? !finalizingRecording :
            supported && hasFolder && previewStop is { IsCancellationRequested: false } && preview?.Received > 0;
        RecordingHint.Text = L(!supported ? "DsRecordUnsupported" : "DsRecordChooseFolder");
        RecordingHint.Visibility = video is not null && !IsRecording && (!supported || !hasFolder)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    internal async Task FinishRecordingForCloseAsync()
    {
        if (recording is not { } active)
            return;
        FinishRecording(active);
        StopPreview();
        try { await active.Completion; }
        catch (Exception) { } // RecordingClicked reports the error and retained partial file.
    }

    private void FinishRecording(DirectShowRecording active)
    {
        finalizingRecording = true;
        RecordingStatus.Text = L("DsRecordFinalizing");
        UpdateRecordingControls();
        if (preview is { } session)
            session.FinishRecording(active);
        else
            active.Complete();
    }

    private void CheckRecordingClicked(object sender, RoutedEventArgs e)
    {
        if (savedMediaPath is { } path)
            CheckRecordingRequested?.Invoke(this, path);
    }

    private async void OpenRecordingReportClicked(object sender, RoutedEventArgs e)
    {
        if (savedReportPath is not { } path)
            return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (!await Launcher.LaunchFileAsync(file))
                RecordingStatus.Text = string.Format(L("DsReportOpenFailed"), path);
        }
        catch (Exception)
        {
            RecordingStatus.Text = string.Format(L("DsReportOpenFailed"), path);
        }
    }

    private async void RecordingClicked(object sender, RoutedEventArgs e)
    {
        if (recording is { } current)
        {
            FinishRecording(current);
            return;
        }

        if (preview is not { } session || previewStop is not { IsCancellationRequested: false } ||
            (VideoFormatPicker.SelectedItem as FormatOption)?.Format is not { } video)
            return;

        var audio = (AudioPicker.SelectedItem as FormatOption)?.Format;
        try
        {
            var folder = outputDirectory();
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            {
                RecordingStatus.Text = L("DsRecordChooseFolder");
                return;
            }
            folder = Path.GetFullPath(folder);
            var prefix = RecordingFileNaming.BuildPrefix(recordingLabel(), DateTime.Now);
            var videoTag = video.IsMpeg2Video ? "MPEG2" : DirectShowPreviewVideo.Encoding(video) ?? "Video";
            var audioTag = audio is null ? "" : $"_{audio.BitsPerSample}bit-PCM";
            var filename = $"{prefix}_DirectShow_{videoTag}{audioTag}";
            var extensions = DirectShowRecording.OutputExtensions(video);
            var stem = Path.Combine(folder, filename);
            var unique = stem;
            for (var suffix = 2; extensions.Any(ext => File.Exists(unique + ext)) ||
                File.Exists(unique + ".mkv.partial"); suffix++)
                unique = $"{stem}_{suffix}";
            var path = unique + ".mkv";

            savedMediaPath = savedReportPath = null;
            var generation = ++recordGeneration;
            var dispatcher = DispatcherQueue;
            var active = session.StartRecording(path, capabilities?.Device.Name, attempt =>
            {
                var key = attempt.Status switch
                {
                    NativeContainerStatus.Trying => "DsTryingContainer",
                    NativeContainerStatus.Verifying => "DsVerifyingContainer",
                    NativeContainerStatus.Selected when attempt.Container == "MKV" => "DsKeepingMkv",
                    _ => null
                };
                if (key is not null)
                    dispatcher.TryEnqueue(() =>
                    {
                        if (IsRecording && generation == recordGeneration)
                            RecordingStatus.Text = string.Format(L(key), attempt.Container);
                    });
            });
            recording = active;
            RecordingStatus.Text = string.Format(L("DsRecordActive"), unique,
                string.Join(" → ", extensions.Select(ext => ext[1..].ToUpperInvariant())));
            UpdateRecordingControls();
            RecordingChanged?.Invoke(this, EventArgs.Empty);
            savedMediaPath = await active.Completion;
            var reportPath = NativeCaptureReport.PathFor(savedMediaPath);
            savedReportPath = active.ReportError is null && File.Exists(reportPath) ? reportPath : null;
            RecordingStatus.Text = active.ReportError is { } reportError
                ? string.Format(L("DsReportWriteFailed"), savedMediaPath, reportError)
                : string.Format(L("DsRecordSaved"), savedMediaPath);
        }
        catch (Exception error)
        {
            if (recording is { ReportError: null } failed && File.Exists(NativeCaptureReport.PathFor(failed.PartialPath)))
                savedReportPath = NativeCaptureReport.PathFor(failed.PartialPath);
            RecordingStatus.Text = recording is { } active && File.Exists(active.PartialPath)
                ? string.Format(L("DsRecordPartial"), active.PartialPath, error.Message)
                : string.Format(L("DsRecordError"), error.Message);
        }
        finally
        {
            if (recording is { } active)
                session.FinishRecording(active);
            recording = null;
            finalizingRecording = false;
            UpdateRecordingControls();
            RecordingChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
