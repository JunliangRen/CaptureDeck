using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CaptureDeck.Core;
using static CaptureDeck.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureDeck.DirectShow.Windows.Interop;

[GeneratedComClass]
internal sealed unsafe partial class DirectShowSink :
    IDirectShowFilter, IDirectShowPin, IDirectShowMemInputPin, IDirectShowFilterFlags, IDisposable
{
    private static readonly Guid ClassId = new("4A4FC139-7958-4EB7-B49A-660E2D93228A");
    private const int NoStopTime = 0x00040270; // VFW_S_NO_STOP_TIME
    private readonly CaptureSampleHandler receive;
    private readonly Action<string> reportFailure;
    private readonly object gate = new();
    private DirectShowMediaType expected;
    private DirectShowMediaType connectedType;
    private nint graph; // Weak; DirectShow's graph owns the filter.
    private nint peer;
    private nint clock;
    private nint allocator;
    private string name = "CaptureDeck managed sink";
    private int state;
    private bool flushing;
    private bool ended;
    private bool disposed;

    internal DirectShowSink(DirectShowMediaType* media, CaptureSampleHandler receive, Action<string> reportFailure)
    {
        this.receive = receive;
        this.reportFailure = reportFailure;
        expected = media->Clone();
    }

    public bool Ended { get { lock (gate) return ended; } }
    public long ReceiveCalls { get; private set; }
    public long ZeroLengthSamples { get; private set; }
    public long RejectedSamples { get; private set; }

    public int GetClassID(Guid* classId)
    {
        if (classId == null) return Pointer;
        *classId = ClassId;
        return Ok;
    }

    public int Stop()
    {
        lock (gate)
        {
            state = 0;
            flushing = false;
            ended = false;
            return Ok;
        }
    }

    public int Pause() => SetState(1);
    public int Run(long start) => SetState(2);

    private int SetState(int newState)
    {
        lock (gate)
        {
            if (disposed) return WrongState;
            if (state == 0) ended = false;
            state = newState;
            return Ok;
        }
    }

    public int GetState(uint timeout, int* result)
    {
        if (result == null) return Pointer;
        lock (gate) *result = state;
        return Ok;
    }

    public int SetSyncSource(nint value)
    {
        lock (gate)
        {
            if (disposed) return WrongState;
            Replace(ref clock, value);
            return Ok;
        }
    }

    public int GetSyncSource(nint* result)
    {
        if (result == null) return Pointer;
        lock (gate) *result = AddRef(clock);
        return Ok;
    }

    public int EnumPins(nint* pins)
    {
        if (pins == null) return Pointer;
        *pins = Export(new SinkPinEnumerator(this), DirectShowInterop.EnumPins);
        return Ok;
    }

    public int FindPin(char* id, nint* pin)
    {
        if (pin == null) return Pointer;
        *pin = 0;
        if (id == null) return Pointer;
        if (new string(id) != "Input") return NotFound;
        *pin = Export(this, Pin);
        return Ok;
    }

    public int QueryFilterInfo(DirectShowFilterInfo* info)
    {
        if (info == null) return Pointer;
        lock (gate)
        {
            *info = default;
            name.AsSpan(0, Math.Min(name.Length, 127)).CopyTo(new Span<char>(info->Name, 128));
            info->Graph = AddRef(graph);
            return Ok;
        }
    }

    public int JoinFilterGraph(nint owner, char* filterName)
    {
        lock (gate)
        {
            graph = owner;
            name = filterName == null ? "" : new string(filterName);
            return Ok;
        }
    }

    public int QueryVendorInfo(nint* vendor)
    {
        if (vendor == null) return Pointer;
        *vendor = 0;
        return NotImplemented;
    }

    public uint GetMiscFlags() => 1; // AM_FILTER_MISC_FLAGS_IS_RENDERER
    public int Connect(nint pin, DirectShowMediaType* type) => Unexpected;

    public int ReceiveConnection(nint pin, DirectShowMediaType* type)
    {
        if (pin == 0 || type == null) return Pointer;
        lock (gate)
        {
            if (disposed || state != 0) return NotStopped;
            if (peer != 0) return AlreadyConnected;
            int direction;
            var hr = IntOut(pin, 9, &direction); // IPin::QueryDirection
            if (hr < 0) return hr;
            if (direction != 1) return InvalidDirection;
            if (!type->Matches(expected)) return TypeNotAccepted;
            connectedType = type->Clone();
            peer = AddRef(pin);
            flushing = false;
            ended = false;
            return Ok;
        }
    }

    public int Disconnect()
    {
        lock (gate)
        {
            if (state != 0) return NotStopped;
            if (peer == 0) return False;
            ClearConnection();
            return Ok;
        }
    }

    public int ConnectedTo(nint* pin)
    {
        if (pin == null) return Pointer;
        lock (gate)
        {
            *pin = AddRef(peer);
            return *pin == 0 ? NotConnected : Ok;
        }
    }

    public int ConnectionMediaType(DirectShowMediaType* type)
    {
        if (type == null) return Pointer;
        lock (gate)
        {
            *type = default;
            if (peer == 0) return NotConnected;
            *type = connectedType.Clone();
            return Ok;
        }
    }

    public int QueryPinInfo(DirectShowPinInfo* info)
    {
        if (info == null) return Pointer;
        *info = default;
        "Input".AsSpan().CopyTo(new Span<char>(info->Name, 128));
        info->Filter = Export(this, BaseFilter);
        return Ok;
    }

    public int QueryDirection(int* direction)
    {
        if (direction == null) return Pointer;
        *direction = 0;
        return Ok;
    }

    public int QueryId(nint* id)
    {
        if (id == null) return Pointer;
        *id = Marshal.StringToCoTaskMemUni("Input");
        return Ok;
    }

    public int QueryAccept(DirectShowMediaType* type)
    {
        if (type == null) return Pointer;
        lock (gate) return !disposed && type->Matches(expected) ? Ok : False;
    }

    public int EnumMediaTypes(nint* types)
    {
        if (types == null) return Pointer;
        *types = Export(new SinkTypeEnumerator(this), DirectShowInterop.EnumMediaTypes);
        return Ok;
    }

    internal nint CopyExpectedType()
    {
        lock (gate)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(DirectShowSink));
            var pointer = (DirectShowMediaType*)Marshal.AllocCoTaskMem(sizeof(DirectShowMediaType));
            try { *pointer = expected.Clone(); }
            catch { Marshal.FreeCoTaskMem((nint)pointer); throw; }
            return (nint)pointer;
        }
    }

    public int QueryInternalConnections(nint* pins, uint* count) => NotImplemented;

    public int EndOfStream()
    {
        lock (gate)
        {
            if (peer == 0) return NotConnected;
            if (state == 0) return WrongState;
            if (flushing) return False;
            ended = true;
            return Ok;
        }
    }

    public int BeginFlush() => SetFlush(true);
    public int EndFlush() => SetFlush(false);

    private int SetFlush(bool value)
    {
        lock (gate)
        {
            flushing = value;
            ended = false;
            return Ok;
        }
    }

    public int NewSegment(long start, long stop, double rate)
    {
        lock (gate) ended = false;
        return Ok;
    }

    public int GetAllocator(nint* result)
    {
        if (result == null) return Pointer;
        *result = 0;
        lock (gate)
        {
            if (disposed) return WrongState;
            if (allocator == 0)
            {
                var hr = Create(MemoryAllocator, MemAllocator, out allocator);
                if (hr < 0) return hr;
            }
            *result = AddRef(allocator);
            return Ok;
        }
    }

    public int NotifyAllocator(nint value, int readOnly)
    {
        if (value == 0) return Pointer;
        lock (gate)
        {
            if (disposed) return WrongState;
            Replace(ref allocator, value);
            return Ok;
        }
    }

    public int GetAllocatorRequirements(int* properties)
    {
        if (properties == null) return Pointer;
        new Span<int>(properties, 4).Clear();
        return NotImplemented;
    }

    public int Receive(nint sample)
    {
        lock (gate)
        {
            ReceiveCalls++;
            var status = ReceiveCore(sample);
            if (status != Ok)
                RejectedSamples++;
            return status;
        }
    }

    private int ReceiveCore(nint sample)
    {
        if (sample == 0) return Pointer;
        if (peer == 0) return NotConnected;
        if (state == 0) return WrongState;
        if (flushing) return False;
        if (ended) return RejectedEos;

        try
        {
            // IMediaSample slots include both HRESULT and native LONG return values.
            nint dynamicType = 0;
            var hr = PointerOut(sample, 13, &dynamicType); // GetMediaType
            try
            {
                if (hr < 0)
                    return SampleError(hr, "Cannot read sample media type");
                if ((hr == Ok && dynamicType == 0) ||
                    (dynamicType != 0 && !((DirectShowMediaType*)dynamicType)->Matches(connectedType)))
                    return SampleError(TypeNotAccepted, "Capture media type changed during recording");
            }
            finally { DirectShowMediaType.Free((DirectShowMediaType*)dynamicType); }

            var length = NoArgs(sample, 11); // GetActualDataLength
            var capacity = NoArgs(sample, 4); // GetSize
            if (length < 0 || capacity < 0 || length > capacity)
                return SampleError(InvalidArgument, "Invalid capture sample length");
            if (length == 0)
            {
                ZeroLengthSamples++;
                return Ok;
            }
            nint bytes = 0;
            hr = PointerOut(sample, 3, &bytes); // GetPointer
            if (hr < 0 || bytes == 0)
                return SampleError(hr < 0 ? hr : Pointer, "Cannot access capture sample");
            var discontinuity = NoArgs(sample, 15); // IsDiscontinuity
            if (discontinuity < 0)
                return SampleError(discontinuity, "Cannot read capture discontinuity flag");
            long start = 0;
            long end = 0;
            var timeStatus = ((delegate* unmanaged[Stdcall]<nint, long*, long*, int>)Method(sample, 5))(
                sample, &start, &end); // IMediaSample::GetTime
            var data = new CaptureSample(new ReadOnlySpan<byte>((void*)bytes, length), discontinuity == Ok,
                timeStatus is Ok or NoStopTime ? TimeSpan.FromTicks(start) : null,
                timeStatus == Ok ? TimeSpan.FromTicks(end) : null);
            return receive(data) ? Ok : Fail;
        }
        catch (Exception ex)
        {
            return SampleError(Fail, $"Capture sample callback failed: {ex.Message}");
        }
    }

    public int ReceiveMultiple(nint* samples, int count, int* processed)
    {
        if (processed == null) return Pointer;
        *processed = 0;
        if (count < 0) return InvalidArgument;
        if (count > 0 && samples == null) return Pointer;
        while (*processed < count)
        {
            var hr = Receive(samples[*processed]);
            if (hr != Ok) return hr;
            (*processed)++;
        }
        return Ok;
    }

    public int ReceiveCanBlock() => Ok;

    private int SampleError(int hr, string message)
    {
        reportFailure($"{message} (0x{hr:X8}).");
        return hr;
    }

    private static nint AddRef(nint value)
    {
        if (value != 0) Marshal.AddRef(value);
        return value;
    }

    private static void Replace(ref nint field, nint value)
    {
        var previous = field;
        field = AddRef(value);
        if (previous != 0) Marshal.Release(previous);
    }

    private void ClearConnection()
    {
        Replace(ref peer, 0);
        Replace(ref allocator, 0);
        fixed (DirectShowMediaType* type = &connectedType) DirectShowMediaType.Clear(type);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            state = 0;
            ClearConnection();
            Replace(ref clock, 0);
            graph = 0;
            fixed (DirectShowMediaType* type = &expected) DirectShowMediaType.Clear(type);
        }
    }
}

