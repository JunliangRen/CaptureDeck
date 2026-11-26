using System.Numerics;
using CaptureRack.Core;

namespace CaptureRack.DeckLink;

public sealed record StereoLevels(double LeftPeak, double RightPeak, long LeftClips, long RightClips,
    double? PhaseCorrelation, IReadOnlyList<float>? Spectrum)
{
    public uint SampleRate { get; init; }
    public uint SampleBits { get; init; }
    public double SpectrumFloorDbfs { get; init; }
    public int ChannelCount { get; init; } = 2;
    public AudioChannelLayout ChannelLayout { get; init; } = AudioChannelLayout.Stereo;
    public AudioMonitorRoute MonitorRoute { get; init; } = AudioMonitorRoute.Default;
    public IReadOnlyList<double>? ChannelPeaks { get; init; }
    public IReadOnlyList<long>? ChannelClips { get; init; }

    public long TotalClips
    {
        get
        {
            if (ChannelClips is not { Count: > 0 } clips)
                return LeftClips + RightClips;

            long total = 0;
            for (var i = 0; i < clips.Count; i++)
                total += clips[i];
            return total;
        }
    }

    public double PeakForChannel(int index)
    {
        if (ChannelPeaks is { Count: > 0 } peaks)
            return index >= 0 && index < peaks.Count ? peaks[index] : 0;
        return index switch { 0 => LeftPeak, 1 => RightPeak, _ => 0 };
    }

    public long ClipsForChannel(int index)
    {
        if (ChannelClips is { Count: > 0 } clips)
            return index >= 0 && index < clips.Count ? clips[index] : 0;
        return index switch { 0 => LeftClips, 1 => RightClips, _ => 0 };
    }
}

public enum CaptureEventKind { Unknown, TimestampRegression, FrameGap, NoInputAtStart, SignalLost, SignalRestored }

public sealed record CaptureEvent(TimeSpan Elapsed, string Description)
{
    public CaptureEventKind Kind { get; init; }
    public long MissingFrames { get; init; }
    public TimeSpan? SignalLossDuration { get; init; }

    public string TimeLabel =>
        $"{(int)Elapsed.TotalHours:00}:{Elapsed.Minutes:00}:{Elapsed.Seconds:00}.{Elapsed.Milliseconds:000}";
}

internal sealed class FrameTimeline(VideoMode mode, bool keepSignalHistory = false)
{
    private const int MaxEvents = 200;
    private readonly Queue<CaptureEvent> events = new();
    private IReadOnlyList<CaptureEvent>? snapshot;
    private long? firstTime;
    private readonly FrameTimestampTracker timestamps = new(mode.FrameDuration);
    private bool? previousNoInput;
    private TimeSpan? lossStarted;
    private bool startedUnlocked;
    private TimeSpan completedLossDuration;
    private readonly List<VideoSignalLoss>? signalLosses = keepSignalHistory ? [] : null;
    public long MissingFrames { get; private set; }
    public long SignalLossCount { get; private set; }

    public TimeSpan SignalLossDuration(TimeSpan elapsed) =>
        completedLossDuration + (lossStarted is { } start ? elapsed - start : TimeSpan.Zero);

    public IReadOnlyList<VideoSignalLoss> SignalLosses(TimeSpan elapsed)
    {
        if (signalLosses is null)
            return [];
        return lossStarted is { } start
            ? [.. signalLosses, new(start, elapsed - start, startedUnlocked, Restored: false)]
            : signalLosses.ToArray();
    }

    public void Observe(long time, bool noInput, TimeSpan? observedElapsed = null)
    {
        firstTime ??= time;
        var elapsedTicks = ((decimal)time - firstTime.Value) * TimeSpan.TicksPerSecond / mode.TimeScale;
        var elapsed = observedElapsed ?? TimeSpan.FromTicks((long)Math.Clamp(elapsedTicks, 0, TimeSpan.MaxValue.Ticks));

        if (timestamps.Observe(time) is { } interval)
        {
            if (interval.Corrected)
                Add(elapsed, CaptureEventKind.TimestampRegression, "Video timestamp discontinuity.");
            if (interval.SuspectedMissingFrames > 0)
            {
                MissingFrames += interval.SuspectedMissingFrames;
                Add(elapsed, CaptureEventKind.FrameGap,
                    $"Video timestamp gap · {interval.SuspectedMissingFrames:N0} suspected missing frame(s).",
                    interval.SuspectedMissingFrames);
            }
        }

        ObserveSignal(elapsed, noInput);
    }

