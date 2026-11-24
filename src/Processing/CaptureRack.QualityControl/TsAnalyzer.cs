using System.Text.Json.Serialization;

namespace CaptureRack.QualityControl;

public readonly record struct TsLayout(int Offset, int Stride)
{
    public static TsLayout Ts188 => new(0, 188);
    public static TsLayout Ts192 => new(4, 192);
    public string Format => $"ts{Stride}";

    internal void Validate()
    {
        if (Stride is not (188 or 192) || Offset < 0 || Offset > Stride - 188)
            throw new ArgumentException("TS layout requires 188-byte packets within a 188- or 192-byte stride.");
    }
}

public sealed class TsStatistics
{
    public long InputBytes { get; internal set; }
    public long Packets { get; internal set; }
    public long SyncErrors { get; internal set; }
    public long MalformedPackets { get; internal set; }
    public long Tei { get; internal set; }
    public long ContinuityErrors { get; internal set; }
    public long Duplicates { get; internal set; }
    public long Discontinuities { get; internal set; }
    public long PcrCount { get; internal set; }
    public long PcrBackwards { get; internal set; }
    public long PtsCount { get; internal set; }
    public long TrailingBytes { get; internal set; }
    public int PcrTickHz => 27000000;
    public int PtsTickHz => 90000;
    public long? FirstSyncErrorPacket { get; internal set; }
    public long? FirstMalformedPacket { get; internal set; }
    public long? FirstTeiPacket { get; internal set; }
    public long? FirstContinuityErrorPacket { get; internal set; }
    public long? FirstPcrBackwardsPacket { get; internal set; }
    public IEnumerable<TsPidStatistics> Pids => ByPid.Values;

    internal SortedDictionary<int, TsPidStatistics> ByPid { get; } = new();
    [JsonIgnore]
    public bool StructuralValidationOk => Packets != 0 && SyncErrors == 0 && MalformedPackets == 0 && TrailingBytes == 0;

    [JsonIgnore]
    public bool HasTransportWarnings => Tei != 0 || ContinuityErrors != 0 || PcrBackwards != 0;
}

public sealed class TsPidStatistics(int pid)
{
    public int Pid { get; } = pid;
    public long Packets { get; internal set; }
    public long Tei { get; internal set; }
    public long ContinuityErrors { get; internal set; }
    public long Duplicates { get; internal set; }
    public long Discontinuities { get; internal set; }
    public long PcrCount { get; internal set; }
    public long PcrBackwards { get; internal set; }
    public long PtsCount { get; internal set; }
    public long? FirstPcr { get; internal set; }
    public long? LastPcr { get; internal set; }
    public long? FirstPts { get; internal set; }
    public long? LastPts { get; internal set; }
}

public sealed class TsAnalyzer
{
    private const int PacketSize = 188;
    private const int NullPid = 0x1fff;
    private const long PcrWrap = (1L << 33) * 300;

    private readonly TsLayout layout;
    private readonly byte[] pending;
    private readonly PidState?[] states = new PidState[NullPid + 1];
    private int pendingCount;
    private bool finished;
    public TsStatistics Statistics { get; } = new();

    private sealed class PidState(TsPidStatistics statistics)
    {
        public TsPidStatistics Statistics { get; } = statistics;
        public int? Continuity;
        public readonly byte[] PreviousPacket = new byte[PacketSize];
        public bool PreviousPacketValid;
        public bool DuplicateSeen;
        public long? PreviousPcr;
        public readonly byte[] PesHeader = new byte[19];
        public int PesLength;
        public bool CollectingPes;

        public void ResetPes()
        {
            PesLength = 0;
            CollectingPes = false;
        }

        public void BreakPacketSequence()
        {
            PreviousPacketValid = false;
            DuplicateSeen = false;
            ResetPes();
        }
    }

    public TsAnalyzer(TsLayout layout)
    {
        layout.Validate();
        this.layout = layout;
        pending = new byte[layout.Stride];
    }

