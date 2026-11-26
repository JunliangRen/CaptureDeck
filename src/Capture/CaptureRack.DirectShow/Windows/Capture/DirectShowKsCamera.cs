using System.Runtime.InteropServices;
using CaptureRack.DirectShow.Windows.Interop;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureRack.DirectShow.Windows.Capture;

internal sealed unsafe class DirectShowKsCamera : IDisposable
{
    private static readonly Guid KsControl = new("28F54685-06FD-11D2-B27A-00A0C9223196");
    private static readonly Guid TopologyInfo = new("720D4AC0-7533-11D0-A5D6-28DB04C10000");
    private static readonly Guid CameraTerminal = new("DFF229E6-F70F-11D0-B917-00A0C9223196");
    private static readonly Guid CameraControl = new("C6E13370-30AC-11D0-A18C-00A0C9118956");
    private static readonly Guid BrioVideoPipe = new("49E40215-F434-47FE-B158-0E885023E51B");
    private const uint Get = 1, Set = 2, BasicSupport = 0x200, Topology = 0x10000000;
    private const uint AutoExposurePriority = 19, BrioFieldOfView = 5;
    private static readonly int[] BrioAngles = [90, 78, 65];
    internal const string LowLightId = "LowLightCompensation", FieldOfViewId = "FieldOfView";

    private readonly ComReference control;
    private readonly ComReference topology;
    private readonly bool isBrio;

    internal DirectShowKsCamera(nint filter, string deviceId)
    {
        control = TryQuery(filter, KsControl);
        topology = TryQuery(filter, TopologyInfo);
        isBrio = deviceId.Contains("#vid_046d&pid_085e&", StringComparison.OrdinalIgnoreCase);
    }

    internal DirectShowControl? ReadLowLight(int? change = null)
    {
        if (control.Value == 0)
            return null;
        if (AccessLowLight(null, change) is { } result)
            return result;
        for (uint node = 0, count = CountNodes(); node < count; node++)
            if (NodeType(node) == CameraTerminal && AccessLowLight(node, change) is { } fromNode)
                return fromNode;
        return null;
    }

    private DirectShowControl? AccessLowLight(uint? node, int? change)
    {
        var topologyFlag = node is null ? 0 : Topology;
        var descriptor = new KsCameraControlBuffer
        {
            Property = new(CameraControl, AutoExposurePriority, BasicSupport | topologyFlag, node.GetValueOrDefault())
        };
        if (!CanWrite(&descriptor.Property, node is null ? 24 : sizeof(KsNodeProperty)))
            return null;
        int size = node is null ? 40 : sizeof(KsCameraControlBuffer);
        descriptor.Property.Flags = Get | topologyFlag;
        KsCameraControlBuffer data = default;
        if (Request(&descriptor.Property, size, &data, size, out var bytes) != Ok || bytes < size)
            return null;
        var current = node is null ? data.FilterValue : data.NodeValue;
        if (current is not (0 or 1))
            return null;
        if (change is { } value)
        {
            descriptor.Property.Flags = Set | topologyFlag;
            if (node is null)
                descriptor.FilterValue = value;
            else
                descriptor.NodeValue = value;
            data = descriptor;
            var status = Request(&descriptor.Property, size, &data, size, out _);
            if (status != Ok)
                throw new DirectShowException($"Set low-light compensation failed (0x{status:X8}). Enable automatic exposure first.");
        }
        return new(LowLightId, 0, 1, 1, 0, current, DirectShowControlMode.Manual, DirectShowControlMode.Manual)
            { Interface = "IKsControl" };
    }

    internal DirectShowControl? ReadBrioFieldOfView(int? change = null)
    {
        if (!isBrio || control.Value == 0)
            return null;
        for (uint node = 0, count = CountNodes(); node < count; node++)
        {
            // Usbvideo exposes the extension property set only on its corresponding topology node.
            var property = new KsNodeProperty(BrioVideoPipe, BrioFieldOfView, Topology | BasicSupport, node);
            if (!CanWrite(&property, sizeof(KsNodeProperty)))
                continue;
            property.Flags = Topology | Get;
            byte current = 0;
            if (Request(&property, sizeof(KsNodeProperty), &current, sizeof(byte), out var bytes) != Ok || bytes != 1 || current >= BrioAngles.Length)
                continue;
            if (change is { } angle)
            {
                property.Flags = Topology | Set;
                var selection = (byte)Array.IndexOf(BrioAngles, angle);
                var status = Request(&property, sizeof(KsNodeProperty), &selection, sizeof(byte), out _);
                if (status != Ok)
                    throw new DirectShowException($"Set BRIO field of view failed (0x{status:X8}).");
            }
            return new(FieldOfViewId, 65, 90, 1, null, BrioAngles[current], DirectShowControlMode.Manual,
                DirectShowControlMode.Manual) { Choices = [65, 78, 90], Interface = "IKsControl (Logitech UVC XU)" };
        }
        return null;
    }

    private uint CountNodes()
    {
        int count = 0;
        return topology.Value != 0 && IntOut(topology.Value, 8, &count) == Ok && count is > 0 and <= 256 ? (uint)count : 0;
    }

    private Guid NodeType(uint node)
    {
        Guid type = default;
        var get = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, int>)Method(topology.Value, 9);
        return get(topology.Value, node, &type) == Ok ? type : Guid.Empty;
    }

    private bool CanWrite(KsNodeProperty* property, int size)
    {
        uint access = 0;
        return Request(property, size, &access, sizeof(uint), out var bytes) == Ok && bytes >= sizeof(uint) &&
            (access & (Get | Set)) == (Get | Set);
    }

    private int Request(KsNodeProperty* property, int size, void* data, int dataSize, out uint bytes)
    {
        uint returned = 0;
        var access = (delegate* unmanaged[Stdcall]<nint, KsNodeProperty*, int, void*, int, uint*, int>)Method(control.Value, 3);
        var status = access(control.Value, property, size, data, dataSize, &returned);
        bytes = returned;
        return status;
    }

    private static ComReference TryQuery(nint filter, Guid iid) =>
        new(Marshal.QueryInterface(filter, in iid, out var value) == Ok ? value : 0);

    public void Dispose()
    {
        topology.Dispose();
        control.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsNodeProperty(Guid set, uint id, uint flags, uint node)
    {
        public Guid Set = set;
        public uint Id = id;
        public uint Flags = flags;
        public uint Node = node;
        public uint Reserved = 0;
    }

    // KSPROPERTY uses 8-byte alignment. The filter form occupies 40 bytes, the node form 48 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct KsCameraControlBuffer
    {
        [FieldOffset(0)] public KsNodeProperty Property;
        [FieldOffset(24)] public int FilterValue;
        [FieldOffset(32)] public int NodeValue;
    }
}