    public void ObserveSignal(TimeSpan elapsed, bool noInput)
    {
        if (previousNoInput == noInput)
            return;

        if (noInput)
        {
            lossStarted = elapsed;
            startedUnlocked = previousNoInput is null;
            if (!startedUnlocked)
                SignalLossCount++;
            Add(elapsed, startedUnlocked ? CaptureEventKind.NoInputAtStart : CaptureEventKind.SignalLost,
                startedUnlocked ? "No input signal at start." : "Video input signal lost.");
        }
        else if (lossStarted is { } start)
        {
            var duration = elapsed - start;
            completedLossDuration += duration;
            signalLosses?.Add(new(start, duration, startedUnlocked, Restored: true));
            lossStarted = null;
            Add(elapsed, CaptureEventKind.SignalRestored, $"Video input signal restored after {duration.TotalSeconds:F3} s.",
                signalLossDuration: duration);
        }
        previousNoInput = noInput;
    }

    private void Add(TimeSpan elapsed, CaptureEventKind kind, string message, long missing = 0,
        TimeSpan? signalLossDuration = null)
    {
        if (events.Count == MaxEvents)
            events.Dequeue();
        events.Enqueue(new(elapsed, message) { Kind = kind, MissingFrames = missing, SignalLossDuration = signalLossDuration });
        snapshot = null;
    }

    public IReadOnlyList<CaptureEvent> Events() => snapshot ??= events.Reverse().ToArray();
}

