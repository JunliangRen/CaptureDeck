using CaptureRack.Audio;
using System.Buffers.Binary;
using CaptureRack.DeckLink;
using CaptureRack.DeckLink.Recording;
using Xunit;

namespace CaptureRack.UT;

public sealed class RecordingTimingTests
{
    private static readonly VideoMode Mode = new(1, "Synthetic", 48, 32, 1_001, 30_000, 0);
    private const int PcmBytesPerFrame = 4;
    private static readonly int Stride = MovRecording.StoredRowBytes(Mode.Width, CapturePixelFormat.V210);
    private static readonly int VideoBytes = Stride * Mode.Height;

    [Theory]
    [InlineData(1501, 0)]
    [InlineData(1502, 1)]
    [InlineData(2002, 1)]
    [InlineData(3003, 2)]
    public void CountsPlausibleForwardGaps(long delta, long missing)
    {
        var timeline = new FrameTimeline(Mode);
        timeline.Observe(10_000, false);
        timeline.Observe(10_000 + delta, false);
        Assert.Equal(missing, timeline.MissingFrames);
    }

    [Fact]
    public void ResynchronizesAfterVideoClockRollbackWithoutInventingADrop()
    {
        var timeline = new FrameTimeline(Mode);
        foreach (var timestamp in new long[] { 0, 1001, 0, 2002, 4004 })
            timeline.Observe(timestamp, false);

        Assert.Equal(1, timeline.MissingFrames);
        Assert.Equal(2, timeline.Events().Count(e => e.Kind == CaptureEventKind.TimestampRegression));
        Assert.Single(timeline.Events(), e => e.Kind == CaptureEventKind.FrameGap);
    }

    [Fact]
    public void VideoClockDiscontinuitiesDoNotOverflowTheTimeline()
    {
        var timeline = new FrameTimeline(Mode);
        timeline.Observe(long.MinValue, false);
        timeline.Observe(long.MaxValue, false);
        Assert.Equal(0, timeline.MissingFrames);
        Assert.Single(timeline.Events(), e => e.Kind == CaptureEventKind.TimestampRegression);
    }

    [Fact]
    public void KeepsEveryPacketAcrossAudioAndVideoTimeDiscontinuities()
    {
        using var stream = new MemoryStream();
        var movie = new QuickTimeMovie(stream, Mode, CapturePixelFormat.V210, VideoBytes, CaptureAudioFormat.Pcm16);
        var received = new List<byte>();
        var video = new long[] { 0, 1001, 0, 2002, 4004 };
        var audio = new long[] { 0, 1603, 3206, 6408, 8000, 25 };
        const int samples = 1602;

        for (var index = 0; index < audio.Length; index++)
        {
            if (index < video.Length)
            {
                var frame = Enumerable.Repeat((byte)(index + 1), VideoBytes).ToArray();
                movie.WriteVideo(frame, video[index], index == 4 ? 0 : Mode.FrameDuration, VideoSignal.Present);
                received.AddRange(frame);
            }
            var packet = Enumerable.Repeat((byte)(0x60 + index), samples * PcmBytesPerFrame).ToArray();
            movie.WriteAudio(packet, audio[index], samples, index < 3 ? VideoSignal.Present : VideoSignal.NoInput);
            received.AddRange(packet);
        }

        movie.Finish();
        var data = stream.ToArray();
        Assert.Equal(received.ToArray(), data.AsSpan(36, received.Count).ToArray());
        Assert.Equal(video.Length, movie.VideoFrames);
        Assert.Equal(samples * audio.Length, movie.AudioSamples);
        Assert.Equal(1, movie.Timing.SuspectedMissingVideoFrames);
        Assert.Equal(2, movie.Timing.VideoCorrections);
        Assert.Equal(1, movie.Timing.VideoInvalidDurations);
        Assert.Equal(3, movie.Timing.AudioDiscontinuities);
        Assert.Equal(1, movie.Timing.AudioTimestampResets);
        Assert.Equal(1600m, movie.Timing.ApparentAudioGapSamples);
        Assert.Equal(10m, movie.Timing.ApparentAudioOverlapSamples);
        Assert.Equal(7985m, movie.Timing.MaximumAudioOffsetSamples);
        var firstVideo = Assert.IsType<CaptureRack.Core.RecordingTimestampIssue>(movie.Timing.FirstVideoCorrection);
        Assert.Equal(3, firstVideo.PacketNumber);
        Assert.Equal(-2002m, firstVideo.Offset);
        var firstAudio = Assert.IsType<CaptureRack.Core.RecordingTimestampIssue>(movie.Timing.FirstAudioIssue);
        Assert.Equal(3, firstAudio.PacketNumber);
        Assert.Equal(3204m, firstAudio.ExpectedTimestamp);
        Assert.Equal(3206, firstAudio.ActualTimestamp);
        Assert.Equal(2m, firstAudio.Offset);
        Assert.Equal(samples, firstAudio.PreviousAudioSampleFrames);
        Assert.Equal(samples, firstAudio.AudioSampleFrames);
        Assert.Equal("Present", firstAudio.InputSignal);

        // stts stores three nominal frames, one longer interval, then one nominal frame.
        var timing = data.AsSpan().IndexOf("stts"u8);
        Assert.True(timing > received.Count);
        Assert.Equal(new uint[] { 3, 3, 1001, 1, 2002, 1, 1001 },
            Enumerable.Range(0, 7).Select(i => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(timing + 8 + i * 4))));
    }

    [Fact]
    public void IgnoresHarmlessPcmTimestampRounding()
    {
        var tracker = new AudioTimestampTracker();
        foreach (var time in new long[] { 100, 201, 301, 400, 500 })
            tracker.Observe(time, 100, VideoSignal.Present);

        Assert.Null(tracker.FirstIssue);
        Assert.Equal(0, tracker.Discontinuities);
        Assert.Equal(1m, tracker.MaximumOffsetSamples);
    }

    [Fact]
    public void AudioClockRecoveryDoesNotTurnTheResetIntoASampleGap()
    {
        var tracker = new AudioTimestampTracker();
        foreach (var time in new long[] { 0, 100, 0, 300, 500 })
            tracker.Observe(time, 100, VideoSignal.Present);

        Assert.Equal(3, tracker.Discontinuities);
        Assert.Equal(1, tracker.Resets);
        Assert.Equal(100m, tracker.ApparentGapSamples);
        Assert.Equal(0m, tracker.ApparentOverlapSamples);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FinalizesAUsableTrackWhenTheOtherTrackIsAbsent(bool video)
    {
        using var stream = new MemoryStream();
        var movie = new QuickTimeMovie(stream, Mode, CapturePixelFormat.V210, VideoBytes, CaptureAudioFormat.Pcm16);
        if (video)
            movie.WriteVideo(new byte[VideoBytes], 9000, Mode.FrameDuration);
        else
            movie.WriteAudio(new byte[4 * PcmBytesPerFrame], 9000, 4);

        movie.Finish();
        var data = stream.ToArray();
        Assert.Equal(video ? 1 : 0, movie.VideoFrames);
        Assert.Equal(video ? 0 : 4, movie.AudioSamples);
        Assert.True(data.AsSpan().IndexOf("moov"u8) > 0);
        Assert.Equal(data.AsSpan().IndexOf("trak"u8), data.AsSpan().LastIndexOf("trak"u8));
    }
}
