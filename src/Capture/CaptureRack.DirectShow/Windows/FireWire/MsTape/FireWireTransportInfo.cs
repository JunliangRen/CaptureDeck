using System.Runtime.InteropServices;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowInterop;
using CaptureRack.DirectShow.Windows.Interop;

namespace CaptureRack.DirectShow.Windows.FireWire.MsTape;

public enum FireWireReadState { Read, Unsupported, Unavailable }

public sealed record FireWireStatusField(string Key, string? Value, FireWireReadState State, string Raw);

public sealed record FireWireTransportInfo(DateTimeOffset ReadAt, string? ModeKey, string? Counter,
    IReadOnlyList<FireWireStatusField> Fields);

internal sealed unsafe class FireWireTransportReader(ComReference reference) : IDisposable
{
    private const int EdBase = 0x1000;
    private const int RawExternalCommand = EdBase + 920;
    private static readonly Guid TransportId = new("A03CD5F0-3045-11cf-8C44-00AA006B6814");
    private FireWireStatusField[] details = [];
    private DateTimeOffset nextDetails;

    public static FireWireTransportReader? Open(nint source) =>
        Marshal.QueryInterface(source, in TransportId, out var ptr) == 0 && ptr != 0
            ? new(new ComReference(ptr)) : null;

    public FireWireTransportInfo Read()
    {
        var now = DateTimeOffset.Now;
        var mode = Status("FwInfoMode", EdBase + 500);
        var avMode = Avc("FwInfoTransportCode", [1, 0x20, 0xd0, 0x7f]);
        var counter = Avc("FwInfoCounter", [1, 0x20, 0x57, 0x71, 0xff, 0xff, 0xff, 0xff], TimeCounter);
        if (now >= nextDetails)
        {
            details =
            [
                Status("FwInfoMediaPresent", EdBase + 505),
                Status("FwInfoMediaType", EdBase + 511),
                Status("FwInfoMediaLength", EdBase + 506),
                Avc("FwInfoMedium", [1, 0x20, 0xda, 0x7f, 0x7f]),
                Avc("FwInfoPower", [1, 0xff, 0xb2, 0x7f], data => data[3] switch { 0x70 => "ON", 0x60 => "OFF", _ => Hex(data) }),
                Avc("FwInfoSpeed", [1, 0x20, 0xdb, 0x7f]),
                Avc("FwInfoRecordFormat", [1, 0x20, 0xd2, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]),
                Avc("FwInfoPlaybackFormat", [1, 0x20, 0xd3, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]),
                Avc("FwInfoInputMode", [1, 0x20, 0x79, 0xff]),
                Avc("FwInfoOutputMode", [1, 0x20, 0x78, 0xff]),
                Avc("FwInfoSmpte", [1, 0x20, 0x51, 0x71, 0xff, 0xff, 0xff, 0xff], TimeCounter),
                Avc("FwInfoAtn", [1, 0x20, 0x52, 0x71, 0xff, 0xff, 0xff, 0xff])
            ];
            nextDetails = now.AddSeconds(4);
        }
        var modeKey = mode.Value switch
        {
            "4296" => "FwInfoPlaying",
            "4297" => "FwInfoStopped",
            "4298" => "FwInfoPaused",
            "4300" => "FwInfoFastForward",
            "4301" => "FwInfoRewind",
            "4302" or "4303" => "FwInfoRecording",
            _ => null
        };
        return new(now, modeKey, counter.Value, [mode, avMode, counter, .. details]);
    }

    private FireWireStatusField Status(string key, int selector)
    {
        var value = int.MinValue;
        var ptr = reference.Value;
        var hr = ((delegate* unmanaged[Stdcall]<nint, int, int*, int>)Method(ptr, 8))(ptr, selector, &value);
        var state = hr == 0 && value != int.MinValue ? FireWireReadState.Read : Failure(hr);
        return new(key, state == FireWireReadState.Read ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            state, $"0x{hr:X8} / 0x{value:X8}");
    }

    private delegate string? Decode(ReadOnlySpan<byte> data);

    private FireWireStatusField Avc(string key, ReadOnlySpan<byte> request, Decode? decode = null)
    {
        // MSTape's ED_RAW_EXT_DEV_CMD reads and writes the status frame in this in/out buffer.
        Span<byte> buffer = stackalloc byte[512];
        buffer.Clear();
        request.CopyTo(buffer);
        var length = request.Length;
        var ptr = reference.Value;
        int hr;
        fixed (byte* data = buffer)
            hr = ((delegate* unmanaged[Stdcall]<nint, int, int*, byte*, int>)Method(ptr, 9))(ptr, RawExternalCommand, &length, data);

        var response = length >= 3 && length <= buffer.Length && hr == 0 ? buffer[..length] : [];
        var raw = $"0x{hr:X8} / {Hex(response)}";
        if (response.IsEmpty)
            return new(key, null, Failure(hr), raw);
        if ((response[0] & 0x0f) != 0x0c || response[1] != request[1] ||
            (response[2] != request[2] && request[2] != 0xd0) || response.Length < 4)
            return new(key, null, (response[0] & 0x0f) == 8 ? FireWireReadState.Unsupported : FireWireReadState.Unavailable, raw);
        var value = decode is null ? Hex(response[3..]) : decode(response);
        return new(key, value, value is null ? FireWireReadState.Unavailable : FireWireReadState.Read, raw);
    }

    private static FireWireReadState Failure(int hr) => hr is 0x32 or unchecked((int)0x80070032) or
        unchecked((int)0x80004001) or unchecked((int)0x80004002) ? FireWireReadState.Unsupported : FireWireReadState.Unavailable;

    private static string Hex(ReadOnlySpan<byte> bytes) => bytes.IsEmpty ? "—" : Convert.ToHexString(bytes);

    private static string? TimeCounter(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || data[3] != 0x71)
            return null;
        var frames = data[4] & 0x3f;
        if ((frames & 15) > 9 || !Bcd(data[5]) || !Bcd(data[6]) || !Bcd(data[7]))
            return null;
        var sign = (data[4] & 0x80) != 0 ? "−" : "";
        return $"{sign}{data[7]:X2}:{data[6]:X2}:{data[5]:X2}:{frames:X2}";

        static bool Bcd(byte value) => (value & 15) <= 9 && (value >> 4) <= 9;
    }

    public void Dispose() => reference.Dispose();
}
