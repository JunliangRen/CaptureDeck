using System.Runtime.InteropServices;
using CaptureDeck.QualityControl;
using CaptureDeck.DirectShow.Windows.Interop;
using static CaptureDeck.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureDeck.DirectShow.Windows.FireWire.MsTape;

public sealed record FireWireDevice(string Id, string Name, string Vendor, string Model, string DevicePath);

internal sealed unsafe partial class MsTapeDevices : IDisposable
{
    private static readonly Guid SystemDeviceEnum = new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
    private static readonly Guid CreateDevEnum = new("29840822-5B84-11D0-BD3B-00A0C911CE86");
    private static readonly Guid VideoInput = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    private static readonly Guid MsTapeClass = new("8C0F6AF2-0EDB-44C1-8AEB-59040BD830ED");
    private static readonly Guid PropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");
    private static readonly Guid KsPropertySet = new("31EFAC30-515C-11D0-A9AA-00AA0061BE93");
    private static readonly Guid PinPropertySet = new("9B00F101-1567-11D1-B3F1-00AA003761C5");
    private static readonly Guid CapturePin = new("FB6C4281-0353-11D1-905F-0000C0CC16BA");

    private readonly List<(FireWireDevice Device, ComReference Moniker)> devices = [];

    private MsTapeDevices() { }

    public FireWireDevice[] Descriptions => devices.Select(item => item.Device).ToArray();

    public static MsTapeDevices Scan()
    {
        var result = new MsTapeDevices();
        try
        {
            result.Enumerate();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public ComReference Bind(string id)
    {
        var item = devices.FirstOrDefault(item => string.Equals(item.Device.Id, id, StringComparison.OrdinalIgnoreCase));
        if (item.Moniker is null)
            throw new FireWireException(3, $"Tape device not found: {id}.");
        return BindMoniker(item.Moniker.Value, 8, BaseFilter, "Bind MSTape capture source");
    }

    private void Enumerate()
    {
        using var factory = Create(SystemDeviceEnum, CreateDevEnum, "Create system device enumerator");
        var category = VideoInput;
        nint pointer = 0;
        var status = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, uint, int>)Method(factory.Value, 3))(
            factory.Value, &category, &pointer, 0); // ICreateDevEnum::CreateClassEnumerator
        using var enumerator = new ComReference(pointer);
        Check(status, "Enumerate video capture devices");
        if (status == False)
            return;
        EnsurePointer(enumerator.Value, "System device enumerator");
        while (true)
        {
            var moniker = ReadNext(enumerator.Value, isMediaType: false);
            if (moniker == 0)
                return;
            using var current = new ComReference(moniker);
            using var bag = BindMoniker(moniker, 9, PropertyBag, "Read device property bag");
            if (!Guid.TryParse(ReadProperty(bag.Value, "DeviceClassGUID") as string, out var classId) || classId != MsTapeClass)
                continue;
            var name = ReadProperty(bag.Value, "FriendlyName") as string;
            var vendor = ReadProperty(bag.Value, "VendorText") as string ?? "";
            var model = ReadProperty(bag.Value, "UnitModelText") as string;
            if (string.IsNullOrEmpty(model))
                model = ReadProperty(bag.Value, "ModelText") as string ?? "";
            if (string.IsNullOrEmpty(name))
                name = ReadProperty(bag.Value, "Description") as string;
            if (string.IsNullOrEmpty(name))
                name = string.IsNullOrEmpty(model) ? "Microsoft AV/C Tape Subunit Device" : model;
            var path = ReadProperty(bag.Value, "DevicePath") as string ?? "";
            var low = ReadWord(bag.Value, "UniqueID_Low");
            var high = ReadWord(bag.Value, "UniqueID_High");
            var id = low.HasValue && high.HasValue ? $"{high.Value:x8}{low.Value:x8}" : DisplayName(moniker);
            devices.Add((new FireWireDevice(id, name, vendor, model, path), new ComReference(current.Detach())));
        }
    }

