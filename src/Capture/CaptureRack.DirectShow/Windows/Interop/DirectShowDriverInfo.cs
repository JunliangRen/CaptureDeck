using System.Runtime.InteropServices;

namespace CaptureRack.DirectShow.Windows.Interop;

internal static unsafe partial class DirectShowDriverInfo
{
    private const string MonikerPrefix = "@device:pnp:";
    private static readonly DevicePropertyKey InstanceId = new(new("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 256);
    private static readonly DevicePropertyKey DriverVersion = new(new("A8B865DD-2E3D-4094-AD97-E593A70C75D6"), 3);
    private static readonly DevicePropertyKey Service = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 6);

    internal static (string? HardwareId, string? DriverVersion) Read(string moniker)
    {
        if (!moniker.StartsWith(MonikerPrefix, StringComparison.OrdinalIgnoreCase))
            return default;
        var path = moniker[MonikerPrefix.Length..];
        var segments = path.Split('#', 4);
        var hardwareId = segments.Length >= 3 && segments[0].StartsWith(@"\\?\", StringComparison.Ordinal)
            ? $"{segments[0][4..]}\\{segments[1]}" : null;
        var instance = DeviceInstance(path);
        if (instance is null || LocateDevNode(out var node, instance, 0) != 0)
            return (hardwareId, null);

        return (hardwareId, ReadNodeString(node, DriverVersion));
    }

    internal static bool IsMsdv(string moniker)
    {
        if (!moniker.StartsWith(@"@device:pnp:\\?\avc#", StringComparison.OrdinalIgnoreCase))
            return false;
        var instance = DeviceInstance(moniker[MonikerPrefix.Length..]);
        return instance is not null && LocateDevNode(out var node, instance, 0) == 0 &&
            string.Equals(ReadNodeString(node, Service), "MSDV", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadNodeString(uint node, DevicePropertyKey key)
    {
        char* value = stackalloc char[256];
        uint bytes = 256 * sizeof(char);
        var status = GetNodeProperty(node, &key, out var type, value, ref bytes, 0);
        return status == 0 && type == 0x12 ? DecodeString(value, bytes, 256) : null;
    }

    private static string? DeviceInstance(string interfacePath)
    {
        var key = InstanceId;
        char* value = stackalloc char[512];
        uint bytes = 512 * sizeof(char);
        var status = GetInterfaceProperty(interfacePath, &key, out var type, value, ref bytes, 0);
        return status == 0 && type == 0x12 ? DecodeString(value, bytes, 512) : null;
    }

    private static string? DecodeString(char* value, uint bytes, int capacity)
    {
        if (bytes == 0 || bytes > capacity * sizeof(char) || bytes % sizeof(char) != 0)
            return null;
        var text = new ReadOnlySpan<char>(value, (int)(bytes / sizeof(char))).TrimEnd('\0');
        return text.IsEmpty ? null : new string(text);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DevicePropertyKey(Guid Format, uint Id);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetInterfaceProperty(string deviceInterface, DevicePropertyKey* key,
        out uint type, char* data, ref uint bytes, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint LocateDevNode(out uint node, string instanceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint GetNodeProperty(uint node, DevicePropertyKey* key,
        out uint type, char* data, ref uint bytes, uint flags);
}
