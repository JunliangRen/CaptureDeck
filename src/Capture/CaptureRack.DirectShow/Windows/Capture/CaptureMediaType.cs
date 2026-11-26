using System.Buffers.Binary;
using CaptureRack.Core;
using CaptureRack.DirectShow.Windows.Interop;

namespace CaptureRack.DirectShow.Windows.Capture;

public sealed record DirectShowFormat(int Index, string PinId, string PinName, string Kind, Guid MajorType,
    Guid Subtype, Guid FormatType)
{
    public string? FourCc { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public uint? ImageBytes { get; init; }
    public int? BitmapBitsPerPixel { get; init; }
    public bool? BitmapTopDown { get; init; }
    internal string? BitmapHeaderBase64 { get; init; }
    public long? FrameInterval100ns { get; init; }
    public double? FramesPerSecond => FrameInterval100ns is > 0 ? 10_000_000d / FrameInterval100ns : null;
    public double? DisplayAspectRatio { get; init; }
    public uint? InterlaceFlags { get; init; }
    public uint? FallbackInterlaceFlags { get; init; }
    internal uint? EffectiveInterlaceFlags => InterlaceFlags ?? FallbackInterlaceFlags;
    public string? FieldOrder => EffectiveInterlaceFlags is { } flags
        ? (flags & 1) == 0 ? "progressive" : (flags & 4) != 0 ? "top-first" : "bottom-first"
        : null;
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }
    public int? BitsPerSample { get; init; }
    public int? ValidBitsPerSample { get; init; }
    public int? BlockAlign { get; init; }
    public uint? AverageBytesPerSecond { get; init; }
    public uint? ChannelMask { get; init; }
    public int? WaveFormatTag { get; init; }
    public Guid? WaveSubformat { get; init; }

    public CaptureAudioInfo? DescribeAudio()
    {
        if (Kind != "audio")
            return null;
        var isFloat = WaveFormatTag == 3 ||
            WaveFormatTag == 0xfffe && WaveSubformat == new Guid("00000003-0000-0010-8000-00AA00389B71");
        var uncompressed = IsPcm || isFloat;
        var codec = uncompressed ? isFloat ? "PCM Float" : "PCM" : WaveFormatTag switch
        {
            0x0050 => "MPEG Audio",
            0x0055 => "MP3",
            0x00ff => "AAC",
            0x2000 => "AC-3",
            _ => FourCc ?? (WaveFormatTag is > 0 and not 0xfffe ? $"WAVE 0x{WaveFormatTag:X4}" : "—")
        };
        long? bitRate = uncompressed && SampleRate is > 0 && BlockAlign is > 0
            ? (long)SampleRate.Value * BlockAlign.Value * 8
            : AverageBytesPerSecond is > 0 ? (long)AverageBytesPerSecond.Value * 8 : null;
        return new(codec, uncompressed && ValidBitsPerSample is > 0 ? ValidBitsPerSample :
            uncompressed && BitsPerSample is > 0 ? BitsPerSample : null,
            SampleRate is > 0 ? SampleRate : null, Channels is > 0 ? Channels : null, bitRate);
    }

    public string? InfraredEncoding => Subtype switch
    {
        var id when id == L8Infrared => "L8_IR",
        var id when id == L16Infrared => "L16_IR",
        var id when id == MjpgInfrared => "MJPG_IR",
        _ => null
    };
    public bool IsInfrared => Kind == "video" && InfraredEncoding is not null;
    public bool IsMpeg2Video => Kind == "video" && (Subtype == Mpeg2Video || FourCc is "MPG2" or "mpg2");
    internal static readonly Guid Mpeg2Video = new("E06D8026-DB46-11CF-B4D1-00805F6CBBEA");

    private static readonly Guid L8Infrared = new("00000032-0002-0010-8000-00aa00389b71");
    private static readonly Guid L16Infrared = new("00000051-0002-0010-8000-00aa00389b71");
    private static readonly Guid MjpgInfrared = new("47504a4d-0002-0010-8000-00aa00389b71");

    public bool IsPcm => Kind == "audio" && (WaveFormatTag == 1 ||
        (WaveFormatTag == 0xfffe && WaveSubformat == new Guid("00000001-0000-0010-8000-00AA00389B71"))) &&
        SampleRate > 0 && Channels > 0 && BlockAlign > 0;

    public string Description => Kind switch
    {
        "video" when Width is not null => $"{FourCc ?? Subtype.ToString()} {Width}x{Height} " +
            $"{FramesPerSecond:0.###} fps · {FieldOrder ?? "field order unspecified"}",
        "audio" when SampleRate is not null => $"{(IsPcm ? "PCM" : Subtype.ToString())} " +
            $"{SampleRate} Hz · {Channels} ch · {BitsPerSample}-bit",
        _ => $"{Kind} {FourCc ?? Subtype.ToString()}"
    };
}

internal static unsafe class CaptureMediaType
{
    private static readonly Guid Stream = new("E436EB83-524F-11CE-9F53-0020AF0BA770");
    private static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid Audio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoInfo = new("05589F80-C356-11CE-BF01-00AA0055595A");
    private static readonly Guid VideoInfo2 = new("F72A76A0-EB0A-11D0-ACE4-0000C0CC16BA");
    private static readonly Guid Mpeg2VideoInfo = new("E06D80E3-DB46-11CF-B4D1-00805F6CBBEA");
    private static readonly Guid WaveFormat = new("05589F81-C356-11CE-BF01-00AA0055595A");

