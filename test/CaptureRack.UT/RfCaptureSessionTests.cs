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
        var receiver = new Receiver();
        var releaseQc = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobs = new List<(RfRampQc Qc, Task Result)>();
        int verificationCount = 0;
        async Task VerifyFile(RfRampQc qc)
        {
            Interlocked.Increment(ref verificationCount);
            verifying.TrySetResult();
            await releaseQc.Task.WaitAsync(Timeout);
            await Task.Run(qc.VerifyAndPublish);
        }
        var session = new RfCaptureSession(VerifyFile,
            (_, _, _, _, _, _) => throw new InvalidOperationException("Opened another receiver"), receiver.Read);
        session.VerificationQueued += (qc, _, result) => jobs.Add((qc, result));
        var preview = session.StartPreviewAsync(Ramp, token => session.ReadReceiverAsync(
            () => null, m => receiver.Meter.Writer.TryWrite(m), token));
        async Task<RfRampQc> Take(string name)
        {
            var firstReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recording = session.RecordAsync(false, Ramp, Path.Combine(directory.FullName, name),
                null, () => null, m => firstReading.TrySetResult());
            await firstReading.Task.WaitAsync(Timeout);
            session.StopRecording();
            await recording.WaitAsync(Timeout);
            return jobs[^1].Qc;
        }
        try
        {
            var initial = await receiver.NextReading();
            var first = await Take("A");
            await verifying.Task.WaitAsync(Timeout);
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

            Assert.Equal(1, Volatile.Read(ref verificationCount));
            Assert.False(jobs[0].Result.IsCompleted);
            Assert.False(jobs[1].Result.IsCompleted);
            releaseQc.SetResult();
            await Task.WhenAll(jobs.Select(job => job.Result)).WaitAsync(Timeout);
            var a = Verify(first);
            var b = Verify(second);
            Assert.Equal(a.Id, b.Id);
            Assert.True(a.Start > 0 && b.Start >= a.Start + first.FileSamples);
        }
        finally
        {
            releaseQc.TrySetResult();
            session.StopRecording();
            await session.StopForCloseAsync().WaitAsync(Timeout);
            await preview.WaitAsync(Timeout);
            directory.Delete(true);
        }
        Assert.Equal(1, receiver.Releases);
    }

    [Fact]
    public async Task FileModeRejectsRecordingEvenWhenCalledOutsideTheView()
    {
        var session = new RfCaptureSession();
        Task Record(bool file) => session.RecordAsync(file, Ramp, "unused", null, () => null, _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Record(true));
        var playback = session.StartPreviewAsync(null, async stop =>
        {
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Record(false));
        Assert.False(session.RecordingActive);
        session.StopPreview();
        await playback.WaitAsync(Timeout);
        Assert.False(session.PreviewActive);
    }

    [Fact]
    public async Task CloseDuringDirectRecordingReleasesOnceAndRejectsNewCommands()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int releases = 0;
        var session = new RfCaptureSession(_ => throw new InvalidOperationException(), (_, _, _, _, _, stop) =>
        {
            entered.SetResult();
            Assert.True(stop.WaitHandle.WaitOne(Timeout));
            Interlocked.Increment(ref releases);
            return null;
        });
        Task Record() => session.RecordAsync(false, Ramp, "unused", null, () => null, _ => { });
        var recording = Record();
        await entered.Task.WaitAsync(Timeout);
        await Assert.ThrowsAsync<InvalidOperationException>(Record);
        var close = session.StopForCloseAsync();
        await Task.WhenAll(recording, close, session.StopForCloseAsync()).WaitAsync(Timeout);
        Assert.False(session.RecordingActive);
        Assert.False(session.PreviewActive);
        Assert.Equal(1, releases);
        await Assert.ThrowsAsync<InvalidOperationException>(Record);
    }

    [Fact]
    public async Task ReceiverBelongsToOnePreviewAndOneConfiguration()
    {
        var receiver = new Receiver();
        var ready = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new RfCaptureSession(_ => Task.CompletedTask,
            (_, _, _, _, _, _) => throw new InvalidOperationException(), receiver.Read);
        Task Read(CancellationToken token) => session.ReadReceiverAsync(() => null, _ => { }, token);
        var preview = session.StartPreviewAsync(Ramp, token =>
        {
            ready.SetResult(token);
            return Read(token);
        });
        var old = await ready.Task;
        try
        {
            Assert.Throws<InvalidOperationException>(() => { _ = Read(old); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RecordAsync(false,
                Ramp with { Format = RfFileFormat.Flac }, "unused", null, () => null, _ => { }));
        }
        finally { session.StopPreview(); await preview.WaitAsync(Timeout); }
        await session.StartPreviewAsync(Ramp, async token =>
        {
            Assert.Throws<InvalidOperationException>(() => { _ = Read(old); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RecordAsync(false, Ramp,
                "unused", null, () => null, _ => { }));
        });
        Assert.Equal(1, receiver.Opens);
        Assert.Equal(1, receiver.Releases);
    }

    [Fact]
    public async Task FailedVerificationDoesNotPoisonSubsequentFile()
    {
        var first = new RfRampQc("first", false, null, new(1, 0, 1021));
        var second = first with { FinalPath = "second" };
        var results = new List<(RfRampQc File, Task Result)>();
        var session = new RfCaptureSession(qc => qc == first
            ? Task.FromException(new InvalidDataException("Invalid first ramp")) : Task.CompletedTask,
            (_, prefix, _, _, _, _) => prefix == "first" ? first : second);
        session.VerificationQueued += (qc, _, result) => results.Add((qc, result));
        foreach (var file in new[] { first, second })
            await session.RecordAsync(false, Ramp, file.FinalPath, null, () => null, _ => { });
        await session.StopForCloseAsync();
        Assert.Equal(first, results[0].File);
        await Assert.ThrowsAsync<InvalidDataException>(() => results[0].Result);
        Assert.Equal(second, results[1].File);
        await results[1].Result;
    }

    private static (Guid Id, long Start) Verify(RfRampQc qc)
    {
        Assert.True(File.Exists(qc.FinalPath + ".ramp.json"));
        Assert.True(qc.FileSamples > 0);
        Assert.Equal(qc.FileSamples * 2, new FileInfo(qc.FinalPath).Length);
        using var json = JsonDocument.Parse(File.ReadAllBytes(qc.FinalPath + ".fifo.json"));
        var report = json.RootElement;
        Assert.Equal(qc.FileSamples, report.GetProperty("InputSamples").GetInt64());
        Assert.Equal(qc.FileSamples, report.GetProperty("WrittenSamples").GetInt64());
        Assert.Equal(qc.FileSamples / 1021, report.GetProperty("NegativeClips").GetInt64());
        Assert.Equal(0, report.GetProperty("PositiveClips").GetInt64());
        ValidationEvidence.Save(Path.GetFileName(qc.FinalPath), new { capture = report, verifiedSamples = qc.FileSamples });
        return (report.GetProperty("PreviewSessionId").GetGuid(), report.GetProperty("FirstPreviewSample").GetInt64());
    }
}
