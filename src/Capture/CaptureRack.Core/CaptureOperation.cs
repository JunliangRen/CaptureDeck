namespace CaptureRack.Core;

// Owned by the transport command context. Workers observe Token; the owner releases
// the operation after device cleanup, even if a worker fails or stop was never requested.
public sealed class CaptureOperation : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken Token => stop.Token;
    public bool IsStopping => stop.IsCancellationRequested;
    public Task Completion => finished.Task;

    public void RequestStop()
    {
        if (!Completion.IsCompleted && !IsStopping) stop.Cancel();
    }

    public Task StopAsync()
    {
        RequestStop();
        return Completion;
    }

    public void Dispose()
    {
        if (!finished.TrySetResult()) return;
        stop.Dispose();
    }
}
