using System.Runtime.ExceptionServices;

namespace CaptureRack.DeckLink;

// Each instance belongs to one preview. Each clip uses the mode of its attached callback.
public sealed class PreviewRecorder
{
    private readonly object gate = new();
    private CaptureCallback? callback;
    private MovRecording? recording;
    private CaptureStats? savedOnDisconnect;
    private ExceptionDispatchInfo? finishError;

    internal void Connect(CaptureCallback input)
    {
        lock (gate)
            callback = input;
    }

    internal void Disconnect()
    {
        lock (gate)
        {
            if (recording is not null)
            {
                try
                {
                    var saved = FinishAttachedRecording();
                    saved.Error ??= "The capture stream disconnected before recording was stopped.";
                    savedOnDisconnect = saved;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or OverflowException)
                {
                    finishError = ExceptionDispatchInfo.Capture(ex);
                }
            }
            callback = null;
        }
    }

    public DateTimeOffset Start(string path)
    {
        lock (gate)
        {
            var input = callback ?? throw new InvalidOperationException("Wait for the preview input to be ready before recording.");
            if (recording is not null || savedOnDisconnect is not null || finishError is not null)
                throw new InvalidOperationException("A recording is already running.");

            recording = input.AttachRecording(path);
            return DateTimeOffset.UtcNow;
        }
    }

    public CaptureStats Finish()
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
            return FinishAttachedRecording();
        }
    }

    private CaptureStats FinishAttachedRecording()
    {
        using var active = recording ?? throw new InvalidOperationException("The recording is no longer active.");
        recording = null;
        var stats = callback!.DetachRecording();
        stats.Recording = active.Finish();
        return stats;
    }
}
