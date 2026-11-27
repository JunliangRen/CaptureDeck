namespace CaptureRack.SignalCapture;

// Transport commands run on the owner's context. Acquisition and verification run off that context.
public sealed class RfCaptureSession
{
    private sealed class Operation
    {
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Finish()
        {
            Stop.Dispose();
            Finished.SetResult();
        }
    }

    private Operation? preview;
    private Operation? recording;
    private RfPreviewSession? receiver;
    private Task? readerTask;
    private bool closing;
    private readonly Func<RfRampQc, Task> verify;
    private readonly Func<RfRecording, string, string?, Func<int?>, Action<RfMeasurement>, CancellationToken, RfRampQc?> capture;

    private readonly Action<Action<byte[], int>, CancellationToken>? read;

    public RfCaptureSession() : this(qc => Task.Run(qc.VerifyAndPublish),
        (request, prefix, ffmpeg, vga, report, stop) => request.Capture(prefix, ffmpeg, vga, report, stop)) { }

    internal RfCaptureSession(Func<RfRampQc, Task> verify,
        Func<RfRecording, string, string?, Func<int?>, Action<RfMeasurement>, CancellationToken, RfRampQc?> capture,
        Action<Action<byte[], int>, CancellationToken>? read = null)
    {
        this.read = read;
        this.verify = verify;
        this.capture = capture;
    }

    public bool PreviewActive => preview is not null;
    public bool PreviewStopping => preview?.Stop.IsCancellationRequested == true;
    public bool RecordingActive => recording is not null;
    public bool RecordingStopping => recording?.Stop.IsCancellationRequested == true;
    public Task VerificationIdle { get; private set; } = Task.CompletedTask;
    public event Action<RfRampQc, Task, Task>? VerificationQueued;

    public async Task StartPreviewAsync(RfRecording? input, Func<CancellationToken, Task> present)
    {
        if (closing || preview is not null || recording is not null)
            throw new InvalidOperationException("RF source is busy.");
        var operation = preview = new();
        receiver = input is null ? null : new RfPreviewSession(input);
        try { await present(operation.Stop.Token); }
        finally
        {
            try
            {
                if (readerTask is { IsCompleted: false } reading)
                {
                    operation.Stop.Cancel();
                    await reading;
                }
            }
            finally
            {
                readerTask = null;
                receiver = null;
                preview = null;
                operation.Finish();
            }
        }
    }

    public Task ReadReceiverAsync(Func<int?> vga, Action<RfMeasurement> report, CancellationToken token,
        Action<byte[], int, long>? previewSamples = null)
    {
        var live = receiver;
        if (live is null || preview is null || preview.Stop.Token != token)
            throw new InvalidOperationException("RF receiver does not belong to this preview.");
        if (readerTask is not null) throw new InvalidOperationException("RF receiver already started.");
        return readerTask = Task.Run(() =>
        {
            if (read is null) live.Run(vga, report, token, previewSamples);
            else live.Run(read, report, token, previewSamples);
        });
    }

    public async Task RecordAsync(bool filePlayback, RfRecording request, string prefix, string? ffmpeg,
        Func<int?> vga, Action<RfMeasurement> report)
    {
        if (filePlayback || (PreviewActive && receiver is null))
            throw new InvalidOperationException("RF file playback cannot record.");
        if (closing || recording is not null || PreviewStopping)
            throw new InvalidOperationException("RF source is busy.");
        if (receiver is not null && readerTask is not { IsCompleted: false })
            throw new InvalidOperationException("RF preview receiver is not running.");
        if (receiver is not null && !receiver.Matches(request))
            throw new InvalidOperationException("RF recording configuration differs from the active receiver.");
        var operation = recording = new();
        var live = receiver;
        try
        {
            var qc = await Task.Run(() => live is null
                ? capture(request, prefix, ffmpeg, vga, report, operation.Stop.Token)
                : live.Record(prefix, ffmpeg, operation.Stop.Token, report));
            if (qc is not null)
            {
                var previous = VerificationIdle;
                var result = VerifyAfterAsync(previous, qc);
                VerificationIdle = ObserveVerificationAsync(result);
                VerificationQueued?.Invoke(qc, previous, result);
            }
        }
        finally
        {
            recording = null;
            operation.Finish();
        }
    }

    private async Task VerifyAfterAsync(Task previous, RfRampQc qc)
    {
        await previous.ConfigureAwait(false);
        await verify(qc).ConfigureAwait(false);
    }

    private static async Task ObserveVerificationAsync(Task result)
    {
        // The per-file result carries the failure to QC history. Later files can still run.
        try { await result.ConfigureAwait(false); }
        catch (Exception) { }
    }

    public void StopRecording() => recording?.Stop.Cancel();

    public void StopPreview()
    {
        if (!RecordingActive) preview?.Stop.Cancel();
    }

    public async Task StopForCloseAsync()
    {
        closing = true;
        if (recording is { } clip)
        {
            clip.Stop.Cancel();
            await clip.Finished.Task;
        }
        if (preview is { } source)
        {
            source.Stop.Cancel();
            await source.Finished.Task;
        }
        await VerificationIdle;
    }
}
