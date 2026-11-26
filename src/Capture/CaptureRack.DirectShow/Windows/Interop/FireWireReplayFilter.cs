using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureRack.DirectShow.Windows.Interop;

[GeneratedComClass]
internal sealed unsafe partial class FireWireReplayFilter(DirectShowSink typeSource, int bufferSize = 4096) : IDirectShowFilter, IDirectShowPin, IDisposable
{
    private int state;
    private nint graph;
    private nint peer;
    private nint allocator;
    private nint receiver;
    private DirectShowMediaType type;

    public int GetClassID(Guid* result) { *result = new Guid("16595445-233a-4133-9941-31376530811a"); return Ok; }
    public int Stop() { state = 0; return Ok; }
    public int Pause() { state = 1; return Ok; }
    public int Run(long start) { state = 2; return Ok; }
    public int GetState(uint timeout, int* result) { *result = state; return Ok; }
    public int SetSyncSource(nint clock) => Ok;
    public int GetSyncSource(nint* clock) { *clock = 0; return Ok; }
    public int EnumPins(nint* pins) { *pins = Export(new FireWireReplayPinEnumerator(this), DirectShowInterop.EnumPins); return Ok; }
    public int FindPin(char* id, nint* pin)
    {
        *pin = id != null && new string(id) == "Output" ? Export(this, Pin) : 0;
        return *pin != 0 ? Ok : NotFound;
    }
    public int QueryFilterInfo(DirectShowFilterInfo* info)
    {
        *info = default;
        info->Graph = graph;
        if (graph != 0)
            Marshal.AddRef(graph);
        "CaptureRack i.LINK simulation".AsSpan().CopyTo(new Span<char>(info->Name, 128));
        return Ok;
    }
    public int JoinFilterGraph(nint owner, char* name) { graph = owner; return Ok; }
    public int QueryVendorInfo(nint* vendor) { *vendor = 0; return NotImplemented; }

    public int Connect(nint pin, DirectShowMediaType* media)
    {
        if (pin == 0 || media == null) return Pointer;
        if (peer != 0) return AlreadyConnected;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint, DirectShowMediaType*, int>)Method(pin, 4))(
            pin, ExportOutput(), media);
        if (hr != Ok) return hr;
        var connected = false;
        try
        {
            Marshal.AddRef(pin);
            peer = pin;
            type = media->Clone();
            var memInput = MemInputPin;
            hr = Marshal.QueryInterface(pin, in memInput, out receiver);
            if (hr != Ok || receiver == 0) return hr < 0 ? hr : Pointer;
            nint value = 0;
            hr = PointerOut(receiver, 3, &value);
            allocator = value;
            if (hr != Ok || allocator == 0) return hr < 0 ? hr : Pointer;
            int* request = stackalloc int[] { 1, bufferSize, 1, 0 };
            int* properties = stackalloc int[4];
            hr = ((delegate* unmanaged[Stdcall]<nint, int*, int*, int>)Method(allocator, 3))(
                allocator, request, properties);
            if (hr != Ok) return hr;
            if (properties[1] < bufferSize) return Fail;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, int, int>)Method(receiver, 4))(receiver, allocator, 0);
            if (hr != Ok) return hr;
            hr = NoArgs(allocator, 5);
            connected = hr == Ok;
            return hr;
        }
        finally
        {
            if (!connected)
            {
                NoArgs(pin, 5); // IPin::Disconnect
                Disconnect();
            }
        }
    }

    private nint exportedPin;
    private nint ExportOutput() => exportedPin != 0 ? exportedPin : exportedPin = Export(this, Pin);

    public int Send(ReadOnlySpan<byte> bytes, bool discontinuity, long? start = null, long? end = null,
        DirectShowMediaType* media = null)
    {
        if (allocator == 0 || receiver == 0)
            return NotConnected;
        nint sample = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, nint, nint, uint, int>)Method(allocator, 7))(
            allocator, &sample, 0, 0, 0), "Get synthetic media sample");
        using var sampleRef = new ComReference(sample);
        if (bytes.Length > NoArgs(sample, 4))
            throw new IOException("The simulated sample exceeded its DirectShow buffer.");
        var startTicks = start.GetValueOrDefault();
        var endTicks = end.GetValueOrDefault();
        Check(((delegate* unmanaged[Stdcall]<nint, long*, long*, int>)Method(sample, 6))(
            sample, start.HasValue ? &startTicks : null, end.HasValue ? &endTicks : null), "Set synthetic sample times");
        nint data = 0;
        Check(PointerOut(sample, 3, &data), "Read synthetic sample buffer");
        bytes.CopyTo(new Span<byte>((void*)data, bytes.Length));
        Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Method(sample, 12))(
            sample, bytes.Length), "Set synthetic sample length");
        Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Method(sample, 16))(
            sample, discontinuity ? 1 : 0), "Set synthetic sample discontinuity");
        Check(((delegate* unmanaged[Stdcall]<nint, DirectShowMediaType*, int>)Method(sample, 14))(
            sample, media), "Set synthetic sample media type");
        return ((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(receiver, 6))(receiver, sample);
    }

    public int ReceiveConnection(nint pin, DirectShowMediaType* media) => InvalidDirection;
    public int Disconnect()
    {
        if (allocator != 0) { NoArgs(allocator, 6); Release(ref allocator); }
        Release(ref receiver);
        Release(ref peer);
        fixed (DirectShowMediaType* media = &type) DirectShowMediaType.Clear(media);
        return Ok;
    }
    public int ConnectedTo(nint* pin)
    {
        *pin = peer;
        if (peer == 0) return NotConnected;
        Marshal.AddRef(peer);
        return Ok;
    }
    public int ConnectionMediaType(DirectShowMediaType* media)
    {
        *media = peer == 0 ? default : type.Clone();
        return peer == 0 ? NotConnected : Ok;
    }
    public int QueryPinInfo(DirectShowPinInfo* info)
    {
        *info = default;
        info->Filter = Export(this, BaseFilter);
        info->Direction = 1;
        "Output".AsSpan().CopyTo(new Span<char>(info->Name, 128));
        return Ok;
    }
    public int QueryDirection(int* direction) { *direction = 1; return Ok; }
    public int QueryId(nint* id) { *id = Marshal.StringToCoTaskMemUni("Output"); return Ok; }
    public int QueryAccept(DirectShowMediaType* media) => typeSource.QueryAccept(media);
    public int EnumMediaTypes(nint* enumeration) => typeSource.EnumMediaTypes(enumeration);
    public int QueryInternalConnections(nint* pins, uint* count) => NotImplemented;
    public int EndOfStream() => Unexpected;
    public int BeginFlush() => Unexpected;
    public int EndFlush() => Unexpected;
    public int NewSegment(long start, long stop, double rate) => Unexpected;

    private static void Release(ref nint pointer)
    {
        if (pointer != 0) Marshal.Release(pointer);
        pointer = 0;
    }

    public void Dispose()
    {
        Disconnect();
        Release(ref exportedPin);
    }
}

[GeneratedComClass]
internal sealed unsafe partial class FireWireReplayPinEnumerator(FireWireReplayFilter source) : SingleComItem, IDirectShowEnumPins
{
    protected override nint CreateItem() => Export(source, Pin);
    public int Next(uint count, nint* pins, uint* fetched) => NextItem(count, pins, fetched);
    public int Clone(nint* result)
    {
        *result = Export(new FireWireReplayPinEnumerator(source) { consumed = consumed }, DirectShowInterop.EnumPins);
        return Ok;
    }
}
