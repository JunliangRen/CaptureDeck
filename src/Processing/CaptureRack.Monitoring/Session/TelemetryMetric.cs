namespace CaptureRack.App;

public enum TelemetrySeverity { Normal, Info, Caution, Danger }

public sealed record TelemetryMetric(
    string Id,
    string Label,
    string Value,
    TelemetrySeverity Severity = TelemetrySeverity.Normal,
    string? Tooltip = null);