    internal static DirectShowFormat ResolveScanMode(DirectShowFormat format, string deviceId,
        IEnumerable<DirectShowFormat> advertised)
    {
        if (format.FormatType != VideoInfo || format.Kind != "video" || format.InterlaceFlags is not null ||
            format.Width is not > 0 || format.Height is not > 0 || format.FrameInterval100ns is not > 0)
            return format;

        var alternatives = advertised.Where(candidate => candidate.FormatType == VideoInfo2 &&
            candidate.InterlaceFlags.HasValue && candidate.PinId == format.PinId &&
            candidate.MajorType == format.MajorType && candidate.Subtype == format.Subtype &&
            candidate.Width == format.Width && candidate.Height == format.Height &&
            candidate.FrameInterval100ns == format.FrameInterval100ns && candidate.FourCc == format.FourCc &&
            candidate.ImageBytes == format.ImageBytes && candidate.BitmapBitsPerPixel == format.BitmapBitsPerPixel &&
            candidate.BitmapTopDown == format.BitmapTopDown)
            // Display preferences do not change the field order or sample layout.
            .Select(candidate => (candidate.InterlaceFlags!.Value & 1) == 0 ? 0u : candidate.InterlaceFlags.Value & 0x37)
            .Distinct().Take(2).ToArray();
        if (alternatives.Length > 0)
            return alternatives.Length == 1 ? format with { FallbackInterlaceFlags = alternatives[0] } : format;

        // The Blackmagic AVStream pin omits scan metadata for these two native SD modes.
        if (!deviceId.StartsWith(@"@device:pnp:\\?\decklink#avstream#", StringComparison.OrdinalIgnoreCase) ||
            format.FourCc is not ("UYVY" or "v210") || format.Width != 720)
            return format;

        const uint interlaced = 0x01;
        const uint topFieldFirst = 0x04;
        const uint bothFieldsRegular = 0x20;
        return (format.Height, format.FrameInterval100ns) switch
        {
            (486, 333666 or 333667) => format with { FallbackInterlaceFlags = interlaced | bothFieldsRegular },
            (576, 400000) => format with { FallbackInterlaceFlags = interlaced | topFieldFirst | bothFieldsRegular },
            _ => format
        };
    }

    internal static DirectShowFormat Describe(in DirectShowMediaType type, int index, string pinId, string pinName, bool includeBitmapHeader = false)
    {
        var video = type.Major == Video || type.Major == Stream && type.Subtype == DirectShowFormat.Mpeg2Video;
        var kind = video ? "video" : type.Major == Audio ? "audio" : "other";
        var result = new DirectShowFormat(index, pinId, pinName, kind, type.Major, type.Subtype, type.FormatType);
        if (type.Format == null || type.FormatSize > int.MaxValue)
            return result;

        var data = new ReadOnlySpan<byte>(type.Format, (int)type.FormatSize);
        var bitmap = type.FormatType == VideoInfo2 || type.FormatType == Mpeg2VideoInfo ? 72 : type.FormatType == VideoInfo ? 48 : -1;
        if (kind == "video" && bitmap >= 0 && data.Length >= bitmap + 40)
        {
            var header = data[bitmap..];
            var height = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            var fourCc = header.Slice(16, 4);
            return result with
            {
                Width = BinaryPrimitives.ReadInt32LittleEndian(header[4..]),
                Height = height == int.MinValue ? null : Math.Abs(height),
                BitmapTopDown = height < 0,
                BitmapHeaderBase64 = includeBitmapHeader ? Convert.ToBase64String(header) : null,
                FourCc = IsFourCc(fourCc) ? System.Text.Encoding.ASCII.GetString(fourCc) : null,
                BitmapBitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]),
                ImageBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]),
                FrameInterval100ns = BinaryPrimitives.ReadInt64LittleEndian(data[40..]),
                InterlaceFlags = bitmap == 72 ? BinaryPrimitives.ReadUInt32LittleEndian(data[48..]) : null,
                DisplayAspectRatio = bitmap == 72 && BinaryPrimitives.ReadUInt32LittleEndian(data[60..]) is > 0 and < 100000 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(data[64..]) is > 0 and < 100000
                    ? (double)BinaryPrimitives.ReadUInt32LittleEndian(data[60..]) / BinaryPrimitives.ReadUInt32LittleEndian(data[64..])
                    : null
            };
        }

        if (kind != "audio" || type.FormatType != WaveFormat || data.Length < 16)
            return result;

        var tag = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var extended = tag == 0xfffe && data.Length >= 40 &&
            BinaryPrimitives.ReadUInt16LittleEndian(data[16..]) >= 22;
        return result with
        {
            WaveFormatTag = tag,
            Channels = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            SampleRate = BinaryPrimitives.ReadInt32LittleEndian(data[4..]),
            AverageBytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
            BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
            ValidBitsPerSample = extended ? BinaryPrimitives.ReadUInt16LittleEndian(data[18..]) : null,
            ChannelMask = extended ? BinaryPrimitives.ReadUInt32LittleEndian(data[20..]) : null,
            WaveSubformat = extended ? new Guid(data.Slice(24, 16)) : null
        };
    }

    private static bool IsFourCc(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
            if (value is < 32 or > 126)
                return false;
        return true;
    }
}
