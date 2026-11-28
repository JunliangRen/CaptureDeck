using System.Buffers.Binary;
using CaptureRack.DirectShow.Windows.Interop;
using CaptureRack.QualityControl;

namespace CaptureRack.DirectShow.Windows.FireWire.MsTape;

public readonly record struct FireWireMediaFormat(TsLayout? Transport, DvEncoding Encoding = DvEncoding.Consumer)
{
    public static FireWireMediaFormat Dv => new(null);
    public bool IsDv => Transport is null;
    public string Id => Transport?.Format ?? (Encoding switch
    {
        DvEncoding.Dvcpro25 => "dv25",
        DvEncoding.Dvcpro50 => "dv50",
        DvEncoding.DvcproHd => "dvh1",
        _ => "dv"
    });
    public string FilePrefix => Transport is { } layout ? $"iLINK{layout.Stride}" : (Encoding switch
    {
        DvEncoding.Dvcpro25 => "iLINK_DVCPRO25",
        DvEncoding.Dvcpro50 => "iLINK_DVCPRO50",
        DvEncoding.DvcproHd => "iLINK_DVCPROHD",
        _ => "iLINK_DV"
    });
    public string Extension => IsDv ? ".dv" : ".ts";
}

internal static unsafe class MsTapeMediaTypes
{
    internal static readonly Guid StreamType = new("E436EB83-524F-11CE-9F53-0020AF0BA770");
    internal static readonly Guid Mpeg2Transport = new("E06D8023-DB46-11CF-B4D1-00805F6CBBEA");
    internal static readonly Guid Mpeg2TransportStride = new("138AA9A4-1EE2-4C5B-988E-19ABFDBC8A11");
    internal static readonly Guid Interleaved = new("73766169-0000-0010-8000-00AA00389B71");
    internal static readonly Guid DvSd = new("64737664-0000-0010-8000-00AA00389B71");
    internal static readonly Guid Dv25 = new("35327664-0000-0010-8000-00AA00389B71");
    internal static readonly Guid Dv50 = new("30357664-0000-0010-8000-00AA00389B71");
    internal static readonly Guid Dvh1 = new("31687664-0000-0010-8000-00AA00389B71");
    internal static readonly Guid DvInfo = new("05589F84-C356-11CE-BF01-00AA0055595A");

    internal static Guid DvSubtype(DvEncoding encoding) => encoding switch
    {
        DvEncoding.Dvcpro25 => Dv25,
        DvEncoding.Dvcpro50 => Dv50,
        DvEncoding.DvcproHd => Dvh1,
        _ => DvSd
    };

    internal static FireWireMediaFormat? GetFormat(in DirectShowMediaType type)
    {
        if (type.Major == Interleaved &&
            (type.FormatSize == 0 || (type.FormatType == DvInfo && type.Format != null && type.FormatSize >= 32)))
        {
            if (type.Subtype == DvSd) return FireWireMediaFormat.Dv;
            if (type.Subtype == Dv25) return new(null, DvEncoding.Dvcpro25);
            if (type.Subtype == Dv50) return new(null, DvEncoding.Dvcpro50);
            if (type.Subtype == Dvh1) return new(null, DvEncoding.DvcproHd);
        }
        return GetLayout(type) is { } layout ? new FireWireMediaFormat(layout) : null;
    }

    private static TsLayout? GetLayout(in DirectShowMediaType type)
    {
        if (type.Major != StreamType)
            return null;
        if (type.Subtype == Mpeg2Transport)
            return type.FormatSize == 0 ? TsLayout.Ts188 : null;
        if (type.Subtype != Mpeg2TransportStride)
            return null;
        if (type.FormatSize == 0)
            return TsLayout.Ts188;
        if (type.Format == null || type.FormatSize < 12)
            return null;
        var fields = new ReadOnlySpan<byte>(type.Format, 12);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(fields);
        var packet = BinaryPrimitives.ReadUInt32LittleEndian(fields[4..]);
        var stride = BinaryPrimitives.ReadUInt32LittleEndian(fields[8..]);
        return packet == 188 && stride is 188 or 192 && offset == stride - 188
            ? new TsLayout((int)offset, (int)stride) : null;
    }
}
