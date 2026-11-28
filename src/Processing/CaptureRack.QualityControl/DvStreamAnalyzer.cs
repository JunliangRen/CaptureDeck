using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaptureRack.QualityControl;

[JsonConverter(typeof(JsonStringEnumConverter<DvEncoding>))]
public enum DvEncoding { Consumer, Dvcpro25, Dvcpro50, DvcproHd }

public static class DvEncodingNames
{
    public static string DisplayName(this DvEncoding encoding) => encoding switch
    {
        DvEncoding.Dvcpro25 => "DVCPRO 25",
        DvEncoding.Dvcpro50 => "DVCPRO50",
        DvEncoding.DvcproHd => "DVCPRO HD",
        _ => "DV25"
    };
}

public sealed record DvFrameInfo(int Width, int Height, double FramesPerSecond, int FrameBytes,
    string? Timecode, string? RecordingDate, string? RecordingTime, int? AudioRate, int? AudioBits,
    bool? Widescreen, DvEncoding Encoding = DvEncoding.Consumer, int? AudioChannels = null, bool? Progressive = null);

public sealed record DvStatistics(long InputBytes, long Frames, long InvalidFrames, long TrailingBytes,
    long FormatChanges, long Discontinuities, DvFrameInfo? FirstFrame, DvFrameInfo? LastFrame, long UnexpectedFormatFrames = 0)
{
    public bool Success => Frames > 0 && InvalidFrames == 0 && TrailingBytes == 0 && UnexpectedFormatFrames == 0;
}

public sealed class DvStreamAnalyzer(DvEncoding? negotiatedEncoding = null, bool requireEncoding = false)
{
    private const int DifBlockBytes = 80;
    private const int SequenceBytes = 150 * DifBlockBytes;
    private const int VideoSourceTypeOffset = 5 * DifBlockBytes + 48 + 3;
    private readonly byte[] frame = new byte[4 * 12 * SequenceBytes];
    private int buffered, frameSize;
    private Profile profile;
    private long bytes, frames, invalid, changes, discontinuities;
    private long unexpectedFormatFrames;
    private DvFrameInfo? first, last;
    private bool malformed;

    public DvStatistics Snapshot => new(bytes, frames, invalid, malformed ? unalignedBytes : buffered,
        changes, discontinuities, first, last, unexpectedFormatFrames);
    private long unalignedBytes;

    // A recording can begin at the next DIF frame boundary even if the source splits a frame into samples.
    public int? BytesToFrameBoundary => malformed || (buffered != 0 && frameSize == 0) ? null : frameSize - buffered;

    public void Feed(ReadOnlySpan<byte> data, bool discontinuity = false)
    {
        if (discontinuity && bytes != 0)
        {
            discontinuities++;
            if (buffered != 0 && !malformed)
            {
                invalid++;
                buffered = frameSize = 0;
            }
        }
        if (malformed && (HasFrameHeader(data) || (discontinuity && data.Length < 4)))
        {
            malformed = false;
            unalignedBytes = 0;
            buffered = frameSize = 0;
        }
        bytes += data.Length;
        if (malformed)
        {
            unalignedBytes += data.Length;
            return;
        }
        while (!data.IsEmpty)
        {
            var target = frameSize == 0 ? VideoSourceTypeOffset + 1 : frameSize;
            var count = Math.Min(target - buffered, data.Length);
            data[..count].CopyTo(frame.AsSpan(buffered));
            buffered += count;
            data = data[count..];
            if (buffered != target)
                continue;
            if (frameSize == 0)
            {
                if (ReadProfile(frame, negotiatedEncoding) is not { } detected)
                {
                    invalid++;
                    malformed = true;
                    unalignedBytes = buffered + (long)data.Length;
                    return;
                }
                profile = detected;
                frameSize = profile.FrameBytes;
                continue;
            }
            var metadata = ReadFrame(frame.AsSpan(0, frameSize), profile);
            if (metadata is null)
                invalid++;
            else
            {
                if (last is { } previous && (previous.Encoding != metadata.Encoding ||
                    previous.Height != metadata.Height || previous.FrameBytes != metadata.FrameBytes ||
                    previous.AudioRate != metadata.AudioRate || previous.AudioBits != metadata.AudioBits ||
                    previous.AudioChannels != metadata.AudioChannels || previous.Widescreen != metadata.Widescreen))
                    changes++;
                first ??= metadata;
                last = metadata;
                frames++;
                if (requireEncoding && negotiatedEncoding != metadata.Encoding)
                    unexpectedFormatFrames++;
            }
            buffered = frameSize = 0;
        }
    }

