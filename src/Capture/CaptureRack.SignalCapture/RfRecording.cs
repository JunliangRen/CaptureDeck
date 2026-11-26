using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CaptureRack.SignalCapture;

public sealed record RfLevels(int Min, int Max, double Rms, long Clips, int[] Trace, long PositiveClips = 0, long NegativeClips = 0);
public sealed record RfMeasurement(long Frames, int FullScale, RfLevels A, RfLevels? B, FifoReading? Fifo = null, bool FifoDisabled = false, string? LegacyMarkerState = null);

public enum RfFileFormat { Flac, Raw, Ldf }

public sealed record RfRecording(RfUsbDevice? Ddd, RtlSdrDevice? Rtl, bool HalfRate, uint Frequency,
    bool RealRf, RfFileFormat Format, bool TestRamp = false, string? Sw401 = null, int SoftwareDecimation = 1, int RtlSampleRate = RfRecording.DefaultRtlSampleRate)
{
    public RfRecording(RfUsbDevice? Ddd, RtlSdrDevice? Rtl, bool HalfRate, uint Frequency,
        bool RealRf, bool Flac, bool TestRamp = false, string? Sw401 = null, int SoftwareDecimation = 1, int RtlSampleRate = RfRecording.DefaultRtlSampleRate)
        : this(Ddd, Rtl, HalfRate, Frequency, RealRf, Flac ? RfFileFormat.Flac : RfFileFormat.Raw, TestRamp, Sw401, SoftwareDecimation, RtlSampleRate) { }

    public bool Flac
    {
        get => Format is RfFileFormat.Flac or RfFileFormat.Ldf;
        init => Format = value ? RfFileFormat.Flac : RfFileFormat.Raw;
    }
    internal string FormatName => Format switch { RfFileFormat.Ldf => "LDF", RfFileFormat.Flac => "FLAC", _ => "s16le" };
    public const int DefaultRtlSampleRate = 1_024_000;
    internal const int DddWriteQueueBlocks = 256; // Up to 64 MiB at 256 KiB per WinUSB transfer.
    public int Rate => Rtl is not null && RealRf ? 8_000_000 : HardwareRate / SoftwareDecimation;
    public int HardwareRate => Ddd is not null ? HalfRate ? 20_000_000 : 40_000_000 : RtlSampleRate;
    public double EstimatedSecondsRemaining(ulong bytes) => bytes / ((double)Rate * (Flac ? 1 : 2));
    public const int LowSpaceSeconds = 10 * 60;
    public string Suffix
    {
        get
        {
            string source = Ddd is not null ? (TestRamp ? "TestData_DdD" : "DdD") : $"RTL-{Frequency}Hz";
            if (Rtl is not null) source += RealRf ? "_hifi_rf_16-bit" : "_IQ";
            string extension = Format switch
            {
                RfFileFormat.Ldf => "ldf",
                RfFileFormat.Flac => "flac",
                _ => Rtl is not null && !RealRf ? "cu8" : "s16"
            };
            return FormattableString.Invariant($"_{source}_{Rate / 1e6:0.000###}msps.{extension}");
        }
    }

    // USB readers own their buffers until the callback returns. A full queue is a recording error.
    public void Run(string prefix, string? ffmpeg, Func<int?> vga, Action<RfMeasurement> report, CancellationToken stop, Action? verifying = null)
    {
        var qc = Capture(prefix, ffmpeg, vga, report, stop);
        if (qc is null) return;
        verifying?.Invoke();
        qc.VerifyAndPublish();
    }

    public RfRampQc? Capture(string prefix, string? ffmpeg, Func<int?> vga, Action<RfMeasurement> report, CancellationToken stop)
    {
        if ((Ddd is null) == (Rtl is null)) throw new ArgumentException("Select one RF source.");
        if (TestRamp && Ddd is null) throw new ArgumentException("FPGA test mode requires a DdD.");
        RampResult? ramp = null;
        var diagnostics = Ddd is null ? null : new DomesdayDiagnostics();
        if (Ddd is null)
            RecordSamples(prefix, ffmpeg, report, stop, (enqueue, token) =>
                RtlSdrProbe.Stream(Rtl!, new(Frequency, (uint)RtlSampleRate, 0, VgaIndex: vga()), vga, enqueue, token));
        else diagnostics!.WithReport(prefix + Suffix + ".fifo.json", () =>
            DomesdayStream.WithDevice(Ddd, (usb, probe, session) =>
            {
                diagnostics.Source = new(Ddd.UsbId, Ddd.InstanceId, probe.Firmware.ToString(), probe.FirmwareRelease,
                    probe.Fx3Commit, probe.GatewareCommit, Rate, HalfRate ? 2 : 1, TestRamp, Sw401, FormatName, SoftwareDecimation);
                RecordSamples(prefix, ffmpeg, report, stop, (enqueue, token) =>
                    ramp = DomesdayStream.Read(usb, probe, session, HalfRate, TestRamp, diagnostics, enqueue, token),
                    inspect: TestRamp ? (_, frames) =>
                    {
                        if (ramp is null || ramp.Samples != frames) throw new InvalidDataException("Live ramp and writer counts differ.");
                    } : null, deferPublish: TestRamp, diagnostics: diagnostics);
            }));
        return TestRamp ? new RfRampQc(prefix + Suffix, Flac, ffmpeg, ramp!, SoftwareDecimation) : null;
    }

    internal void RecordSamples(string prefix, string? ffmpeg, Action<RfMeasurement> report, CancellationToken stop,
        Action<Action<byte[], int>, CancellationToken> acquire, Action<string, long>? inspect = null, bool deferPublish = false, DomesdayDiagnostics? diagnostics = null)
    {
        if (Format == RfFileFormat.Ldf && Ddd is null) throw new ArgumentException("LaserDisc LDF recording requires a Domesday Duplicator source.");
        var final = prefix + Suffix;
        var partial = final + ".partial";
        if (File.Exists(final)) throw new IOException("RF output already exists.");
        var decimator = SoftwareDecimation > 1 ? new DomesdayDecimator(SoftwareDecimation) : null;
        var converter = Rtl is not null && RealRf ? new RtlHifiConverter(Frequency, RtlSampleRate) : null;
        using var output = new SampleWriter(partial, Flac, ffmpeg, this, diagnostics?.Source);
        var queue = System.Threading.Channels.Channel.CreateBounded<(byte[] Data, int Length)>(Ddd is null ? 128 : DddWriteQueueBlocks);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        RfMeasurement? lastReading = null;
        long frames = 0;
        var clock = Stopwatch.StartNew();
        var adcA = new LevelWindow();
        var adcB = new LevelWindow();
        bool iq = Rtl is not null;
        void WriteSamples(byte[] data, int length, short[] converted)
        {
            if (iq)
            {
                adcA.AddIq(data.AsSpan(0, length), 0);
                adcB.AddIq(data.AsSpan(0, length), 1);
            }
            else adcA.AddDdd(MemoryMarshal.Cast<byte, short>(data.AsSpan(0, length)));
            if (decimator is not null)
            {
                int count = decimator.Process(MemoryMarshal.Cast<byte, short>(data.AsSpan(0, length)), converted);
                output.Write(MemoryMarshal.AsBytes(converted.AsSpan(0, count)));
                diagnostics?.Wrote(count);
            }
            else if (converter is null) output.Write(data.AsSpan(0, length));
            else
            {
                var count = converter.Process(data.AsSpan(0, length), converted);
                output.Write(MemoryMarshal.AsBytes(converted.AsSpan(0, count)));
            }
            frames += length / 2;
            if (clock.ElapsedMilliseconds >= 100)
            {
                report(lastReading = new(frames, iq ? 128 : 512, adcA.Take(), iq ? adcB.Take() : null, diagnostics?.Latest, diagnostics?.PollingDisabled == true, diagnostics?.LegacyMarkerState));
                clock.Restart();
            }
        }
        Exception? firstError = null;
        var consumer = Task.Run(async () =>
        {
            short[] converted = converter is null ? [] : new short[converter.OutputCapacity(32768)];
            try
            {
                while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    (byte[] Data, int Length) block;
                    lock (queue)
                        if (!queue.Reader.TryRead(out block)) continue;
                    try
                    {
                        if (converter is not null && converted.Length < converter.OutputCapacity(block.Length / 2))
                            converted = new short[converter.OutputCapacity(block.Length / 2)];
                        if (decimator is not null && converted.Length < block.Length / 2 + 32)
                            converted = new short[block.Length / 2 + 32];
                        WriteSamples(block.Data, block.Length, converted);
                        if (decimator is null) diagnostics?.Wrote(block.Length / 2);
                    }
                    finally { ArrayPool<byte>.Shared.Return(block.Data); }
                }
                if (decimator is not null && frames > 0)
                {
                    int count = decimator.Finish(converted);
                    output.Write(MemoryMarshal.AsBytes(converted.AsSpan(0, count)));
                    diagnostics?.Wrote(count);
                }
                if (converter is not null)
                    output.Write(MemoryMarshal.AsBytes(converted.AsSpan(0, converter.Finish(converted))));
            }
            catch (Exception ex)
            {
                diagnostics?.CaptureFault(ex);
                Interlocked.CompareExchange(ref firstError, ex, null);
                cancel.Cancel();
            }
        });
        void Enqueue(byte[] buffer, int count)
        {
            var copy = ArrayPool<byte>.Shared.Rent(count);
            Buffer.BlockCopy(buffer, 0, copy, 0, count);
            lock (queue)
            {
                if (queue.Writer.TryWrite((copy, count)))
                {
                    diagnostics?.ObserveQueue(queue.Reader.Count);
                    return;
                }
            }
            ArrayPool<byte>.Shared.Return(copy);
            throw new IOException("RF recording queue overflow. Output retained as partial.");
        }
        try
        {
            acquire(Enqueue, cancel.Token);
        }
        catch (Exception ex)
        {
            var previous = Interlocked.CompareExchange(ref firstError, ex, null);
            if (previous is not null)
                firstError = new IOException($"{previous.Message} [Acquisition: {ex.Message}]", previous);
        }
        finally
        {
            queue.Writer.Complete();
            try { consumer.GetAwaiter().GetResult(); }
            finally { while (queue.Reader.TryRead(out var block)) ArrayPool<byte>.Shared.Return(block.Data); }
        }
        if (firstError is not null)
        {
            if (diagnostics?.LegacyMarkerState == "Lost" && lastReading is not null)
                report(lastReading with { LegacyMarkerState = "Lost" });
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
        }
        if (frames == 0) throw new IOException("No RF samples received.");
        if (adcA.Count > 0) report(new(frames, iq ? 128 : 512, adcA.Take(), iq ? adcB.Take() : null, diagnostics?.Latest, diagnostics?.PollingDisabled == true, diagnostics?.LegacyMarkerState));
        if (diagnostics is not null)
        {
            diagnostics.InputSamples = frames;
            diagnostics.PositiveClips = adcA.PositiveClips;
            diagnostics.NegativeClips = adcA.NegativeClips;
        }
        output.Finish();
        inspect?.Invoke(partial, frames);
        if (!deferPublish) File.Move(partial, final);
    }

    internal sealed class LevelWindow
    {
        private int min = int.MaxValue, max = int.MinValue;
        private long squares, positiveClips, negativeClips;
        private readonly int[] trace = new int[256];
        public long PositiveClips => positiveClips;
        public long NegativeClips => negativeClips;
        public long Count { get; private set; }

        private void Add(int value, int scale)
        {
            min = Math.Min(min, value);
            max = Math.Max(max, value);
            squares += value * value;
            if (value == -scale) negativeClips++;
            if (value == scale - 1) positiveClips++;
        }
        public void AddIq(ReadOnlySpan<byte> data, int ch)
        {
            for (int i = ch; i < data.Length; i += 2) Add(data[i] - 128, 128);
            Count += data.Length / 2;
            for (int i = 0; i < Math.Min(trace.Length, data.Length / 2); i++) trace[i] = data[i * 2 + ch] - 128;
        }
        public void AddDdd(ReadOnlySpan<short> data)
        {
            foreach (var sample in data) Add(sample >> 6, 512);
            Count += data.Length;
            for (int i = 0; i < Math.Min(trace.Length, data.Length); i++) trace[i] = data[i] >> 6;
        }
        public RfLevels Take()
        {
            var result = new RfLevels(min, max, Math.Sqrt((double)squares / Count), positiveClips + negativeClips, (int[])trace.Clone(), positiveClips, negativeClips);
            min = int.MaxValue;
            max = int.MinValue;
            squares = Count = 0;
            return result;
        }
    }

    private sealed class SampleWriter : IDisposable
    {
        private readonly Stream stream;
        private readonly Process? encoder;
        private readonly Task<string>? errors;
        private bool closed;
        public SampleWriter(string path, bool flac, string? ffmpeg, RfRecording source, DomesdaySource? provenance)
        {
            int rate = source.Rate;
            bool iq = source.Rtl is not null && !source.RealRf;
            if (!flac)
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 256 * 1024);
                return;
            }
            var start = new ProcessStartInfo(ffmpeg ?? throw new IOException("FFmpeg is required for FLAC."))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-n", "-f", iq ? "u8" : "s16le",
                "-ar", ((rate + 500) / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture), "-ac", iq ? "2" : "1",
                "-i", "pipe:0", "-c:a", "flac", "-compression_level", "0", "-metadata", $"RF_SAMPLE_RATE_HZ={rate}",
                "-metadata", $"RF_SAMPLE_RATE={rate}", "-metadata", FormattableString.Invariant($"RF_SAMPLE_RATE_KHZ={rate / 1000m:0.###}") })
                start.ArgumentList.Add(arg);
            if (provenance is not null)
                foreach (var tag in provenance.FlacTags())
                {
                    start.ArgumentList.Add("-metadata");
                    start.ArgumentList.Add(tag);
                }
            else if (source.TestRamp)
            {
                start.ArgumentList.Add("-metadata");
                start.ArgumentList.Add("DDD_TEST_MODE=true");
            }
            if (source.Rtl is not null)
            {
                foreach (string tag in new[] { $"RF_SOURCE={(iq ? "RTLSDR_IQ" : "RTLSDR_HIFI")}",
                    $"SOURCE_SAMPLE_RATE={source.RtlSampleRate}", $"SOURCE_CENTER_FREQUENCY={source.Frequency}" })
                {
                    start.ArgumentList.Add("-metadata");
                    start.ArgumentList.Add(tag);
                }
            }
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(source.Format == RfFileFormat.Ldf ? "ogg" : "flac");
            start.ArgumentList.Add(path);
            encoder = Process.Start(start) ?? throw new IOException("Could not launch FFmpeg.");
            errors = encoder.StandardError.ReadToEndAsync();
            stream = encoder.StandardInput.BaseStream;
        }
        public void Write(ReadOnlySpan<byte> data) => stream.Write(data);
        public void Finish()
        {
            stream.Dispose();
            closed = true;
            if (encoder is null) return;
            encoder.WaitForExit();
            if (encoder.ExitCode != 0) throw new IOException(errors!.GetAwaiter().GetResult());
        }
        public void Dispose()
        {
            // Finish reports close/encoder failures on success. During unwinding
            // preserve the acquisition or writer failure that led here.
            try { if (!closed) stream.Dispose(); }
            catch (IOException) when (!closed) { }
            finally { encoder?.WaitForExit(); encoder?.Dispose(); }
        }
    }
}
