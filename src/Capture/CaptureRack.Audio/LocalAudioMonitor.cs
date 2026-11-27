using System.Runtime.InteropServices;

namespace CaptureRack.Audio;

// Non-blocking local speaker monitor for stereo PCM preview.
// Drops excess packets instead of stalling the capture callback, and never alters recorded audio.
public sealed class LocalAudioMonitor : IDisposable
{
    private readonly WasapiPlayback playback;
    private double volume = 0.8;
    public TimeSpan? PlayedMediaTime => playback.PlayedMediaTime;
    public bool Enabled { get => playback.Enabled; set => playback.Enabled = value; }

    public double Volume
    {
        get => Volatile.Read(ref volume);
        set => Volatile.Write(ref volume, Math.Clamp(value, 0.0, 1.0));
    }

    public LocalAudioMonitor(uint sampleRate = 48000) : this(new WasapiPlayback(sampleRate)) { }

    internal LocalAudioMonitor(WasapiPlayback playback)
    {
        this.playback = playback;
    }

    public void SubmitPcm16(ReadOnlySpan<short> samples, TimeSpan? mediaEnd = null)
    {
        if (!Enabled || samples.IsEmpty)
            return;

        var gain = Volume;
        if (gain <= 0.0001) return;
        var bytes = new byte[samples.Length * 2];
        var dest = MemoryMarshal.Cast<byte, short>(bytes);

        for (var i = 0; i < samples.Length; i++)
            dest[i] = (short)Math.Clamp((int)Math.Round(samples[i] * gain), short.MinValue, short.MaxValue);
        playback.Submit(bytes, mediaEnd);
    }

    private void SubmitPcm32(ReadOnlySpan<int> samples)
    {
        if (!Enabled || samples.IsEmpty)
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
        playback.Submit(bytes, null);
    }

    public void SubmitInterleavedPcm16(ReadOnlySpan<short> samples, int channels, AudioMonitorRoute route,
        AudioChannelLayout layout = AudioChannelLayout.Unknown, int activePairMask = 1)
    {
        if (!Enabled || samples.IsEmpty || channels <= 0)
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
        playback.Submit(bytes, null);
    }

    public void SubmitInterleavedPcm32(ReadOnlySpan<int> samples, int channels, AudioMonitorRoute route,
        AudioChannelLayout layout = AudioChannelLayout.Unknown, int activePairMask = 1)
    {
        if (!Enabled || samples.IsEmpty || channels <= 0)
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
        playback.Submit(bytes, null);
    }

    public void Dispose() => playback.Dispose();
}
