using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CaptureRack.DeckLink;

public enum TestSignalScenario { Normal, TimingFaults }

// The generator uses the same sample consumer and MOV writer as hardware capture.
public static class TestSignal
{
    public const string BackendId = "builtin.test-signal";
    public const CapturePixelFormat PixelFormat = CapturePixelFormat.V210;
    public const CaptureAudioFormat AudioFormat = CaptureAudioFormat.Pcm16;
    public static VideoMode Mode { get; } = new(0x6E747363, "525i59.94 NTSC", 720, 486, 1001, 30000, 0x6C6F7772);

    public static CaptureStats Run(TestSignalScenario scenario, PreviewFeed feed, PreviewRecorder recorder,
        LocalAudioMonitor? audioMonitor, Action<DeckLinkProgress> progress, CancellationToken cancellation)
    {
        using var session = new Session(scenario, feed, recorder, audioMonitor);
        var clock = Stopwatch.StartNew();
        var nextProgress = TimeSpan.Zero;
        var readAudio = !recorder.ReadAudioFromUi;
        for (long index = 0; !cancellation.IsCancellationRequested; index++)
        {
            var target = TimeSpan.FromSeconds(index * (double)Mode.FrameDuration / Mode.TimeScale);
            var remaining = target - clock.Elapsed;
            if (remaining > TimeSpan.Zero && cancellation.WaitHandle.WaitOne(remaining))
                break;

            session.Emit(index);
            if (clock.Elapsed >= nextProgress)
            {
                progress(new(Mode, session.Snapshot(readAudio)));
                nextProgress = clock.Elapsed + TimeSpan.FromMilliseconds(100);
            }
        }

        return session.Snapshot(readAudio);
    }

    internal sealed class Session : IDisposable
    {
        private readonly TestSignalScenario scenario;
        private readonly PreviewFeed feed;
        private readonly PreviewRecorder recorder;
        private readonly CaptureCallback callback;
        private readonly GeneratedVideo video;
        private TimeSpan elapsed;
        private readonly GeneratedAudio audio = new();
        private const int CycleFrames = 120;

        internal Session(TestSignalScenario scenario, PreviewFeed feed, PreviewRecorder recorder,
            LocalAudioMonitor? audioMonitor = null)
        {
            this.scenario = scenario;
            this.feed = feed;
            this.recorder = recorder;
            video = new(scenario == TestSignalScenario.TimingFaults);
            // Require a readable frame before opening the MOV, even though the mode is fixed.
            callback = new(feed, PixelFormat, AudioFormat, Mode, null, automaticMode: true, audioMonitor,
                signalClock: () => elapsed);
            callback.UseMonitoringAudioFormat(CaptureAudioFormat.Pcm32);
            recorder.Connect(callback);
        }

        internal void Emit(long index)
        {
            elapsed = TimeSpan.FromTicks(index * Mode.FrameDuration * TimeSpan.TicksPerSecond / Mode.TimeScale);
            var phase = (int)(index % CycleFrames);
            var faults = scenario == TestSignalScenario.TimingFaults;
            var noInput = faults && phase is >= 45 and < 57;
            var skipVideo = faults && phase == 30;
            if (!skipVideo)
                video.Render(index, noInput);

            var start = SampleAt(index);
            var resetOffset = faults
                ? (index / CycleFrames + (phase >= 70 ? 1 : 0)) * 4800
                : 0;
            audio.Render(start, (int)(SampleAt(index + 1) - start), start - resetOffset, noInput);
            callback.AcceptManagedSamples(skipVideo ? null : video, audio);
            feed.ConvertLatest(lumaScaleBits: 10, Mode.PreviewFieldInterval);
        }

        internal CaptureStats Snapshot(bool readAudio = true) => callback.Snapshot(publishMonitor: true, readAudio: readAudio);

        public void Dispose() => recorder.Disconnect();

        private static long SampleAt(long index) =>
            index * Mode.FrameDuration * CaptureAudio.SampleRate / Mode.TimeScale;
    }

    private sealed class GeneratedVideo : IDeckLinkVideoInputFrame, IDeckLinkVideoBuffer
    {
        private const int Digits = 6;
        private const int DigitLeft = 39;
        private const int DigitTop = 344;
        private const int DigitWidth = 7; // In v210 groups of six pixels.
        private const int DigitHeight = 84;
        private static readonly int[] Segments = [0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f];
        private static readonly (uint Y, uint Cb, uint Cr)[] Bars =
        [
            (180, 128, 128), (162, 44, 142), (131, 156, 44), (112, 72, 58),
            (84, 184, 198), (65, 100, 212), (35, 212, 114)
        ];

        private readonly int stride = MovRecording.StoredRowBytes(Mode.Width, PixelFormat);
        private readonly byte[] pixels;
        private readonly byte[] chart;
        private readonly byte[] black;
        private long index;
        private bool noInput;

