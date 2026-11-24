using System.Runtime.InteropServices;

namespace CaptureDeck.RF;

internal static unsafe partial class RfUsbDiscovery
{
    private const uint BufferSmall = 0x1a;
    private const uint PresentUsbDevices = 0x101; // FILTER_PRESENT | FILTER_ENUMERATOR
    private const uint ServiceProperty = 5; // CM_DRP_SERVICE
    private const uint StringRegistryType = 1;
    private static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    public static string[] GetPresentUsbIds()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Check(GetDeviceIdListSize(out var size, "USB", PresentUsbDevices), "Get USB device list length");
            if (size <= 1)
                return [];
            var buffer = new char[checked((int)size)];
            fixed (char* data = buffer)
            {
                var result = GetDeviceIdList("USB", data, size, PresentUsbDevices);
                if (result == BufferSmall)
                    continue;
                Check(result, "Enumerate USB device instances");
                return ReadMultiString(buffer);
            }
        }
        throw new IOException("The USB device list kept changing. Try again.");
    }

    public static string GetInterfacePath(string instanceId)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Check(GetInterfaceListSize(out var size, in UsbDeviceInterface, instanceId, 0),
                "Get selected device's USB interface list length");
            if (size <= 1)
                break;

            var buffer = new char[checked((int)size)];
            fixed (char* data = buffer)
            {
                var result = GetInterfaceList(in UsbDeviceInterface, instanceId, data, size, 0);
                if (result == BufferSmall)
                    continue;
                Check(result, "Find selected device's present USB interface");
            }
            var paths = ReadMultiString(buffer);
            if (paths.Length == 1)
                return paths[0];
            break;
        }
        throw new IOException("The selected device does not have a unique present USB interface.");
    }

    public static string? GetDriverService(string instanceId)
    {
        if (LocateDevNode(out var device, instanceId, 0) != 0)
            return null;
        char* buffer = stackalloc char[256];
        uint size = 512;
        if (GetRegistryProperty(device, ServiceProperty, out var type, buffer, ref size, 0) != 0 ||
            type != StringRegistryType || size is < 2 or > 512 || (size & 1) != 0)
            return null;
        var value = new ReadOnlySpan<char>(buffer, (int)(size / 2));
        var terminator = value.IndexOf('\0');
        return terminator >= 0 ? value[..terminator].ToString() : null;
    }

    private static string[] ReadMultiString(char[] value) =>
        new string(value).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static void Check(uint result, string operation)
    {
        if (result != 0)
            throw new IOException($"{operation} failed (CONFIGRET 0x{result:X}).");
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDeviceIdListSize(out uint count, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDeviceIdList(string filter, char* data, uint count, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetInterfaceListSize(out uint count, in Guid classGuid, string instanceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetInterfaceList(in Guid classGuid, string instanceId, char* data, uint count, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint LocateDevNode(out uint device, string instanceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static partial uint GetRegistryProperty(uint device, uint property, out uint type, char* data, ref uint size, uint flags);
}
