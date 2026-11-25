using CaptureRack.DirectShow.Windows.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private Task? recordingTask;
    private string? recordingDetail;
    public bool IsRecording => recording is not null;
    public bool IsFinalizing => finalizingRecording;
    public bool SupportsRecording => ActiveVideo is { } video && DirectShowRecording.Supports(video, ActiveAudio);
    public bool CanRecord => SupportsRecording && IsCapturing && !IsStopping && HasInput;
    public string? SavedPath => savedMediaPath;
    public string? RecordingDetail => recordingDetail;

    internal void SetRecordingTarget(Func<string> directory, Func<string> label)
    {
        outputDirectory = directory;
        recordingLabel = label;
        UpdateRecordingControls();
    }

    internal void UpdateRecordingControls()
    {
        OpenRecordingReportButton.Content = L("DsOpenRecordingReport");
        OpenRecordingReportButton.Visibility = !IsRecording && savedReportPath is not null
            ? Visibility.Visible : Visibility.Collapsed;
        var video = VideoFormat;
        var supported = SupportsRecording;
        var hasFolder = Path.IsPathFullyQualified(outputDirectory());
        RecordingHint.Text = L(!supported ? "DsRecordUnsupported" : "DsRecordChooseFolder");
        RecordingHint.Visibility = video is not null && !IsRecording && (!supported || !hasFolder)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FinishRecording(DirectShowRecording active)
    {
        finalizingRecording = true;
        recordingDetail = L("DsRecordFinalizing");
        UpdateRecordingControls();
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (preview is { } session)
            session.FinishRecording(active);
        else
            active.Complete();
    }

    private async void OpenRecordingReportClicked(object sender, RoutedEventArgs e)
    {
        if (savedReportPath is not { } path)
            return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (!await Launcher.LaunchFileAsync(file))
                ShowNotice("DsFailed", InfoBarSeverity.Error, string.Format(L("DsReportOpenFailed"), path));
        }
        catch (Exception)
        {
            ShowNotice("DsFailed", InfoBarSeverity.Error, string.Format(L("DsReportOpenFailed"), path));
        }
    }

    public Task ToggleRecordingAsync()
    {
        if (recording is { } current)
        {
            if (!IsFinalizing)
                FinishRecording(current);
            return recordingTask ?? Task.CompletedTask;
        }
        return recordingTask = RecordAsync();
    }

    private async Task RecordAsync()
    {
        if (!CanRecord || preview is not { } session || ActiveVideo is not { } video)
            return;

        var audio = ActiveAudio;
        try
        {
            var folder = outputDirectory();
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            {
                recordingDetail = L("DsRecordChooseFolder");
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
                            recordingDetail = string.Format(L(key), attempt.Container);
                    });
            });
            recording = active;
            recordingDetail = string.Format(L("DsRecordActive"), unique,
                string.Join(" → ", extensions.Select(ext => ext[1..].ToUpperInvariant())));
            UpdateRecordingControls();
            RenderEvidence();
            savedMediaPath = await active.Completion;
            var reportPath = NativeCaptureReport.PathFor(savedMediaPath);
            savedReportPath = active.ReportError is null && File.Exists(reportPath) ? reportPath : null;
            recordingDetail = active.ReportError is { } reportError
                ? string.Format(L("DsReportWriteFailed"), savedMediaPath, reportError)
                : string.Format(L("DsRecordSaved"), savedMediaPath);
        }
        catch (Exception error)
        {
            if (recording is { ReportError: null } failed && File.Exists(NativeCaptureReport.PathFor(failed.PartialPath)))
                savedReportPath = NativeCaptureReport.PathFor(failed.PartialPath);
            recordingDetail = recording is { } active && File.Exists(active.PartialPath)
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
            RenderEvidence();
        }
    }
}
