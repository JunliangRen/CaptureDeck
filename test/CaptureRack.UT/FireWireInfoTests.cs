using System.Buffers.Binary;
using CaptureRack.DirectShow.Windows.FireWire.MsTape;
using CaptureRack.QualityControl;
using Xunit;

namespace CaptureRack.UT;

public sealed class FireWireInfoTests
{
    [Fact]
    public void LiveInfoUpdatesWhenTheSameProgramChangesItsTracks()
    {
        var monitor = new FireWireTsMonitor(TsLayout.Ts192);
        var original = Packet(0, 0, Section(0, 0xb0, [0, 1, 0xc1, 0, 0, 0, 42, 0xe1, 0]));
        Assert.False(monitor.Feed(original.AsSpan(0, 77)));
        Assert.False(monitor.Feed(original.AsSpan(77)));
        Assert.False(monitor.Feed(Pmt(cc: 0, version: 0, videoPid: 0x111, audioType: 0xf, audioPid: 0x112)));
        monitor.Feed(Pcr(0x111, 0, 27_000_000));
        monitor.Feed(Pcr(0x111, 1, 54_000_000));

        Assert.True(monitor.Feed(Pmt(cc: 1, version: 1, videoPid: 0x121, audioType: 3, audioPid: 0x122)));
        var changed = Assert.IsType<FireWireTsInfo>(monitor.Snapshot);
        Assert.Equal(2, changed.Segment);
        Assert.Null(changed.ElapsedSeconds);
        var program = Assert.Single(changed.Programs);
        Assert.Equal(42, program.Number);
        Assert.Equal(0x121, program.PcrPid);
        Assert.Equal(new[] { 0x121, 0x122 }, program.Tracks.Select(t => t.Pid));
        Assert.Equal(3, program.Tracks[1].StreamType);

        Assert.False(monitor.Feed(Pmt(cc: 2, version: 1, videoPid: 0x121, audioType: 3, audioPid: 0x122)));
        monitor.Feed(Pcr(0x121, 0, 270_000_000));
        monitor.Feed(Pcr(0x121, 1, 297_000_000));
        Assert.True(monitor.Feed(Pcr(0x121, 2, 27_000_000)));
        Assert.Equal(3, monitor.Snapshot!.Segment);
        Assert.Equal(1, monitor.Snapshot.ClockJumps);
        Assert.Null(monitor.Snapshot.ElapsedSeconds);
    }

    [Fact]
    public void SitProgramChangeAndDitSourceChangeOpenNewSegments()
    {
        var monitor = new FireWireTsMonitor(TsLayout.Ts192);
        monitor.Feed(Packet(0, 0, Section(0, 0xb0, [0, 1, 0xc1, 0, 0, 0, 42, 0xe1, 0])));
        monitor.Feed(Pmt(cc: 0, version: 0, videoPid: 0x111, audioType: 0xf, audioPid: 0x112));

        Assert.False(monitor.Feed(Sit(0, (byte)'A', 0x18)));
        Assert.True(monitor.Feed(Sit(1, (byte)'B', 0x19)));
        Assert.Equal(2, monitor.Snapshot!.Segment);
        var program = Assert.Single(monitor.Snapshot.Programs);
        Assert.Equal("NHKB", program.EventName);
        Assert.Contains("19:00:00", program.BroadcastStart);

        Assert.False(monitor.Feed(Packet(0x1e, 0, [0x7e, 0x70, 1, 0x7f])));
        Assert.True(monitor.Feed(Packet(0x1e, 1, [0x7e, 0x70, 1, 0xff])));
        Assert.Equal(3, monitor.Snapshot!.Segment);
        Assert.Empty(monitor.Snapshot.Programs);
    }

    private static byte[] Sit(int cc, byte name, byte startHour) =>
        Packet(0x1f, cc, Section(0x7f, 0xf0,
        [
            0xff, 0xff, 0xc1, 0, 0, 0xf0, 0,
            0, 42, 0xf0, 26,
            0x4d, 9, 0x6a, 0x70, 0x6e, 5, 0x0e, 0x4e, 0x48, 0x4b, name,
            0xc3, 13, 1, 0xcf, 0x0c, startHour, 0, 0, 0, 0, 0, 0, 0, 0, 0xf8
        ]));