    internal static bool HasFrameHeader(ReadOnlySpan<byte> header) => header.Length >= 4 &&
        header[0] == 0x1f && (header[1] & 0xfb) == 0x03 && header[2] == 0;

    public static int FrameSize(ReadOnlySpan<byte> header) => ReadProfile(header, null)?.FrameBytes ?? 0;

    private readonly record struct Profile(DvEncoding Encoding, bool Pal, int DifChannels, int Width, int Height, bool Progressive = false)
    {
        public int Sequences => Pal ? 12 : 10;
        public int FrameBytes => Sequences * DifChannels * SequenceBytes;
        public double FramesPerSecond => (Pal ? 25 : 30000d / 1001) * (Progressive ? 2 : 1);
    }

    private static Profile? ReadProfile(ReadOnlySpan<byte> header, DvEncoding? negotiated)
    {
        if (header.Length <= VideoSourceTypeOffset || !HasFrameHeader(header))
            return null;
        var pal = (header[3] & 0x80) != 0;
        // VAUX source STYPE distinguishes SD and HD layouts with identical DIF frame sizes.
        return (header[VideoSourceTypeOffset] & 0x1f) switch
        {
            0 => new((pal ? (header[4] & 7) != 0 : negotiated == DvEncoding.Dvcpro25)
                ? DvEncoding.Dvcpro25 : DvEncoding.Consumer, pal, 1, 720, pal ? 576 : 480),
            4 => new(DvEncoding.Dvcpro50, pal, 2, 720, pal ? 576 : 480),
            0x14 => new(DvEncoding.DvcproHd, pal, 4, pal ? 1440 : 1280, 1080),
            0x18 => new(DvEncoding.DvcproHd, pal, 2, 960, 720, Progressive: true),
            // Some older DV writers leave the source pack unset.
            0x1f when (header[3] & 0x7f) == 0x3f && header[VideoSourceTypeOffset] == 0xff =>
                new(DvEncoding.Consumer, pal, 1, 720, pal ? 576 : 480),
            _ => null
        };
    }

    private static DvFrameInfo? ReadFrame(ReadOnlySpan<byte> data, Profile profile)
    {
        string? timecode = null, date = null, time = null;
        int? audioRate = null, audioBits = null, audioChannels = null;
        bool? wide = profile.Encoding == DvEncoding.DvcproHd ? true : null;
        var pal = profile.Pal;
        // In 720p, consecutive frames can alternate between pairs of DIF channels.
        var channelOffset = profile.Progressive && (data[1] & 4) == 0 ? 2 : 0;
        for (int sequence = 0; sequence < data.Length / SequenceBytes; sequence++)
        {
            var block = data.Slice(sequence * SequenceBytes, SequenceBytes);
            var channel = sequence / profile.Sequences + channelOffset;
            var sequenceId = (sequence % profile.Sequences << 4) | ((channel & 1) << 3) | ((channel & 2) == 0 ? 7 : 3);
            if (block[0] != 0x1f || block[1] != sequenceId || block[2] != 0 || ((block[3] & 0x80) != 0) != pal)
                return null;

            for (var dif = 1; dif <= 2; dif++)
            {
                if ((block[dif * DifBlockBytes] >> 5) != 1)
                    continue;
                for (var offset = 6; offset <= 46; offset += 8)
                {
                    var pack = block.Slice(dif * DifBlockBytes + offset, 5);
                    if (pack[0] == 0x13 && timecode is null && ReadTime(pack) is { } clock &&
                        Bcd(pack[1] & 0x3f) is int number && number < (pal ? 25 : 30))
                        timecode = $"{clock}{(!pal && (pack[1] & 0x40) != 0 ? ';' : ':')}{number:00}";
                    else if (pack[0] == 0x62)
                        date ??= ReadDate(pack);
                    else if (pack[0] == 0x63)
                        time ??= ReadTime(pack);
                }
            }
            for (var dif = 3; dif <= 5; dif++)
            {
                if ((block[dif * DifBlockBytes] >> 5) != 2)
                    continue;
                for (var offset = 3; offset <= 73; offset += 5)
                {
                    var pack = block.Slice(dif * DifBlockBytes + offset, 5);
                    switch (pack[0])
                    {
                        case 0x61: wide ??= (pack[2] & 7) switch { 0 => false, 2 => true, 7 when (data[4] & 7) == 0 => true, _ => null }; break;
                        case 0x62: date ??= ReadDate(pack); break;
                        case 0x63: time ??= ReadTime(pack); break;
                    }
                }
            }
            for (var dif = 6; dif < 150; dif += 16)
            {
                var start = dif * DifBlockBytes;
                if ((block[start] >> 5) != 3)
                    continue;
                var pack = block.Slice(start + 3, 5);
                switch (pack[0])
                {
                    case 0x50:
                        audioRate ??= ((pack[4] >> 3) & 7) switch { 0 => 48000, 1 => 44100, 2 => 32000, _ => null };
                        audioBits ??= (pack[4] & 7) switch { 0 => 16, 1 => 12, _ => null };
                        audioChannels ??= (pack[3] & 0x1f) switch
                        {
                            0 => audioRate == 32000 && audioBits == 12 ? 4 : 2,
                            2 => 4,
                            3 => 8,
                            _ => null
                        };
                        break;
                    case 0x52: date ??= ReadDate(pack); break;
                    case 0x53: time ??= ReadTime(pack); break;
                }
            }
        }
        return new(profile.Width, profile.Height, profile.FramesPerSecond, data.Length, timecode, date, time,
            audioRate, audioBits, wide, profile.Encoding, audioChannels,
            profile.Encoding == DvEncoding.DvcproHd ? profile.Progressive : null);
    }