        public GeneratedVideo(bool includeLumaExcursions)
        {
            pixels = GC.AllocateUninitializedArray<byte>(stride * Mode.Height, pinned: true);
            chart = new byte[pixels.Length];
            black = new byte[pixels.Length];
            for (var row = 0; row < Mode.Height; row++)
            {
                var chartRow = MemoryMarshal.Cast<byte, uint>(chart.AsSpan(row * stride, stride));
                var blackRow = MemoryMarshal.Cast<byte, uint>(black.AsSpan(row * stride, stride));
                for (var group = 0; group < Mode.Width / 6; group++)
                {
                    var (y, cb, cr) = row < 324
                        ? Bars[group * Bars.Length / (Mode.Width / 6)]
                        : row < 416 + 26 ? (24u, 128u, 128u) : (16u + (uint)group * 219 / 119, 128u, 128u);
                    if (includeLumaExcursions && group is 0 or 119)
                        (y, cb, cr) = (group == 0 ? 8u : 245u, 128u, 128u);
                    FillGroup(chartRow[(group * 4)..], y, cb, cr);
                    FillGroup(blackRow[(group * 4)..], 16, 128, 128);
                }
            }
        }

        public void Render(long frameIndex, bool signalLost)
        {
            index = frameIndex;
            noInput = signalLost;
            (noInput ? black : chart).CopyTo(pixels, 0);
            if (noInput)
                return;

            Span<char> number = stackalloc char[Digits];
            (frameIndex % 1_000_000).TryFormat(number, out _, "D6", CultureInfo.InvariantCulture);
            for (var row = 0; row < DigitHeight; row++)
            {
                var line = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan((DigitTop + row) * stride, stride));
                for (var digit = 0; digit < Digits; digit++)
                {
                    var mask = Segments[number[digit] - '0'];
                    for (var column = 0; column < DigitWidth - 1; column++)
                    {
                        var segment = row switch
                        {
                            < 6 => 0,
                            < 39 => column == 0 ? 5 : column == 5 ? 1 : -1,
                            < 45 => 6,
                            < 78 => column == 0 ? 4 : column == 5 ? 2 : -1,
                            _ => 3
                        };
                        if (segment >= 0 && (mask & (1 << segment)) != 0)
                            FillGroup(line[((DigitLeft + digit * DigitWidth + column) * 4)..], 220, 128, 128);
                    }
                }
            }
        }

        private static void FillGroup(Span<uint> words, uint y, uint cb, uint cr)
        {
            y <<= 2;
            cb <<= 2;
            cr <<= 2;
            words[0] = cb | (y << 10) | (cr << 20);
            words[1] = y | (cb << 10) | (y << 20);
            words[2] = cr | (y << 10) | (cb << 20);
            words[3] = y | (cr << 10) | (y << 20);
        }

        public int GetWidth() => Mode.Width;
        public int GetHeight() => Mode.Height;
        public int GetRowBytes() => stride;
        public uint GetPixelFormat() => (uint)PixelFormat;
        public uint GetFlags() => noInput ? 1u << 31 : 0;
        public int GetTimecode(uint format, out IntPtr timecode) { timecode = 0; return 1; }
        public int GetAncillaryData(out IntPtr buffer) { buffer = 0; return 1; }
        public int GetStreamTime(out long time, out long duration, long scale)
        {
            time = index * Mode.FrameDuration * scale / Mode.TimeScale;
            duration = Mode.FrameDuration * scale / Mode.TimeScale;
            return 0;
        }

        public unsafe int GetBytes(out IntPtr bytes)
        {
            fixed (byte* pointer = pixels)
                bytes = (IntPtr)pointer;
            return 0;
        }

        public int GetSize(out ulong size) { size = (ulong)pixels.Length; return 0; }
        public int StartAccess(uint flags) => 0;
        public int EndAccess(uint flags) => 0;
    }

    private sealed class GeneratedAudio : IDeckLinkAudioInputPacket
    {
        private static readonly (int Left, int Right)[] Tone = Enumerable.Range(0, 2400)
            .Select(i => (Sample(i, 440, 0.125), Sample(i, 660, 0.089))).ToArray();
        private readonly int[] samples = GC.AllocateUninitializedArray<int>(1602 * 2, pinned: true);
        private int count;
        private long timestamp;

        private static int Sample(int index, int frequency, double amplitude) =>
            (int)(Math.Sin(Math.Tau * index * frequency / CaptureAudio.SampleRate) * amplitude * int.MaxValue);

        public void Render(long start, int frames, long time, bool silent)
        {
            count = frames;
            timestamp = time;
            for (var i = 0; i < count; i++)
            {
                var (left, right) = silent ? default : Tone[(int)((start + i) % Tone.Length)];
                samples[i * 2] = left;
                samples[i * 2 + 1] = right;
            }
        }

        public int GetSampleFrameCount() => count;
        public unsafe int GetBytes(out IntPtr bytes)
        {
            fixed (int* pointer = samples)
                bytes = (IntPtr)pointer;
            return 0;
        }

        public int GetPacketTime(out long time, long scale)
        {
            time = timestamp * scale / CaptureAudio.SampleRate;
            return 0;
        }
    }
}
