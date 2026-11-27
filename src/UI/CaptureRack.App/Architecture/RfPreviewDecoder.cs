#if REALTIME_RF
using CaptureRack.Audio;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using VHSDecode.Preview;

namespace CaptureRack.App;

// Only the acquisition callback produces blocks. The decoder owns each copy until Process returns.
internal sealed class RfPreviewDecoder
{
    private readonly record struct Block(byte[] Bytes, int Length, long FirstSample);
    private readonly Channel<Block> input = Channel.CreateBounded<Block>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        SingleWriter = true
    });
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly LocalAudioMonitor? speaker;
    internal sealed record FieldPair(PreviewField First, PreviewField Second, short[]? SwitchTrace)
    {
        public RfGuideGeometry? Geometry { get; init; }
        public PreviewField Top => First.IsTopField ? First : Second;
        public PreviewField Bottom => First.IsTopField ? Second : First;
    }
    private PreviewField? previous;
    private readonly Queue<FieldPair> waiting = new();
    private readonly RfSwitchTrigger trigger = new();
    private readonly bool useTrigger;
    public volatile bool MeasureSwitch;
    public volatile int FieldMode;
    private byte[] woven = [];
    public volatile bool FullRaster;
    private string? error;
    private int ended;
    private RealtimePreviewSession? session;
    public PreviewLockStatus? Locks => Volatile.Read(ref session)?.LockStatus;
    public CaptureRack.DeckLink.PreviewFeed ScopeFeed { get; } = new() { RenderScopes = false };
    public string? Error => Volatile.Read(ref error);
    private void SubmitField(PreviewField field)
    {
        var first = previous;
        previous = field;
        if (first is null || first.IsTopField == field.IsTopField ||
            first.Width != field.Width || Math.Abs(first.Height - field.Height) > 1 ||
            Math.Abs((field.Timestamp - first.Timestamp).TotalSeconds * field.FieldsPerSecond - 1) > 0.45)
            return;
        previous = null;
        var pair = new FieldPair(first, field, MeasureSwitch && useTrigger ? trigger.Find(first.Timestamp, sourceRate) : null);
        if (speaker is { Enabled: true, PlayedMediaTime: { } clock } && synchronize)
        {
            waiting.Enqueue(pair);
            while (waiting.Count > 8) waiting.Dequeue();
            FieldPair? due = null;
            while (waiting.TryPeek(out var candidate) && candidate.First.Timestamp <= clock)
                due = waiting.Dequeue();
            if (due is null) return;
            pair = due;
        }
        else waiting.Clear();
        RenderPair(pair);
    }

    internal RealtimePreviewOptions Options { get; }
    private readonly int sourceRate;
    private readonly bool synchronize;

    public void RenderPair(FieldPair pair)
    {
        bool full = FullRaster;
        var top = pair.Top;
        var bottom = pair.Bottom;
        var first = pair.First;
        var field = pair.Second;
        int mode = FieldMode;
        var upper = full ? top.RasterRect : top.ActiveVideoRect;
        var lower = full ? bottom.RasterRect : bottom.ActiveVideoRect;
        if (upper.Width != lower.Width || Math.Abs(upper.Height - lower.Height) > 1) return;
        int stride = checked(upper.Width * 4);
        int fullHeight = upper.Height + lower.Height;
        int height = mode switch { 2 => upper.Height, 3 => lower.Height, _ => fullHeight };
        int length = checked(stride * height);
        if (woven.Length != length) woven = new byte[length];
        int row = 0;
        if (mode == 0)
        {
            for (int y = 0; y < Math.Max(upper.Height, lower.Height); y++)
            {
                if (y < upper.Height) CopyLine(top, upper, y);
                if (y < lower.Height) CopyLine(bottom, lower, y);
            }
        }
        else
        {
            if (mode != 3) for (int y = 0; y < upper.Height; y++) CopyLine(top, upper, y);
            if (mode != 2) for (int y = 0; y < lower.Height; y++) CopyLine(bottom, lower, y);
        }
        void CopyLine(PreviewField source, PreviewRectangle bounds, int y)
        {
            int offset = (y + bounds.Y) * source.Stride + bounds.X * 4;
            source.Bgra.Span.Slice(offset, stride).CopyTo(woven.AsSpan(row++ * stride));
        }
        var upperActive = top.ActiveVideoRect;
        var lowerActive = bottom.ActiveVideoRect;
        int upperTop = upperActive.Y - upper.Y, lowerTop = lowerActive.Y - lower.Y;
        int upperBottom = upper.Height - upperTop - upperActive.Height;
        int lowerBottom = lower.Height - lowerTop - lowerActive.Height;
        int left = mode == 3 ? lowerActive.X - lower.X : upperActive.X - upper.X;
        int activeWidth = mode == 3 ? lowerActive.Width : upperActive.Width;
        int activeTop = mode switch { 0 => upperTop + lowerTop, 3 => lowerTop, _ => upperTop };
        int activeBottom = mode switch { 0 => upperBottom + lowerBottom, 2 => upperBottom, _ => lowerBottom };
        var geometry = RfGuideGeometry.Create(upper.Width, height, left, activeTop,
            upper.Width - left - activeWidth, activeBottom, mode == 0 && field.FieldsPerSecond > 55);
        ScopeFeed.SubmitDecodedFrame(woven, upper.Width, height, mode == 0 ? first.IsTopField : null,
            field.FieldsPerSecond / 2, upper.Width, height, upper.Width * field.PixelAspectRatio / fullHeight, sourceTag: pair with { Geometry = geometry });
    }

    public RfPreviewDecoder(RealtimePreviewOptions options, RfPreviewAudio? audio, string? trackLabel = null, LocalAudioMonitor? speaker = null)
    {
        Options = options;
        this.speaker = speaker;
        sourceRate = options.InputSampleRateHz;
        synchronize = options.SourceFormat == PreviewSourceFormat.LaserDisc || options.AudioTrack == PreviewAudioTrack.BetamaxHiFi;
        useTrigger = options.SourceFormat is not (PreviewSourceFormat.Cvbs or PreviewSourceFormat.LaserDisc) && options.AudioTrack != PreviewAudioTrack.VhsHiFi;
        worker = Task.Run(async () =>
        {
            long? trackToken = null;

            short[] pcm = [];
            void Audio(PreviewAudioBlock block)
            {
                if (audio is null) return;
                if (!block.IsLocked)
                {
                    if (trackToken.HasValue) audio.LoseLock(trackToken.Value);
                    trackToken = null;
                    return;
                }

                trackToken ??= audio.SelectTrack(trackLabel ?? options.AudioTrack.ToString(), block.Channels, (uint)block.SampleRateHz);
                var samples = block.InterleavedSamples.Span;
                if (pcm.Length < samples.Length) pcm = new short[samples.Length];
                for (int i = 0; i < samples.Length; i++)
                    pcm[i] = (short)Math.Clamp(MathF.Round(samples[i] * 32768f), short.MinValue, short.MaxValue);
                speaker?.SubmitPcm16(pcm.AsSpan(0, samples.Length), block.PresentationEndTimestamp);
                audio.SubmitPcm(trackToken.Value, pcm.AsSpan(0, samples.Length));
            }
            try
            {
                using var decoder = new RealtimePreviewSession(options, options.AudioTrack == PreviewAudioTrack.VhsHiFi ? null : SubmitField,
                    options.AudioTrack == PreviewAudioTrack.None ? null : Audio);
                Volatile.Write(ref session, decoder);
                long? next = null;
                await foreach (var block in input.Reader.ReadAllAsync(stop.Token))
                {
                    try
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        var samples = MemoryMarshal.Cast<byte, short>(block.Bytes.AsSpan(0, block.Length));
                        if (next != block.FirstSample)
                        {
                            if (trackToken.HasValue) audio!.LoseLock(trackToken.Value);
                            trackToken = null;
                            previous = null;
                            waiting.Clear();
                        }
                        if (MeasureSwitch && useTrigger) trigger.Add(samples, block.FirstSample);
                        decoder.Process(samples, block.FirstSample, stop.Token);
                        next = block.FirstSample + block.Length / 2;
                        if (trackToken.HasValue && decoder.LockStatus.AudioLocked == false)
                        {
                            audio!.LoseLock(trackToken.Value);
                            trackToken = null;
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(block.Bytes); }
                }
                if (!stop.IsCancellationRequested) decoder.Finish();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { Volatile.Write(ref error, ex.Message); }
            finally
            {
                Volatile.Write(ref session, null);
                Interlocked.Exchange(ref ended, 1);
                input.Writer.TryComplete();
                while (input.Reader.TryRead(out var block)) ArrayPool<byte>.Shared.Return(block.Bytes);
                if (trackToken.HasValue) audio!.LoseLock(trackToken.Value);
            }
        });
    }

    public void Offer(byte[] bytes, int length, long firstSample)
    {
        if (Volatile.Read(ref ended) != 0) return;
        var copy = ArrayPool<byte>.Shared.Rent(length);
        bytes.AsSpan(0, length).CopyTo(copy);
        if (!input.Writer.TryWrite(new(copy, length, firstSample))) ArrayPool<byte>.Shared.Return(copy);
    }

    public async Task StopAsync(bool drain = false)
    {
        if (!drain) stop.Cancel();
        input.Writer.TryComplete();
        await worker;
        speaker?.Dispose();
        stop.Dispose();
    }
}
#endif