    [Fact]
    public void AribNamesUseTheSignalledCharacterSetAndKeepUnsupportedBytes()
    {
        Assert.Equal("NHK", FireWireBroadcastText.Decode([0x0e, 0x4e, 0x48, 0x4b]));
        Assert.Equal("日本", FireWireBroadcastText.Decode([0x46, 0x7c, 0x4b, 0x5c]));
        Assert.Equal("HEX 1B20", FireWireBroadcastText.Decode([0x1b, 0x20]));
    }

    [Fact]
    public void MultisectionPatReplacesProgramsOnlyAfterAllSectionsArrive()
    {
        var monitor = new FireWireTsMonitor(TsLayout.Ts192);
        monitor.Feed(Packet(0, 0, Section(0, 0xb0, [0, 1, 0xc1, 0, 0, 0, 42, 0xe1, 0])));
        Assert.Equal(42, Assert.Single(monitor.Snapshot!.Programs).Number);

        Assert.False(monitor.Feed(Packet(0, 1, Section(0, 0xb0, [0, 1, 0xc3, 1, 1, 0, 6, 0xe1, 6]))));
        Assert.True(monitor.Feed(Packet(0, 2, Section(0, 0xb0, [0, 1, 0xc3, 0, 1, 0, 5, 0xe1, 5]))));
        Assert.Equal(2, monitor.Snapshot!.Segment);
        Assert.Equal(new[] { 5, 6 }, monitor.Snapshot.Programs.Select(program => program.Number));
        Assert.False(monitor.Feed(Packet(0, 3, Section(0, 0xb0, [0, 1, 0xc3, 1, 1, 0, 6, 0xe1, 6]))));
    }

    private static byte[] Pmt(int cc, int version, int videoPid, byte audioType, int audioPid) =>
        Packet(0x100, cc, Section(2, 0xb0,
        [
            0, 42, (byte)(0xc1 | (version << 1)), 0, 0,
            (byte)(0xe0 | (videoPid >> 8)), (byte)videoPid, 0xf0, 0,
            2, (byte)(0xe0 | (videoPid >> 8)), (byte)videoPid, 0xf0, 0,
            audioType, (byte)(0xe0 | (audioPid >> 8)), (byte)audioPid, 0xf0, 0
        ]));

    private static byte[] Pcr(int pid, int cc, long ticks)
    {
        var packet = Packet(pid, cc, []);
        packet[5] &= 0x1f;
        packet[7] = (byte)(0x20 | cc);
        packet[8] = 183;
        packet[9] = 0x10;
        var value = ticks / 300;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(10), (uint)(value >> 1));
        packet[14] = (byte)(((value & 1) << 7) | 0x7e);
        packet[15] = 0;
        return packet;
    }

    private static byte[] Packet(int pid, int cc, ReadOnlySpan<byte> section)
    {
        var packet = new byte[192];
        packet.AsSpan().Fill(0xff);
        packet[4] = 0x47;
        packet[5] = (byte)(0x40 | (pid >> 8));
        packet[6] = (byte)pid;
        packet[7] = (byte)(0x10 | cc);
        packet[8] = 0;
        section.CopyTo(packet.AsSpan(9));
        return packet;
    }

    private static byte[] Section(byte tableId, byte header, ReadOnlySpan<byte> body)
    {
        var section = new byte[3 + body.Length + 4];
        section[0] = tableId;
        section[1] = (byte)(header | ((body.Length + 4) >> 8));
        section[2] = (byte)(body.Length + 4);
        body.CopyTo(section.AsSpan(3));
        uint crc = uint.MaxValue;
        foreach (var value in section.AsSpan(0, section.Length - 4))
        {
            crc ^= (uint)value << 24;
            for (var i = 0; i < 8; i++) crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04c11db7u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(section.AsSpan(section.Length - 4), crc);
        return section;
    }
}