    private static ComReference BindMoniker(nint moniker, int slot, Guid iid, string operation)
    {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, Guid*, nint*, int>)Method(moniker, slot))(
            moniker, 0, 0, &iid, &result), operation);
        return new ComReference(EnsurePointer(result, operation));
    }

    [LibraryImport("oleaut32.dll")]
    private static partial int VariantClear(nint variant);

    private static object? ReadProperty(nint bag, string name)
    {
        // The x64 VARIANT holds up to 24 bytes including its tag and pointer fields.
        long* variant = stackalloc long[3];
        new Span<long>(variant, 3).Clear();
        try
        {
            fixed (char* text = name)
            {
                var hr = ((delegate* unmanaged[Stdcall]<nint, char*, nint, nint, int>)Method(bag, 3))(
                    bag, text, (nint)variant, 0); // IPropertyBag::Read
                return hr < 0 ? null : Marshal.GetObjectForNativeVariant((nint)variant);
            }
        }
        finally { VariantClear((nint)variant); }
    }

    private static uint? ReadWord(nint bag, string name) =>
        ReadProperty(bag, name) switch
        {
            uint unsigned => unsigned,
            int signed => unchecked((uint)signed),
            ushort unsigned => unsigned,
            short signed => unchecked((uint)signed),
            byte unsigned => unsigned,
            sbyte signed => unchecked((uint)signed),
            long signed when signed is >= int.MinValue and <= uint.MaxValue => unchecked((uint)signed),
            ulong unsigned when unsigned <= uint.MaxValue => (uint)unsigned,
            string text when uint.TryParse(text, out var value) => value,
            _ => null
        };

    private static string DisplayName(nint moniker)
    {
        nint pointer = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Method(moniker, 20))(
                moniker, 0, 0, &pointer), "Read MSTape moniker name");
            return Marshal.PtrToStringUni(pointer) ??
                throw new FireWireException(3, "MSTape moniker did not provide a name.");
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    private static nint EnsurePointer(nint pointer, string operation) =>
        pointer != 0 ? pointer : throw new FireWireException(3, $"{operation} returned a null pointer.");

    private static nint ReadNext(nint enumerator, bool isMediaType)
    {
        nint pointer = 0;
        uint fetched = 0;
        var result = Next(enumerator, &pointer, &fetched);
        if (result == Ok && fetched == 1 && pointer != 0)
            return pointer;
        if (result == False && fetched == 0 && pointer == 0)
            return 0;
        if (isMediaType)
            DirectShowMediaType.Free((DirectShowMediaType*)pointer);
        else if (pointer != 0)
            Marshal.Release(pointer);
        Check(result, "Read a DirectShow enumerator item");
        throw new FireWireException(3, "DirectShow enumerator returned an incomplete item.");
    }

    public static MsTapeOutput SelectOutput(nint source)
    {
        nint enumerator = 0;
        Check(PointerOut(source, 10, &enumerator), "Enumerate MSTape pins"); // IBaseFilter::EnumPins
        using var pins = new ComReference(EnsurePointer(enumerator, "MSTape pin enumerator"));
        var best = new MsTapeOutput();
        var bestScore = int.MaxValue;
        try
        {
            while (ReadNext(pins.Value, isMediaType: false) is var pin && pin != 0)
            {
                using var current = new ComReference(pin);
                int direction;
                Check(IntOut(pin, 9, &direction), "Read MSTape pin direction");
                if (direction != 1)
                    continue;
                var capture = IsCapturePin(pin);
                nint mediaEnumerator = 0;
                Check(PointerOut(pin, 12, &mediaEnumerator), "Enumerate capture pin types"); // IPin::EnumMediaTypes
                using var types = new ComReference(EnsurePointer(mediaEnumerator, "Media type enumerator"));
                while (ReadNext(types.Value, isMediaType: true) is var pointer && pointer != 0)
                {
                    var type = (DirectShowMediaType*)pointer;
                    if (MsTapeMediaTypes.GetLayout(in *type) == TsLayout.Ts192 && (capture ? 0 : 1) < bestScore)
                    {
                        best.Set(pin, type);
                        bestScore = capture ? 0 : 1;
                        continue;
                    }
                    DirectShowMediaType.Free(type);
                }
            }
            if (best.Pin == 0)
                throw new FireWireException(4, "No native 192-byte MSTape output pin is available.");
            return best;
        }
        catch
        {
            best.Dispose();
            throw;
        }
    }

    private static bool IsCapturePin(nint pin)
    {
        var iid = KsPropertySet;
        if (Marshal.QueryInterface(pin, in iid, out var pointer) < 0)
            return false;
        using var properties = new ComReference(pointer);
        var set = PinPropertySet;
        Guid category = default;
        uint returned = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, uint, Guid*, uint, uint*, int>)Method(pointer, 4))(
            pointer, &set, 0, 0, 0, &category, (uint)sizeof(Guid), &returned);
        return hr >= 0 && returned == sizeof(Guid) && category == CapturePin;
    }

    public void Dispose()
    {
        foreach (var item in devices)
            item.Moniker.Dispose();
        devices.Clear();
    }
}

internal sealed unsafe class MsTapeOutput : IDisposable
{
    public nint Pin { get; private set; }
    public DirectShowMediaType* MediaType { get; private set; }
    public TsLayout Layout => TsLayout.Ts192;

    public void Set(nint pin, DirectShowMediaType* mediaType)
    {
        Dispose();
        Marshal.AddRef(pin);
        Pin = pin;
        MediaType = mediaType;
    }

    public void Dispose()
    {
        if (Pin != 0)
            Marshal.Release(Pin);
        DirectShowMediaType.Free(MediaType);
        Pin = 0;
        MediaType = null;
    }
}
