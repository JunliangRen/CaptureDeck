using System.Threading.Channels;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CaptureRack.Audio;

// All endpoint COM objects and the render event belong to the playback thread.
// The source can continue delivering or recording while the endpoint is unavailable.
public sealed class WasapiPlayback : IDisposable
{
    internal sealed class Session
    {
        private sealed record Packet(byte[] Data, TimeSpan? End);
        private readonly Channel<Packet> packets = Channel.CreateBounded<Packet>(
            new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private Packet? pending;
        private int offset;
        internal long PlayedTicks = long.MinValue;

        internal void Submit(byte[] data, TimeSpan? end)
        {
            if (data.Length >= 4 && data.Length % 4 == 0) packets.Writer.TryWrite(new(data, end));
        }

        // One render thread consumes whole stereo frames, retaining an unfinished packet.
        internal bool Take(int capacity, out ArraySegment<byte> bytes, out TimeSpan? end, out int trailingFrames)
        {
            bytes = default;
            end = null;
            trailingFrames = 0;
            if (capacity <= 0 || (pending is null && !packets.Reader.TryRead(out pending))) return false;
            int remaining = (pending.Data.Length - offset) / 4;
            int frames = Math.Min(capacity, remaining);
            bytes = new(pending.Data, offset, frames * 4);
            end = pending.End;
            trailingFrames = remaining - frames;
            offset += bytes.Count;
            if (trailingFrames == 0)
            {
                pending = null;
                offset = 0;
            }
            return true;
        }
    }

    internal sealed class MediaTimeline(int rate)
    {
        private readonly Queue<(long Start, long End, long MediaEnd)> anchors = new();

        internal void Add(long start, int frames, long endTicks, int trailingFrames) =>
            anchors.Enqueue((start, start + frames, MediaTicksAt(endTicks, trailingFrames, rate)));

        internal long Read(bool valid, long played)
        {
            if (!valid) return long.MinValue;
            while (anchors.TryPeek(out var old) && old.End <= played) anchors.Dequeue();
            return anchors.TryPeek(out var anchor) && played >= anchor.Start
                ? MediaTicksAt(anchor.MediaEnd, anchor.End - played, rate) : long.MinValue;
        }
    }

    private readonly Lock lifecycle = new();
    private readonly AutoResetEvent changed = new(false);
    private readonly Thread worker;
    private readonly int rate;
    private volatile Session? active;
    private volatile bool disposed;
    private readonly Action<Session, string> play;

    public WasapiPlayback(uint sampleRate) : this(sampleRate, null) { }

    internal WasapiPlayback(uint sampleRate, Action<Session, string>? play)
    {
        this.play = play ?? OpenAndRender;
        rate = checked((int)sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        worker = new Thread(Run) { IsBackground = true, Name = "CaptureRack WASAPI monitor" };
        worker.Start();
    }

    public bool Enabled
    {
        get => active is not null;
        set
        {
            lock (lifecycle)
            {
                if (Enabled == value || disposed) return;
                active = value ? new Session() : null;
                changed.Set();
            }
        }
    }

    public TimeSpan? PlayedMediaTime
    {
        get
        {
            var session = active;
            long ticks = session is null ? long.MinValue : Volatile.Read(ref session.PlayedTicks);
            return ticks == long.MinValue ? null : TimeSpan.FromTicks(ticks);
        }
    }

    public void Submit(byte[] stereoPcm16, TimeSpan? mediaEnd) => active?.Submit(stereoPcm16, mediaEnd);

    private void Run()
    {
        try
        {
            while (!disposed)
            {
                var session = active;
                if (session is null) { changed.WaitOne(); continue; }
                try { play(session, PlaybackDevices.SelectedId); }
                catch (Exception ex) when (PlaybackDevices.IsDeviceError(ex))
                {
                    Trace.TraceWarning("WASAPI playback: {0}", ex.Message);
                    Volatile.Write(ref session.PlayedTicks, long.MinValue);
                    changed.WaitOne(500);
                }
                lock (lifecycle)
                {
                    if (active == session) active = new Session();
                }
            }
        }
        finally
        {
            lock (lifecycle) changed.Dispose();
        }
    }

    private void OpenAndRender(Session session, string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = PlaybackDevices.Open(enumerator, id);
        Render(enumerator, device, id, session);
    }

    private void Render(MMDeviceEnumerator enumerator, MMDevice device, string id, Session session)
    {
        using var ready = new AutoResetEvent(false);
        using var client = device.CreateAudioClient();
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback |
            AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            TimeSpan.FromMilliseconds(40).Ticks, 0, new WaveFormat(rate, 16, 2), Guid.Empty);
        client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
        var render = client.AudioRenderClient;
        var clock = client.AudioClockClient;
        ulong frequency = clock.Frequency;
        if (frequency == 0) throw new InvalidOperationException("Invalid WASAPI audio clock frequency.");
        int capacity = client.BufferSize;
        var timeline = new MediaTimeline(rate);
        long nextDeviceCheck = Environment.TickCount64 + 1000;
        string endpoint = device.ID;
        WaitHandle[] signals = [ready, changed];
        client.Start();
        try
        {
            while (session == active && id == PlaybackDevices.SelectedId)
            {
                if (id.Length == 0 && Environment.TickCount64 >= nextDeviceCheck)
                {
                    using var current = PlaybackDevices.Open(enumerator, id);
                    if (current.ID != endpoint) break;
                    nextDeviceCheck = Environment.TickCount64 + 1000;
                }

                int padding = client.CurrentPadding;
                bool clockValid = clock.GetPosition(out ulong position, out _);
                long played = (long)((UInt128)position * (uint)rate / frequency);
                Volatile.Write(ref session.PlayedTicks, timeline.Read(clockValid, played));

                int available = capacity - padding;
                int copied = 0;
                while (session == active && session.Take(available - copied, out var bytes, out var mediaEnd, out int trailing))
                {
                    int frames = bytes.Count / 4;
                    var pointer = render.GetBuffer(frames);
                    Marshal.Copy(bytes.Array!, bytes.Offset, pointer, bytes.Count);
                    render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
                    if (clockValid && mediaEnd is { } end)
                    {
                        long start = played + padding + copied;
                        timeline.Add(start, frames, end.Ticks, trailing);
                    }
                    copied += frames;
                }
                WaitHandle.WaitAny(signals, 100);
            }
        }
        finally { client.Stop(); }
    }

    internal static long MediaTicksAt(long endTicks, long remainingFrames, int sampleRate) =>
        endTicks - remainingFrames * TimeSpan.TicksPerSecond / sampleRate;

    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed) return;
            active = null;
            disposed = true;
            changed.Set();
        }
        // The worker owns the endpoint and wait handles. A stalled driver must not hold the caller.
        worker.Join(250);
    }
}
