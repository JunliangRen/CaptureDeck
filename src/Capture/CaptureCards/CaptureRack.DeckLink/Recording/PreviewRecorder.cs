using System.Runtime.ExceptionServices;
using CaptureRack.Recording;

namespace CaptureRack.DeckLink;

// Each instance belongs to one preview. Each clip uses the mode of its attached callback.
public sealed class PreviewRecorder
{
    private readonly object gate = new();
    private CaptureCallback? callback;
    private MovRecording? recording;
    private CaptureStats? savedOnDisconnect;
    private ExceptionDispatchInfo? finishError;
    private AudioMonitorRoute audioMonitorRoute = AudioMonitorRoute.Default;

    internal void Connect(CaptureCallback input)
    {
        lock (gate)
        {
            callback = input;
            input.SetAudioMonitorRoute(audioMonitorRoute);
        }
    }

    public void SetAudioMonitorRoute(AudioMonitorRoute route)
    {
        CaptureCallback? current;
        lock (gate)
        {
            audioMonitorRoute = route;
            current = callback;
        }
        current?.SetAudioMonitorRoute(route);
    }

    internal void Disconnect()
    {
        lock (gate)
        {
            if (recording is not null)
            {
                try
                {
                    savedOnDisconnect = FinishAttachedRecording("The capture stream disconnected before recording was stopped.");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or OverflowException)
                {
                    finishError = ExceptionDispatchInfo.Capture(ex);
                }
            }
            callback = null;
        }
    }

    public bool ReadAudioFromUi { get; init; }

    public StereoLevels? ReadAudioLevels()
    {
        CaptureCallback? current;
        lock (gate)
            current = callback;
        return current?.ReadAudioLevels();
    }

    public DateTimeOffset Start(string path, CaptureAudioFormat? outputAudioFormat = null, RecordingEncoding? encoding = null)
    {
        lock (gate)
        {
            var input = callback ?? throw new InvalidOperationException("Wait for the preview input to be ready before recording.");
            if (recording is not null || savedOnDisconnect is not null || finishError is not null)
                throw new InvalidOperationException("A recording is already running.");

            recording = input.AttachRecording(path, outputAudioFormat, encoding);
            return DateTimeOffset.UtcNow;
        }
    }

    public CaptureStats Finish(string? captureError = null)
    {
        lock (gate)
        {
            if (savedOnDisconnect is { } saved)
            {
                savedOnDisconnect = null;
                return saved;
            }
            if (finishError is { } failure)
            {
                finishError = null;
                failure.Throw();
            }
            return FinishAttachedRecording(captureError);
        }
    }

    private CaptureStats FinishAttachedRecording(string? captureError)
    {
        using var active = recording ?? throw new InvalidOperationException("The recording is no longer active.");
        recording = null;
        var stats = callback!.DetachRecording();
        stats.Error ??= captureError;
        stats.Recording = active.Finish(stats.Error);
        return stats;
    }
}
