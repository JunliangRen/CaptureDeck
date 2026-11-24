using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CaptureDeck.QualityControl;

namespace CaptureDeck.FireWire;

internal static unsafe partial class DirectShow
{
    internal static readonly Guid BaseFilter = typeof(IFireWireFilter).GUID;
    internal static readonly Guid Pin = typeof(IFireWirePin).GUID;
    internal static readonly Guid MemInputPin = typeof(IFireWireMemInputPin).GUID;
    internal static readonly Guid EnumPins = typeof(IFireWireEnumPins).GUID;
    internal static readonly Guid EnumMediaTypes = typeof(IFireWireEnumMediaTypes).GUID;
    internal static readonly Guid MiscFlags = typeof(IFireWireFilterFlags).GUID;
    internal static readonly Guid MemoryAllocator = new("1E651CC0-B199-11D0-8212-00C04FC32C45");
    internal static readonly Guid MemAllocator = new("56A8689C-0AD4-11CE-B03A-0020AF0BA770");
    internal static readonly Guid StreamType = new("E436EB83-524F-11CE-9F53-0020AF0BA770");
    internal static readonly Guid Mpeg2Transport = new("E06D8023-DB46-11CF-B4D1-00805F6CBBEA");
    internal static readonly Guid Mpeg2TransportStride = new("138AA9A4-1EE2-4C5B-988E-19ABFDBC8A11");

    internal const int Ok = 0;
    internal const int False = 1;
    internal const int Pointer = unchecked((int)0x80004003);
    internal const int NotImplemented = unchecked((int)0x80004001);
    internal const int Unexpected = unchecked((int)0x8000FFFF);
    internal const int InvalidArgument = unchecked((int)0x80070057);
    internal const int Fail = unchecked((int)0x80004005);
    internal const int NotConnected = unchecked((int)0x80040209);
    internal const int AlreadyConnected = unchecked((int)0x80040204);
    internal const int NotStopped = unchecked((int)0x80040224);
    internal const int WrongState = unchecked((int)0x80040227);
    internal const int RejectedEos = unchecked((int)0x8004022C);
    internal const int TypeNotAccepted = unchecked((int)0x8004022A);
    internal const int NotFound = unchecked((int)0x80040216);
    internal const int InvalidDirection = unchecked((int)0x80040208);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    internal static int Create(Guid clsid, Guid iid, out nint result)
    {
        nint pointer = 0;
        var status = CoCreateInstance(&clsid, 0, 1, &iid, &pointer);
        result = pointer;
        return status;
    }

    internal static ComReference Create(Guid clsid, Guid iid, string operation)
    {
        var status = Create(clsid, iid, out var pointer);
        Check(status, operation);
        return Own(pointer, operation);
    }

    internal static ComReference Own(nint pointer, string operation) =>
        pointer != 0 ? new ComReference(pointer) : throw new FireWireException(3, $"{operation} returned a null pointer.");

    internal static void Check(int status, string operation)
    {
        if (status < 0)
            throw new FireWireException(3, $"{operation} failed (0x{status:X8}).");
    }

    internal static nint Method(nint instance, int index) => (*(nint**)instance)[index];

    internal static int NoArgs(nint instance, int index) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Method(instance, index))(instance);

    internal static int IntOut(nint instance, int slot, int* result) =>
        ((delegate* unmanaged[Stdcall]<nint, int*, int>)Method(instance, slot))(instance, result);

    internal static int PointerOut(nint instance, int slot, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(instance, slot))(instance, result);

    internal static int Next(nint instance, nint* result, uint* fetched) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Method(instance, 3))(instance, 1, result, fetched);

    internal static ComReference Query(nint instance, Guid iid, string operation)
    {
        Check(Marshal.QueryInterface(instance, in iid, out var value), operation);
        return Own(value, operation);
    }

    private static readonly StrategyBasedComWrappers Wrappers = new();

    internal static nint Export(object instance, Guid iid)
    {
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try
        {
            Check(Marshal.QueryInterface(unknown, in iid, out var value), "Export managed DirectShow interface");
            return value;
        }
        finally { Marshal.Release(unknown); }
    }
}

internal sealed class ComReference(nint value) : IDisposable
{
    public nint Value { get; private set; } = value;

    public nint Detach()
    {
        var result = Value;
        Value = 0;
        return result;
    }

    public void Dispose()
    {
        var value = Detach();
        if (value != 0)
            Marshal.Release(value);
    }
}

public sealed class FireWireException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DirectShowMediaType
{
    public Guid Major;
    public Guid Subtype;
    public int FixedSizeSamples;
    public int TemporalCompression;
    public uint SampleSize;
    public Guid FormatType;
    public nint Unknown;
    public uint FormatSize;
    public byte* Format;

