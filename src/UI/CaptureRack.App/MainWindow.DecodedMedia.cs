using System.Diagnostics;
using CaptureRack.DeckLink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private ILiveMediaSource? LiveMediaSource => IsDirectShowActive ? directShowCapabilitiesView :
        IsFireWireActive ? fireWireView : null;
    private bool IsCaptureInputActive => rfPreviewStop is not null || captureTask is not null || LiveMediaSource?.IsCapturing == true;
    private bool IsCaptureRecording => rfStop is not null || recordingSession || LiveMediaSource?.IsRecording == true;
    private bool finishingMediaForClose;
    private bool decodedInputReady;
    private bool decodedFieldOrderAvailable;
    private long lastDecodedStats;
    private double decodedDisplayAspectRatio;
    private WorkspaceCapabilities? appliedDecodedCapabilities;

    private WorkspaceCapabilities ActiveWorkspaceCapabilities
    {
        get
        {
            var caps = activeModule.Descriptor.Capabilities;
            if (IsRfActive)
                return caps with
                {
                    SupportsLocalAudioMonitor = RfHasAudio,
                    SupportsVideoPreview = !IsRfHifiFile && rfInspector.PreviewOptions.PreviewType != RfPreviewType.RfOnly,
                    SupportsVideoScopes = true,
                    SupportsPreviewDisplayOptions = !IsRfHifiFile,
                    SupportsEvents = false
                };
            return LiveMediaSource is { } media ? caps with
            {
                SupportsVideoPreview = media.HasPreview,
                SupportsVideoScopes = media.HasPreview,
                SupportsPreviewDisplayOptions = media.HasPreview,
                SupportsAudioMeters = media.HasAudio,
                SupportsAudioSpectrum = media.HasAudio,
                SupportsLocalAudioMonitor = media.HasAudio
            } : caps;
        }
    }

    private WorkspaceLayoutMode EffectiveWorkspaceLayout
    {
        get
        {
            if (IsRfActive) return workspaceLayout == WorkspaceLayoutMode.Events ? WorkspaceLayoutMode.Split : workspaceLayout;
            var caps = ActiveWorkspaceCapabilities;
            return workspaceLayout switch
            {
                WorkspaceLayoutMode.Split or WorkspaceLayoutMode.Scopes when !caps.SupportsVideoScopes => WorkspaceLayoutMode.Focus,
                WorkspaceLayoutMode.Spectrum when !caps.SupportsAudioSpectrum => WorkspaceLayoutMode.Focus,
                WorkspaceLayoutMode.Events when !caps.SupportsEvents => WorkspaceLayoutMode.Focus,
                _ => workspaceLayout
            };
        }
    }

    private SplitSecondaryInstrument EffectiveSplitInstrument => splitSecondary switch
    {
        SplitSecondaryInstrument.Spectrum when !IsRfActive && !ActiveWorkspaceCapabilities.SupportsAudioSpectrum => SplitSecondaryInstrument.Vector,
        SplitSecondaryInstrument.Events when !ActiveWorkspaceCapabilities.SupportsEvents => SplitSecondaryInstrument.Vector,
        _ => splitSecondary
    };

    private void DecodedMediaStateChanged(object? sender, EventArgs e)
    {
        if (sender != LiveMediaSource || !settingsReady || LiveMediaSource is not { } media)
            return;
        RefreshDecodedMediaState(media);
    }

    private void RefreshDecodedMediaState(ILiveMediaSource media)
    {
        var caps = ActiveWorkspaceCapabilities;
        if (caps != appliedDecodedCapabilities || decodedFieldOrderAvailable != media.HasDeinterlace)
        {
            appliedDecodedCapabilities = caps;
            decodedFieldOrderAvailable = media.HasDeinterlace;
            if (previewFullscreen && !caps.SupportsVideoPreview)
                SetPreviewFullscreen(false);
            ApplyModuleCapabilities();
        }

        if (media.IsRecording && !media.IsFinalizing)
        {
            if (!recordingClock.IsRunning)
            {
                recordingClock.Restart();
                UpdateRecordingIndicator(active: true);
                UpdateRecordingClock();
            }
        }
        else
        {
            StopRecordingClock();
        }
        decodedInputReady = media.HasInput;
        var waitingForFireWireVideo = IsFireWireActive && media.HasPreview && !media.HasDeinterlace;
        liveSignal = decodedInputReady && !waitingForFireWireVideo ? VideoSignal.Present : VideoSignal.Unknown;
        if (IsFireWireActive && media.IsCapturing && !media.IsRecording && qcStop is null &&
            liveSignal == VideoSignal.Unknown && statusTitle == "SignalPresent")
            SetStatus("WaitingFrames", media.PreviewHint ?? "WaitingFirstFrame");
        PreviewPlaceholderText.Text = L(media.PreviewHint ?? "WaitingPreview");
        PreviewPlaceholderHint.Text = "";
        RenderCounts(null);
        UpdateRecordingFilenamePreview();
        UpdateConfigurationStatus();
        UpdateNavigationStyles();
        UpdateControls();
    }

    private IReadOnlyList<TelemetryMetric> BuildDecodedMediaTelemetry(ILiveMediaSource media) =>
        media.BuildLiveTelemetry(preview?.SkippedFrames ?? 0, L, settingsCulture);

    private async Task StartDecodedMediaAsync(ILiveMediaSource media)
    {
        ResetMonitoring();
        var feed = new PreviewFeed();
        ResetPreview(feed);
        ApplyPreviewSettingsToLiveFeed();
        SetStatus("WaitingFrames", "WaitingFirstFrame");
        var task = media.StartAsync(feed);
        media.SetAudioMonitor(audioMonitorEnabled, audioMonitorVolume);
        StartPreviewClock();
        try
        {
            await task;
        }
        finally
        {
            StopPreviewClock();
            ResetPreview(null);
            ResetMonitoring();
            RenderPreviewCaption();
            if (qcStop is null && !media.IsRecording)
            {
                if (media.PreviewFailure is { } error)
                    SetStatus("PreviewFailed", () => error, InfoBarSeverity.Error);
                else
                    SetStatus("Idle", "PreviewStopped");
            }
            RefreshDecodedMediaState(media);
        }
    }

    private void UpdateDecodedMediaMonitoring()
    {
        if (LiveMediaSource is not { IsCapturing: true } media)
            return;
        if (decodedInputReady != media.HasInput || decodedFieldOrderAvailable != media.HasDeinterlace ||
            ActiveWorkspaceCapabilities != appliedDecodedCapabilities)
            RefreshDecodedMediaState(media);

        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(lastDecodedStats, now) < TimeSpan.FromMilliseconds(120))
            return;
        lastDecodedStats = now;
        SetPreviewAudio(media.AudioInfo);
        if (IsFireWireActive)
        {
            fireWireView.RefreshStreamInfo();
            var tape = fireWireView.PreviewTransportSummary;
            PreviewTapeDetail.Text = tape ?? "";
            PreviewTapeDetail.Visibility = tape is null ? Visibility.Collapsed : Visibility.Visible;
        }
        RenderCounts(null);
        if (media.PreviewHint is { } hint)
        {
            PreviewPlaceholderText.Text = L(hint);
            SyncFullscreenPreviewWindow();
        }
        if (media.IsRecording && statusTitle is "StartRecording" or "StopRecording")
            RenderStatus();
        else if (qcStop is null && statusTitle == "WaitingFrames" && liveSignal == VideoSignal.Present)
            SetStatus("SignalPresent", "VideoLocked", InfoBarSeverity.Success);
    }

    private async Task RecordDecodedMediaAsync(ILiveMediaSource media)
    {
        var wasRecording = media.IsRecording;
        if (!wasRecording)
        {
            var directory = OutputPath.Text.Trim();
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            {
                if (previewFullscreen)
                    SetPreviewFullscreen(false);
                holdRecordingStatus = true;
                UpdateLivePreviewStatus();
                SetStatus("ChooseOutput", "ExistingOutput", InfoBarSeverity.Warning);
                if (!IsInspectorOpen)
                    ToggleInspector();
                return;
            }
        }
        var previousFile = media.SavedPath;
        var recording = media.ToggleRecordingAsync();
        if (!wasRecording && media.IsRecording && media.RecordingDetail is { } detail)
            SetStatus("StartRecording", () => media.RecordingDetail ?? detail);
        if (wasRecording && media.IsFinalizing)
            SetStatus("StopRecording", () => media.RecordingDetail ?? L("QcFinishing"));
        RenderCaptureActions();

        await recording;
        if (wasRecording || finishingMediaForClose || closing)
            return;
        if (media.SavedPath is { } path && path != previousFile)
            await RunMediaQcFileAsync(path);
        else if (!string.IsNullOrEmpty(media.RecordingDetail))
            SetStatus("RecordingFailed", () => media.RecordingDetail!, InfoBarSeverity.Error);
        CloseAfterRecordingIfRequested();
    }
}
