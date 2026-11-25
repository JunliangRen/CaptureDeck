using System.Runtime.InteropServices;
using CaptureRack.QualityControl;
using CaptureRack.DirectShow.Windows.Interop;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowInterop;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowDiscovery;

namespace CaptureRack.DirectShow.Windows.FireWire.MsTape;

public sealed record FireWireDevice(string Id, string Name, string Vendor, string Model, string DevicePath);

internal sealed unsafe partial class MsTapeDevices : IDisposable
{
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
        using var enumerator = VideoMonikers();
        if (enumerator.Value == 0)
            return;
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

    private static nint EnsurePointer(nint pointer, string operation) =>
        pointer != 0 ? pointer : throw new FireWireException(3, $"{operation} returned a null pointer.");

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
