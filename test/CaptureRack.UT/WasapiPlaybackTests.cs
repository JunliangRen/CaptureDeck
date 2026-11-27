using CaptureRack.Audio;
using Xunit;

namespace CaptureRack.UT;

public sealed class WasapiPlaybackTests
{
    [Fact]
    public void DeviceRemovedDuringEnumerationLeavesOtherOutputsSelectable()
    {
        var outputs = PlaybackDevices.ReadAvailable(3, i => i == 1
            ? throw new System.Runtime.InteropServices.COMException("Endpoint removed", unchecked((int)0x88890004))
            : new PlaybackDevice($"device-{i}", "USB Audio"));
        Assert.Equal(new[] { "device-0", "device-2" }, outputs.Select(output => output.Id));
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void InvalidClockReadingPreservesAnchorsAndUnderrunClearsClock(int rate)
    {
        var timeline = new WasapiPlayback.MediaTimeline(rate);
        int half = rate / 2;
        timeline.Add(100, half, TimeSpan.TicksPerSecond, half);
        timeline.Add(100 + half, half, TimeSpan.TicksPerSecond, 0);
        Assert.Equal(long.MinValue, timeline.Read(true, 99));
        Assert.Equal(0, timeline.Read(true, 100));
        Assert.Equal(long.MinValue, timeline.Read(false, long.MaxValue));
        Assert.Equal(TimeSpan.TicksPerSecond / 2, timeline.Read(true, 100 + half));
        Assert.Equal(long.MinValue, timeline.Read(true, 100 + rate));
    }

    [Fact]
    public void PartialWritesKeepStereoFramesAndPacketTimestampAcrossQueueOverflow()
    {
        var session = new WasapiPlayback.Session();
        byte[] pcm = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        var end = TimeSpan.FromSeconds(2);
        session.Submit(pcm, end);
        Assert.True(session.Take(1, out var head, out var packetEnd, out int trailing));
        Assert.Equal(pcm[..4], head.ToArray());
        Assert.Equal(end, packetEnd);
        Assert.Equal(2, trailing);

        for (byte i = 1; i <= 12; i++) session.Submit([i, 0, i, 0], null);
        Assert.False(session.Take(0, out _, out _, out _));
        Assert.True(session.Take(4, out var tail, out packetEnd, out trailing));
        Assert.Equal(pcm[4..], tail.ToArray());
        Assert.Equal(end, packetEnd);
        Assert.Equal(0, trailing);
        for (byte i = 5; i <= 12; i++)
        {
            Assert.True(session.Take(1, out var next, out packetEnd, out _));
            Assert.Equal(i, next.Array![next.Offset]);
            Assert.Null(packetEnd);
        }
        Assert.False(session.Take(1, out _, out _, out _));
    }

    [Fact]
    public async Task RetiringWorkerCannotClearNewSessionOrPublishItsClock()
    {
        using var releaseOld = new ManualResetEventSlim();
        var first = new TaskCompletionSource<WasapiPlayback.Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseNew = new ManualResetEventSlim();
        using var output = new WasapiPlayback(48000, (session, deviceId) =>
        {
            if (first.TrySetResult(session))
            {
                releaseOld.Wait(TimeSpan.FromSeconds(5));
                Volatile.Write(ref session.PlayedTicks, 999);
            }
            else
            {
                var found = session.Take(1, out var next, out _, out _);
                second.TrySetResult(found ? next.ToArray() : []);
                releaseNew.Wait(TimeSpan.FromSeconds(5));
            }
        });
        try
        {
            output.Enabled = true;
            var old = await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Volatile.Write(ref old.PlayedTicks, 100);
            Assert.Equal(TimeSpan.FromTicks(100), output.PlayedMediaTime);
            output.Enabled = false;
            output.Enabled = true;
            output.Submit([1, 2, 3, 4], null);
            releaseOld.Set();
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await second.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Null(output.PlayedMediaTime);
        }
        finally { output.Enabled = false; releaseOld.Set(); releaseNew.Set(); }
    }

    [Fact]
    public async Task StalledEndpointDoesNotHoldDispose()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource();
        var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new WasapiPlayback(44100, (_, _) =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(8));
            left.TrySetResult();
        });
        output.Enabled = true;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await Task.Run(output.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
            output.Enabled = true;
            Assert.False(output.Enabled);
            Assert.False(left.Task.IsCompleted);
        }
        finally { release.Set(); await left.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
}