    public readonly TsLayout? GetLayout()
    {
        if (Major != DirectShow.StreamType)
            return null;
        if (Subtype == DirectShow.Mpeg2Transport)
            return FormatSize == 0 ? TsLayout.Ts188 : null;
        if (Subtype != DirectShow.Mpeg2TransportStride)
            return null;
        if (FormatSize == 0)
            return TsLayout.Ts188;
        if (Format == null || FormatSize < 12)
            return null;
        var fields = new ReadOnlySpan<byte>(Format, 12);
        var offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fields);
        var packet = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fields[4..]);
        var stride = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fields[8..]);
        return packet == 188 && stride is 188 or 192 && offset <= stride - 188
            ? new TsLayout((int)offset, (int)stride) : null;
    }

    public readonly bool Matches(in DirectShowMediaType other) =>
        Major == other.Major && Subtype == other.Subtype && FormatType == other.FormatType &&
        FixedSizeSamples == other.FixedSizeSamples && TemporalCompression == other.TemporalCompression &&
        SampleSize == other.SampleSize && Unknown == other.Unknown && FormatSize == other.FormatSize && (FormatSize == 0 ||
        (Format != null && other.Format != null &&
        new ReadOnlySpan<byte>(Format, checked((int)FormatSize)).SequenceEqual(new ReadOnlySpan<byte>(other.Format, checked((int)other.FormatSize)))));

    public readonly DirectShowMediaType Clone()
    {
        var result = this;
        result.Format = null;
        if (FormatSize != 0)
        {
            var length = checked((int)FormatSize);
            result.Format = (byte*)Marshal.AllocCoTaskMem(length);
            new ReadOnlySpan<byte>(Format, length).CopyTo(new Span<byte>(result.Format, length));
        }
        if (Unknown != 0)
            Marshal.AddRef(Unknown);
        return result;
    }

    public static void Clear(DirectShowMediaType* type)
    {
        Marshal.FreeCoTaskMem((nint)type->Format);
        if (type->Unknown != 0)
            Marshal.Release(type->Unknown);
        *type = default;
    }

    public static void Free(DirectShowMediaType* type)
    {
        if (type == null)
            return;
        Clear(type);
        Marshal.FreeCoTaskMem((nint)type);
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct DirectShowFilterInfo
{
    public fixed char Name[128];
    public nint Graph;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct DirectShowPinInfo
{
    public nint Filter;
    public int Direction;
    public fixed char Name[128];
}

[GeneratedComInterface, Guid("0000010C-0000-0000-C000-000000000046")]
internal unsafe partial interface IFireWirePersist
{
    [PreserveSig] int GetClassID(Guid* classId);
}

[GeneratedComInterface, Guid("56A86899-0AD4-11CE-B03A-0020AF0BA770")]
internal unsafe partial interface IFireWireMediaFilter : IFireWirePersist
{
    [PreserveSig] int Stop();
    [PreserveSig] int Pause();
    [PreserveSig] int Run(long start);
    [PreserveSig] int GetState(uint timeout, int* state);
    [PreserveSig] int SetSyncSource(nint clock);
    [PreserveSig] int GetSyncSource(nint* clock);
}

[GeneratedComInterface, Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770")]
internal unsafe partial interface IFireWireFilter : IFireWireMediaFilter
{
    [PreserveSig] int EnumPins(nint* pins);
    [PreserveSig] int FindPin(char* id, nint* pin);
    [PreserveSig] int QueryFilterInfo(DirectShowFilterInfo* info);
    [PreserveSig] int JoinFilterGraph(nint graph, char* name);
    [PreserveSig] int QueryVendorInfo(nint* vendor);
}

[GeneratedComInterface, Guid("56A86891-0AD4-11CE-B03A-0020AF0BA770")]
internal unsafe partial interface IFireWirePin
{
    [PreserveSig] int Connect(nint pin, DirectShowMediaType* type);
    [PreserveSig] int ReceiveConnection(nint pin, DirectShowMediaType* type);
    [PreserveSig] int Disconnect();
    [PreserveSig] int ConnectedTo(nint* pin);
    [PreserveSig] int ConnectionMediaType(DirectShowMediaType* type);
    [PreserveSig] int QueryPinInfo(DirectShowPinInfo* info);
    [PreserveSig] int QueryDirection(int* direction);
    [PreserveSig] int QueryId(nint* id);
    [PreserveSig] int QueryAccept(DirectShowMediaType* type);
    [PreserveSig] int EnumMediaTypes(nint* types);
    [PreserveSig] int QueryInternalConnections(nint* pins, uint* count);
    [PreserveSig] int EndOfStream();
    [PreserveSig] int BeginFlush();
    [PreserveSig] int EndFlush();
    [PreserveSig] int NewSegment(long start, long stop, double rate);
}

[GeneratedComInterface, Guid("56A8689D-0AD4-11CE-B03A-0020AF0BA770")]
internal unsafe partial interface IFireWireMemInputPin
{
    [PreserveSig] int GetAllocator(nint* allocator);
    [PreserveSig] int NotifyAllocator(nint allocator, int readOnly);
    [PreserveSig] int GetAllocatorRequirements(int* properties);
    [PreserveSig] int Receive(nint sample);
    [PreserveSig] int ReceiveMultiple(nint* samples, int count, int* processed);
    [PreserveSig] int ReceiveCanBlock();
}

[GeneratedComInterface, Guid("2DD74950-A890-11D1-ABE8-00A0C905F375")]
internal partial interface IFireWireFilterFlags
{
    [PreserveSig] uint GetMiscFlags();
}

[GeneratedComInterface, Guid("56A86892-0AD4-11CE-B03A-0020AF0BA770")]
internal unsafe partial interface IFireWireEnumPins
{
    [PreserveSig] int Next(uint count, nint* output, uint* fetched);
    [PreserveSig] int Skip(uint count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(nint* result);
}

[GeneratedComInterface, Guid("89C31040-846B-11CE-97D3-00AA0055595A")]
internal unsafe partial interface IFireWireEnumMediaTypes
{
    [PreserveSig] int Next(uint count, nint* output, uint* fetched);
    [PreserveSig] int Skip(uint count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(nint* result);
}
