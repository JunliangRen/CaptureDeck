using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using CaptureRack.QualityControl;

namespace CaptureRack.DirectShow.Windows.FireWire.MsTape;

public sealed record FireWireTrack(int Pid, int StreamType, string? Language);
public sealed record FireWireProgram(int Number, int PmtPid, int? PcrPid, IReadOnlyList<FireWireTrack> Tracks,
    string? ServiceName = null, string? EventName = null, string? BroadcastStart = null, int? EventId = null);
public sealed record FireWirePidCount(int Pid, long Packets, long ContinuityErrors, long TransportErrors);
public sealed record FireWireTsInfo(DateTimeOffset ReadAt, int Segment, double? ElapsedSeconds, double? BitsPerSecond,
    long Packets, long ContinuityErrors, long MalformedPackets, long SyncErrors, long TransportErrors, long ClockJumps,
    string? PacketPrefix, string? BroadcastTime, string? Network, IReadOnlyList<int> ServiceTablePids,
    IReadOnlyList<FireWireProgram> Programs, IReadOnlyList<FireWirePidCount> Pids);

internal sealed class FireWireTsMonitor(TsLayout layout)
{
    private readonly byte[] pending = new byte[layout.Stride];
    private readonly Dictionary<int, SectionBuffer> sections = [];
    private readonly Dictionary<int, FireWireProgram> programs = [];
    private readonly HashSet<int> serviceTablePids = [];
    private readonly HashSet<int> pmtPids = [];
    private TsAnalyzer analyzer = new(layout);
    private int pendingLength, segment = 1;
    private long lastInput, lastPublished, bytesAtPublish;
    private long? previousClock;
    private double elapsed;
    private bool clockKnown, pcrClock, changed;
    private int primaryProgram;
    private int? transportId;
    private (int Id, int Version, int LastSection)? pendingPat;
    private readonly Dictionary<int, Dictionary<int, int>> patSections = [];
    private uint? prefix;
    private string? broadcastTime, network;
    private long clockJumps;
    public FireWireTsInfo? Snapshot { get; private set; }

    public bool Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return false;
        var now = Stopwatch.GetTimestamp();
        if (lastInput != 0 && Stopwatch.GetElapsedTime(lastInput, now) >= FireWireLiveSession.InputGap)
        {
            StartSegment(clearTables: true);
            pendingLength = 0;
        }
        lastInput = now;
        while (!bytes.IsEmpty)
        {
            if (pendingLength == 0 && bytes.Length >= layout.Stride)
            {
                Packet(bytes[..layout.Stride]);
                bytes = bytes[layout.Stride..];
                continue;
            }
            var take = Math.Min(layout.Stride - pendingLength, bytes.Length);
            bytes[..take].CopyTo(pending.AsSpan(pendingLength));
            pendingLength += take;
            bytes = bytes[take..];
            if (pendingLength == layout.Stride)
            {
                Packet(pending);
                pendingLength = 0;
            }
        }

