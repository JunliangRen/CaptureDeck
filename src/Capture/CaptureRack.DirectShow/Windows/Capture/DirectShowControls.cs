using System.Runtime.InteropServices;
using CaptureRack.DirectShow.Windows.Interop;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureRack.DirectShow.Windows.Capture;

[Flags]
public enum DirectShowControlMode { Auto = 1, Manual = 2 }

public sealed record DirectShowControl(string Id, int Minimum, int Maximum, int Step, int? DefaultValue,
    int Value, DirectShowControlMode SupportedModes, DirectShowControlMode CurrentMode)
{
    public string? Interface { get; init; }
    public int[]? Choices { get; init; }
}

public sealed record DirectShowControlChange(string Id, int Value, DirectShowControlMode Mode);

internal static unsafe class DirectShowControls
{
    private static readonly Guid Camera = new("C6E13370-30AC-11D0-A18C-00A0C9118956"); // IAMCameraControl
    private static readonly Guid Video = new("C6E13360-30AC-11D0-A18C-00A0C9118956"); // IAMVideoProcAmp
    private static readonly (string Id, int Property)[] CameraProperties =
    [
        ("Exposure", 4), (DirectShowKsCamera.LowLightId, 19), ("Focus", 6), ("Zoom", 3), ("Pan", 0), ("Tilt", 1),
        ("Roll", 2), ("Iris", 5)
    ];
    private static readonly (string Id, int Property)[] VideoProperties =
    [
        ("WhiteBalance", 7), ("Brightness", 0), ("Contrast", 1), ("Saturation", 3), ("Sharpness", 4),
        ("Hue", 2), ("Gamma", 5), ("ColorEnable", 6), ("BacklightCompensation", 8), ("Gain", 9)
    ];

    internal static DirectShowControl[] Read(nint filter, string deviceId)
    {
        using var camera = TryQuery(filter, Camera);
        using var video = TryQuery(filter, Video);
        List<DirectShowControl> result = [];
        ReadProperties(camera.Value, CameraProperties, "IAMCameraControl", result);
        using var ks = new DirectShowKsCamera(filter, deviceId);
        if (!result.Any(property => property.Id == DirectShowKsCamera.LowLightId) && ks.ReadLowLight() is { } lowLight)
            result.Add(lowLight);
        if (ks.ReadBrioFieldOfView() is { } fieldOfView)
            result.Add(fieldOfView);
        ReadProperties(video.Value, VideoProperties, "IAMVideoProcAmp", result);
        return [.. result];
    }

    internal static DirectShowControl[] Apply(nint filter, string deviceId, DirectShowControlChange change)
    {
        using var camera = TryQuery(filter, Camera);
        using var video = TryQuery(filter, Video);
        var property = Array.Find(CameraProperties, item => item.Id == change.Id);
        var control = camera.Value;
        if (property.Id is null)
        {
            property = Array.Find(VideoProperties, item => item.Id == change.Id);
            control = video.Value;
        }

        var available = property.Id is null ? null : ReadProperty(control, property);
        if (available is null)
        {
            using var ks = new DirectShowKsCamera(filter, deviceId);
            var updated = change.Id switch
            {
                DirectShowKsCamera.LowLightId when ks.ReadLowLight() is { } lowLight => ks.ReadLowLight(Validate(lowLight, change)),
                DirectShowKsCamera.FieldOfViewId when ks.ReadBrioFieldOfView() is { } fieldOfView => ks.ReadBrioFieldOfView(Validate(fieldOfView, change)),
                _ => null
            };
            if (updated is null)
                throw new NotSupportedException($"DirectShow camera control '{change.Id}' is unavailable.");
            return Read(filter, deviceId);
        }

        var set = (delegate* unmanaged[Stdcall]<nint, int, int, int, int>)Method(control, 4);
        var status = set(control, property.Property, Validate(available, change), (int)change.Mode);
        if (status != Ok)
            throw new DirectShowException($"Set DirectShow camera control '{change.Id}' failed (0x{status:X8}).");
        return Read(filter, deviceId);
    }

    internal static int Validate(DirectShowControl available, DirectShowControlChange change)
    {
        if (change.Mode is not (DirectShowControlMode.Auto or DirectShowControlMode.Manual) ||
            (available.SupportedModes & change.Mode) == 0)
            throw new ArgumentException($"DirectShow camera control '{change.Id}' does not support {change.Mode} mode.");
        if (change.Mode == DirectShowControlMode.Auto)
            return available.DefaultValue ?? available.Value;
        var value = change.Value;
        if (available.Choices is { } choices && !choices.Contains(value))
            throw new ArgumentOutOfRangeException(nameof(change), $"Value must be one of {string.Join(", ", choices)}.");
        if (value < available.Minimum || value > available.Maximum || ((long)value - available.Minimum) % available.Step != 0)
            throw new ArgumentOutOfRangeException(nameof(change), $"Value must be in {available.Minimum}..{available.Maximum} with step {available.Step}.");
        return value;
    }

    private static void ReadProperties(nint control, (string Id, int Property)[] properties, string api, List<DirectShowControl> result)
    {
        foreach (var property in properties)
            if (ReadProperty(control, property) is { } available)
                result.Add(available with { Interface = api });
    }

    private static DirectShowControl? ReadProperty(nint control, (string Id, int Property) property)
    {
        if (control == 0)
            return null;
        int min = 0, max = 0, step = 0, defaultValue = 0, supported = 0, value = 0, mode = 0;
        var range = (delegate* unmanaged[Stdcall]<nint, int, int*, int*, int*, int*, int*, int>)Method(control, 3);
        var get = (delegate* unmanaged[Stdcall]<nint, int, int*, int*, int>)Method(control, 5);
        if (range(control, property.Property, &min, &max, &step, &defaultValue, &supported) != Ok || min > max ||
            get(control, property.Property, &value, &mode) != Ok)
            return null;
        var modes = (DirectShowControlMode)supported & (DirectShowControlMode.Auto | DirectShowControlMode.Manual);
        if (modes == 0)
            return null;
        return new(property.Id, min, max, Math.Max(step, 1), defaultValue, value, modes, (DirectShowControlMode)mode);
    }

    private static ComReference TryQuery(nint filter, Guid iid) =>
        new(Marshal.QueryInterface(filter, in iid, out var value) == Ok ? value : 0);
}
