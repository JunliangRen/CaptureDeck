using System.Buffers.Binary;
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
    public uint? InterlaceFlags { get; init; }
    public string? FieldOrder => InterlaceFlags is { } flags
        ? (flags & 1) == 0 ? "progressive" : (flags & 4) != 0 ? "top-first" : "bottom-first"
        : null;
    public int? SampleRate { get; init; }
    public int? Channels { get; init; }
    public int? BitsPerSample { get; init; }
    public int? ValidBitsPerSample { get; init; }
    public int? BlockAlign { get; init; }
    public uint? ChannelMask { get; init; }
    public int? WaveFormatTag { get; init; }
    public Guid? WaveSubformat { get; init; }

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
                InterlaceFlags = bitmap == 72 ? BinaryPrimitives.ReadUInt32LittleEndian(data[48..]) : null
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
