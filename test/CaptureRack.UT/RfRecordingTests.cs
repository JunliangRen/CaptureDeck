using System.Diagnostics;
using System.Runtime.InteropServices;
using CaptureRack.QualityControl;
using CaptureRack.SignalCapture;
using Xunit;

namespace CaptureRack.UT;

public class RfRecordingTests
{
    [Fact]
    public void ContendedWriteQueueIdentifiesTheDelayedUsbCallbackAndPreservesTheFaultWindow()
    {
        var diagnostics = new DomesdayDiagnostics();
        var timing = diagnostics.UsbCallbackTiming;
        var queue = System.Threading.Channels.Channel.CreateBounded<(byte[] Data, int Length)>(1);
        using var started = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            timing.Begin(3, 0);
            started.Set();
            RfRecording.EnqueueBlock(queue, new byte[256], 256, diagnostics);
            diagnostics.Position = new(128, 0, null, null, 128);
            diagnostics.FinishUsbCallback();
        });
        lock (queue)
        {
            worker.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)));
            Thread.Sleep(102);
        }
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.True(queue.Reader.TryRead(out var block));
        System.Buffers.ArrayPool<byte>.Shared.Return(block.Data);
        diagnostics.UsbState = new("CheckUnderflow", 129, 2, 15, 256, 256, false, null);
        diagnostics.CaptureFault(new IOException("Simulated exhausted request ring"));
        var delayed = Assert.Single(timing.Recent);
        Assert.Equal((1L, 3, 0L, 128L), (delayed.Number, delayed.Slot, delayed.FirstSample, delayed.EndSample));
        Assert.True(delayed.QueueLockMs >= 95, $"Measured queue lock {delayed.QueueLockMs} ms");
        Assert.Equal(delayed.TotalMs, delayed.OtherMs + delayed.ConvertMs + delayed.RentMs + delayed.CopyMs +
            delayed.QueueLockMs + delayed.EnqueueMs, 6);
        Assert.Equal(1, delayed.QueueBlocks);
        Assert.Equal(delayed, timing.Longest);
        Assert.Equal(delayed.TotalMs, diagnostics.UsbCallbackMaxMs, 6);
        ValidationEvidence.Save("usb-callback-stall", diagnostics);
        for (int i = 0; i < 17; i++)
        {
            timing.Begin(i % 16, 128);
            diagnostics.FinishUsbCallback();
        }
        Assert.Equal(16, timing.Recent.Length);
        Assert.Equal(3, timing.Recent[0].Number);
        Assert.Equal(18, timing.Recent[^1].Number);
    }

    [Theory]
    [InlineData(250000)]
    [InlineData(1024000)]
    [InlineData(1536000)]
    [InlineData(1234567)]
    [InlineData(2400000)]
    public void RtlHifiKeepsEightMspsAndFractionalPhaseAcrossUsbBlocks(int rate)
    {
        byte[] iq = new byte[6346];
        new Random(23).NextBytes(iq);
        short[] Convert(int block)
        {
            var converter = new RtlHifiConverter(1500000, rate);
            var buffer = new short[converter.OutputCapacity(Math.Max(block / 2, 24))];
            var result = new List<short>();
            for (int i = 0; i < iq.Length; i += block)
            {
                int n = converter.Process(iq.AsSpan(i, Math.Min(block, iq.Length - i)), buffer);
                result.AddRange(buffer.Take(n));
            }
            result.AddRange(buffer.Take(converter.Finish(buffer)));
            return result.ToArray();
        }
        var expected = Convert(iq.Length);
        Assert.Equal((iq.Length / 2 * 8000000L + rate - 1) / rate, expected.LongLength);
        Assert.Equal(expected, Convert(46));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void SoftwareDecimationPreservesPassbandRejectsAliasesAndFlushesOddTail(int factor)
    {
        short[] Tone(double frequency) => Enumerable.Range(0, 32771)
            .Select(i => (short)Math.Round(20000 * Math.Sin(2 * Math.PI * frequency * i))).ToArray();
        short[] Filter(short[] input, int block)
        {
            var filter = new DomesdayDecimator(factor);
            var result = new List<short>();
            var temp = new short[block + 32];
            for (int i = 0; i < input.Length; i += block)
            {
                int n = filter.Process(input.AsSpan(i, Math.Min(block, input.Length - i)), temp);
                result.AddRange(temp.Take(n));
            }
            result.AddRange(temp.Take(filter.Finish(temp)));
            return result.ToArray();
        }
        var signal = Tone(0.1 / factor);
        var pass = Filter(signal, 77);
        Assert.Equal(Filter(signal, 32771), pass);
        Assert.Equal((signal.Length + factor - 1) / factor, pass.Length);
        var stop = Filter(Tone(0.7 / factor), 83);
        for (int i = 40; i < pass.Length - 40; i++)
        {
            Assert.InRange(Math.Abs(pass[i] - signal[i * factor]), 0, 8);
            Assert.InRange(Math.Abs((int)stop[i]), 0, 8);
        }
    }

    [Fact]
    public void BipolarMeterCountsRailsIndependentlyAcrossDisplayWindows()
    {
        var meter = new RfRecording.LevelWindow();
        meter.AddDdd([short.MinValue, -64, 0]);
        var negative = meter.Take();
        Assert.Equal((1L, 0L, -512, 0), (negative.NegativeClips, negative.PositiveClips, negative.Min, negative.Max));
        meter.AddDdd([32704, 32704, 64]);
        var positive = meter.Take();
        Assert.Equal((1L, 2L, 3L, 1, 511), (positive.NegativeClips, positive.PositiveClips, positive.Clips, positive.Min, positive.Max));
    }

    [Fact]
    public void SpectrumWindowUsesContiguousAdcAndIqSamplesAcrossCallbacks()
    {
        var adc = new RfRecording.LevelWindow();
        adc.AddDdd([64, 128]);
        adc.AddDdd([192, 256]);
        Assert.Equal(new[] { 1, 2, 3, 4 }, adc.Take().Trace);
        adc.AddDdd([-64]);
        Assert.Equal(new[] { -1 }, adc.Take().Trace);

        var i = new RfRecording.LevelWindow();
        var q = new RfRecording.LevelWindow();
        byte[] pairs = [128, 127, 129, 126];
        for (int block = 0; block < 2050; block++)
        {
            i.AddIq(pairs, 0);
            q.AddIq(pairs, 1);
        }
        Assert.Equal(Enumerable.Repeat(new[] { 0, 1 }, 2048).SelectMany(v => v), i.Take().Trace);
        Assert.Equal(Enumerable.Repeat(new[] { -1, -2 }, 2048).SelectMany(v => v), q.Take().Trace);
    }

    [Fact]
    public void ManualSwitchPatternIsOptionalAndIndependentOfFirmwareIdentity()
    {
        var source = new DomesdaySource("1D50:603B", "test", "Legacy", null, null, null,
            40_000_000, 1, true, "1010", "FLAC");
        Assert.Contains("DDD_FRONT_END_GAIN=SW401 1010", source.FlacTags());
        Assert.Contains("DDD_TEST_MODE=true", source.FlacTags());
        Assert.DoesNotContain(source.FlacTags(), tag => tag.StartsWith("DDD_FIRMWARE") || tag.StartsWith("DDD_GATEWARE"));
        Assert.DoesNotContain((source with { Sw401 = null }).FlacTags(), tag => tag.StartsWith("DDD_FRONT_END_GAIN"));
    }

    [Theory]
    [InlineData(false, false, 48000000000L)]
    [InlineData(false, true, 24000000000L)]
    [InlineData(true, false, 24000000000L)]
    [InlineData(true, true, 12000000000L)]
    public void DddSpaceWarningUsesFormatAndDecimation(bool halfRate, bool flac, long tenMinutes)
    {
        var capture = new RfRecording(new("id", "DdD", 0x1209, 0x2347, "WinUSB"), null, halfRate, 0, false, flac);
        Assert.Equal(RfRecording.LowSpaceSeconds, capture.EstimatedSecondsRemaining((ulong)tenMinutes));
        Assert.True(capture.EstimatedSecondsRemaining((ulong)tenMinutes - 1) < RfRecording.LowSpaceSeconds);
    }

    [Theory]
    [InlineData(true, 1021)]
    [InlineData(false, 1021)]
    [InlineData(false, 1024)]
    public void LiveAndFileRampAgreeAcrossWrapsAndBufferBoundaries(bool current, int period)
    {
        const int first = 1018;
        var ramp = new DomesdayRamp(current);
        var decoder = new DomesdayStream(false, ramp);
        using var pcm = new MemoryStream();
        int total = period * 129 + 13;
        for (int offset = 0; offset < total;)
        {
            var words = new ushort[Math.Min(16631, total - offset)];
            for (int i = 0; i < words.Length; i++)
                words[i] = (ushort)((((offset + i) / 65535) << 10) | ((first + offset + i) % period));
            var bytes = MemoryMarshal.AsBytes(words.AsSpan());
            decoder.Convert(bytes);
            pcm.Write(bytes);
            offset += words.Length;
        }
        var result = ramp.Result;
        Assert.Equal(new RampResult(total, first, period), result);
        pcm.Position = 0;
        DomesdayRampFile.VerifyPcm(pcm, result);
        Assert.Throws<InvalidDataException>(() => ramp.Check((first + total + 1) % period));

        var data = pcm.ToArray();
        void Reject(byte[] damaged) =>
            Assert.Throws<InvalidDataException>(() => DomesdayRampFile.VerifyPcm(new MemoryStream(damaged), result));
        Reject(data[..^1]);
        Reject(data[..^(period * 2)]);
        Reject([.. data, .. data[^(period * 2)..]]);
        data[^2] ^= 1;
        Reject(data);
    }

    [Fact]
    public void DroppedSampleReportCorrelatesSequenceUsbFifoAndWriterQueue()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            string prefix = Path.Combine(dir.FullName, "drop");
            var capture = new RfRecording(new("id", "DdD", 0x1209, 0x2347, "WinUSB"), null, false, 0, false, false);
            var diagnostics = new DomesdayDiagnostics();
            var decoder = new DomesdayStream(false, diagnostics: diagnostics);
            string path = prefix + capture.Suffix + ".fifo.json";
            Assert.Throws<InvalidDataException>(() => diagnostics.WithReport(path, () => capture.RecordSamples(
                prefix, null, _ => {}, default, (enqueue, _) =>
                {
                    byte[] fifo = new byte[23];
                    fifo[0] = 0xbd;
                    fifo[1] = 1;
                    fifo[3] = 42;
                    fifo[5] = 75;
                    fifo[15] = 3;
                    fifo[18] = 4;
                    fifo[20] = 2;
                    for (byte latch = 0; latch < 3; latch++)
                    {
                        fifo[2] = latch;
                        diagnostics.Tick(latch * 250, () => {}, () => 23, fifo);
                    }
                    // The middle run lost one word; its marker still advances normally.
                    void Feed(int marker, int count)
                    {
                        var words = Enumerable.Repeat((ushort)(marker << 10), count).ToArray();
                        byte[] bytes = MemoryMarshal.AsBytes(words.AsSpan()).ToArray();
                        diagnostics.UsbState = new("Consume", 0x81, marker, 15, bytes.Length, bytes.Length, false);
                        decoder.Convert(bytes);
                        enqueue(bytes, bytes.Length);
                    }
                    Feed(0, 1);
                    Feed(1, 65535);
                    Feed(2, 65534);
                    Feed(3, 1);
                }, diagnostics: diagnostics)));
            using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            var fault = report.RootElement.GetProperty("Fault");
            var seq = fault.GetProperty("Sequence");
            Assert.Equal(131070, seq.GetProperty("SampleIndex").GetInt64());
            Assert.Equal(2, seq.GetProperty("ExpectedMarker").GetInt32());
            Assert.Equal(3, seq.GetProperty("ActualMarker").GetInt32());
            Assert.Equal(15, fault.GetProperty("Usb").GetProperty("SubmittedBulkRequests").GetInt32());
            var fifo = fault.GetProperty("LatestFifo");
            Assert.Equal(42, fifo.GetProperty("UsedWords").GetInt32());
            Assert.Equal(75, fifo.GetProperty("RunPeakWords").GetInt32());
            Assert.Equal(1536, fifo.GetProperty("NearFullSamples").GetInt64());
            Assert.InRange(fault.GetProperty("WriteQueuePeakBlocks").GetInt32(), 1, report.RootElement.GetProperty("WriteQueueCapacityBlocks").GetInt32());
            Assert.Equal(131070, report.RootElement.GetProperty("WrittenSamples").GetInt64());
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void CurrentGatewareRejectsOutOfRangeAndDuplicateRampCodes()
    {
        var ramp = new DomesdayRamp(true);
        Assert.Throws<InvalidDataException>(() => ramp.Check(1021));
        ramp.Check(1020);
        ramp.Check(0);
        Assert.Throws<InvalidDataException>(() => ramp.Check(0));
    }

    [Theory]
    [InlineData(65535)]
    [InlineData(65536)]
    public void ProtocolSequenceLearnsLengthAcrossReadsAndRejectsLoss(int length)
    {
        var decoder = new DomesdayStream(false);
        void Feed(int marker, int count)
        {
            var words = Enumerable.Repeat((ushort)(marker << 10), count).ToArray();
            decoder.Convert(MemoryMarshal.AsBytes(words.AsSpan()));
            Assert.All(words.Take(2), word => Assert.Equal(0x8000, (int)word));
        }
        Feed(61, 17);
        Feed(62, 32000);
        Feed(62, length - 32000);
        Feed(0, length - 1);
        Assert.Throws<InvalidDataException>(() => Feed(1, 1));
    }

    [Fact]
    public void FailedFileRampVerificationKeepsTheClosedPartialRecording()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var request = new RfRecording(new("id", "DdD", 0x1209, 0x2347, "WinUSB"), null,
                false, 0, false, false, TestRamp: true);
            string prefix = Path.Combine(dir.FullName, "capture");
            string final = prefix + request.Suffix;
            var damaged = new byte[] { 0, 128, 65, 128 }; // Expected second PCM word is 0x8040.
            request.RecordSamples(prefix, null, _ => {}, default,
                (enqueue, _) => enqueue(damaged, damaged.Length), deferPublish: true);
            var qc = new RfRampQc(final, false, null, new(2, 0, 1021));

            string nextPrefix = Path.Combine(dir.FullName, "next");
            byte[] next = [0, 128, 64, 128];
            request.RecordSamples(nextPrefix, null, _ => {}, default,
                (enqueue, _) => enqueue(next, next.Length), deferPublish: true);
            Assert.Throws<InvalidDataException>(qc.VerifyAndPublish);
            Assert.False(File.Exists(final + ".ramp.json"));
            var nextQc = qc with { FinalPath = nextPrefix + request.Suffix };
            nextQc.VerifyAndPublish();
            Assert.Equal(next, File.ReadAllBytes(nextQc.FinalPath));
            Assert.True(File.Exists(nextQc.FinalPath + ".ramp.json"));
            Assert.False(File.Exists(final));
            Assert.Equal(damaged, File.ReadAllBytes(final + ".partial"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void FifoFailuresAreCumulativeAndDoNotHideAnInterveningOverflow()
    {
        int calls = 0;
        var fifo = new DomesdayFifoTelemetry();
        var block = new byte[23];
        int? Complete()
        {
            calls++;
            if (calls == 1) throw new IOException("control transfer failed");
            if (calls == 5) return 22;
            block[0] = 0xbd;
            block[1] = 0x21;
            block[2] = calls == 2 ? (byte)255 : (byte)0;
            block[9] = 4;
            block[11] = 19;
            block[18] = 64;
            block[20] = 32;
            return 23;
        }
        for (int i = 0; i < 8; i++)
        {
            fifo.Tick(i * 250, () => {}, Complete, block);
            if (i == 1) Assert.Null(fifo.Latest!.ObservedDroppedSamples); // Baseline predates capture.
        }
        Assert.Equal(19, fifo.Latest!.ObservedDroppedSamples);
        Assert.Equal(5, calls);
        Assert.Equal(2, fifo.ReadFailures);
        Assert.True(fifo.PollingDisabled);
        Assert.Equal(1, fifo.ValidIntervals);
        Assert.Equal(4, fifo.OverflowEvents);
        Assert.Equal(19, fifo.DroppedSamples);
        Assert.True(fifo.CounterSaturated);
        Assert.False(fifo.MissedIntervals);
    }

    [Fact]
    public void UnknownFullLengthTelemetryDoesNotExhaustTheTransportFailureBudget()
    {
        var fifo = new DomesdayFifoTelemetry();
        var block = new byte[23];
        fifo.Tick(0, () => {}, () => 23, block);
        fifo.Tick(250, () => {}, () => 23, block);
        Assert.Equal(2, fifo.UnrecognizedReadings);
        Assert.False(fifo.PollingDisabled);
        Assert.Equal(0, fifo.ValidIntervals);
    }

    [Fact]
    public void SlowFifoRequestLeavesBulkRunningAndNeverOverlaps()
    {
        var fifo = new DomesdayFifoTelemetry();
        var block = new byte[23];
        var times = new List<int>();
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var request = new RfRecording(new("id", "DdD", 0x1209, 0x2347, "WinUSB"), null,
                false, 0, false, false, TestRamp: true);
            var ramp = new DomesdayRamp(true);
            var decoder = new DomesdayStream(false, ramp);
            string prefix = Path.Combine(dir.FullName, "slow-fifo");
            request.RecordSamples(prefix, null, _ => {}, default, (enqueue, _) =>
            {
                // Simulated completion clock. Between control completions the same
                // bulk path keeps validating, converting and queuing ADC samples.
                for (int ms = 0; ms < 1500; ms += 15)
                {
                    var words = new ushort[256];
                    for (int i = 0; i < words.Length; i++)
                        words[i] = (ushort)(((ms / 15) * words.Length + i) % 1021);
                    byte[] bytes = MemoryMarshal.AsBytes(words.AsSpan()).ToArray();
                    decoder.Convert(bytes);
                    enqueue(bytes, bytes.Length);
                    fifo.Tick(ms, () => times.Add(ms), () =>
                    {
                        if (times.Count == 1 && ms < 900) return null;
                        return times.Count == 2 ? 23 : 0;
                    }, block);
                }
            }, (partial, frames) =>
            {
                Assert.Equal(25600L, frames);
                DomesdayRampFile.Verify(partial, false, null, ramp.Result);
            });
            Assert.True(File.Exists(prefix + request.Suffix));
        }
        finally { dir.Delete(true); }
        Assert.Equal(new[] { 0, 915, 1170 }, times);
        Assert.Equal(2, fifo.ReadFailures);
        Assert.True(fifo.PollingDisabled);
    }

    [Fact]
    public void FailedRollbackKeepsFirstErrorAndLocksOnlyTheAffectedPath()
    {
        string path = Guid.NewGuid().ToString();
        var session = new DomesdaySession(path);
        var first = new IOException("bulk failed");
        session.Fail(first);
        var restored = new List<(byte, byte)>();
        session.Rollback(false, true, (command, value) =>
        {
            Assert.Equal(0xb5, command);
            Assert.Equal(0, value);
            throw new IOException("injected B5 failure");
        }, (register, value) => restored.Add((register, value)));
        Assert.Equal(new (byte, byte)[] { (0x10, 0), (0x12, 1) }, restored);
        var error = Assert.Throws<IOException>(session.ThrowIfFailed);
        Assert.Same(first, error.InnerException);
        Assert.Contains("injected B5 failure", error.Message);
        Assert.Throws<IOException>(() => DomesdaySession.CheckPath(path.ToUpperInvariant()));
        DomesdaySession.CheckPath(Guid.NewGuid().ToString());
    }

    [Theory]
    [InlineData("markerless")]
    [InlineData("wrap")]
    [InlineData("one missing sample")]
    [InlineData("skipped marker")]
    [InlineData("stuck after lock")]
    public void LegacyMarkerDiagnosticsAcrossTransfers(string scenario)
    {
        var stats = new DomesdayDiagnostics();
        var stream = new DomesdayStream(true, diagnostics: stats);
        void Feed(int marker, int count) => stream.Convert(MemoryMarshal.AsBytes(
            Enumerable.Repeat((ushort)((marker << 10) | 512), count).ToArray().AsSpan()));
        if (scenario == "markerless")
        {
            for (int i = 0; i < 4; i++) Feed(0, 65536);
            Assert.Equal("Unlocked", stats.LegacyMarkerState);
            Assert.Null(stats.LegacyMarkerLockSample);
            Assert.Equal(262144, stats.Position!.SampleIndex);
            return;
        }
        Feed(62, 17);
        Feed(63, 32000);
        Assert.Equal("Locked", stats.LegacyMarkerState);
        Assert.Equal(17, stats.LegacyMarkerLockSample);
        Feed(63, scenario == "one missing sample" ? 33535 : 33536);
        if (scenario == "wrap")
        {
            Feed(0, 65536);
            Feed(1, 1);
            Assert.Equal("Locked", stats.LegacyMarkerState);
            return;
        }
        int actual = scenario == "skipped marker" ? 1 : scenario == "stuck after lock" ? 63 : 0;
        Assert.Throws<InvalidDataException>(() => Feed(actual, 1));
        Assert.Equal("Lost", stats.LegacyMarkerState);
        Assert.Equal(scenario == "one missing sample" ? 65552 : 65553, stats.Position!.SampleIndex);
        Assert.Equal(scenario == "one missing sample" ? 63 : 0, stats.Position.ExpectedMarker);
        Assert.Equal(actual, stats.Position.ActualMarker);
    }

    [Fact]
    public void LegacyWrapPreservesSignedAdcScale()
    {
        ushort[] words = [(63 << 10) + 1023, 0, 512];
        new DomesdayStream(true).Convert(MemoryMarshal.AsBytes(words.AsSpan()));
        Assert.Equal(new ushort[] { 32704, 32768, 0 }, words);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, false, true)]
    public void RawAndDecodedFlacMatchForSameSamples(bool ddd, bool hifi, bool ldf = false)
    {
        if (QcReports.FindTools().FfmpegPath is not { } ffmpeg) return;
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var request = new RfRecording(ddd ? new("id", "DdD", 0x1209, 0x2347, "WinUSB") : null,
                ddd ? null : new(0, "RTL", null, null, null), false, 1_700_000, hifi, false);
            byte[] block = new byte[32768];
            new Random(7).NextBytes(block);
            var prefix = Path.Combine(dir.FullName, "test");
            void Supply(Action<byte[],int> emit, CancellationToken token)
            {
                emit(block, 50);
                emit(block[50..], block.Length - 50);
            }
            RfMeasurement? meter = null;
            request.RecordSamples(prefix, null, m => meter = m, default, Supply);
            var raw = File.ReadAllBytes(prefix + request.Suffix);
            Assert.Equal(1_024_000, request.RtlSampleRate);
            long expectedFrames = hifi ? (block.Length / 2 * 8_000_000L + 1_023_999) / 1_024_000 : block.Length / 2;
            Assert.Equal(expectedFrames * 2, raw.LongLength);
            Assert.Equal(block.Length / 2, meter!.Frames);
            Assert.True(meter.A.Min < 0 && meter.A.Max > 0 && meter.A.Rms > 0);
            if (!hifi) Assert.Equal(block, raw);
            request = request with { Format = ldf ? RfFileFormat.Ldf : RfFileFormat.Flac };
            request.RecordSamples(prefix, ffmpeg, _ => {}, default, Supply);
            if (ldf)
            {
                Assert.EndsWith(".ldf", request.Suffix);
                using var file = File.OpenRead(prefix + request.Suffix);
                var header = new byte[4];
                file.ReadExactly(header);
                Assert.True(header.AsSpan().SequenceEqual("OggS"u8));
            }
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (string arg in new[] { "-v", "error", "-i", prefix + request.Suffix, "-f", ddd || hifi ? "s16le" : "u8", "pipe:1" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            using var decoded = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(decoded);
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(raw, decoded.ToArray());
        }
        finally { dir.Delete(true); }
    }
}
