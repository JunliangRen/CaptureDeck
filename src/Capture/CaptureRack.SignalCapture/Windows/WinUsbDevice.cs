using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CaptureRack.SignalCapture;

internal sealed unsafe partial class WinUsbDevice : IDisposable
{
    private const uint GenericReadWrite = 0xc0000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint FlagOverlapped = 0x40000000;
    private const uint PipeTransferTimeout = 3;
    private const uint MaximumTransferSize = 8;
    private const int BulkPipe = 2;

    private readonly SafeFileHandle file;
    private readonly SafeWinUsbHandle usb;

    private WinUsbDevice(SafeFileHandle file, SafeWinUsbHandle usb)
    {
        this.file = file;
        this.usb = usb;
    }

    public static WinUsbDevice Open(string path)
    {
        var file = CreateFile(path, GenericReadWrite, ShareReadWrite, 0, OpenExisting, FlagOverlapped, 0);
        try
        {
            Check(!file.IsInvalid, "Open the selected RF USB interface");
            Check(Initialize(file, out var handle) != 0, "Initialize WinUSB");
            var usb = new SafeWinUsbHandle(handle);
            if (!usb.IsInvalid)
                return new WinUsbDevice(file, usb);
            usb.Dispose();
            throw new IOException("WinUSB returned an invalid handle.");
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public (ushort UsbVersion, ushort Vendor, ushort Product, ushort Release) ReadDeviceDescriptor()
    {
        byte* descriptor = stackalloc byte[18];
        Check(GetDescriptor(usb, 1, 0, 0, descriptor, 18, out var length) != 0, "Read USB device descriptor");
        if (length != 18 || descriptor[0] != 18 || descriptor[1] != 1)
            throw new InvalidDataException("The USB device descriptor is incomplete.");

        var data = new ReadOnlySpan<byte>(descriptor, 18);
        return (BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[12..]));
    }

    public IReadOnlyList<RfUsbEndpoint> ReadBulkInEndpoints()
    {
        Check(GetCurrentAlternateSetting(usb, out var alternate) != 0, "Read the active USB alternate setting");
        UsbInterfaceDescriptor settings;
        Check(QueryInterfaceSettings(usb, alternate, &settings) != 0, "Read USB interface settings");
        if (settings.Length != 9 || settings.DescriptorType != 4 || settings.AlternateSetting != alternate)
            throw new InvalidDataException("The active USB interface descriptor is invalid.");
        var bulkIn = new List<RfUsbEndpoint>(settings.Endpoints);
        for (byte i = 0; i < settings.Endpoints; i++)
        {
            PipeInformation pipe;
            Check(QueryPipe(usb, alternate, i, &pipe) != 0, "Read USB endpoint descriptor");
            if (pipe.Type == BulkPipe && (pipe.Id & 0x80) != 0 && pipe.MaximumPacketSize != 0)
                bulkIn.Add(new RfUsbEndpoint(settings.Number, alternate, pipe.Id, pipe.MaximumPacketSize));
        }
        return bulkIn;
    }

    public uint ReadMaximumTransferSize(byte endpoint)
    {
        uint policyLength = sizeof(uint);
        uint maximumTransfer = 0;
        Check(GetPipePolicy(usb, endpoint, MaximumTransferSize, ref policyLength, &maximumTransfer) != 0,
            "Read the RF bulk endpoint transfer limit");
        if (policyLength != sizeof(uint) || maximumTransfer == 0)
            throw new InvalidDataException("WinUSB returned an invalid bulk transfer limit.");
        return maximumTransfer;
    }

    public void SetControlTimeout(uint milliseconds) =>
        Check(SetPipePolicy(usb, 0, PipeTransferTimeout, sizeof(uint), &milliseconds) != 0, "Set the WinUSB control timeout");

    public uint ReadVendorControl(byte request, Span<byte> buffer, string operation)
    {
        var packet = new SetupPacket { RequestType = 0xc0, Request = request, Length = checked((ushort)buffer.Length) };
        fixed (byte* data = buffer)
        {
            Check(ControlTransfer(usb, packet, data, packet.Length, out var length, 0) != 0, operation);
            return length;
        }
    }

    public void Dispose()
    {
        usb.Dispose();
        file.Dispose();
    }

    private static void Check(bool success, string operation)
    {
        if (!success)
        {
            var code = Marshal.GetLastPInvokeError();
            throw new IOException($"{operation} failed (Win32 {code}): {new Win32Exception(code).Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbInterfaceDescriptor
    {
        public byte Length, DescriptorType, Number, AlternateSetting, Endpoints, Class, Subclass, Protocol, StringIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PipeInformation
    {
        public int Type;
        public byte Id;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SetupPacket
    {
        public byte RequestType, Request;
        public ushort Value, Index, Length;
    }

    private sealed class SafeWinUsbHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeWinUsbHandle(nint value) : base(ownsHandle: true) => SetHandle(value);
        protected override bool ReleaseHandle() => Free(handle) != 0;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_Initialize", SetLastError = true)]
    private static partial int Initialize(SafeFileHandle file, out nint usb);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_Free")]
    private static partial int Free(nint usb);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_GetDescriptor", SetLastError = true)]
    private static partial int GetDescriptor(SafeWinUsbHandle usb, byte type, byte index, ushort language,
        byte* data, uint length, out uint transferred);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_GetCurrentAlternateSetting", SetLastError = true)]
    private static partial int GetCurrentAlternateSetting(SafeWinUsbHandle usb, out byte alternateSetting);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_QueryInterfaceSettings", SetLastError = true)]
    private static partial int QueryInterfaceSettings(SafeWinUsbHandle usb, byte alternateSetting, UsbInterfaceDescriptor* settings);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_QueryPipe", SetLastError = true)]
    private static partial int QueryPipe(SafeWinUsbHandle usb, byte alternateSetting, byte index, PipeInformation* pipe);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_SetPipePolicy", SetLastError = true)]
    private static partial int SetPipePolicy(SafeWinUsbHandle usb, byte pipe, uint policy, uint length, uint* value);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_GetPipePolicy", SetLastError = true)]
    private static partial int GetPipePolicy(SafeWinUsbHandle usb, byte pipe, uint policy, ref uint length, uint* value);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_ControlTransfer", SetLastError = true)]
    private static partial int ControlTransfer(SafeWinUsbHandle usb, SetupPacket packet, byte* data,
        uint length, out uint transferred, nint overlapped);
}
