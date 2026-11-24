using System.Runtime.InteropServices;
using System.Text;

namespace CaptureRack.SignalCapture;

public static unsafe class RtlSdrProbe
{
    public static IReadOnlyList<RtlSdrDevice> List()
    {
        using var library = new Library();
        var devices = new List<RtlSdrDevice>();
        var count = library.GetDeviceCount();
        for (uint index = 0; index < count; index++)
            devices.Add(library.GetDevice(index));
        return devices;
    }

    public static RtlSdrCapabilities Probe(uint index)
    {
        using var library = new Library();
        return library.WithReceiver(index, library.GetCapabilities);
    }

    public static RtlSdrConfigured Configure(uint index, RtlSdrSettings settings)
    {
        using var library = new Library();
        return library.WithReceiver(index, (device, receiver) => library.Apply(device, receiver, settings));
    }

    private sealed class Library : IDisposable
    {
        private readonly nint handle = Load();

        private static nint Load()
        {
            var path = Environment.GetEnvironmentVariable("CAPTURERACK_RTLSDR_LIBRARY");
            if (string.IsNullOrWhiteSpace(path))
                path = Environment.GetEnvironmentVariable("CAPTUREDECK_RTLSDR_LIBRARY");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = new[] { "rtlsdr.dll", "librtlsdr.dll" }
                    .Select(name => Path.Combine(AppContext.BaseDirectory, name)).FirstOrDefault(File.Exists);
            }
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("RTL-SDR driver DLL not found. Place rtlsdr.dll beside CaptureRack.Probe or set CAPTURERACK_RTLSDR_LIBRARY to its full path.");
            if (!Path.IsPathFullyQualified(path))
                throw new IOException("CAPTURERACK_RTLSDR_LIBRARY must be a full path.");

            try
            {
                return NativeLibrary.Load(path, typeof(RtlSdrProbe).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                throw new IOException($"Cannot load RTL-SDR driver '{path}'. Check its architecture and dependencies.", ex);
            }
        }

        public void Dispose() => NativeLibrary.Free(handle);

        private nint Required(string name)
        {
            if (NativeLibrary.TryGetExport(handle, name, out var address))
                return address;
            throw new IOException($"RTL-SDR driver does not export {name}.");
        }

        public uint GetDeviceCount() =>
            ((delegate* unmanaged[Cdecl]<uint>)Required("rtlsdr_get_device_count"))();

        public RtlSdrDevice GetDevice(uint index)
        {
            var name = ((delegate* unmanaged[Cdecl]<uint, nint>)Required("rtlsdr_get_device_name"))(index);
            var label = Marshal.PtrToStringUTF8(name);
            if (string.IsNullOrEmpty(label))
                label = "RTL-SDR";
            Span<byte> manufacturer = stackalloc byte[256];
            Span<byte> product = stackalloc byte[256];
            Span<byte> serial = stackalloc byte[256];
            manufacturer.Clear();
            product.Clear();
            serial.Clear();
            fixed (byte* m = manufacturer, p = product, s = serial)
            {
                var read = (delegate* unmanaged[Cdecl]<uint, byte*, byte*, byte*, int>)Required("rtlsdr_get_device_usb_strings");
                return read(index, m, p, s) == 0
                    ? new(index, label, UsbString(manufacturer), UsbString(product), UsbString(serial))
                    : new(index, label, null, null, null);
            }
        }

        private static string? UsbString(ReadOnlySpan<byte> value)
        {
            var end = value.IndexOf((byte)0);
            if (end >= 0)
                value = value[..end];
            return value.IsEmpty ? null : Encoding.UTF8.GetString(value);
        }

        public T WithReceiver<T>(uint index, Func<RtlSdrDevice, nint, T> action)
        {
            if (index >= GetDeviceCount())
                throw new IOException($"RTL-SDR device index {index} is not present. Run rf sdr rtl-sdr list first.");
            var device = GetDevice(index);
            var open = (delegate* unmanaged[Cdecl]<nint*, uint, int>)Required("rtlsdr_open");
            var close = (delegate* unmanaged[Cdecl]<nint, int>)Required("rtlsdr_close");
            nint receiver = 0;
            Check(open(&receiver, index), "Open RTL-SDR device");
            if (receiver == 0)
                throw new IOException("RTL-SDR driver returned an empty receiver handle.");
            try { return action(device, receiver); }
            finally { close(receiver); }
        }

