using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace CaptureRack.Audio;

public sealed record PlaybackDevice(string Id, string Name);

public static class PlaybackDevices
{
    // Empty selects the current Windows multimedia render endpoint.
    private static string selected = "";
    public static string SelectedId
    {
        get => Volatile.Read(ref selected);
        set => Volatile.Write(ref selected, value ?? "");
    }

    public static IReadOnlyList<PlaybackDevice> List()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            return ReadAvailable(devices.Count, i =>
            {
                using var device = devices[i];
                return new(device.ID, device.FriendlyName);
            });
        }
        catch (Exception ex) when (IsDeviceError(ex)) { return []; }
    }

    internal static IReadOnlyList<PlaybackDevice> ReadAvailable(int count, Func<int, PlaybackDevice> read)
    {
        var result = new List<PlaybackDevice>(count);
        for (int i = 0; i < count; i++)
        {
            // Hot unplug can invalidate one entry after Windows returned the collection.
            try { result.Add(read(i)); }
            catch (Exception ex) when (IsDeviceError(ex)) { }
        }
        return result;
    }

    internal static bool IsDeviceError(Exception ex) => ex is COMException or InvalidOperationException or
        ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException;

    internal static MMDevice Open(MMDeviceEnumerator enumerator, string id) =>
        id.Length == 0 ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(id);
}