    public static TsLayout Detect(ReadOnlySpan<byte> bytes)
    {
        var standard = Matches(bytes, TsLayout.Ts188);
        var prefixed = Matches(bytes, TsLayout.Ts192);
        if (standard && prefixed)
            throw new InvalidDataException("Ambiguous TS188/TS192 layout; specify the declared driver layout.");
        if (!standard && !prefixed)
            throw new InvalidDataException("Cannot identify TS layout: need at least five aligned valid TS188 or TS192 packets; specify the declared driver layout.");
        return standard ? TsLayout.Ts188 : TsLayout.Ts192;
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, TsLayout layout)
    {
        var count = Math.Min(64, bytes.Length / layout.Stride);
        if (count < 5)
            return false;
        for (var index = 0; index < count; index++)
        {
            if (!ValidHeader(bytes.Slice(index * layout.Stride + layout.Offset, PacketSize)))
                return false;
        }
        return true;
    }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (finished)
            throw new InvalidOperationException("Cannot feed a finished TS analyzer.");
        Statistics.InputBytes += bytes.Length;
        if (pendingCount != 0)
        {
            var take = Math.Min(layout.Stride - pendingCount, bytes.Length);
            bytes[..take].CopyTo(pending.AsSpan(pendingCount));
            pendingCount += take;
            if (pendingCount != layout.Stride)
                return;
            ProcessPacket(pending.AsSpan(layout.Offset, PacketSize));
            pendingCount = 0;
            bytes = bytes[take..];
        }
        while (bytes.Length >= layout.Stride)
        {
            ProcessPacket(bytes.Slice(layout.Offset, PacketSize));
            bytes = bytes[layout.Stride..];
        }
        bytes.CopyTo(pending);
        pendingCount = bytes.Length;
    }

    public void Finish()
    {
        Statistics.TrailingBytes = pendingCount;
        finished = true;
    }

    private void ProcessPacket(ReadOnlySpan<byte> packet)
    {
        var index = Statistics.Packets++;
        if (packet[0] != 0x47)
        {
            Statistics.SyncErrors++;
            Statistics.FirstSyncErrorPacket ??= index;
            foreach (var state in states)
                state?.BreakPacketSequence();
            return;
        }

        var pid = ((packet[1] & 0x1f) << 8) | packet[2];
        var current = states[pid];
        if (current is null)
        {
            var stats = new TsPidStatistics(pid);
            Statistics.ByPid.Add(pid, stats);
            current = states[pid] = new PidState(stats);
        }
        current.Statistics.Packets++;
        if ((packet[1] & 0x80) != 0)
        {
            Statistics.Tei++;
            Statistics.FirstTeiPacket ??= index;
            current.Statistics.Tei++;
            current.BreakPacketSequence();
            return;
        }
        if (!ValidHeader(packet) || !TryReadAdaptation(packet, out var payloadOffset, out var discontinuity, out var pcr))
        {
            Malformed(index);
            current.BreakPacketSequence();
            return;
        }

        if (discontinuity)
        {
            Statistics.Discontinuities++;
            current.Statistics.Discontinuities++;
            current.Continuity = null;
            current.PreviousPcr = null;
            current.BreakPacketSequence();
        }
        var payload = (packet[3] & 0x10) != 0;
        var duplicate = pid != NullPid && CheckContinuity(current, packet, payload, index);
        if (pcr is long clock)
            TrackPcr(current, clock, index);
        if (!duplicate && payload && pid != NullPid)
        {
            if (!ProcessPes(current, packet[1], packet[3], packet[payloadOffset..]))
            {
                Malformed(index);
                return;
            }
        }
    }

    private void Malformed(long index)
    {
        Statistics.MalformedPackets++;
        Statistics.FirstMalformedPacket ??= index;
    }

    private bool CheckContinuity(PidState state, ReadOnlySpan<byte> packet, bool payload, long index)
    {
        var counter = packet[3] & 0x0f;
        var duplicate = false;
        if (state.Continuity is int previous)
        {
            duplicate = payload && counter == previous && state.PreviousPacketValid &&
                        SamePacket(state.PreviousPacket, packet);
            if (duplicate)
            {
                Statistics.Duplicates++;
                state.Statistics.Duplicates++;
            }
            if (duplicate ? state.DuplicateSeen : counter != ((previous + (payload ? 1 : 0)) & 0x0f))
            {
                Statistics.ContinuityErrors++;
                Statistics.FirstContinuityErrorPacket ??= index;
                state.Statistics.ContinuityErrors++;
                state.ResetPes();
            }
        }
        state.Continuity = counter;
        state.DuplicateSeen = duplicate;
        state.PreviousPacketValid = payload;
        if (payload)
            packet.CopyTo(state.PreviousPacket);
        return duplicate;
    }

    private static bool SamePacket(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> packet)
    {
        // The sender may update PCR when transmitting a legal duplicate.
        var carriesPcr = (previous[3] & packet[3] & 0x20) != 0 &&
                         previous[4] >= 7 && packet[4] >= 7 && (previous[5] & packet[5] & 0x10) != 0;
        return carriesPcr
            ? previous[..6].SequenceEqual(packet[..6]) && previous[12..].SequenceEqual(packet[12..])
            : previous.SequenceEqual(packet);
    }

    private void TrackPcr(PidState state, long clock, long index)
    {
        Statistics.PcrCount++;
        state.Statistics.PcrCount++;
        state.Statistics.FirstPcr ??= clock;
        state.Statistics.LastPcr = clock;
        if (state.PreviousPcr is long previous && (PcrWrap + clock - previous) % PcrWrap > PcrWrap / 2)
        {
            Statistics.PcrBackwards++;
            Statistics.FirstPcrBackwardsPacket ??= index;
            state.Statistics.PcrBackwards++;
        }
        state.PreviousPcr = clock;
    }

    private static bool ValidHeader(ReadOnlySpan<byte> packet) =>
        packet[0] == 0x47 && ((packet[3] >> 4) & 3) switch
        {
            1 => true,
            2 => packet[4] == 183,
            3 => packet[4] <= 182,
            _ => false
        };

    private static bool TryReadAdaptation(ReadOnlySpan<byte> packet, out int offset, out bool discontinuity, out long? pcr)
    {
        offset = 4;
        discontinuity = false;
        pcr = null;
        if ((packet[3] & 0x20) == 0)
            return true;
        offset = 5 + packet[4];
        if (packet[4] == 0)
            return true;

        var flags = packet[5];
        var fields = packet[6..offset];
        discontinuity = (flags & 0x80) != 0;
        if ((flags & 0x10) != 0)
        {
            if (fields.Length < 6)
                return false;
            var extension = ((fields[4] & 1) << 8) | fields[5];
            if ((fields[4] & 0x7e) != 0x7e || extension >= 300)
                return false;
            var clock = ((long)fields[0] << 25) | ((long)fields[1] << 17) |
                        ((long)fields[2] << 9) | ((long)fields[3] << 1) | (uint)(fields[4] >> 7);
            pcr = clock * 300 + extension;
            fields = fields[6..];
        }
        var fixedLength = ((flags & 0x08) != 0 ? 6 : 0) + ((flags & 0x04) != 0 ? 1 : 0);
        if (fields.Length < fixedLength)
            return false;
        fields = fields[fixedLength..];
        if ((flags & 0x02) != 0)
        {
            if (fields.IsEmpty || fields.Length <= fields[0])
                return false;
            fields = fields[(fields[0] + 1)..];
        }
        return (flags & 0x01) == 0 || (!fields.IsEmpty && fields.Length > fields[0]);
    }

    private bool ProcessPes(PidState state, byte startFlags, byte transportFlags, ReadOnlySpan<byte> payload)
    {
        if ((transportFlags & 0xc0) != 0)
        {
            state.ResetPes();
            return true;
        }
        if ((startFlags & 0x40) != 0)
        {
            state.ResetPes();
            state.CollectingPes = true;
        }
        if (!state.CollectingPes)
            return true;
        var amount = Math.Min(state.PesHeader.Length - state.PesLength, payload.Length);
        payload[..amount].CopyTo(state.PesHeader.AsSpan(state.PesLength));
        state.PesLength += amount;
        var header = state.PesHeader.AsSpan(0, state.PesLength);
        if (header.Length < 3)
            return true;
        if (header[0] != 0 || header[1] != 0 || header[2] != 1)
        {
            state.ResetPes();
            return true;
        }
        if (header.Length >= 6 && (header[3] < 0xbd || header[3] is 0xbe or 0xbf or 0xf0 or 0xf1 or 0xf2 or 0xf8 or 0xff))
        {
            state.ResetPes();
            return true;
        }
        if (header.Length < 9)
            return true;
        if ((header[6] & 0xc0) != 0x80)
        {
            state.ResetPes();
            return true;
        }
        var flags = header[7] >> 6;
        var required = flags == 3 ? 10 : 5;
        var pesLength = (header[4] << 8) | header[5];
        if (flags == 1 || (flags >= 2 && header[8] < required) || (pesLength != 0 && pesLength < 3 + header[8]))
        {
            state.ResetPes();
            return false;
        }
        if (flags == 0)
        {
            state.ResetPes();
            return true;
        }
        if (header.Length < 9 + required)
            return true;
        var pts = Timestamp(header[9..], flags);
        var valid = pts.HasValue && (flags != 3 || Timestamp(header[14..], 1).HasValue);
        state.ResetPes();
        if (!valid)
            return false;
        Statistics.PtsCount++;
        state.Statistics.PtsCount++;
        state.Statistics.FirstPts ??= pts;
        state.Statistics.LastPts = pts;
        return true;
    }

    private static long? Timestamp(ReadOnlySpan<byte> data, int prefix)
    {
        if ((data[0] >> 4) != prefix || (data[0] & data[2] & data[4] & 1) == 0)
            return null;
        return ((long)(data[0] & 0x0e) << 29) | ((long)data[1] << 22) |
               ((long)(data[2] & 0xfe) << 14) | ((long)data[3] << 7) | (uint)(data[4] >> 1);
    }
}