        public RtlSdrCapabilities GetCapabilities(RtlSdrDevice device, nint receiver)
        {
            var tuner = (RtlSdrTuner)ReadInt(receiver, "rtlsdr_get_tuner_type");
            var readGains = (delegate* unmanaged[Cdecl]<nint, int*, int>)Required("rtlsdr_get_tuner_gains");
            var count = readGains(receiver, null);
            Check(count, "Query tuner gains");
            if (count > 1024)
                throw new IOException("RTL-SDR driver reported too many tuner gain values.");
            var gains = new int[count];
            if (count != 0)
            {
                fixed (int* buffer = gains)
                {
                    if (readGains(receiver, buffer) != count)
                        throw new IOException("RTL-SDR driver returned an inconsistent tuner gain list.");
                }
            }

            var supportsVga = NativeLibrary.TryGetExport(handle, "rtlsdr_set_blog_v4_vga_index", out _) &&
                NativeLibrary.TryGetExport(handle, "rtlsdr_is_blog_v4", out var detect) &&
                ((delegate* unmanaged[Cdecl]<nint, int>)detect)(receiver) > 0;
            return RtlSdrCapabilities.FromDevice(device, tuner, gains, supportsVga);
        }

        public RtlSdrConfigured Apply(RtlSdrDevice device, nint receiver, RtlSdrSettings settings)
        {
            var capabilities = GetCapabilities(device, receiver);
            var vga = settings.Validate(capabilities);
            if (settings.Ppm is int ppm && ppm != ReadInt(receiver, "rtlsdr_get_freq_correction"))
                Set(receiver, "rtlsdr_set_freq_correction", ppm, "Set frequency correction");

            SetUnsigned(receiver, "rtlsdr_set_sample_rate", settings.SampleRateHz, "Set sample rate");
            SetUnsigned(receiver, "rtlsdr_set_center_freq", settings.FrequencyHz, "Set center frequency");
            Set(receiver, "rtlsdr_set_agc_mode", settings.DigitalAgc ? 1 : 0, "Set RTL2832U digital AGC");
            Set(receiver, "rtlsdr_set_tuner_gain_mode", settings.GainTenthsDb.HasValue ? 1 : 0, "Set tuner gain mode");
            if (settings.GainTenthsDb is int gain)
                Set(receiver, "rtlsdr_set_tuner_gain", gain, "Set tuner gain");
            if (vga is int index)
                Set(receiver, "rtlsdr_set_blog_v4_vga_index", index, "Set Blog V4 VGA index");

            return new(capabilities, settings, ReadUnsigned(receiver, "rtlsdr_get_center_freq"),
                ReadUnsigned(receiver, "rtlsdr_get_sample_rate"), ReadInt(receiver, "rtlsdr_get_freq_correction"),
                settings.GainTenthsDb.HasValue ? ReadInt(receiver, "rtlsdr_get_tuner_gain") : null, vga);
        }

        private int ReadInt(nint receiver, string name) =>
            ((delegate* unmanaged[Cdecl]<nint, int>)Required(name))(receiver);

        private uint ReadUnsigned(nint receiver, string name) =>
            ((delegate* unmanaged[Cdecl]<nint, uint>)Required(name))(receiver);

        private void Set(nint receiver, string name, int value, string operation) =>
            Check(((delegate* unmanaged[Cdecl]<nint, int, int>)Required(name))(receiver, value), operation);

        private void SetUnsigned(nint receiver, string name, uint value, string operation) =>
            Check(((delegate* unmanaged[Cdecl]<nint, uint, int>)Required(name))(receiver, value), operation);

        private static void Check(int result, string operation)
        {
            if (result < 0)
                throw new IOException($"{operation} failed (librtlsdr {result}).");
        }
    }
}
