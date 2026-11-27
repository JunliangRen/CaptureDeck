using CaptureRack.App;
using CaptureRack.DeckLink;
using Xunit;

namespace CaptureRack.UT;

public class RfInspectionTests
{
    [Fact]
    public void RawPlaybackUsesRecorderRateAfterTapeLabel()
    {
        var file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_5msps_CaptureRack_DdD_40.000msps.s16");
        try
        {
            File.WriteAllBytes(file, [0, 0, 64, 0]);
            var input = CaptureRack.SignalCapture.RfPlaybackFile.Open(file, null);
            Assert.Equal(40_000_000, input.Rate);
            long position = -1;
            input.Play(1, null, _ => { }, (buffer, length, first) =>
            {
                position = first;
                Assert.Equal(2, length);
                Assert.Equal(64, buffer[0]);
            }, default);
            Assert.Equal(1, position);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void HeadSwitchRequiresAnEdgeAndContiguousHistory()
    {
        const int rate = 20_000_000;
        var trigger = new RfSwitchTrigger();
        var data = new short[64_000];
        var field = TimeSpan.FromMilliseconds(2);
        trigger.Add(data, 0);
        Assert.Null(trigger.Find(field, rate));
        for (int i = 0; i < data.Length; i++)
            data[i] = (short)((i % 2 == 0 ? 1 : -1) * (i < 39_000 ? 100 : 1200));
        trigger.Add(data, 0);
        var trace = Assert.IsType<short[]>(trigger.Find(field, rate));
        Assert.Equal(100, Math.Abs(trace[0]));
        Assert.Equal(1200, Math.Abs(trace[^1]));
        trigger.Add(data, 500_000);
        Assert.Null(trigger.Find(field, rate));
    }

    [Fact]
    public void DeviceClockHandlesDriverUnitsAndCounterWrap()
    {
        const uint rate = 48_000;
        Assert.Equal(24000L, LocalAudioMonitor.GetPlayedFrames(48000, rate, 1, 500));
        Assert.Equal(24000L, LocalAudioMonitor.GetPlayedFrames(48000, rate, 2, 24000));
        Assert.Equal(24000L, LocalAudioMonitor.GetPlayedFrames(48000, rate, 4, 96000));
        Assert.Equal((1L << 32) - 1, LocalAudioMonitor.GetPlayedFrames((1L << 32) + 32, rate, 2, uint.MaxValue));
        Assert.Null(LocalAudioMonitor.GetPlayedFrames(48000, rate, 8, 0));
    }
}
