using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using CaptureRack.SignalCapture;
using Xunit;

namespace CaptureRack.UT;

public sealed class RfCaptureSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly RfRecording Ramp = new(new RfUsbDevice("test", "Simulated", 1, 2, null),
        null, false, 0, false, RfFileFormat.Raw, TestRamp: true);

    // Only the USB read is simulated. Metering, fan-out, bounded file writer, reports and QC are real.
    private sealed class Receiver
    {
        public int Opens;
        public int Releases;
        public readonly Channel<RfMeasurement> Meter = Channel.CreateUnbounded<RfMeasurement>();

        public void Read(Action<byte[], int> deliver, CancellationToken stop)
        {
            Interlocked.Increment(ref Opens);
            var words = Enumerable.Range(0, 1021).Select(code => (short)((code - 512) << 6)).ToArray();
            var block = MemoryMarshal.AsBytes(words.AsSpan()).ToArray();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    deliver(block, block.Length);
                    stop.WaitHandle.WaitOne(2);
                }
            }
            finally { Interlocked.Increment(ref Releases); }
        }

        public async Task<RfMeasurement> NextReading()
        {
            while (Meter.Reader.TryRead(out _)) { }
            using var timeout = new CancellationTokenSource(Timeout);
            return await Meter.Reader.ReadAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task ConsecutiveClipsUseOnePreviewAndIndependentCountersWhileVerificationIsPending()
    {
        var directory = Directory.CreateTempSubdirectory();
        var session = new RfPreviewSession(Ramp);
        var receiver = new Receiver();
        using var stop = new CancellationTokenSource(Timeout);
        var preview = Task.Run(() => session.Run(receiver.Read, m => receiver.Meter.Writer.TryWrite(m), stop.Token));
        async Task<RfRampQc> Take(string name)
        {
            using var clipStop = new CancellationTokenSource(Timeout);
            var qc = await Task.Run(() => session.Record(Path.Combine(directory.FullName, name), null,
                clipStop.Token, m => { if (m.Frames > 0) clipStop.Cancel(); }));
            return Assert.IsType<RfRampQc>(qc);
        }
        try
        {
            var initial = await receiver.NextReading();
            var first = await Take("A");
            var afterFirst = await receiver.NextReading();
            Assert.True(afterFirst.Frames > initial.Frames);
            var second = await Take("B");
            Assert.False(File.Exists(first.FinalPath));
            Assert.True(File.Exists(first.FinalPath + ".partial"));
            var afterSecond = await receiver.NextReading();
            Assert.True(afterSecond.Frames > afterFirst.Frames);
            Assert.True(afterSecond.A.NegativeClips > afterFirst.A.NegativeClips);
            Assert.Equal(1, receiver.Opens);
            Assert.Equal(0, receiver.Releases);

            var a = Verify(first);
            var b = Verify(second);
            Assert.Equal(a.Id, b.Id);
            Assert.True(a.Start > 0 && b.Start >= a.Start + first.FileSamples);
        }
        finally
        {
            stop.Cancel();
            await preview.WaitAsync(Timeout);
            directory.Delete(true);
        }
        Assert.Equal(1, receiver.Releases);
    }

    private static (Guid Id, long Start) Verify(RfRampQc qc)
    {
        qc.VerifyAndPublish();
        Assert.True(qc.FileSamples > 0);
        Assert.Equal(qc.FileSamples * 2, new FileInfo(qc.FinalPath).Length);
        using var json = JsonDocument.Parse(File.ReadAllBytes(qc.FinalPath + ".fifo.json"));
        var report = json.RootElement;
        Assert.Equal(qc.FileSamples, report.GetProperty("InputSamples").GetInt64());
        Assert.Equal(qc.FileSamples, report.GetProperty("WrittenSamples").GetInt64());
        Assert.Equal(qc.FileSamples / 1021, report.GetProperty("NegativeClips").GetInt64());
        Assert.Equal(0, report.GetProperty("PositiveClips").GetInt64());
        return (report.GetProperty("PreviewSessionId").GetGuid(), report.GetProperty("FirstPreviewSample").GetInt64());
    }
}
