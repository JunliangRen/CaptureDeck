using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CaptureRack.DeckLink;

// Non-blocking local speaker monitor for stereo PCM preview.
// Drops excess packets instead of stalling the DeckLink audio callback, and never alters recorded audio.
public sealed class LocalAudioMonitor : IDisposable
{
    private const int BufferCount = 6;
    private const int MaxQueuedPackets = 8;
    private readonly ConcurrentQueue<byte[]> queue = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly CancellationTokenSource stop = new();
    private readonly Thread worker;
    private readonly uint sampleRate;
    private volatile bool enabled;
    private double volume = 0.8;
    private bool disposed;

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
                return;

            enabled = value;
            if (!value)
                DrainQueue();
            wake.Set();
        }
    }

    public double Volume
    {
        get => Volatile.Read(ref volume);
        set => Volatile.Write(ref volume, Math.Clamp(value, 0.0, 1.0));
    }

    public LocalAudioMonitor(uint sampleRate = CaptureAudio.SampleRate)
    {
        this.sampleRate = sampleRate;
        worker = new Thread(RunPlaybackLoop)
        {
            IsBackground = true,
            Name = "CaptureRack Audio Monitor"
        };
        worker.Start();
    }

    public void SubmitPcm16(ReadOnlySpan<short> samples)
    {
        if (!enabled || disposed || samples.IsEmpty)
            return;

        var gain = Volume;
        var bytes = new byte[samples.Length * 2];
        var dest = MemoryMarshal.Cast<byte, short>(bytes);
        if (gain <= 0.0001)
            return;

        for (var i = 0; i < samples.Length; i++)
            dest[i] = (short)Math.Clamp((int)Math.Round(samples[i] * gain), short.MinValue, short.MaxValue);
        Enqueue(bytes);
    }

    internal void SubmitPcm32(ReadOnlySpan<int> samples)
    {
        if (!enabled || disposed || samples.IsEmpty)
            return;

        var gain = Volume;
        if (gain <= 0.0001)
            return;

        var bytes = new byte[samples.Length * 2];
        var dest = MemoryMarshal.Cast<byte, short>(bytes);
        for (var i = 0; i < samples.Length; i++)
        {
            var high16 = samples[i] >> 16;
            dest[i] = (short)Math.Clamp((int)Math.Round(high16 * gain), short.MinValue, short.MaxValue);
        }
        Enqueue(bytes);
    }

    public void SubmitInterleavedPcm16(ReadOnlySpan<short> samples, int channels, AudioMonitorRoute route,
        AudioChannelLayout layout = AudioChannelLayout.Unknown, int activePairMask = 1)
    {
        if (!enabled || disposed || samples.IsEmpty || channels <= 0)
            return;
        if (channels == 2)
        {
            SubmitPcm16(samples);
            return;
        }

        var gain = Volume;
        if (gain <= 0.0001)
            return;

        var frameCount = samples.Length / channels;
        if (frameCount <= 0)
            return;

        var safeChannels = Math.Min(channels, AudioMonitorRoute.MaxSupportedChannels);
        var bytes = new byte[frameCount * 4];
        var dest = MemoryMarshal.Cast<byte, short>(bytes);
        Span<double> frame = stackalloc double[safeChannels];
        var scale = 32767.0 * gain;
        for (var f = 0; f < frameCount; f++)
        {
            var baseOffset = f * channels;
            for (var ch = 0; ch < safeChannels; ch++)
                frame[ch] = samples[baseOffset + ch] / 32768.0;
            var (l, r) = AudioMonitorMixer.RouteFrame(frame, route, layout, activePairMask);
            dest[f * 2] = (short)Math.Clamp((int)Math.Round(l * scale), short.MinValue, short.MaxValue);
            dest[f * 2 + 1] = (short)Math.Clamp((int)Math.Round(r * scale), short.MinValue, short.MaxValue);
        }
        Enqueue(bytes);
    }

    internal void SubmitInterleavedPcm32(ReadOnlySpan<int> samples, int channels, AudioMonitorRoute route,
        AudioChannelLayout layout = AudioChannelLayout.Unknown, int activePairMask = 1)
    {
        if (!enabled || disposed || samples.IsEmpty || channels <= 0)
            return;
        if (channels == 2)
        {
            SubmitPcm32(samples);
            return;
        }

        var gain = Volume;
        if (gain <= 0.0001)
            return;

        var frameCount = samples.Length / channels;
        if (frameCount <= 0)
            return;

        var safeChannels = Math.Min(channels, AudioMonitorRoute.MaxSupportedChannels);
        var bytes = new byte[frameCount * 4];
        var dest = MemoryMarshal.Cast<byte, short>(bytes);
        Span<double> frame = stackalloc double[safeChannels];
        var scale = 32767.0 * gain;
        for (var f = 0; f < frameCount; f++)
        {
            var baseOffset = f * channels;
            for (var ch = 0; ch < safeChannels; ch++)
                frame[ch] = samples[baseOffset + ch] / 2147483648.0;
            var (l, r) = AudioMonitorMixer.RouteFrame(frame, route, layout, activePairMask);
            dest[f * 2] = (short)Math.Clamp((int)Math.Round(l * scale), short.MinValue, short.MaxValue);
            dest[f * 2 + 1] = (short)Math.Clamp((int)Math.Round(r * scale), short.MinValue, short.MaxValue);
        }
        Enqueue(bytes);
    }

    private void Enqueue(byte[] packet)
    {
        while (queue.Count >= MaxQueuedPackets)
            queue.TryDequeue(out _);
        queue.Enqueue(packet);
        wake.Set();
    }

    private void DrainQueue()
    {
        while (queue.TryDequeue(out _))
        {
        }
    }

    private void RunPlaybackLoop()
    {
        nint device = 0;
        var active = new List<(nint HeaderPtr, GCHandle BufferHandle)>(BufferCount);
        var token = stop.Token;

        void CloseDevice()
        {
            if (device == 0)
                return;

            waveOutReset(device);
            ReclaimCompleted(device, active, forceAll: true);
            waveOutClose(device);
            device = 0;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!enabled)
                {
                    CloseDevice();
                    wake.WaitOne(100);
                    continue;
                }

                if (device == 0)
                {
                    var format = new WaveFormatEx
                    {
                        FormatTag = 1, // WAVE_FORMAT_PCM
                        Channels = (ushort)CaptureAudio.Channels,
                        SamplesPerSec = sampleRate,
                        BitsPerSample = 16,
                        BlockAlign = (ushort)(CaptureAudio.Channels * 2),
                        AvgBytesPerSec = sampleRate * CaptureAudio.Channels * 2,
                        Size = 0
                    };
                    if (waveOutOpen(out device, unchecked((uint)-1), ref format, 0, 0, 0) != 0)
                    {
                        device = 0;
                        wake.WaitOne(250);
                        continue;
                    }
                }

                ReclaimCompleted(device, active, forceAll: false);
                var submitted = false;
                while (enabled && active.Count < BufferCount && queue.TryDequeue(out var packet))
                {
                    if (SubmitWaveBuffer(device, packet, active))
                        submitted = true;
                }

                if (!submitted)
                    wake.WaitOne(12);
            }
        }
        finally
        {
            CloseDevice();
        }
    }

    private static bool SubmitWaveBuffer(nint device, byte[] packet, List<(nint HeaderPtr, GCHandle BufferHandle)> active)
    {
        var bufferHandle = GCHandle.Alloc(packet, GCHandleType.Pinned);
        var headerSize = Marshal.SizeOf<WaveHdr>();
        var headerPtr = Marshal.AllocHGlobal(headerSize);
        var prepared = false;
        try
        {
            var header = new WaveHdr
            {
                Data = bufferHandle.AddrOfPinnedObject(),
                BufferLength = (uint)packet.Length
            };
            Marshal.StructureToPtr(header, headerPtr, false);
            if (waveOutPrepareHeader(device, headerPtr, (uint)headerSize) != 0)
                return false;
            prepared = true;
            if (waveOutWrite(device, headerPtr, (uint)headerSize) != 0)
                return false;

            active.Add((headerPtr, bufferHandle));
            return true;
        }
        finally
        {
            if (active.Count == 0 || active[^1].HeaderPtr != headerPtr)
            {
                if (prepared)
                    waveOutUnprepareHeader(device, headerPtr, (uint)headerSize);
                Marshal.FreeHGlobal(headerPtr);
                bufferHandle.Free();
            }
        }
    }

    private static void ReclaimCompleted(nint device, List<(nint HeaderPtr, GCHandle BufferHandle)> active, bool forceAll)
    {
        const uint doneFlag = 0x00000001; // WHDR_DONE
        var headerSize = (uint)Marshal.SizeOf<WaveHdr>();
        for (var i = active.Count - 1; i >= 0; i--)
        {
            var (headerPtr, bufferHandle) = active[i];
            var header = Marshal.PtrToStructure<WaveHdr>(headerPtr);
            if (!forceAll && (header.Flags & doneFlag) == 0)
                continue;

            waveOutUnprepareHeader(device, headerPtr, headerSize);
            Marshal.FreeHGlobal(headerPtr);
            bufferHandle.Free();
            active.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        enabled = false;
        stop.Cancel();
        wake.Set();
        worker.Join(1000);
        DrainQueue();
        wake.Dispose();
        stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutOpen(out nint handle, uint deviceId, ref WaveFormatEx format,
        nint callback, nint instance, uint flags);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutPrepareHeader(nint handle, nint header, uint size);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutWrite(nint handle, nint header, uint size);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutUnprepareHeader(nint handle, nint header, uint size);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutReset(nint handle);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveOutClose(nint handle);
}