internal abstract unsafe class SingleComItem
{
    protected bool consumed;

    protected int NextItem(uint count, nint* output, uint* fetched)
    {
        if (output == null || (count != 1 && fetched == null)) return Pointer;
        if (fetched != null) *fetched = 0;
        if (count == 0) return Ok;
        *output = 0;
        if (consumed) return False;
        *output = CreateItem();
        consumed = true;
        if (fetched != null) *fetched = 1;
        return count == 1 ? Ok : False;
    }

    protected abstract nint CreateItem();

    public int Skip(uint count)
    {
        if (count == 0) return Ok;
        var status = !consumed && count == 1 ? Ok : False;
        consumed = true;
        return status;
    }

    public int Reset()
    {
        consumed = false;
        return Ok;
    }
}

[GeneratedComClass]
internal sealed unsafe partial class SinkPinEnumerator(DirectShowSink sink) : SingleComItem, IDirectShowEnumPins
{
    protected override nint CreateItem() => Export(sink, Pin);
    public int Next(uint count, nint* pins, uint* fetched) => NextItem(count, pins, fetched);

    public int Clone(nint* result)
    {
        if (result == null) return Pointer;
        *result = Export(new SinkPinEnumerator(sink) { consumed = consumed }, DirectShowInterop.EnumPins);
        return Ok;
    }
}

[GeneratedComClass]
internal sealed unsafe partial class SinkTypeEnumerator(DirectShowSink sink) : SingleComItem, IDirectShowEnumMediaTypes
{
    protected override nint CreateItem() => sink.CopyExpectedType();
    public int Next(uint count, nint* types, uint* fetched) => NextItem(count, types, fetched);

    public int Clone(nint* result)
    {
        if (result == null) return Pointer;
        *result = Export(new SinkTypeEnumerator(sink) { consumed = consumed }, DirectShowInterop.EnumMediaTypes);
        return Ok;
    }
}
