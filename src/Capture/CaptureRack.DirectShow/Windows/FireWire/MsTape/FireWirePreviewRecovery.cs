using System.Diagnostics;
using System.Threading.Channels;
using CaptureRack.Core;

namespace CaptureRack.DirectShow.Windows.FireWire.MsTape;

/// <summary>Re-probes tape audio and video when playback resumes or the decoder stalls.</summary>
public sealed class FireWirePreviewRecovery(bool dv = false)
{
    private static readonly TimeSpan VideoTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private FireWirePreviewDecoder? decoder;
    private long lastInput;
    private long decodedFrames;
    private int streamVersion;

    public int? VideoPid => Volatile.Read(ref decoder)?.VideoPid;
    public int? AudioPid => Volatile.Read(ref decoder)?.AudioPid;
    public CaptureAudioInfo? AudioInfo => Volatile.Read(ref decoder)?.AudioInfo;
    public long DecodedFrames => Interlocked.Read(ref decodedFrames);

    public void NotifyStreamChange() => Interlocked.Increment(ref streamVersion);

    public bool Offer(CaptureSample sample)
    {
        if (!sample.Bytes.IsEmpty)
            Interlocked.Exchange(ref lastInput, Stopwatch.GetTimestamp());
        return Volatile.Read(ref decoder)?.Offer(sample) ?? true;
    }

    public Task RunAsync(string executable, Action<ReadOnlyMemory<byte>, FireWireFrameInfo> video,
        Action<ReadOnlyMemory<byte>> audio, Action resetDisplay, CancellationToken stop) =>
        RunAsync(executable, video, (pcm, _, _) => audio(pcm), resetDisplay, stop);

    public async Task RunAsync(string executable, Action<ReadOnlyMemory<byte>, FireWireFrameInfo> video,
        Action<ReadOnlyMemory<byte>, int, string?> audio, Action resetDisplay, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var version = Volatile.Read(ref streamVersion);
                var attempt = new FireWirePreviewDecoder(dv);
                long lastVideo = 0;
                using var cancelAttempt = CancellationTokenSource.CreateLinkedTokenSource(stop);
                Volatile.Write(ref decoder, attempt);
                var decoding = attempt.RunAsync(executable, (pixels, info) =>
                {
                    Interlocked.Exchange(ref lastVideo, Stopwatch.GetTimestamp());
                    video(pixels, info);
                    Interlocked.Increment(ref decodedFrames);
                }, audio, cancelAttempt.Token);

                var interrupted = false;
                long firstInput = 0;
                try
                {
                    while (!decoding.IsCompleted && !stop.IsCancellationRequested)
                    {
                        var input = Interlocked.Read(ref lastInput);
                        if (input >= started)
                        {
                            if (firstInput == 0)
                                firstInput = input;
                            var videoAt = Interlocked.Read(ref lastVideo);
                            var waitingSince = Math.Max(firstInput, videoAt);
                            if (version != Volatile.Read(ref streamVersion) ||
                                Stopwatch.GetElapsedTime(input) >= FireWireLiveSession.InputGap ||
                                Stopwatch.GetElapsedTime(waitingSince) >= VideoTimeout)
                            {
                                interrupted = true;
                                break;
                            }
                        }
                        await Task.WhenAny(decoding, Task.Delay(PollInterval, stop)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Volatile.Write(ref decoder, null);
                    cancelAttempt.Cancel();
                    try { await decoding.ConfigureAwait(false); }
                    catch (Exception ex) when (stop.IsCancellationRequested ||
                        (ex is IOException or ChannelClosedException && Interlocked.Read(ref lastInput) >= started)) { }
                }

                if (stop.IsCancellationRequested)
                    break;
                resetDisplay();
                if (!interrupted)
                    await Task.Delay(PollInterval, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
