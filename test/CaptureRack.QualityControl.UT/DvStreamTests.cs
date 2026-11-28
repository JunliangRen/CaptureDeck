using Xunit;

namespace CaptureRack.QualityControl.UT;

public sealed class DvStreamTests
{
    [Fact]
    public void Dv50RejectsADuplicateDifChannel()
    {
        var bytes = MakeFrame(4, 2);
        var analyzer = new DvStreamAnalyzer();
        analyzer.Feed(bytes);
        Assert.Equal(DvEncoding.Dvcpro50, analyzer.Snapshot.LastFrame?.Encoding);
        bytes.AsSpan(0, 120000).CopyTo(bytes.AsSpan(120000));
        analyzer.Feed(bytes);
        Assert.Equal(1, analyzer.Snapshot.Frames);
        Assert.Equal(1, analyzer.Snapshot.InvalidFrames);
        Assert.Equal(0, analyzer.Snapshot.TrailingBytes);
    }

    [Fact]
    public void ProgressiveHdAcceptsBothDifChannelPairsAcrossFragmentedSamples()
    {
        var first = MakeFrame(0x18, 2);
        var second = MakeFrame(0x18, 2, firstChannel: 2);
        var analyzer = new DvStreamAnalyzer(DvEncoding.DvcproHd);
        analyzer.Feed(first.AsSpan(0, 3));
        analyzer.Feed(first.AsSpan(3, 15200));
        Assert.Equal(first.Length - 15203, analyzer.BytesToFrameBoundary);
        analyzer.Feed(first.AsSpan(15203));
        analyzer.Feed(second);
        Assert.True(analyzer.Snapshot.Success);
        Assert.Equal(2, analyzer.Snapshot.Frames);
        Assert.Equal(0, analyzer.Snapshot.FormatChanges);
    }

    [Fact]
    public void RecoveryKeepsTrailingBytesSeparateFromPreviousDamage()
    {
        var frame = MakeFrame(0, 1);
        var analyzer = new DvStreamAnalyzer();
        analyzer.Feed(new byte[500]);
        analyzer.Feed(new byte[17]);
        Assert.Equal(517, analyzer.Snapshot.TrailingBytes);
        analyzer.Feed(frame.AsSpan(0, 3), discontinuity: true);
        analyzer.Feed(frame.AsSpan(3));
        Assert.Equal(1, analyzer.Snapshot.Frames);
        Assert.Equal(0, analyzer.Snapshot.TrailingBytes);
        analyzer.Feed(new byte[600]);
        Assert.Equal(600, analyzer.Snapshot.TrailingBytes);
        Assert.Equal(2, analyzer.Snapshot.InvalidFrames);
        analyzer.Feed(frame);
        Assert.Equal(2, analyzer.Snapshot.Frames);
        Assert.Equal(0, analyzer.Snapshot.TrailingBytes);
    }

    [Fact]
    public void ExplicitProfileRejectsWrongContentEvenWhenFrameSizesMatch()
    {
        var analyzer = new DvStreamAnalyzer(DvEncoding.Dvcpro50, requireEncoding: true);
        analyzer.Feed(MakeFrame(4, 2));
        analyzer.Feed(MakeFrame(0x18, 2));
        analyzer.Feed(MakeFrame(4, 2));
        Assert.Equal(1, analyzer.Snapshot.UnexpectedFormatFrames);
        Assert.False(analyzer.Snapshot.Success);
        Assert.Equal(3, analyzer.Snapshot.Frames);
    }

    private static byte[] MakeFrame(byte sourceType, int channels, int firstChannel = 0)
    {
        const int sequences = 10;
        const int sequenceSize = 12000;
        var bytes = new byte[sequenceSize * sequences * channels];
        for (var channel = 0; channel < channels; channel++)
        {
            var channelIndex = channel + firstChannel;
            var channelBits = (channelIndex & 1) * 8 + ((channelIndex & 2) == 0 ? 7 : 3);
            for (var sequence = 0; sequence < sequences; sequence++)
            {
                var offset = (channel * sequences + sequence) * sequenceSize;
                bytes[offset] = 0x1f;
                bytes[offset + 1] = (byte)(sequence * 16 + channelBits);
                bytes[offset + 3] = 0x3f;
                bytes[offset + 451] = sourceType;
            }
        }
        return bytes;
    }
}
