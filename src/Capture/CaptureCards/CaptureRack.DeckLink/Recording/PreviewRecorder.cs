namespace CaptureRack.DeckLink;

// Each instance belongs to one preview. Each clip uses the mode of its attached callback.
public sealed class PreviewRecorder
{
    private readonly object gate = new();
    private CaptureCallback? callback;
    private MovRecording? recording;

    internal void Connect(CaptureCallback input)
    {
        lock (gate)
            callback = input;
    }

    internal void Disconnect()
    {
        lock (gate)
        {
            if (recording is { } active)
            {
                callback!.DetachRecording();
                recording = null;
                active.Dispose();
            }
            callback = null;
        }
    }

    public DateTimeOffset Start(string path)
    {
        lock (gate)
        {
            var input = callback ?? throw new InvalidOperationException("Wait for the preview input to be ready before recording.");
            if (recording is not null)
                throw new InvalidOperationException("A recording is already running.");

            recording = input.AttachRecording(path);
            return DateTimeOffset.UtcNow;
        }
    }

    public CaptureStats Finish()
    {
        lock (gate)
        {
            using var active = recording ?? throw new InvalidOperationException("The recording is no longer active.");
            var stats = callback!.DetachRecording();
            recording = null;
            if (stats.Error is { } error)
                throw new IOException(error);
            stats.Recording = active.Finish();
            return stats;
        }
    }
}
