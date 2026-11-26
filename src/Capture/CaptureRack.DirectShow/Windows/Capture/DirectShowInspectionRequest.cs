namespace CaptureRack.DirectShow.Windows.Capture;

public enum DirectShowInspectionOperation { List, Probe, Negotiate, Sample, Controls, DiagnosticDevice, DiagnosticCheck }

public sealed record DirectShowInspectionRequest(DirectShowInspectionOperation Operation, string? Device = null,
    DirectShowFormat? Video = null, DirectShowFormat? Audio = null, DirectShowControlChange? ControlChange = null,
    bool IncludeControls = true, bool Sample = false);

public sealed record DirectShowDiagnosticDevice(string? HardwareId, string? DriverVersion,
    DirectShowControl[]? CameraControls, DirectShowDiagnosticFailure? ControlFailure);

public sealed record DirectShowDiagnosticFailure(string Stage, string? HResult, string Message)
{
    public static DirectShowDiagnosticFailure FromException(string stage, Exception error) => new(stage,
        error switch
        {
            Interop.DirectShowException { NativeStatus: int native } => $"0x{native:X8}",
            System.Runtime.InteropServices.COMException com => $"0x{com.HResult:X8}",
            _ => ReadHResult(error.Message)
        }, error.Message);

    public static DirectShowDiagnosticFailure FromResult(string stage, string error) => new(stage, ReadHResult(error), error);

    public static DirectShowDiagnosticFailure FromCaptureError(string error) => FromResult(
        error.StartsWith("Stop DirectShow graph", StringComparison.Ordinal) ? "Stop the DirectShow graph." : "Receive samples.", error);

    private static string? ReadHResult(string message)
    {
        var match = System.Text.RegularExpressions.Regex.Match(message, @"\(0x([0-9a-fA-F]{8})\)");
        return match.Success ? "0x" + match.Groups[1].Value.ToUpperInvariant() : null;
    }
}

public sealed record DirectShowDiagnosticCheck(DirectShowFormat? NegotiatedVideo, DirectShowFormat? NegotiatedAudio,
    DirectShowCaptureResult? Result, DirectShowDiagnosticFailure? Failure);
