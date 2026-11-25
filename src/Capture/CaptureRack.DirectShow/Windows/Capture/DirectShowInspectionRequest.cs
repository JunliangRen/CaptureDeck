namespace CaptureRack.DirectShow.Windows.Capture;

public enum DirectShowInspectionOperation { List, Probe, Negotiate, Sample }

public sealed record DirectShowInspectionRequest(DirectShowInspectionOperation Operation, string? Device = null,
    DirectShowFormat? Video = null, DirectShowFormat? Audio = null);
