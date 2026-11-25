using CaptureRack.DeckLink;
using Xunit;

namespace CaptureRack.UT;

public sealed class VbiLineEvidenceTests
{
    [Fact]
    public void CaptionNeedsClockStartBitsAndTwoOddParityBytes()
    {
        var good = BuildCaption(0xC1, 0xC2);
        var wrongParity = BuildCaption(0x41, 0xC2);
        Assert.True(VbiLineEvidence.HasCaption608(good));
        Assert.False(VbiLineEvidence.HasCaption608(wrongParity));
        Assert.False(VbiLineEvidence.HasCaption608(new byte[720]));

        var evidence = new VbiLineEvidence();
        evidence.Observe(21, good);
        Assert.Null(evidence.Kind);
        evidence.Observe(21, good);
        Assert.Equal(VancLineKind.Caption608, evidence.Kind);
    }

    [Fact]
    public void UnchangedPictureRowsDoNotCountAsMappedVbi()
    {
        var signal = BuildCaption(0xC1, 0xC2);
        var evidence = new VbiLineEvidence();
        for (var frame = 0; frame < 6; frame++)
            evidence.Observe(21, signal, [signal]);

        Assert.Null(evidence.Kind);
    }

    [Fact]
    public void RepeatedUnidentifiedWaveformIsReportedSeparately()
    {
        var row = new byte[720];
        for (var x = 40; x < 680; x++)
            row[x] = (byte)(x % 56 < 28 ? 120 : 16);

        var evidence = new VbiLineEvidence();
        evidence.Observe(18, row);
        evidence.Observe(18, row);
        Assert.Equal(VancLineKind.Waveform, evidence.Kind);

        var activePicture = new VbiLineEvidence();
        activePicture.Observe(26, row);
        activePicture.Observe(26, row);
        Assert.Null(activePicture.Kind);
    }

    [Fact]
    public void ScanPrefillsOnlyConfirmedLinesWithinTheMappingRange()
    {
        var disabled = new VancInputOptions();
        Assert.Equal(new VancInputOptions(false, 21, 0, 0), disabled.WithDetectedLines(
            [new(21, VancLineKind.Caption608), new(18, VancLineKind.Waveform), new(284, VancLineKind.AncPacket)]));
        Assert.Null(disabled.WithDetectedLines([new(18, VancLineKind.Waveform)]));
        Assert.Equal(new VancInputOptions(true, 9, 10, 18), (disabled with { Enabled = true }).WithDetectedLines(
            [new(21, VancLineKind.Caption608), new(18, VancLineKind.AncPacket), new(10, VancLineKind.AncPacket),
             new(9, VancLineKind.AncPacket), new(9, VancLineKind.AncPacket)]));
    }

    private static byte[] BuildCaption(byte first, byte second)
    {
        const double period = 26.8125;
        const double firstRisingMidpoint = 22;
        var row = new byte[720];
        var word = 4u | ((uint)first << 3) | ((uint)second << 11);
        for (var x = 0; x < row.Length; x++)
        {
            var clock = (x - firstRisingMidpoint) / period;
            var dataBit = (int)Math.Floor(clock - 6.75);
            row[x] = clock is >= -0.25 and < 6.75
                ? (byte)Math.Round(64 + 48 * Math.Sin(2 * Math.PI * clock))
                : (byte)(dataBit is >= 0 and < 19 && ((word >> dataBit) & 1) != 0 ? 112 : 16);
        }

        return row;
    }
}