public sealed class StereoMonitor(CaptureAudioFormat format, uint sampleRate = CaptureAudio.SampleRate,
    uint? sourceBits = null, int channels = 2, AudioChannelLayout layout = AudioChannelLayout.Unknown)
{
    private const int SpectrumWindow = 2048;
    private const int DisplayFramesPerSecond = 60;
    private const int BufferedDisplayFrames = 4;
    private const double ActivityThresholdLinear = 0.000251188643150958; // -72 dBFS
    private readonly int channelCount = Math.Clamp(channels, 1, AudioMonitorRoute.MaxSupportedChannels);
    private readonly AudioChannelLayout channelLayout = layout == AudioChannelLayout.Unknown
        ? AudioChannelLayoutHelper.Detect(null, Math.Clamp(channels, 1, AudioMonitorRoute.MaxSupportedChannels))
        : layout;
    private readonly int displayHop = SamplesPerDisplayFrame(sampleRate);
    private readonly double[] leftRing = new double[SpectrumWindow + BufferedDisplayFrames * SamplesPerDisplayFrame(sampleRate)];
    private readonly double[] rightRing = new double[SpectrumWindow + BufferedDisplayFrames * SamplesPerDisplayFrame(sampleRate)];
    private readonly double fullScale = format == CaptureAudioFormat.Pcm16 ? 32768.0 : 2147483648.0;
    private readonly long nearFullScale = format == CaptureAudioFormat.Pcm16 ? short.MaxValue : 0x7FFFFF00L;
    private readonly long[] channelPeaks = new long[Math.Clamp(channels, 1, AudioMonitorRoute.MaxSupportedChannels)];
    private readonly long[] channelClips = new long[Math.Clamp(channels, 1, AudioMonitorRoute.MaxSupportedChannels)];
    private readonly long[] pairActiveUntilSample = new long[(Math.Clamp(channels, 1, AudioMonitorRoute.MaxSupportedChannels) + 1) / 2];
    private readonly long activityHoldSamples = Math.Max(1, (long)sampleRate * 2);
    private int packedRoute = AudioMonitorRoute.Default.Pack();
    private int activePairMask = 1;
    private long receivedSamples;
    private long lastDisplaySample;
    private StereoLevels? lastLevel;
    private long monitoredLeftPeak;
    private long monitoredRightPeak;
    private double leftPower;
    private double rightPower;
    private double crossPower;
    private bool receivedAudio;

    public int Channels => channelCount;
    public AudioChannelLayout Layout => channelLayout;
    public int ActivePairMask => Volatile.Read(ref activePairMask);

    public AudioMonitorRoute Route
    {
        get => AudioMonitorRoute.Unpack(Volatile.Read(ref packedRoute)).ClampForChannels(channelCount);
        set => Volatile.Write(ref packedRoute, value.ClampForChannels(channelCount).Pack());
    }

    public (long Left, long Right) ClipCounts =>
        (channelClips[0], channelCount > 1 ? channelClips[1] : 0);

    public long[] CopyChannelClips() => (long[])channelClips.Clone();

    public void Add(int left, int right)
    {
        var leftMagnitude = Math.Abs((long)left);
        var rightMagnitude = Math.Abs((long)right);
        channelPeaks[0] = Math.Max(channelPeaks[0], leftMagnitude);
        if (leftMagnitude >= nearFullScale)
            channelClips[0]++;
        if (channelCount > 1)
        {
            channelPeaks[1] = Math.Max(channelPeaks[1], rightMagnitude);
            if (rightMagnitude >= nearFullScale)
                channelClips[1]++;
        }

        var l = left / fullScale;
        var r = right / fullScale;
        RecordMonitoredSample(l, r, leftMagnitude, rightMagnitude);
    }

    public (double Left, double Right) AddFrame(ReadOnlySpan<int> frameSamples)
    {
        if (channelCount == 2 && frameSamples.Length >= 2)
        {
            Add(frameSamples[0], frameSamples[1]);
            return (frameSamples[0] / fullScale, frameSamples[1] / fullScale);
        }

        Span<double> normalized = stackalloc double[channelCount];
        var activeThreshold = (long)(fullScale * ActivityThresholdLinear);
        var mask = 1;
        for (var ch = 0; ch < channelCount; ch++)
        {
            var raw = ch < frameSamples.Length ? frameSamples[ch] : 0;
            var mag = Math.Abs((long)raw);
            if (mag > channelPeaks[ch])
                channelPeaks[ch] = mag;
            if (mag >= nearFullScale)
                channelClips[ch]++;

            var pair = ch >> 1;
            if (mag > activeThreshold)
                pairActiveUntilSample[pair] = receivedSamples + activityHoldSamples;
            if (pairActiveUntilSample[pair] > receivedSamples)
                mask |= 1 << pair;

            normalized[ch] = raw / fullScale;
        }
        Volatile.Write(ref activePairMask, mask);

        var (l, r) = AudioMonitorMixer.RouteFrame(normalized, Route, channelLayout, mask);
        RecordMonitoredSample(l, r, (long)Math.Round(Math.Abs(l) * fullScale), (long)Math.Round(Math.Abs(r) * fullScale));
        return (l, r);
    }

    public (double Left, double Right) AddFrame(ReadOnlySpan<short> frameSamples)
    {
        if (channelCount == 2 && frameSamples.Length >= 2)
        {
            Add(frameSamples[0], frameSamples[1]);
            return (frameSamples[0] / fullScale, frameSamples[1] / fullScale);
        }

        Span<double> normalized = stackalloc double[channelCount];
        var activeThreshold = (long)(fullScale * ActivityThresholdLinear);
        var mask = 1;
        for (var ch = 0; ch < channelCount; ch++)
        {
            var raw = ch < frameSamples.Length ? frameSamples[ch] : (short)0;
            var mag = Math.Abs((long)raw);
            if (mag > channelPeaks[ch])
                channelPeaks[ch] = mag;
            if (mag >= nearFullScale)
                channelClips[ch]++;

            var pair = ch >> 1;
            if (mag > activeThreshold)
                pairActiveUntilSample[pair] = receivedSamples + activityHoldSamples;
            if (pairActiveUntilSample[pair] > receivedSamples)
                mask |= 1 << pair;

            normalized[ch] = raw / fullScale;
        }
        Volatile.Write(ref activePairMask, mask);

        var (l, r) = AudioMonitorMixer.RouteFrame(normalized, Route, channelLayout, mask);
        RecordMonitoredSample(l, r, (long)Math.Round(Math.Abs(l) * fullScale), (long)Math.Round(Math.Abs(r) * fullScale));
        return (l, r);
    }

    private void RecordMonitoredSample(double l, double r, long leftMag, long rightMag)
    {
        monitoredLeftPeak = Math.Max(monitoredLeftPeak, leftMag);
        monitoredRightPeak = Math.Max(monitoredRightPeak, rightMag);

        // Accumulate correlation and save a bounded sample window without doing FFT in the SDK callback.
        leftPower += l * l;
        rightPower += r * r;
        crossPower += l * r;
        var index = (int)(receivedSamples % leftRing.Length);
        leftRing[index] = l;
        rightRing[index] = r;
        receivedSamples++;
        receivedAudio = true;
    }

    private static int SamplesPerDisplayFrame(uint rate) =>
        (int)Math.Clamp(rate / DisplayFramesPerSecond, 1u, 768_000u / DisplayFramesPerSecond);

    public AudioWindow? Read() => receivedAudio ? ReadWindow(receivedSamples, ReadLevels()) : null;

    // Use overlapping windows so one capture packet can feed several display frames.
    // If the display falls behind, stay within the most recent buffered audio.
    public AudioWindow? ReadForDisplay()
    {
        long end = Math.Max(displayHop, lastDisplaySample + displayHop);
        if (end > receivedSamples)
            return null;
        if (end < receivedSamples - (long)BufferedDisplayFrames * displayHop)
            end = receivedSamples - displayHop;
        lastDisplaySample = end;
        var level = receivedAudio ? ReadLevels() : lastLevel! with { MonitorRoute = Route };
        return ReadWindow(end, level);
    }

    private StereoLevels ReadLevels()
    {
        var route = Route;
        var power = Math.Sqrt(leftPower * rightPower);
        var peaks = new double[channelCount];
        var clips = new long[channelCount];
        for (var ch = 0; ch < channelCount; ch++)
        {
            peaks[ch] = channelPeaks[ch] / fullScale;
            clips[ch] = channelClips[ch];
            channelPeaks[ch] = 0;
        }

        double leftPeak;
        double rightPeak;
        long leftClips;
        long rightClips;
        if (channelCount == 1)
        {
            leftPeak = rightPeak = peaks[0];
            leftClips = rightClips = clips[0];
        }
        else if (route.Mode == AudioMonitorMode.Downmix && channelCount > 2)
        {
            leftPeak = monitoredLeftPeak / fullScale;
            rightPeak = monitoredRightPeak / fullScale;
            leftClips = clips[0];
            rightClips = clips[1];
        }
        else
        {
            var pairIdx = Math.Clamp(route.PairIndex, 0, (channelCount - 1) / 2);
            var li = pairIdx * 2;
            var ri = li + 1;
            leftPeak = peaks[li];
            rightPeak = ri < channelCount ? peaks[ri] : peaks[li];
            leftClips = clips[li];
            rightClips = ri < channelCount ? clips[ri] : clips[li];
        }

        var level = new StereoLevels(leftPeak, rightPeak, leftClips, rightClips,
            power > 0 ? Math.Clamp(crossPower / power, -1, 1) : null, null)
        {
            SampleRate = sampleRate,
            SampleBits = sourceBits ?? (uint)format,
            SpectrumFloorDbfs = -20 * Math.Log10(Math.ScaleB(1, (int)(sourceBits ?? (uint)format) - 1)),
            ChannelCount = channelCount,
            ChannelLayout = channelLayout,
            MonitorRoute = route,
            ChannelPeaks = peaks,
            ChannelClips = clips
        };
        monitoredLeftPeak = monitoredRightPeak = 0;
        leftPower = rightPower = crossPower = 0;
        receivedAudio = false;
        return lastLevel = level;
    }

    private AudioWindow ReadWindow(long end, StereoLevels level)
    {
        var left = new double[SpectrumWindow];
        var right = new double[SpectrumWindow];
        var sampleCount = (int)Math.Min(end, SpectrumWindow);
        var start = end - sampleCount;
        var ringStart = (int)(start % leftRing.Length);
        var firstPart = Math.Min(sampleCount, leftRing.Length - ringStart);
        var target = SpectrumWindow - sampleCount;
        leftRing.AsSpan(ringStart, firstPart).CopyTo(left.AsSpan(target));
        rightRing.AsSpan(ringStart, firstPart).CopyTo(right.AsSpan(target));
        leftRing.AsSpan(0, sampleCount - firstPart).CopyTo(left.AsSpan(target + firstPart));
        rightRing.AsSpan(0, sampleCount - firstPart).CopyTo(right.AsSpan(target + firstPart));
        return new AudioWindow(level, left, right);
    }

    public sealed record AudioWindow(StereoLevels Level, double[] Left, double[] Right)
    {
        private static readonly double[] Hann = Enumerable.Range(0, SpectrumWindow)
            .Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (SpectrumWindow - 1))).ToArray();

        public StereoLevels Analyze()
        {
            var left = Transform(Left);
            var right = Transform(Right);
            var spectrum = new float[SpectrumWindow / 8];
            for (var i = 0; i < spectrum.Length; i++)
            {
                var power = 0.0;
                for (var bin = i * 4; bin < i * 4 + 4; bin++)
                {
                    var l = left[bin].Magnitude;
                    var r = right[bin].Magnitude;
                    power = Math.Max(power, (l * l + r * r) / 2);
                }

                var magnitude = Math.Sqrt(power) * 4 / SpectrumWindow;
                spectrum[i] = (float)Math.Max(20 * Math.Log10(magnitude), Level.SpectrumFloorDbfs);
            }
            return Level with { Spectrum = spectrum };
        }

        private static Complex[] Transform(double[] input)
        {
            var bins = new Complex[SpectrumWindow];
            for (int i = 0, reversed = 0; i < bins.Length; i++)
            {
                bins[reversed] = new Complex(input[i] * Hann[i], 0);
                var bit = bins.Length >> 1;
                for (; bit != 0 && (reversed & bit) != 0; bit >>= 1)
                    reversed ^= bit;
                reversed ^= bit;
            }

            for (var size = 2; size <= bins.Length; size <<= 1)
            {
                var half = size / 2;
                var rotation = Complex.FromPolarCoordinates(1, -2 * Math.PI / size);
                for (var block = 0; block < bins.Length; block += size)
                {
                    var phase = Complex.One;
                    for (var offset = 0; offset < half; offset++)
                    {
                        var a = block + offset;
                        var b = a + half;
                        var even = bins[a];
                        var odd = bins[b] * phase;
                        bins[a] = even + odd;
                        bins[b] = even - odd;
                        phase *= rotation;
                    }
                }
            }
            return bins;
        }
    }
}
