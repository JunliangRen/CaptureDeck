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

    private SafeWinUsbHandle streamInterface;
    private readonly List<SafeWinUsbHandle> associated = [];
    private byte? originalAlternate;

    private WinUsbDevice(SafeFileHandle file, SafeWinUsbHandle usb)
    {
        this.file = file;
        this.usb = streamInterface = usb;
    }

    public static WinUsbDevice Open(string path)
    {
        DomesdaySession.CheckPath(path);
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

    public (ushort UsbVersion, ushort Vendor, ushort Product, ushort Release, byte ProductStringIndex) ReadDeviceDescriptor()
    {
        byte* descriptor = stackalloc byte[18];
        Check(GetDescriptor(usb, 1, 0, 0, descriptor, 18, out var length) != 0, "Read USB device descriptor");
        if (length != 18 || descriptor[0] != 18 || descriptor[1] != 1)
            throw new InvalidDataException("The USB device descriptor is incomplete.");

        var data = new ReadOnlySpan<byte>(descriptor, 18);
        return (BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[12..]), data[15]);
    }

    public string? ReadProductString(byte index)
    {
        const int capacity = 256;
        byte* data = stackalloc byte[capacity];
        // DdD firmware advertises US English (0x0409) for its USB strings.
        if (index == 0 || GetDescriptor(usb, 3, index, 0x0409, data, capacity, out uint length) == 0 ||
            length < 2 || length > capacity || length != data[0] || data[1] != 3 || length % 2 != 0)
            return null;
        return System.Text.Encoding.Unicode.GetString(new ReadOnlySpan<byte>(data + 2, (int)length - 2));
    }

    public IReadOnlyList<RfUsbEndpoint> ReadBulkInEndpoints()
    {
        Check(GetCurrentAlternateSetting(usb, out var alternate) != 0, "Read the active USB alternate setting");
        UsbInterfaceDescriptor settings;
        Check(QueryInterfaceSettings(usb, alternate, &settings) != 0, "Read USB interface settings");
        if (settings.Length != 9 || settings.DescriptorType != 4 || settings.AlternateSetting != alternate)
            throw new InvalidDataException("The active USB interface descriptor is invalid.");
        var bulkIn = new List<RfUsbEndpoint>();
        AddBulkIn(usb, alternate, settings, bulkIn);
        return bulkIn;
    }

    private static void AddBulkIn(SafeWinUsbHandle handle, byte alternateIndex, UsbInterfaceDescriptor settings,
        List<RfUsbEndpoint> endpoints)
    {
        for (byte i = 0; i < settings.Endpoints; i++)
        {
            PipeInformation pipe;
            Check(QueryPipe(handle, alternateIndex, i, &pipe) != 0, "Read USB endpoint descriptor");
            if (pipe.Type == BulkPipe && (pipe.Id & 0x80) != 0 && pipe.MaximumPacketSize != 0)
                endpoints.Add(new(settings.Number, settings.AlternateSetting, pipe.Id, pipe.MaximumPacketSize));
        }
    }

    public RfUsbEndpoint SelectLegacyEndpoint()
    {
        var interfaces = new Dictionary<byte, SafeWinUsbHandle>();
        var endpoints = new List<RfUsbEndpoint>();
        var handle = usb;
        for (int index = 0; index < 256; index++)
        {
            for (int alt = 0; alt < 256; alt++)
            {
                UsbInterfaceDescriptor descriptor;
                if (QueryInterfaceSettings(handle, (byte)alt, &descriptor) == 0)
                {
                    if (alt > 0 && Marshal.GetLastPInvokeError() == 259) break;
                    Check(false, "Read legacy alternate interface");
                }
                interfaces[descriptor.Number] = handle;
                AddBulkIn(handle, (byte)alt, descriptor, endpoints);
            }
            if (index == 255) break;
            if (GetAssociatedInterface(usb, (byte)index, out var raw) == 0)
            {
                if (Marshal.GetLastPInvokeError() == 259) break;
                Check(false, "Open associated legacy interface");
            }
            handle = new SafeWinUsbHandle(raw);
            associated.Add(handle);
        }
        var endpoint = DomesdayUsb.SelectStreamEndpoint(RfUsbFirmware.Legacy, endpoints)
            ?? throw new InvalidDataException("No legacy bulk IN endpoint found.");
        streamInterface = interfaces[endpoint.InterfaceNumber];
        Check(GetCurrentAlternateSetting(streamInterface, out var current) != 0, "Read legacy alternate setting");
        if (current != endpoint.AlternateSetting)
        {
            Check(SetAlternate(streamInterface, endpoint.AlternateSetting) != 0, "Select legacy alternate setting");
            originalAlternate = current;
        }
        return endpoint;
    }

    public void RestoreAlternate()
    {
        if (originalAlternate is not byte alternate) return;
        Check(SetAlternate(streamInterface, alternate) != 0, "Restore legacy alternate setting");
        originalAlternate = null;
    }

    public void ClearLegacyHalt(byte endpoint) =>
        Check(ResetPipe(streamInterface, endpoint) != 0, "Clear legacy bulk IN halt");

    public uint ReadMaximumTransferSize(byte endpoint)
    {
        uint policyLength = sizeof(uint);
        uint maximumTransfer = 0;
        Check(GetPipePolicy(streamInterface, endpoint, MaximumTransferSize, ref policyLength, &maximumTransfer) != 0,
            "Read the RF bulk endpoint transfer limit");
        if (policyLength != sizeof(uint) || maximumTransfer == 0)
            throw new InvalidDataException("WinUSB returned an invalid bulk transfer limit.");
        return maximumTransfer;
    }

    public void SetControlTimeout(uint milliseconds) =>
        Check(SetPipePolicy(usb, 0, PipeTransferTimeout, sizeof(uint), &milliseconds) != 0, "Set the WinUSB control timeout");

    public uint ReadVendorControl(byte request, Span<byte> buffer, string operation, ushort value = 0)
    {
        var packet = new SetupPacket { RequestType = 0xc0, Request = request, Value = value, Length = checked((ushort)buffer.Length) };
        fixed (byte* data = buffer)
        {
            Check(ControlTransfer(usb, packet, data, packet.Length, out var length, 0) != 0, operation);
            return length;
        }
    }

    public void Command(byte request, ushort value)
    {
        var packet = new SetupPacket { RequestType = 0x40, Request = request, Value = value };
        Check(ControlTransfer(usb, packet, null, 0, out _, 0) != 0, $"DdD command 0x{request:X2}");
    }

    // All storage addressed by Windows is pinned and owned by one request until
    // completion, including immediate completions that still need to be reaped.
    private sealed class Request(WinUsbDevice device, int size) : IDisposable
    {
        private readonly EventWaitHandle ready = new(false, EventResetMode.ManualReset);
        private readonly NativeOverlapped[] operation = GC.AllocateArray<NativeOverlapped>(1, pinned: true);
        public readonly byte[] Buffer = GC.AllocateUninitializedArray<byte>(size, pinned: true);
        public bool Pending { get; private set; }

        public void Submit(byte endpoint)
        {
            ready.Reset();
            operation[0] = new NativeOverlapped { EventHandle = ready.SafeWaitHandle.DangerousGetHandle() };
            fixed (NativeOverlapped* op = operation)
            fixed (byte* data = Buffer)
            {
                bool control = endpoint == 0;
                int success = control
                    ? ControlTransferAsync(device.usb, new SetupPacket { RequestType = 0xc0, Request = 0xb7,
                        Value = 0x40, Length = (ushort)Buffer.Length }, data, (uint)Buffer.Length, null, op)
                    : ReadPipe(device.streamInterface, endpoint, data, (uint)Buffer.Length, null, op);
                Check(success != 0 || Marshal.GetLastPInvokeError() == 997, control ? "Submit FIFO telemetry" : "Submit bulk read");
            }
            Pending = true;
        }

        public bool Wait(int milliseconds) => ready.WaitOne(milliseconds);

        public int? Complete()
        {
            fixed (NativeOverlapped* op = operation)
            {
                int result = GetResult(device.usb, op, out uint count, 0);
                if (result == 0 && Marshal.GetLastPInvokeError() == 996) return null;
                Pending = false;
                Check(result != 0, "Complete WinUSB request");
                return checked((int)count);
            }
        }

        public void Cancel()
        {
            if (!Pending) return;
            fixed (NativeOverlapped* op = operation)
            {
                // ERROR_NOT_FOUND means completion won the cancellation race.
                Check(CancelIoEx(device.file, op) != 0 || Marshal.GetLastPInvokeError() == 1168, "Cancel WinUSB request");
            }
        }

        public void Reap(long deadline)
        {
            if (!Pending) return;
            if (Wait((int)Math.Clamp(deadline - Environment.TickCount64, 0, 1000)))
            {
                try { Complete(); }
                catch (IOException) when (!Pending) { } // A terminal I/O error also returns ownership.
            }
            if (Pending) throw new IOException("WinUSB cancellation did not complete within the shared one-second deadline.");
        }
        public void Dispose() => ready.Dispose();
    }

    // A driver can retain a buffer after CancelIoEx. Root the request and its device
    // handles for the process lifetime when ownership cannot be established.
    private static readonly System.Collections.Concurrent.ConcurrentBag<Request> unreaped = [];
    public bool HasUnreapedRequests { get; private set; }

    public void Stream(RfUsbEndpoint endpoint, uint maximum, Func<byte[], int, bool> consume,
        CancellationToken stop, DomesdayFifoTelemetry? fifo, DomesdaySession session, DomesdayDiagnostics diagnostics)
    {
        const int depth = 16;
        var size = (int)Math.Min(maximum, 256 * 1024u) / endpoint.MaxPacketBytes * endpoint.MaxPacketBytes;
        diagnostics.BulkTransferBytes = size;
        if (size == 0) throw new IOException("Invalid WinUSB transfer size.");
        byte raw = 1;
        Check(SetPipePolicy(streamInterface, endpoint.Address, 7, 1, (uint*)&raw) != 0, "Enable WinUSB RAW_IO");
        var bulk = new Request[depth];
        var control = fifo is null ? null : new Request(this, 23);
        Action? submitControl = control is null ? null : () => control.Submit(0);
        Func<int?>? completeControl = control is null ? null : control.Complete;
        int slot = 0, submitted = 0;
        int? actual = null;
        string operation = "Submit";
        UsbRequestState State(Exception? error = null) => new(operation, endpoint.Address, slot, submitted,
            size, actual, control?.Pending == true, (error?.InnerException as Win32Exception)?.NativeErrorCode);
        try
        {
            for (int i = 0; i < depth; i++)
            {
                slot = i;
                bulk[i] = new Request(this, size);
                bulk[i].Submit(endpoint.Address);
                submitted++;
            }
            bool stopping = false;
            for (int i = 0; bulk[i].Pending; i = (i + 1) % depth)
            {
                slot = i;
                actual = null;
                operation = "Wait";
                var request = bulk[i];
                if (!request.Wait(2000)) throw new IOException("DdD stream timed out.");
                operation = "Complete";
                int? bytes = actual = request.Complete();
                if (bytes.HasValue) submitted--;
                if (bytes != size) throw new IOException($"Short DdD bulk read: requested {size}, received {bytes}.");
                operation = "Consume";
                diagnostics.UsbState = State();
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                bool streaming;
                try { streaming = consume(request.Buffer, size); }
                finally { diagnostics.ObserveCallback(start); }
                stopping |= stop.IsCancellationRequested;
                if (stopping) continue;
                operation = "Resubmit";
                actual = null;
                request.Submit(endpoint.Address);
                submitted++;
                operation = "CheckUnderflow";
                slot = (i + depth - 1) % depth;
                var previous = bulk[slot];
                if (streaming && previous.Pending && (actual = previous.Complete()).HasValue)
                {
                    submitted--;
                    throw new IOException("DdD USB host underflow: the preceding request was already complete.");
                }
                if (streaming && control is not null)
                    fifo!.Tick(Environment.TickCount64, submitControl!, completeControl!, control.Buffer);
            }
        }
        catch (Exception ex)
        {
            submitted = bulk.Count(item => item?.Pending == true);
            diagnostics.UsbState = State(ex);
            diagnostics.CaptureFault(ex);
            session.Fail(ex);
        }
        finally
        {
            var requests = bulk.Append(control).OfType<Request>().ToArray();
            foreach (var request in requests)
                if (request.Pending) session.Cleanup("Cancel request", request.Cancel);
            long deadline = Environment.TickCount64 + 1000;
            foreach (var request in requests)
            {
                if (request.Pending) session.Cleanup("Reap request", () => request.Reap(deadline));
                if (request.Pending)
                {
                    HasUnreapedRequests = true;
                    unreaped.Add(request);
                }
                else request.Dispose();
            }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CancelIoEx(SafeFileHandle file, NativeOverlapped* op);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_ControlTransfer", SetLastError = true)]
    private static partial int ControlTransferAsync(SafeWinUsbHandle usb, SetupPacket packet, byte* data,
        uint length, uint* transferred, NativeOverlapped* overlapped);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_ReadPipe", SetLastError = true)]
    private static partial int ReadPipe(SafeWinUsbHandle usb, byte pipe, byte* data, uint size, uint* count, NativeOverlapped* operation);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_GetOverlappedResult", SetLastError = true)]
    private static partial int GetResult(SafeWinUsbHandle usb, NativeOverlapped* operation, out uint size, int wait);

    public void Dispose()
    {
        if (HasUnreapedRequests) return;
        foreach (var handle in associated) handle.Dispose();
        usb.Dispose();
        file.Dispose();
    }

    private static void Check(bool success, string operation)
    {
        if (!success)
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            throw new IOException($"{operation} failed (Win32 {error.NativeErrorCode}): {error.Message}", error);
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

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_GetAssociatedInterface", SetLastError = true)]
    private static partial int GetAssociatedInterface(SafeWinUsbHandle usb, byte index, out nint handle);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_SetCurrentAlternateSetting", SetLastError = true)]
    private static partial int SetAlternate(SafeWinUsbHandle usb, byte alternate);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_ResetPipe", SetLastError = true)]
    private static partial int ResetPipe(SafeWinUsbHandle usb, byte endpoint);

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
