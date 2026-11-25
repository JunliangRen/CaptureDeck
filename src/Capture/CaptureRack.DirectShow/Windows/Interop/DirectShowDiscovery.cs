using System.Runtime.InteropServices;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureRack.DirectShow.Windows.Interop;

internal static unsafe partial class DirectShowDiscovery
{
    private static readonly Guid SystemDeviceEnum = new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
    private static readonly Guid CreateDevEnum = new("29840822-5B84-11D0-BD3B-00A0C911CE86");
    private static readonly Guid VideoInput = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    internal static readonly Guid PropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");
    internal static readonly Guid MsTapeClass = new("8C0F6AF2-0EDB-44C1-8AEB-59040BD830ED");

    internal static ComReference VideoMonikers()
    {
        using var factory = Create(SystemDeviceEnum, CreateDevEnum, "Create system device enumerator");
        var category = VideoInput;
        nint pointer = 0;
        var status = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, uint, int>)Method(factory.Value, 3))(
            factory.Value, &category, &pointer, 0);
        Check(status, "Enumerate video capture devices");
        return status == False ? new ComReference(0) : Own(pointer, "System device enumerator");
    }

    internal static ComReference BindMoniker(nint moniker, int slot, Guid iid, string operation)
    {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, Guid*, nint*, int>)Method(moniker, slot))(
            moniker, 0, 0, &iid, &result), operation);
        return Own(result, operation);
    }

    [LibraryImport("oleaut32.dll")]
    private static partial int VariantClear(nint variant);

    internal static object? ReadProperty(nint bag, string name)
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

    internal static string DisplayName(nint moniker)
    {
        nint pointer = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Method(moniker, 20))(
                moniker, 0, 0, &pointer), "Read DirectShow moniker name");
            return Marshal.PtrToStringUni(pointer) ??
                throw new DirectShowException("DirectShow moniker did not provide a name.");
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    internal static nint ReadNext(nint enumerator, bool isMediaType = false)
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
        throw new DirectShowException("DirectShow enumerator returned an incomplete item.");
    }

}
