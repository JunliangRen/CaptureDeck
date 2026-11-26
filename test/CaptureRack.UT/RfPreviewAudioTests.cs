using CaptureRack.App;
using Xunit;

namespace CaptureRack.UT;

public class RfPreviewAudioTests
{
    [Fact]
    public void TrackGenerationSeparatesSilenceLossAndReplacementPcm()
    {
        var sink = new RfPreviewAudio();
        var first = sink.SelectTrack("Stereo", 2, 48000);
        Assert.False(sink.Read().Locked);
        sink.SubmitPcm(first, [32767, 0, 0, -16384]);
        var playing = sink.Read();
        Assert.Equal(1, playing.Levels!.LeftClips);
        Assert.Equal(0.5, playing.Levels.RightPeak);
        sink.SubmitPcm(first, [0, 0]);
        var silent = sink.Read();
        Assert.True(silent.Locked);
        Assert.Equal(0, silent.Levels!.LeftPeak);
        Assert.True(sink.Read().Locked);
        sink.LoseLock(first);
        sink.SubmitPcm(first, [32767, 32767]);
        Assert.False(sink.Read().Locked);
        var next = sink.SelectTrack("Mono", 1, 44100);
        sink.SubmitPcm(next, [8192]);
        sink.LoseLock(first);
        var mono = sink.Read();
        Assert.True(mono.Locked);
        Assert.Equal("Mono", mono.Track);
        Assert.Equal(1, mono.Levels!.ChannelCount);
        Assert.Equal(0.25, mono.Levels.LeftPeak);
        Assert.Equal(0, mono.Levels.TotalClips);
    }
}
