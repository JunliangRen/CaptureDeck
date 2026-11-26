using System.Runtime.InteropServices;
using CaptureRack.DirectShow.Windows.Interop;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowDiscovery;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureRack.DirectShow.Windows.Capture;

public sealed record DirectShowDevice(int Index, string Id, string Name);
public sealed record DirectShowDeviceCapabilities(DirectShowDevice Device, bool HasDroppedFrameCounter,
    IReadOnlyList<DirectShowFormat> Formats);

internal sealed unsafe class CaptureSource : IDisposable
{
    private static readonly Guid StreamConfig = new("C6E13340-30AC-11D0-A18C-00A0C9118956");
    private static readonly Guid DroppedFrames = new("C6E13344-30AC-11D0-A18C-00A0C9118956");
    private readonly List<FormatEntry> formats = [];
    private readonly List<ComReference> pins = [];
    private readonly ComReference source;

    private CaptureSource(ComReference source) => this.source = source;
    internal nint Filter => source.Value;

    internal static IReadOnlyList<DirectShowDevice> List()
    {
        List<DirectShowDevice> result = [];
        Visit((device, _) => { result.Add(device); return false; });
        return result;
    }

    internal static CaptureSource Open(string selector, out DirectShowDevice device, bool readFormats = true)
    {
        DirectShowDevice? selected = null;
        CaptureSource? opened = null;
        Visit((candidate, moniker) =>
        {
            if (candidate.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) != selector &&
                !string.Equals(candidate.Id, selector, StringComparison.OrdinalIgnoreCase))
                return false;
            selected = candidate;
            opened = new CaptureSource(BindMoniker(moniker, 8, BaseFilter, $"Bind {candidate.Name}"));
            try
            {
                if (readFormats)
                    opened.ReadFormats();
            }
            catch
            {
                opened.Dispose();
                throw;
            }
            return true;
        });
        device = selected ?? throw new InvalidOperationException($"DirectShow device '{selector}' was not found. Run directshow list.");
        return opened!;
    }

    private static void Visit(Func<DirectShowDevice, nint, bool> visitor)
    {
        using var monikers = VideoMonikers();
        if (monikers.Value == 0)
            return;

        var index = 0;
        while (ReadNext(monikers.Value) is var pointer && pointer != 0)
        {
            using var moniker = new ComReference(pointer);
            using var bag = BindMoniker(pointer, 9, PropertyBag, "Read DirectShow device properties");
            if (Guid.TryParse(ReadProperty(bag.Value, "DeviceClassGUID") as string, out var classId) &&
                classId == MsTapeClass)
                continue;
            var id = DisplayName(pointer);
            if (DirectShowDriverInfo.IsMsdv(id))
                continue;

            var name = ReadProperty(bag.Value, "FriendlyName") as string ??
                ReadProperty(bag.Value, "Description") as string ?? "Video capture device";
            if (visitor(new(index++, id, name), pointer))
                return;
        }
    }

    internal IReadOnlyList<DirectShowFormat> DescribeFormats(string deviceId) =>
        formats.Select(entry => ResolveScanMode(entry.Format, deviceId)).ToArray();

    internal DirectShowFormat ResolveScanMode(DirectShowFormat format, string deviceId) =>
        CaptureMediaType.ResolveScanMode(format, deviceId, formats.Select(entry => entry.Format));

    internal bool HasDroppedFrameCounter
    {
        get
        {
            using var counter = TryQuery(Filter, DroppedFrames);
            return counter.Value != 0;
        }
    }

    internal int? ReadDroppedFrames()
    {
        using var counter = TryQuery(Filter, DroppedFrames);
        if (counter.Value == 0)
            return null;
        int count = 0;
        return IntOut(counter.Value, 3, &count) == Ok ? count : null;
    }

    internal FormatEntry Select(int index, string kind)
    {
        if ((uint)index >= (uint)formats.Count || formats[index].Format.Kind != kind)
            throw new InvalidOperationException($"Format {index} is not a {kind} format. Run directshow probe for available indexes.");
        return formats[index];
    }

    internal static void SetFormat(FormatEntry entry)
    {
        if (!entry.Configurable)
            return;
        using var config = Query(entry.Pin, StreamConfig, "Get IAMStreamConfig");
        Check(((delegate* unmanaged[Stdcall]<nint, DirectShowMediaType*, int>)Method(config.Value, 3))(
            config.Value, entry.MediaType), $"Set DirectShow {entry.Format.Kind} format");
    }

    private void ReadFormats()
    {
        nint enumeration = 0;
        Check(PointerOut(Filter, 10, &enumeration), "Enumerate DirectShow filter pins");
        using var allPins = Own(enumeration, "Pin enumerator");
        while (ReadNext(allPins.Value) is var pin && pin != 0)
        {
            using var owned = new ComReference(pin);
            int direction = 0;
            Check(IntOut(pin, 9, &direction), "Get DirectShow pin direction");
            if (direction != 1)
                continue;

            var id = ReadPinId(pin);
            DirectShowPinInfo pinInfo = default;
            var status = ((delegate* unmanaged[Stdcall]<nint, DirectShowPinInfo*, int>)Method(pin, 8))(pin, &pinInfo);
            if (pinInfo.Filter != 0)
                Marshal.Release(pinInfo.Filter);
            var name = status == Ok
                ? new string(pinInfo.Name, 0, new ReadOnlySpan<char>(pinInfo.Name, 128).IndexOf('\0') is var end && end >= 0 ? end : 128)
                : id;
            pins.Add(new ComReference(owned.Detach()));
            if (!ReadStreamCapabilities(pin, id, name))
                ReadMediaTypes(pin, id, name);
        }
    }

    private bool ReadStreamCapabilities(nint pin, string id, string name)
    {
        using var config = TryQuery(pin, StreamConfig);
        if (config.Value == 0)
            return false;
        int count = 0, size = 0;
        var caps = (delegate* unmanaged[Stdcall]<nint, int*, int*, int>)Method(config.Value, 5);
        if (caps(config.Value, &count, &size) != Ok || count is <= 0 or > 10000 || size is <= 0 or > 65536)
            return false;

        var get = (delegate* unmanaged[Stdcall]<nint, int, DirectShowMediaType**, byte*, int>)Method(config.Value, 6);
        var buffer = new byte[size];
        var added = false;
        for (var index = 0; index < count; index++)
        {
            DirectShowMediaType* media = null;
            int hr;
            fixed (byte* bytes = buffer)
                hr = get(config.Value, index, &media, bytes);
            if (hr == Ok && media != null)
            {
                AddFormat(pin, id, name, media, configurable: true);
                added = true;
            }
            else
                DirectShowMediaType.Free(media);
        }
        return added;
    }

    private void ReadMediaTypes(nint pin, string id, string name)
    {
        nint pointer = 0;
        var status = PointerOut(pin, 12, &pointer);
        using var mediaTypes = new ComReference(pointer);
        if (status != Ok || pointer == 0)
            return;
        while (ReadNext(pointer, isMediaType: true) is var media && media != 0)
            AddFormat(pin, id, name, (DirectShowMediaType*)media, configurable: false);
    }

    private void AddFormat(nint pin, string id, string name, DirectShowMediaType* media, bool configurable)
    {
        try
        {
            formats.Add(new FormatEntry(pin, media,
                CaptureMediaType.Describe(in *media, formats.Count, id, name), configurable));
        }
        catch
        {
            DirectShowMediaType.Free(media);
            throw;
        }
    }

    private static string ReadPinId(nint pin)
    {
        nint pointer = 0;
        try
        {
            Check(PointerOut(pin, 10, &pointer), "Read DirectShow pin ID");
            return Marshal.PtrToStringUni(pointer) ?? throw new DirectShowException("DirectShow pin did not return an ID.");
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    private static ComReference TryQuery(nint obj, Guid iid) =>
        new(Marshal.QueryInterface(obj, in iid, out var pointer) >= 0 ? pointer : 0);

    public void Dispose()
    {
        foreach (var entry in formats)
            DirectShowMediaType.Free(entry.MediaType);
        foreach (var pin in pins)
            pin.Dispose();
        source.Dispose();
    }

    internal sealed class FormatEntry(nint pin, DirectShowMediaType* mediaType, DirectShowFormat format, bool configurable)
    {
        public nint Pin { get; } = pin;
        public DirectShowMediaType* MediaType { get; } = mediaType;
        public DirectShowFormat Format { get; } = format;
        public bool Configurable { get; } = configurable;
    }
}
