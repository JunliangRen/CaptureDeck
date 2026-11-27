using CaptureRack.Core;

namespace CaptureRack.SignalCapture;

// Transport commands run on the owner's context. Acquisition and verification run off that context.
public sealed class RfCaptureSession
{
    private CaptureOperation? preview;
    private CaptureOperation? recording;
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
    public bool PreviewStopping => preview?.IsStopping == true;
    public bool RecordingActive => recording is not null;
    public bool RecordingStopping => recording?.IsStopping == true;
    public Task VerificationIdle { get; private set; } = Task.CompletedTask;
    public event Action<RfRampQc, Task, Task>? VerificationQueued;

    public async Task StartPreviewAsync(RfRecording? input, Func<CancellationToken, Task> present)
    {
        if (closing || preview is not null || recording is not null)
            throw new InvalidOperationException("RF source is busy.");
        var operation = preview = new();
        receiver = input is null ? null : new RfPreviewSession(input);
        try { await present(operation.Token); }
        finally
        {
            try
            {
                if (readerTask is { IsCompleted: false } reading)
                {
                    operation.RequestStop();
                    await reading;
                }
            }
            finally
            {
                readerTask = null;
                receiver = null;
                preview = null;
                operation.Dispose();
            }
        }
    }

    public Task ReadReceiverAsync(Func<int?> vga, Action<RfMeasurement> report, CancellationToken token,
        Action<byte[], int, long>? previewSamples = null)
    {
        var live = receiver;
        if (live is null || preview is null || preview.Token != token)
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
                ? capture(request, prefix, ffmpeg, vga, report, operation.Token)
                : live.Record(prefix, ffmpeg, operation.Token, report));
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
            operation.Dispose();
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

    public void StopRecording() => recording?.RequestStop();

    public void StopPreview()
    {
        if (!RecordingActive) preview?.RequestStop();
    }

    public async Task StopForCloseAsync()
    {
        closing = true;
        if (recording is { } clip)
        {
            await clip.StopAsync();
        }
        if (preview is { } source)
        {
            await source.StopAsync();
        }
        await VerificationIdle;
    }
}