    private static int? Bcd(int value) => (value & 15) <= 9 && (value >> 4) <= 9 ? (value >> 4) * 10 + (value & 15) : null;

    private static string? ReadTime(ReadOnlySpan<byte> pack) =>
        Bcd(pack[4] & 0x3f) is int h and < 24 && Bcd(pack[3] & 0x7f) is int m and < 60 &&
        Bcd(pack[2] & 0x7f) is int s and < 60 ? $"{h:00}:{m:00}:{s:00}" : null;

    private static string? ReadDate(ReadOnlySpan<byte> pack)
    {
        if (Bcd(pack[4]) is not int year || Bcd(pack[3] & 0x1f) is not int month || month is < 1 or > 12 ||
            Bcd(pack[2] & 0x3f) is not int day)
            return null;
        year += year >= 70 ? 1900 : 2000;
        return day >= 1 && day <= DateTime.DaysInMonth(year, month) ? $"{year:0000}-{month:00}-{day:00}" : null;
    }
}

public sealed record DvFileReport(string InputPath, DvStatistics Analysis)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public int SchemaVersion => 1;
    public string Type => "analysis_result";
    public bool Success => Analysis.Success;
    public int ExitCode => Success ? 0 : 4;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

public static class DvFile
{
    public static DvFileReport Analyze(string path, CancellationToken cancellation = default, DvEncoding? encoding = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        using var stream = File.OpenRead(fullPath);
        // The raw 525-line 25 Mb/s layouts share a profile. Recover the native subtype from an app-generated name.
        var analyzer = new DvStreamAnalyzer(encoding ?? RecordingNameHint(fullPath), requireEncoding: encoding.HasValue);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = stream.Read(buffer);
            if (count == 0)
                return new(fullPath, analyzer.Snapshot);
            analyzer.Feed(buffer.AsSpan(0, count));
        }
    }

    private static DvEncoding? RecordingNameHint(string path)
    {
        const string marker = "CaptureRack_iLINK_DVCPRO25_";
        const int timestampLength = 19; // yyyyMMdd_HHmmss_fff
        var name = Path.GetFileNameWithoutExtension(path).AsSpan();
        if (name.Length < marker.Length + timestampLength)
            return null;
        var suffix = name[^(marker.Length + timestampLength)..];
        return suffix.StartsWith(marker, StringComparison.OrdinalIgnoreCase) &&
            DateTime.TryParseExact(suffix[marker.Length..], "yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _) ? DvEncoding.Dvcpro25 : null;
    }
}
