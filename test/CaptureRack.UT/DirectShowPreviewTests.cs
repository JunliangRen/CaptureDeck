using CaptureRack.DirectShow.Windows.Capture;
using Xunit;

namespace CaptureRack.UT;

public sealed class DirectShowPreviewTests
{
    [Theory]
    [InlineData("YUY2", new byte[] { 16, 128, 235, 128, 16, 128, 235, 128 })]
    [InlineData("NV12", new byte[] { 16, 235, 16, 235, 128, 128 })]
    [InlineData("I420", new byte[] { 16, 235, 16, 235, 128, 128 })]
    [InlineData("UYVY", new byte[] { 128, 16, 128, 235, 128, 16, 128, 235 })]
    public void VideoConversionKeepsBlackAndWhite(string encoding, byte[] input)
    {
        var format = Video(encoding);
        var output = new byte[16];
        Assert.True(DirectShowPreviewVideo.TryConvert(format, encoding, input, output, 2, 2));
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, output[..8]);
        Assert.Equal(output[..8], output[8..]);
        Assert.False(DirectShowPreviewVideo.TryConvert(format, encoding, input.AsSpan(0, input.Length - 1), output, 2, 2));
    }

    [Fact]
    public void InfraredPreservesTheFullGrayRange()
    {
        byte[] output = new byte[16];
        Assert.True(DirectShowPreviewVideo.TryConvert(Video("L8_IR"), "L8_IR", [0, 32, 210, 255], output, 2, 2));
        Assert.Equal(new byte[] { 0, 0, 0, 255, 32, 32, 32, 255, 210, 210, 210, 255, 255, 255, 255, 255 }, output);
    }

    [Fact]
    public void Rgb24HonorsBottomUpOrientationAndRowPadding()
    {
        var format = Video("RGB24") with { Width = 1, BitmapTopDown = false };
        byte[] output = new byte[8];
        Assert.True(DirectShowPreviewVideo.TryConvert(format, "RGB24", [1, 2, 3, 0, 4, 5, 6, 0], output, 1, 2));
        Assert.Equal(new byte[] { 4, 5, 6, 255, 1, 2, 3, 255 }, output);
    }

    [Fact]
    public void Pcm24AudioPreservesSignAndTheFirstTwoChannels()
    {
        var format = new DirectShowFormat(0, "1", "Audio", "audio", Guid.Empty, Guid.Empty, Guid.Empty)
        {
            WaveFormatTag = 1, SampleRate = 48000, Channels = 3, BitsPerSample = 24, BlockAlign = 9
        };
        Assert.True(DirectShowPreviewAudio.Supports(format));
        short[] playback = new short[2];
        var peaks = DirectShowPreviewAudio.ReadPeaks(format, [0, 0, 0x80, 0, 0, 0x40, 0, 0, 0], playback);
        Assert.Equal(new short[] { short.MinValue, 16384 }, playback);
        Assert.Equal(1d, peaks.Left, 8);
        Assert.Equal(0.5, peaks.Right);
    }

    private static DirectShowFormat Video(string encoding) =>
        new(0, "0", "Capture", "video", Guid.Empty, Guid.Empty, Guid.Empty)
        {
            Width = 2, Height = 2, FourCc = encoding
        };
}
