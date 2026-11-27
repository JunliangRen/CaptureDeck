using CaptureRack.Audio;

namespace CaptureRack.App;

internal enum RfPreviewType { RfOnly, VideoOnly, BetamaxHifi, LaserDisc, Vhs, Betamax, VhsHifi, SVhs, SecamVhs, MesecamVhs }

internal sealed record RfAudioReading(long Generation, string? Track, bool Locked, StereoLevels? Levels);

// One sink per preview selection. Track tokens prevent delayed PCM or lock events
// from the preceding track (including a reacquisition of the same name).
internal sealed class RfPreviewAudio(string? initialTrack = null)
{
    private readonly object gate = new();
    private long generation;
    private string? track = initialTrack;
    private StereoMonitor? monitor;
    private bool locked;

    public long SelectTrack(string name, int channels, uint sampleRate)
    {
        if (channels is < 1 or > 2 || sampleRate == 0) throw new ArgumentOutOfRangeException();
        lock (gate)
        {
            track = name;
            monitor = new StereoMonitor(CaptureAudioFormat.Pcm16, sampleRate, channels: channels);
            locked = false;
            return ++generation;
        }
    }

    public void LoseLock(long token)
    {
        lock (gate)
        {
            if (generation != token) return;
            generation++;
            monitor = null;
            locked = false;
        }
    }

    public void SubmitPcm(long token, ReadOnlySpan<short> pcm)
    {
        lock (gate)
        {
            if (generation != token || monitor is null || pcm.IsEmpty) return;
            if (pcm.Length % monitor.Channels != 0) throw new ArgumentException("Incomplete PCM frame.", nameof(pcm));
            for (int i = 0; i < pcm.Length; i += monitor.Channels)
                monitor.AddFrame(pcm.Slice(i, monitor.Channels));
            locked = true;
        }
    }

    public RfAudioReading Read(bool spectrum = false)
    {
        StereoMonitor.AudioWindow? window;
        RfAudioReading reading;
        lock (gate)
        {
            window = spectrum ? monitor?.Read() : null;
            reading = new(generation, track, locked, window?.Level ?? monitor?.ReadMeterLevels());
        }
        return window is null ? reading : reading with { Levels = window.Analyze() };
    }
}