        UpdateClock();
        if (lastPublished == 0 || changed || Stopwatch.GetElapsedTime(lastPublished, now).TotalMilliseconds >= 500)
        {
            var stats = analyzer.Statistics;
            var interval = Stopwatch.GetElapsedTime(lastPublished, now).TotalSeconds;
            double? bitrate = lastPublished == 0 || changed || interval <= 0 ? null : (stats.InputBytes - bytesAtPublish) * 8 / interval;
            Snapshot = new(DateTimeOffset.Now, segment, clockKnown ? elapsed : null, bitrate, stats.Packets,
                stats.ContinuityErrors, stats.MalformedPackets, stats.SyncErrors, stats.Tei, Math.Max(clockJumps, stats.PcrBackwards),
                prefix?.ToString("X8"), broadcastTime, network, serviceTablePids.Order().ToArray(), programs.Values.OrderBy(p => p.Number).ToArray(),
                stats.Pids.Select(pid => new FireWirePidCount(pid.Pid, pid.Packets, pid.ContinuityErrors, pid.Tei)).ToArray());
            bytesAtPublish = stats.InputBytes;
            lastPublished = now;
        }
        var result = changed;
        changed = false;
        return result;
    }

    private void StartSegment(bool clearTables)
    {
        if (!changed)
            segment++;
        changed = true;
        analyzer = new TsAnalyzer(layout);
        elapsed = 0;
        clockKnown = pcrClock = false;
        previousClock = null;
        prefix = null;
        broadcastTime = network = null;
        serviceTablePids.Clear();
        if (clearTables)
        {
            programs.Clear();
            pmtPids.Clear();
            sections.Clear();
            pendingPat = null;
            patSections.Clear();
            transportId = null;
            primaryProgram = 0;
        }
        else
        {
            foreach (var (number, program) in programs.ToArray())
                programs[number] = program with { ServiceName = null, EventName = null, BroadcastStart = null, EventId = null };
        }
    }

    private void UpdateClock()
    {
        if (!programs.TryGetValue(primaryProgram, out var program))
            return;
        var clock = analyzer.Statistics.Pids.FirstOrDefault(pid => pid.Pid == program.PcrPid)?.LastPcr;
        var pcr = clock.HasValue;
        var videoPid = program.Tracks.FirstOrDefault(track => track.StreamType is 1 or 2 or 0x1b or 0x24)?.Pid;
        clock ??= analyzer.Statistics.Pids.FirstOrDefault(pid => pid.Pid == videoPid)?.LastPts * 300;
        if (clock is not { } current)
            return;
        if (previousClock is { } previous && pcr == pcrClock)
        {
            const long modulus = (1L << 33) * 300;
            var delta = current - previous;
            if (delta < -modulus / 2) delta += modulus;
            if (delta > modulus / 2) delta -= modulus;
            if (delta is < -27_000_000 or > 135_000_000)
            {
                clockJumps++;
                StartSegment(clearTables: false);
                return;
            }
            if (delta < 0)
                return; // Presentation timestamps may arrive out of display order.
            elapsed += delta / 27_000_000d;
        }
        clockKnown = true;
        pcrClock = pcr;
        previousClock = current;
    }

    private void Packet(ReadOnlySpan<byte> stride)
    {
        analyzer.Feed(stride);
        var packet = stride.Slice(layout.Offset, 188);
        if (packet[0] != 0x47 || (packet[1] & 0x80) != 0)
            return;
        if (layout.Offset == 4)
            prefix = BinaryPrimitives.ReadUInt32BigEndian(stride);
        var pid = ((packet[1] & 31) << 8) | packet[2];
        if (pid is not (0 or 0x11 or 0x12 or 0x14 or 0x1e or 0x1f) && !pmtPids.Contains(pid))
            return;
        if ((packet[3] & 0x10) == 0 || (packet[3] & 0xc0) != 0)
            return;
        var adaptation = (packet[3] & 0x20) != 0;
        var discontinuity = adaptation && packet[4] > 0 && (packet[5] & 0x80) != 0;
        var offset = adaptation ? 5 + packet[4] : 4;
        if (offset >= packet.Length)
            return;
        if (!sections.TryGetValue(pid, out var parser))
            sections[pid] = parser = new SectionBuffer();
        parser.Feed(packet[offset..], (packet[1] & 0x40) != 0, packet[3] & 15, discontinuity, pid, ReadSection);
    }

    private void ReadSection(int pid, ReadOnlySpan<byte> section)
    {
        var table = section[0];
        if (table is not (0x70 or 0x7e) && !ValidCrc(section))
            return;
        if ((section[1] & 0x80) != 0 && (section.Length < 8 || (section[5] & 1) == 0))
            return;
        switch (pid, table)
        {
            case (0, 0):
                ReadPat(section);
                break;
            case (_, 2) when section.Length >= 16:
                ReadPmt(pid, section);
                break;
            case (0x11, 0x42):
                serviceTablePids.Add(pid);
                ReadSdt(section);
                break;
            case (0x12, 0x4e):
                serviceTablePids.Add(pid);
                ReadEit(section);
                break;
            case (0x1e, 0x7e) when section.Length == 4:
                if ((section[3] & 0x80) != 0 && transportId.HasValue)
                    StartSegment(clearTables: true);
                serviceTablePids.Add(pid);
                break;
            case (0x1f, 0x7f):
                serviceTablePids.Add(pid);
                ReadSit(section);
                break;
            case (0x14, 0x70 or 0x73):
                serviceTablePids.Add(pid);
                if (section.Length >= 8) broadcastTime = TvDate(section[3..8]);
                break;
        }
    }

    private void ReadPat(ReadOnlySpan<byte> section)
    {
        if (section.Length < 12) return;
        var id = U16(section[3..]);
        var numberInTable = section[6];
        var lastSection = section[7];
        if (numberInTable > lastSection)
            return;
        var table = (Id: id, Version: (section[5] >> 1) & 0x1f, LastSection: (int)lastSection);
        if (pendingPat != table)
        {
            pendingPat = table;
            patSections.Clear();
        }
        var entries = new Dictionary<int, int>();
        for (var i = 8; i + 4 <= section.Length - 4; i += 4)
        {
            var number = U16(section[i..]);
            if (number != 0) entries[number] = U16(section[(i + 2)..]) & 0x1fff;
        }
        patSections[numberInTable] = entries;
        if (patSections.Count != lastSection + 1)
            return;

        var next = new Dictionary<int, int>();
        foreach (var part in patSections.Values)
            foreach (var (number, pid) in part)
                next[number] = pid;
        if (transportId is { } old && (old != id || programs.Count != next.Count ||
            programs.Any(pair => !next.TryGetValue(pair.Key, out var pid) || pid != pair.Value.PmtPid)))
            StartSegment(clearTables: true);
        transportId = id;
        foreach (var (number, pmt) in next)
        {
            programs.TryAdd(number, new(number, pmt, null, []));
            pmtPids.Add(pmt);
        }
        primaryProgram = programs.Keys.Order().FirstOrDefault();
    }

    private void ReadPmt(int pid, ReadOnlySpan<byte> section)
    {
        var number = U16(section[3..]);
        if (!programs.TryGetValue(number, out var old) || old.PmtPid != pid)
            return;
        var pcr = U16(section[8..]) & 0x1fff;
        var end = section.Length - 4;
        var i = 12 + (U16(section[10..]) & 0xfff);
        var tracks = new List<FireWireTrack>();
        while (i + 5 <= end)
        {
            var infoLength = U16(section[(i + 3)..]) & 0xfff;
            if (i + 5 + infoLength > end) return;
            var language = Descriptor(section.Slice(i + 5, infoLength), 0x0a);
            tracks.Add(new(U16(section[(i + 1)..]) & 0x1fff, section[i],
                language.Length >= 3 ? Encoding.ASCII.GetString(language[..3]) : null));
            i += 5 + infoLength;
        }
        if (i != end) return;
        if (old.PcrPid is not null && (old.PcrPid != pcr || !old.Tracks.SequenceEqual(tracks)))
        {
            StartSegment(clearTables: false);
            old = programs[number];
        }
        if (old.PcrPid != pcr || !old.Tracks.SequenceEqual(tracks))
            programs[number] = old with { PcrPid = pcr, Tracks = tracks.ToArray() };
    }

    private void ReadSdt(ReadOnlySpan<byte> section)
    {
        for (var i = 11; i + 5 <= section.Length - 4;)
        {
            var number = U16(section[i..]);
            var total = U16(section[(i + 3)..]) & 0xfff;
            i += 5;
            if (i + total > section.Length - 4) return;
            var service = Descriptor(section.Slice(i, total), 0x48);
            if (programs.TryGetValue(number, out var program) && service.Length >= 3)
            {
                if (ServiceName(service) is { } name)
                    programs[number] = program with { ServiceName = name };
            }
            i += total;
        }
    }

    private void ReadSit(ReadOnlySpan<byte> section)
    {
        if (section.Length < 14)
            return;
        var end = section.Length - 4;
        var transmissionLength = U16(section[8..]) & 0xfff;
        if (10 + transmissionLength > end)
            return;
        var transmission = section.Slice(10, transmissionLength);
        ReadPartialTsClock(Descriptor(transmission, 0xc3));
        var networkId = Descriptor(transmission, 0xc2);
        if (networkId.Length >= 7)
            network = $"{Encoding.ASCII.GetString(networkId[..3])} · {Encoding.ASCII.GetString(networkId[3..5])} · {U16(networkId[5..])}";

        for (var i = 10 + transmissionLength; i + 4 <= end;)
        {
            var number = U16(section[i..]);
            var length = U16(section[(i + 2)..]) & 0xfff;
            i += 4;
            if (i + length > end)
                return;
            var descriptors = section.Slice(i, length);
            i += length;
            if (!programs.TryGetValue(number, out var program))
                continue;

            var time = Descriptor(descriptors, 0xc3);
            var start = time.Length >= 6 ? TvDate(time[1..6]) : null;
            var title = ShortEventTitle(Descriptor(descriptors, 0x4d));
            var service = ServiceName(Descriptor(descriptors, 0x48)) ?? program.ServiceName;
            if (program.EventId is null && program.Number == primaryProgram &&
                (start is not null && program.BroadcastStart is { } previous && previous != start ||
                 title is not null && program.EventName is { } previousTitle && previousTitle != title))
            {
                var clock = broadcastTime;
                var source = network;
                StartSegment(clearTables: false);
                broadcastTime = clock;
                network = source;
                serviceTablePids.Add(0x1f);
                program = programs[number];
            }
            ReadPartialTsClock(time);
            programs[number] = program with
            {
                ServiceName = service,
                EventName = program.EventId is null ? title ?? program.EventName : program.EventName ?? title,
                BroadcastStart = program.EventId is null ? start ?? program.BroadcastStart : program.BroadcastStart ?? start
            };
        }
    }

    private void ReadPartialTsClock(ReadOnlySpan<byte> time)
    {
        if (time.Length >= 18 && (time[12] & 1) != 0)
            broadcastTime = TvDate(time[13..18]) ?? broadcastTime;
    }

    private static string? ServiceName(ReadOnlySpan<byte> service)
    {
        if (service.Length < 3) return null;
        var start = 2 + service[1];
        return start < service.Length && start + 1 + service[start] <= service.Length
            ? BroadcastLabel(service.Slice(start + 1, service[start])) : null;
    }

    private static string? ShortEventTitle(ReadOnlySpan<byte> name) =>
        name.Length >= 4 && 4 + name[3] <= name.Length ? BroadcastLabel(name.Slice(4, name[3])) : null;

    private void ReadEit(ReadOnlySpan<byte> section)
    {
        if (section.Length < 30 || section[6] != 0 || !programs.TryGetValue(U16(section[3..]), out var program))
            return;
        var flags = U16(section[24..]);
        var total = flags & 0xfff;
        if (26 + total > section.Length - 4) return;
        var name = Descriptor(section.Slice(26, total), 0x4d);
        var eventId = U16(section[14..]);
        if (program.Number == primaryProgram && program.EventId is { } previousEvent && previousEvent != eventId)
        {
            var services = programs.Values.ToDictionary(p => p.Number, p => p.ServiceName);
            StartSegment(clearTables: false);
            foreach (var (number, service) in services)
                programs[number] = programs[number] with { ServiceName = service };
            program = programs[program.Number];
        }
        programs[program.Number] = program with
        {
            EventId = eventId,
            EventName = ShortEventTitle(name) ?? program.EventName,
            BroadcastStart = TvDate(section[16..21])
        };
    }

    private static ReadOnlySpan<byte> Descriptor(ReadOnlySpan<byte> data, int tag)
    {
        while (data.Length >= 2 && data[1] <= data.Length - 2)
        {
            var size = data[1];
            if (data[0] == tag) return data.Slice(2, size);
            data = data[(size + 2)..];
        }
        return [];
    }

    private static string? BroadcastLabel(ReadOnlySpan<byte> bytes) => FireWireBroadcastText.Decode(bytes);

    private static string? TvDate(ReadOnlySpan<byte> bytes)
    {
        if (U16(bytes) == 0xffff || !Bcd(bytes[2], 23) || !Bcd(bytes[3], 59) || !Bcd(bytes[4], 59))
            return null;
        var date = new DateOnly(1858, 11, 17).AddDays(U16(bytes));
        return FormattableString.Invariant($"{date:yyyy-MM-dd} {bytes[2]:X2}:{bytes[3]:X2}:{bytes[4]:X2}");

        static bool Bcd(byte value, int max) => (value & 15) <= 9 && (value >> 4) * 10 + (value & 15) <= max;
    }

    private static int U16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static bool ValidCrc(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (var b in data)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++)
                crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04c11db7u : 0);
        }
        return crc == 0;
    }

    private sealed class SectionBuffer
    {
        private readonly byte[] buffer = new byte[4096];
        private int count, target, lastContinuity = -1;
        internal delegate void SectionHandler(int pid, ReadOnlySpan<byte> section);

        public void Feed(ReadOnlySpan<byte> payload, bool start, int cc, bool discontinuity, int pid, SectionHandler handler)
        {
            if (!discontinuity && cc == lastContinuity) return;
            if (discontinuity || cc != ((lastContinuity + 1) & 15)) count = target = 0;
            lastContinuity = cc;
            if (start)
            {
                var pointer = payload[0];
                if (pointer >= payload.Length) { count = target = 0; return; }
                if (count != 0) Read(payload.Slice(1, pointer), false, pid, handler);
                count = target = 0;
                Read(payload[(1 + pointer)..], true, pid, handler);
            }
            else if (count != 0)
                Read(payload, false, pid, handler);
        }

        private void Read(ReadOnlySpan<byte> payload, bool allowNew, int pid, SectionHandler handler)
        {
            while (!payload.IsEmpty && (count != 0 || allowNew && payload[0] != 0xff))
            {
                var remaining = (count < 3 ? 3 : target) - count;
                var take = Math.Min(payload.Length, remaining);
                payload[..take].CopyTo(buffer.AsSpan(count));
                count += take;
                payload = payload[take..];
                if (count == 3 && target == 0)
                {
                    target = 3 + (((buffer[1] & 15) << 8) | buffer[2]);
                    if (target <= 3 || target > buffer.Length) { count = target = 0; return; }
                }
                if (count != target || target == 0) continue;
                handler(pid, buffer.AsSpan(0, count));
                count = target = 0;
            }
        }
    }
}
