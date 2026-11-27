using CaptureRack.Core;
using CaptureRack.DeckLink;

namespace CaptureRack.App;

internal interface ILiveMediaSource
{
    event EventHandler? StateChanged;
    bool CanStart { get; }
    bool IsCapturing { get; }
    bool IsStopping { get; }
    bool CanRecord { get; }
    bool SupportsRecording { get; }
    bool IsRecording { get; }
    bool IsFinalizing { get; }
    bool HasInput { get; }
    long ReceivedUnits { get; }
    long DecodedFrames { get; }
    bool HasPreview { get; }
    bool HasAudio { get; }
    CaptureAudioInfo? AudioInfo { get; }
    bool HasDeinterlace { get; }
    string? PreviewHint { get; }
    string? PreviewFailure { get; }
    string? SavedPath { get; }
    string? RecordingReportPath { get; }
    string? RecordingDetail { get; }

    IReadOnlyList<TelemetryMetric> BuildLiveTelemetry(long previewSkippedFrames, Func<string, string> localize, IFormatProvider formatProvider);
    Task StartAsync(PreviewFeed feed);
    void StopPreview();
    Task ToggleRecordingAsync();
    Task StopForCloseAsync();
    void SetAudioMonitor(bool enabled, double volume);
    void SetAudioMonitorRoute(AudioMonitorRoute route);
    StereoLevels? ReadAudioLevels();
}

internal interface ILiveMediaInspector
{
    bool HasOpenDropdown { get; }
    string? BuildFilenamePreview(string label);
    void NotifyOutputFolderChanged();
}
