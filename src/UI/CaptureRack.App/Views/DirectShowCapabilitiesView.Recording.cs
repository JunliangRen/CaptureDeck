using CaptureRack.Core;
using CaptureRack.DirectShow.Windows.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CaptureRack.QualityControl;
using CaptureRack.Recording;
using Windows.Storage;
using Windows.System;

namespace CaptureRack.App.Views;

public sealed partial class DirectShowCapabilitiesView
{
    private Func<string> outputDirectory = () => "";
    private Func<string> recordingLabel = () => "";
    private Func<RecordingEncoding> recordingEncoding = () => new();
    private Func<RecordingOutput> recordingOutput = () => RecordingOutput.Native;
    private DirectShowRecording? recording;
    private long? lastRejectedPackets;
    private CaptureOperation? recordingOperation;
    private string? savedMediaPath, savedReportPath;
    private int recordGeneration;

    private string? recordingDetail;
    public bool IsRecording => recording is not null;
    public bool IsFinalizing => recordingOperation?.IsStopping == true;
    public bool SupportsRecording => ActiveVideo is { } video && DirectShowRecording.Supports(video, ActiveAudio);
    public bool CanRecord => !closing && operation is null && SupportsRecording && IsCapturing && !IsStopping && HasInput;
    public string? SavedPath => savedMediaPath;
    public string? RecordingReportPath => savedReportPath;
    public string? RecordingDetail => recordingDetail;

    internal void SetRecordingTarget(Func<string> directory, Func<string> label,
        Func<RecordingEncoding> encoding, Func<RecordingOutput> output)
    {
        outputDirectory = directory;
        recordingLabel = label;
        recordingEncoding = encoding;
        recordingOutput = output;
        UpdateRecordingControls();
    }

    internal void UpdateRecordingControls()
    {
        UpdateCameraControlState();
        CopyDiagnosticsButton.IsEnabled = operation is null && !IsStopping && !IsRecording;
        OpenRecordingReportButton.Content = L("DsOpenRecordingReport");
        OpenRecordingReportButton.Visibility = !IsRecording && savedReportPath is not null
            ? Visibility.Visible : Visibility.Collapsed;
        var video = VideoFormat;
        var supported = SupportsRecording;
        var folder = outputDirectory();
        var hasFolder = Path.IsPathFullyQualified(folder) && Directory.Exists(folder);
        RecordingHint.Text = L(!supported ? "DsRecordUnsupported" : "DsRecordChooseFolder");
        RecordingHintBanner.Visibility = video is not null && !IsRecording && (!supported || !hasFolder)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FinishRecording(DirectShowRecording active)
    {
        recordingOperation?.RequestStop();
        recordingDetail = L("DsRecordFinalizing");
        if (preview is { } session)
            session.FinishRecording(active);
        else
            active.Complete();
        UpdateRecordingControls();
        StateChanged?.Invoke(this, EventArgs.Empty);
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
            return recordingOperation?.Completion ?? Task.CompletedTask;
        }
        return recordingOperation?.Completion ?? RecordAsync();
    }

    private async Task RecordAsync()
    {
        if (!CanRecord || preview is not { } session || ActiveVideo is not { } video)
            return;

        using var clip = recordingOperation = new CaptureOperation();
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
            var encoding = recordingEncoding();
            var filename = encoding.IsSoftware
                ? $"{prefix}_DirectShow_{encoding.FileTagForAudioChannels(audio?.Channels)}"
                : $"{prefix}_DirectShow_{videoTag}{audioTag}";
            var extensions = encoding.IsSoftware ? [".mkv"] : DirectShowRecording.OutputExtensions(video);
            var stem = Path.Combine(folder, filename);
            var unique = stem;
            for (var suffix = 2; extensions.Any(ext => File.Exists(unique + ext)) ||
                File.Exists(unique + ".mkv.partial") || File.Exists(unique + ".mkv.partial.mkv"); suffix++)
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
                        {
                            recordingDetail = string.Format(L(key), attempt.Container);
                            StateChanged?.Invoke(this, EventArgs.Empty);
                        }
                    });
            }, encoding);
            lastRejectedPackets = null;
            recording = active;
            recordingDetail = string.Format(L("DsRecordActive"), unique,
                string.Join(" → ", extensions.Select(ext => ext[1..].ToUpperInvariant())));
            UpdateRecordingControls();
            RenderEvidence();
            savedMediaPath = await active.Completion;
            var reportPath = encoding.IsSoftware ? savedMediaPath + ".encoding.json" : NativeCaptureReport.PathFor(savedMediaPath);
            savedReportPath = active.ReportError is null && File.Exists(reportPath) ? reportPath : null;
            recordingDetail = active.ReportError is { } reportError
                ? string.Format(L("DsReportWriteFailed"), savedMediaPath, reportError)
                : string.Format(L("DsRecordSaved"), savedMediaPath);
        }
        catch (Exception error)
        {
            if (recording is { ReportError: null } failed)
                savedReportPath = new[] { failed.PartialPath + ".checkpoint.json", NativeCaptureReport.PathFor(failed.PartialPath) }
                    .FirstOrDefault(File.Exists);
            recordingDetail = recording is { } active && File.Exists(active.PartialPath)
                ? string.Format(L("DsRecordPartial"), active.PartialPath, error.Message)
                : string.Format(L("DsRecordError"), error.Message);
        }
        finally
        {
            if (recording is { } active)
            {
                session.FinishRecording(active);
                lastRejectedPackets = active.RejectedPackets;
            }
            recording = null;
            recordingOperation = null;
            UpdateRecordingControls();
            RenderEvidence();
        }
    }
}
