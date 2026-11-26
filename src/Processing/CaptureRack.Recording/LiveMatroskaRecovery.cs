using System.Buffers.Binary;
using CaptureRack.Recording.Matroska;
using CaptureRack.Recording.Windows;

namespace CaptureRack.Recording;

/// <summary>Finds complete clusters in the streaming MKV produced by the live encoder.</summary>
public sealed class LiveMatroskaRecovery
{
    private const ulong Ebml = 0x1A45DFA3, Segment = 0x18538067, Info = 0x1549A966,
        Tracks = 0x1654AE6B, Cluster = 0x1F43B675;

    private long scanPosition;
    private long segmentStart;
    private readonly List<(long Time, long Cluster, long Block)> cues = [];
    private bool header, info, tracks;
    private bool stopped;
    internal long CompletePrefix { get; private set; }
    internal bool IsComplete(long fileLength) => !stopped && CompletePrefix > 0 && scanPosition == fileLength;

    internal void Scan(Stream input, long limit, bool verifyIntegrity = false, CancellationToken cancellation = default)
    {
        if (stopped)
            return;
        input.Position = scanPosition;
        byte[]? crcBuffer = verifyIntegrity ? new byte[64 * 1024] : null;
        while (input.Position < limit)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = input.Position;
            if (!ReadVint(input, limit, false, out var id, out _) ||
                !ReadVint(input, limit, true, out var size, out var unknown))
                return;
            if (segmentStart == 0 && id == Segment)
            {
                if (!header || !unknown)
                    throw new InvalidDataException("Recovery expects a Matroska recording with an open Segment.");
                scanPosition = segmentStart = input.Position;
                continue;
            }
            if (entry == 0 && id != Ebml)
                throw new InvalidDataException("This file is not Matroska.");
            if (unknown || size > (ulong)(limit - input.Position))
                return;
            var end = input.Position + (long)size;
            if (entry == 0)
            {
                var payload = input.Position;
                if (crcBuffer is not null && !ValidateMetadata(input, end, crcBuffer, cancellation))
                    throw new InvalidDataException("The EBML header is damaged.");
                input.Position = payload;
                if (!IsMatroskaHeader(input, end))
                    throw new InvalidDataException("This file has no Matroska document type.");
                header = true;
            }
            else if (segmentStart != 0)
            {
                if (id is Info or Tracks)
                {
                    var valid = size != 0 && (crcBuffer is null || ValidateMetadata(input, end, crcBuffer, cancellation));
                    if (id == Info)
                        info = valid;
                    else
                        tracks = valid;
                }
                if (id == Cluster)
                {
                    if (!info || !tracks || !ScanCluster(input, entry, end, crcBuffer, cancellation))
                    {
                        stopped = true;
                        return;
                    }
                    CompletePrefix = end;
                }
            }
            scanPosition = end;
            input.Position = scanPosition;
        }
    }

    private static bool ValidateMetadata(Stream input, long end, byte[] crcBuffer, CancellationToken cancellation)
    {
        var payload = input.Position;
        while (input.Position < end)
        {
            var child = input.Position;
            if (!ReadVint(input, end, false, out var id, out _) ||
                !ReadVint(input, end, true, out var size, out var unknown) || unknown ||
                size > (ulong)(end - input.Position))
                return false;
            var next = input.Position + (long)size;
            if (id == 0xBF)
            {
                if (child != payload || size != 4)
                    return false;
                if (!VerifyChecksum(input, end, crcBuffer, cancellation))
                    return false;
            }
            input.Position = next;
        }
        return true;
    }

    private static bool IsMatroskaHeader(Stream input, long end)
    {
        Span<byte> name = stackalloc byte[8];
        while (input.Position < end)
        {
            if (!ReadVint(input, end, false, out var id, out _) ||
                !ReadVint(input, end, true, out var size, out var unknown) ||
                unknown || size > (ulong)(end - input.Position))
                return false;
            if (id == 0x4282 && size == (ulong)name.Length) // DocType
            {
                input.ReadExactly(name);
                return name.SequenceEqual("matroska"u8);
            }
            input.Position += (long)size;
        }
        return false;
    }

    internal void AppendIndex(Stream output)
    {
        if (cues.Count != 0)
            NativeMatroskaWriter.WriteCueIndex(output, cues.OrderBy(cue => cue.Time));
    }

    private bool ScanCluster(Stream input, long clusterPosition, long end, byte[]? crcBuffer, CancellationToken cancellation)
    {
        var payload = input.Position;
        long? time = null;
        (long Time, long Cluster, long Block)? cue = null;
        var hasBlock = false;
        while (input.Position < end)
        {
            var entry = input.Position;
            if (!ReadVint(input, end, false, out var id, out _) ||
                !ReadVint(input, end, true, out var size, out var unknown) || unknown ||
                size > (ulong)(end - input.Position))
                return false;
            var next = input.Position + (long)size;
            switch (id)
            {
                case 0xBF: // CRC-32
                    if (entry != payload || size != 4)
                        return false;
                    if (crcBuffer is not null && !VerifyChecksum(input, end, crcBuffer, cancellation))
                        return false;
                    break;
                case 0xE7: // Timestamp
                    if (time.HasValue || size is < 1 or > 8)
                        return false;
                    ulong timestamp = 0;
                    while (input.Position < next)
                        timestamp = timestamp << 8 | (uint)input.ReadByte();
                    if (timestamp > long.MaxValue - short.MaxValue)
                        return false;
                    time = (long)timestamp;
                    break;
                case 0xA3: // SimpleBlock
                    if (!ReadVint(input, next, true, out var track, out _) || track == 0 || next - input.Position < 4)
                        return false;
                    var relative = (short)(input.ReadByte() << 8 | input.ReadByte());
                    var flags = input.ReadByte();
                    if (track == 1 && time.HasValue && (flags & 0x80) != 0 && time.Value + relative >= 0)
                        cue ??= (time.Value + relative, clusterPosition - segmentStart, entry - payload);
                    hasBlock = true;
                    break;
                case 0xA0: // BlockGroup
                    if (size == 0)
                        return false;
                    hasBlock = true;
                    break;
            }
            input.Position = next;
        }
        if (!time.HasValue || !hasBlock)
            return false;
        if (cue is { } point)
            cues.Add(point);
        return true;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var crc = index;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            table[index] = crc;
        }
        return table;
    }

    private static bool VerifyChecksum(Stream input, long end, byte[] buffer, CancellationToken cancellation)
    {
        Span<byte> checksum = stackalloc byte[4];
        input.ReadExactly(checksum);
        var crc = uint.MaxValue;
        while (input.Position < end)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, end - input.Position);
            input.ReadExactly(buffer.AsSpan(0, count));
            foreach (var value in buffer.AsSpan(0, count))
                crc = CrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        }
        return ~crc == BinaryPrimitives.ReadUInt32LittleEndian(checksum);
    }

    public static async Task<long> RecoverAsync(string inputPath, string outputPath, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(inputPath);
        var target = Path.GetFullPath(outputPath);
        if (!Path.GetExtension(target).Equals(".mkv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The recovered output must end in .mkv.", nameof(outputPath));
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a different path for the recovered MKV.");
        if (Path.Exists(target))
            throw new IOException($"The recovery destination already exists: {target}.");
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var recovery = new LiveMatroskaRecovery();
        recovery.Scan(input, input.Length, verifyIntegrity: true, cancellation);
        var remaining = recovery.CompletePrefix;
        if (remaining == 0)
            throw new InvalidDataException("No complete Matroska cluster was found.");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                var buffer = new byte[128 * 1024];
                input.Position = 0;
                while (remaining != 0)
                {
                    var count = (int)Math.Min(remaining, buffer.Length);
                    await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    remaining -= count;
                }
                recovery.AppendIndex(output);
                output.Flush(flushToDisk: true);
            }
            cancellation.ThrowIfCancellationRequested();
            WindowsFile.MoveWriteThrough(temporary, target);
            return recovery.CompletePrefix;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool ReadVint(Stream input, long limit, bool isSize, out ulong value, out bool unknown)
    {
        value = 0;
        unknown = false;
        if (input.Position >= limit)
            return false;
        var first = input.ReadByte();
        if (first <= 0)
            return false;
        var mask = 128;
        var length = 1;
        while ((first & mask) == 0)
        {
            length++;
            mask >>= 1;
        }
        if ((!isSize && length > 4) || input.Position + length - 1 > limit)
            return false;
        value = (ulong)(isSize ? first & (mask - 1) : first);
        for (var i = 1; i < length; i++)
        {
            var part = input.ReadByte();
            if (part < 0)
                return false;
            value = value << 8 | (uint)part;
        }
        unknown = isSize && value == (1UL << (length * 7)) - 1;
        return true;
    }
}
