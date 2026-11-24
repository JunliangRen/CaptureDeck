using System.Buffers.Binary;
using CaptureDeck.DirectShow.Windows.Interop;
using CaptureDeck.QualityControl;

namespace CaptureDeck.DirectShow.Windows.FireWire.MsTape;

internal static unsafe class MsTapeMediaTypes
{
    internal static readonly Guid StreamType = new("E436EB83-524F-11CE-9F53-0020AF0BA770");
    internal static readonly Guid Mpeg2Transport = new("E06D8023-DB46-11CF-B4D1-00805F6CBBEA");
    internal static readonly Guid Mpeg2TransportStride = new("138AA9A4-1EE2-4C5B-988E-19ABFDBC8A11");

    internal static TsLayout? GetLayout(in DirectShowMediaType type)
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
        return packet == 188 && stride is 188 or 192 && offset <= stride - 188
            ? new TsLayout((int)offset, (int)stride) : null;
    }
}
