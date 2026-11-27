using System.Runtime.InteropServices;
using CaptureRack.Audio;
using Xunit;

namespace CaptureRack.UT;

public sealed class LocalAudioMonitorTests
{
    [Theory]
    [InlineData(CaptureAudioFormat.Pcm16)]
    [InlineData(CaptureAudioFormat.Pcm32)]
    public void MeterReportsSourceChannelClipsAndSelectedPairPeaks(CaptureAudioFormat format)
    {
        var meter = new StereoMonitor(format, channels: 4) { Route = AudioMonitorRoute.Pair(1) };
        if (format == CaptureAudioFormat.Pcm16)
            meter.AddFrame(new short[] { short.MinValue, 0, 8192, -16384 });
        else
            meter.AddFrame(new int[] { int.MinValue, 0, 1 << 29, -(1 << 30) });

        var levels = Assert.IsType<StereoLevels>(meter.ReadMeterLevels());
        Assert.Equal((0.25, 0.5), (levels.LeftPeak, levels.RightPeak));
        Assert.Equal((uint)format, levels.SampleBits);
        Assert.Equal(1, levels.ClipsForChannel(0));
        Assert.Equal(1, levels.TotalClips);
        Assert.Equal((0L, 0L), (levels.LeftClips, levels.RightClips));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public async Task SpeakerPacketsApplyGainAndRouteEachFrame(int bits)
    {
        var ready = new TaskCompletionSource<WasapiPlayback.Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new ManualResetEventSlim();
        using var monitor = new LocalAudioMonitor(new WasapiPlayback(48000, (session, _) =>
        {
            ready.TrySetResult(session);
            stop.Wait(TimeSpan.FromSeconds(10));
        })) { Enabled = true, Volume = 0.5 };
        try
        {
            var session = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            void Submit(short[] input, int channels, AudioMonitorRoute route,
                AudioChannelLayout layout = AudioChannelLayout.Unknown)
            {
                if (bits == 16)
                    monitor.SubmitInterleavedPcm16(input, channels, route, layout);
                else
                    monitor.SubmitInterleavedPcm32(input.Select(x => (x << 16) | 1).ToArray(), channels, route, layout);
            }

            short[] ReadPacket()
            {
                Assert.True(session.Take(16, out var bytes, out _, out int trailing));
                Assert.Equal(0, trailing);
                return MemoryMarshal.Cast<byte, short>(bytes.AsSpan()).ToArray();
            }

            Submit([short.MinValue, short.MaxValue], 2, AudioMonitorRoute.Default);
            Assert.Equal(new short[] { -16384, 16384 }, ReadPacket());
            Submit([100, 200, -8000, 4000, 300, 400, 6000, -2000], 4, AudioMonitorRoute.Pair(1));
            Assert.Equal(new short[] { -4000, 2000, 3000, -1000 }, ReadPacket());
            Submit([1, 2, -8000], 3, AudioMonitorRoute.Pair(1));
            Assert.Equal(new short[] { -4000, -4000 }, ReadPacket());
            Submit([0, 0, 8192, 30000, 0, -8192], 6, AudioMonitorRoute.Downmix, AudioChannelLayout.Surround51);
            Assert.Equal(new short[] { 2896, 0 }, ReadPacket());
        }
        finally
        {
            monitor.Enabled = false;
            stop.Set();
        }
    }
}
