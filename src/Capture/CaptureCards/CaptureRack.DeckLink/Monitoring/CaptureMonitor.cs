using System.Numerics;
using CaptureRack.Core;

namespace CaptureRack.DeckLink;

public sealed record StereoLevels(double LeftPeak, double RightPeak, long LeftClips, long RightClips,
    double? PhaseCorrelation, IReadOnlyList<float>? Spectrum)
{
    public uint SampleRate { get; init; }
    public uint SampleBits { get; init; }
    public double SpectrumFloorDbfs { get; init; }
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

internal sealed class StereoMonitor(CaptureAudioFormat format)
{
    private const int SpectrumWindow = 2048;
    private readonly double[] leftRing = new double[SpectrumWindow];
    private readonly double[] rightRing = new double[SpectrumWindow];
    private readonly double fullScale = format == CaptureAudioFormat.Pcm16 ? 32768.0 : 2147483648.0;
    private readonly long nearFullScale = format == CaptureAudioFormat.Pcm16 ? short.MaxValue : 0x7FFFFF00L;
    private int next;
    private int sampleCount;
    private long leftClips;
    private long rightClips;
    private long leftPeak;
    private long rightPeak;
    private double leftPower;
    private double rightPower;
    private double crossPower;
    private bool receivedAudio;

    public (long Left, long Right) ClipCounts => (leftClips, rightClips);

    public void Add(int left, int right)
    {
        var leftMagnitude = Math.Abs((long)left);
        var rightMagnitude = Math.Abs((long)right);
        leftPeak = Math.Max(leftPeak, leftMagnitude);
        rightPeak = Math.Max(rightPeak, rightMagnitude);
        if (leftMagnitude >= nearFullScale)
            leftClips++;
        if (rightMagnitude >= nearFullScale)
            rightClips++;

        // Accumulate correlation and save a bounded sample window without doing FFT in the SDK callback.
        var l = left / fullScale;
        var r = right / fullScale;
        leftPower += l * l;
        rightPower += r * r;
        crossPower += l * r;
        leftRing[next] = l;
        rightRing[next] = r;
        next = (next + 1) % SpectrumWindow;
        sampleCount = Math.Min(sampleCount + 1, SpectrumWindow);
        receivedAudio = true;
    }

    public AudioWindow? Read()
    {
        if (!receivedAudio)
            return null;

        var power = Math.Sqrt(leftPower * rightPower);
        var level = new StereoLevels(leftPeak / fullScale, rightPeak / fullScale, leftClips, rightClips,
            power > 0 ? Math.Clamp(crossPower / power, -1, 1) : null, null)
        {
            SampleRate = CaptureAudio.SampleRate,
            SampleBits = (uint)format,
            SpectrumFloorDbfs = -20 * Math.Log10(fullScale)
        };
        var left = new double[SpectrumWindow];
        var right = new double[SpectrumWindow];
        for (var i = 0; i < sampleCount; i++)
        {
            var index = (next - sampleCount + i + SpectrumWindow) % SpectrumWindow;
            left[SpectrumWindow - sampleCount + i] = leftRing[index];
            right[SpectrumWindow - sampleCount + i] = rightRing[index];
        }

        leftPeak = rightPeak = 0;
        leftPower = rightPower = crossPower = 0;
        receivedAudio = false;
        return new AudioWindow(level, left, right);
    }

    internal sealed record AudioWindow(StereoLevels Level, double[] Left, double[] Right)
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
