using CaptureRack.Core;
using CaptureRack.DeckLink;
using CaptureRack.QualityControl;

namespace CaptureRack.App;

public enum TelemetrySeverity { Normal, Info, Caution, Danger }

public sealed record TelemetryMetric(
    string Id,
    string Label,
    string Value,
    TelemetrySeverity Severity = TelemetrySeverity.Normal,
    string? Tooltip = null);

public sealed record WorkspaceCapabilities(
    bool SupportsVideoPreview,
    bool SupportsVideoScopes,
    bool SupportsAudioMeters,
    bool SupportsAudioSpectrum,
    bool SupportsEvents,
    bool SupportsPreviewDisplayOptions,
    bool SupportsLocalAudioMonitor,
    bool SupportsTransportControl,
    bool SupportsStandaloneAudioOverlay);

public sealed record CaptureSourceDescriptor(
    string BackendId,
    CaptureBackendCategory Category,
    string DisplayNameKey,
    string SubtitleKey,
    bool IsImplementedInUi,
    WorkspaceCapabilities Capabilities,
    string IconGlyph = "\uE714",
    string? CliCommandHint = null);

public interface ICaptureWorkspaceModule
{
    CaptureSourceDescriptor Descriptor { get; }

    IReadOnlyList<TelemetryMetric> BuildIdleTelemetry(Func<string, string> localize);
}

public sealed class DeckLinkWorkspaceModule : ICaptureWorkspaceModule
{
    public static readonly WorkspaceCapabilities DeckLinkCapabilities = new(
        SupportsVideoPreview: true,
        SupportsVideoScopes: true,
        SupportsAudioMeters: true,
        SupportsAudioSpectrum: true,
        SupportsEvents: true,
        SupportsPreviewDisplayOptions: true,
        SupportsLocalAudioMonitor: true,
        SupportsTransportControl: true,
        SupportsStandaloneAudioOverlay: false);

    public CaptureSourceDescriptor Descriptor { get; } = new(
        DeckLinkBackend.BackendId,
        CaptureBackendCategory.Video,
        "BackendDeckLinkName",
        "BackendDeckLinkDesc",
        IsImplementedInUi: true,
        DeckLinkCapabilities,
        IconGlyph: "\uE714");

    public IReadOnlyList<TelemetryMetric> BuildIdleTelemetry(Func<string, string> localize) => IdleMetrics(localize);

    internal static TelemetryMetric[] IdleMetrics(Func<string, string> localize) =>
        [new("valid", localize("MetricValidFrames"), "—"), new("noinput", localize("MetricNoInput"), "—"),
         new("missing", localize("MetricMissing"), "—"), new("skipped", localize("MetricSkipped"), "—")];

    internal static IReadOnlyList<TelemetryMetric> BuildVideoTelemetry(CaptureStats stats, long previewSkippedFrames,
        Func<string, string> localize, IFormatProvider formatProvider)
    {
        var valid = stats.VideoFrames;
        var noInput = stats.NoInputFrames;
        var missing = stats.MissingVideoFrames;
        var skipped = Math.Max(0, previewSkippedFrames);
        var audio = stats.AudioPackets;
        var summary = string.Format(formatProvider, localize("CaptureCounts"), valid, noInput, audio, missing, skipped);

        return new TelemetryMetric[]
        {
            new("valid", localize("MetricValidFrames"), valid.ToString("N0", formatProvider),
                TelemetrySeverity.Normal, summary),
            new("noinput", localize("MetricNoInput"), noInput.ToString("N0", formatProvider),
                noInput > 0 ? TelemetrySeverity.Caution : TelemetrySeverity.Normal, summary),
            new("missing", localize("MetricMissing"), missing.ToString("N0", formatProvider),
                missing > 0 ? TelemetrySeverity.Danger : TelemetrySeverity.Normal, summary),
            new("skipped", localize("MetricSkipped"), skipped.ToString("N0", formatProvider),
                skipped > 0 ? TelemetrySeverity.Caution : TelemetrySeverity.Normal, summary)
        };
    }
}

public sealed class TestSignalWorkspaceModule : ICaptureWorkspaceModule
{
    public CaptureSourceDescriptor Descriptor { get; } = new(
        TestSignal.BackendId, CaptureBackendCategory.Video, "BackendTestSignalName", "BackendTestSignalDesc",
        IsImplementedInUi: true, DeckLinkWorkspaceModule.DeckLinkCapabilities with { SupportsTransportControl = false },
        IconGlyph: "\uE7F4");

    public IReadOnlyList<TelemetryMetric> BuildIdleTelemetry(Func<string, string> localize) =>
        DeckLinkWorkspaceModule.IdleMetrics(localize);
}

public sealed class DirectShowWorkspaceModule : ICaptureWorkspaceModule
{
    public const string BackendId = "windows.directshow";

    public CaptureSourceDescriptor Descriptor { get; } = new(
        BackendId,
        CaptureBackendCategory.Video,
        "BackendDirectShowName",
        "BackendDirectShowDesc",
        IsImplementedInUi: true,
        new(true, true, true, true, false, true, true, false, false),
        IconGlyph: "\uE722",
        CliCommandHint: "CaptureRack.Probe.exe directshow list");

    public IReadOnlyList<TelemetryMetric> BuildIdleTelemetry(Func<string, string> localize) => IdleMetrics(localize);

    internal static TelemetryMetric[] IdleMetrics(Func<string, string> localize) =>
        [new("ds-received", localize("DsVideo"), "—"), new("ds-decoded", localize("MetricValidFrames"), "—"),
         new("ds-skipped", localize("MetricSkipped"), "—"), new("ds-record-rejected", localize("DsRecordRejected"), "—")];

    public static IReadOnlyList<TelemetryMetric> BuildLiveTelemetry(
        long received,
        long decoded,
        long unreadable,
        long previewSkippedFrames,
        Func<string, string> localize,
        IFormatProvider formatProvider, long? rejectedPackets = null)
    {
        var skipped = Math.Max(0, previewSkippedFrames);
        var metrics = new List<TelemetryMetric>(4)
        {
            new("ds-received", localize("DsVideo"), received.ToString("N0", formatProvider)),
            new("ds-decoded", localize("MetricValidFrames"), decoded.ToString("N0", formatProvider))
        };
        if (unreadable > 0)
        {
            metrics.Add(new("ds-unreadable", localize("DsMetricUnreadable"), unreadable.ToString("N0", formatProvider),
                TelemetrySeverity.Danger));
        }
        metrics.Add(new("ds-skipped", localize("MetricSkipped"), skipped.ToString("N0", formatProvider),
            skipped > 0 ? TelemetrySeverity.Caution : TelemetrySeverity.Normal));
        metrics.Add(new("ds-record-rejected", localize("DsRecordRejected"), rejectedPackets?.ToString("N0", formatProvider) ?? "—",
            rejectedPackets > 0 ? TelemetrySeverity.Danger : TelemetrySeverity.Normal));
        return metrics;
    }
}

public sealed class FireWireWorkspaceModule : ICaptureWorkspaceModule
{
    public const string BackendId = "windows.firewire";

    public CaptureSourceDescriptor Descriptor { get; } = new(
        BackendId,
        CaptureBackendCategory.FireWire,
        "BackendFireWireName",
        "BackendFireWireDesc",
        IsImplementedInUi: true,
        new(true, true, true, true, false, true, true, false, false),
        IconGlyph: "\uE88E",
        CliCommandHint: "CaptureRack.Probe.exe firewire list");

    public IReadOnlyList<TelemetryMetric> BuildIdleTelemetry(Func<string, string> localize) =>
        (TelemetryMetric[])[new("ts-packets", localize("TsMetricPackets"), "—"), new("ts-cc", localize("TsMetricContinuity"), "—")];

    public static IReadOnlyList<TelemetryMetric> BuildTsTelemetry(
        long packets,
        long decodedFrames,
        long? continuityErrors,
        long syncErrors,
        double bitsPerSecond,
        long previewSkippedFrames,
        Func<string, string> localize,
        IFormatProvider formatProvider)
    {
        var skipped = Math.Max(0, previewSkippedFrames);
        var metrics = new List<TelemetryMetric>(5)
        {
            new("ts-packets", localize("TsMetricPackets"), packets.ToString("N0", formatProvider)),
            new("ts-decoded", localize("MetricValidFrames"), decodedFrames.ToString("N0", formatProvider))
        };
        if (bitsPerSecond > 0)
        {
            var mbps = bitsPerSecond / 1_000_000d;
            var rateText = mbps >= 1
                ? $"{mbps.ToString("0.##", formatProvider)} Mb/s"
                : $"{(bitsPerSecond / 1000d).ToString("N0", formatProvider)} kb/s";
            metrics.Add(new("ts-bitrate", localize("FwBitrateShort"), rateText));
        }
        if (syncErrors > 0)
        {
            metrics.Add(new("ts-sync", localize("TsMetricSync"), syncErrors.ToString("N0", formatProvider),
                TelemetrySeverity.Danger));
        }
        metrics.Add(new("ts-cc", localize("TsMetricContinuity"), continuityErrors?.ToString("N0", formatProvider) ?? "—",
            continuityErrors > 0 ? TelemetrySeverity.Danger : TelemetrySeverity.Normal));
        if (skipped > 0)
        {
            metrics.Add(new("ts-skipped", localize("MetricSkipped"), skipped.ToString("N0", formatProvider),
                TelemetrySeverity.Caution));
        }
        return metrics;
    }

    public static IReadOnlyList<TelemetryMetric> BuildDvTelemetry(
        long dvFrames,
        long decodedFrames,
        long invalidFrames,
        long discontinuities,
        long previewSkippedFrames,
        Func<string, string> localize,
        IFormatProvider formatProvider)
    {
        var skipped = Math.Max(0, previewSkippedFrames);
        var metrics = new List<TelemetryMetric>(4)
        {
            new("dv-frames", localize("DvInfoFrames"), dvFrames.ToString("N0", formatProvider)),
            new("dv-decoded", localize("MetricValidFrames"), decodedFrames.ToString("N0", formatProvider)),
            new("dv-invalid", localize("DvInfoInvalidFrames"), invalidFrames.ToString("N0", formatProvider),
                invalidFrames > 0 ? TelemetrySeverity.Danger : TelemetrySeverity.Normal)
        };
        if (discontinuities > 0)
        {
            metrics.Add(new("dv-disc", localize("DvInfoDiscontinuities"), discontinuities.ToString("N0", formatProvider),
                TelemetrySeverity.Caution));
        }
        if (skipped > 0 || discontinuities == 0)
        {
            metrics.Add(new("dv-skipped", localize("MetricSkipped"), skipped.ToString("N0", formatProvider),
                skipped > 0 ? TelemetrySeverity.Caution : TelemetrySeverity.Normal));
        }
        return metrics;
    }
}

internal static class RfFifoMetrics
{
    public static TelemetryMetric DroppedSamples(long? observed, bool limited, string label, IFormatProvider format)
    {
        var value = observed is null || (limited && observed == 0) ? "—" :
            (limited ? "≥" : "") + observed.Value.ToString("N0", format);
        return new("rf-fifo-lost", label, value, observed > 0 ? TelemetrySeverity.Danger :
            limited ? TelemetrySeverity.Caution : TelemetrySeverity.Normal);
    }
}

public sealed class PlannedWorkspaceModule(
    CaptureSourceDescriptor descriptor,
    Func<Func<string, string>, TelemetryMetric[]> telemetryFactory) : ICaptureWorkspaceModule
{
    public CaptureSourceDescriptor Descriptor { get; } = descriptor;

    public IReadOnlyList<TelemetryMetric> BuildIdleTelemetry(Func<string, string> localize) =>
        telemetryFactory(localize);
}

public sealed class WorkspaceModuleRegistry
{
    private readonly ICaptureWorkspaceModule[] modules;

    public IReadOnlyList<ICaptureWorkspaceModule> Modules => modules;
    public ICaptureWorkspaceModule DefaultModule { get; }

    public WorkspaceModuleRegistry(ICaptureWorkspaceModule? primaryModule = null)
    {
        DefaultModule = primaryModule ?? new DeckLinkWorkspaceModule();
        modules =
        [
            DefaultModule,
            new TestSignalWorkspaceModule(),
            new DirectShowWorkspaceModule(),
            new FireWireWorkspaceModule(),
            new PlannedWorkspaceModule(
                new("windows.signalcapture", CaptureBackendCategory.SignalCapture,
                    "BackendRfName", "BackendRfDesc", IsImplementedInUi: true,
                    new(false, false, false, true, true, false, false, true, true),
                    IconGlyph: "\uE959",
                    CliCommandHint: "CaptureRack.Probe.exe rf list"),
                loc =>
                [
                    new("rf-rate", loc("RfMetricSampleRate"), "—"),
                    new("rf-overruns", loc("RfMetricOverruns"), "—"),
                    new("rf-clips", loc("RfMetricAdcClips"), "—")
                ]),
            new PlannedWorkspaceModule(
                new("windows.audio", CaptureBackendCategory.Audio,
                    "BackendAudioName", "BackendAudioDesc", IsImplementedInUi: false,
                    new(false, false, true, true, true, false, true, false, false),
                    IconGlyph: "\uE720"),
                loc =>
                [
                    new("aud-ch", loc("AudioMetricChannels"), "—"),
                    new("aud-rate", loc("RfMetricSampleRate"), "—"),
                    new("aud-clips", loc("RfMetricAdcClips"), "—")
                ])
        ];
    }

    public ICaptureWorkspaceModule Resolve(string? backendId)
    {
        if (string.IsNullOrWhiteSpace(backendId))
            return DefaultModule;

        return modules.FirstOrDefault(module =>
            string.Equals(module.Descriptor.BackendId, backendId, StringComparison.OrdinalIgnoreCase))
            ?? DefaultModule;
    }

    public IReadOnlyList<ICaptureWorkspaceModule> ByCategory(CaptureBackendCategory category) =>
        modules.Where(module => module.Descriptor.Category == category).ToArray();

    public static bool CanSwitchBackend(bool previewActive, bool recordingActive, bool recordingTransition, bool qcActive) =>
        !previewActive && !recordingActive && !recordingTransition && !qcActive;
}

public sealed class CaptureSourceCatalog
{
    public IReadOnlyList<CaptureSourceDescriptor> Sources { get; }

    public CaptureSourceCatalog(ICaptureWorkspaceModule activeModule)
        : this(new WorkspaceModuleRegistry(activeModule))
    {
    }

    public CaptureSourceCatalog(WorkspaceModuleRegistry registry)
    {
        Sources = registry.Modules.Select(module => module.Descriptor).ToArray();
    }

    public IReadOnlyList<CaptureSourceDescriptor> ByCategory(CaptureBackendCategory category) =>
        Sources.Where(source => source.Category == category).ToArray();
}

public static class AnalogInputOptionsBuilder
{
    public static AnalogInputOptions Build(
        string? ntscIre,
        string? componentLevel,
        string? audioLevel,
        bool gainOverride,
        double gainLeftDb,
        double gainRightDb) => new()
    {
        NtscIre = ntscIre switch
        {
            "Ire0" => AnalogNtscIreMode.Ire0,
            "Ire75" => AnalogNtscIreMode.Ire75,
            _ => AnalogNtscIreMode.Driver
        },
        ComponentLevel = componentLevel switch
        {
            "Smpte" => AnalogComponentLevelMode.Smpte,
            "Betacam" => AnalogComponentLevelMode.Betacam,
            _ => AnalogComponentLevelMode.Driver
        },
        AudioLevel = audioLevel switch
        {
            "Pro" => AnalogAudioLevelMode.Pro,
            "Consumer" => AnalogAudioLevelMode.Consumer,
            _ => AnalogAudioLevelMode.Driver
        },
        GainLeftDb = gainOverride ? Math.Clamp(gainLeftDb, -12, 12) : null,
        GainRightDb = gainOverride ? Math.Clamp(gainRightDb, -12, 12) : null
    };
}

public static class RecordingFileNaming
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string SanitizeLabel(string? rawLabel)
    {
        if (string.IsNullOrWhiteSpace(rawLabel))
            return "";

        var chars = rawLabel.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidChars, chars[i]) >= 0 || char.IsControl(chars[i]))
                chars[i] = '_';
        }
        var cleaned = new string(chars).Trim(' ', '.', '_');
        if (cleaned.Length > 48)
            cleaned = cleaned[..48].TrimEnd(' ', '.', '_');
        return cleaned;
    }

    public static string BuildPrefix(string? rawLabel, DateTime localTime) =>
        BuildPrefix(rawLabel, localTime, "CaptureRack");

    public static string BuildFireWireName(string? rawLabel, DateTime? localTime) =>
        BuildFireWireName(rawLabel, localTime, new(TsLayout.Ts192));

    public static string BuildFireWireName(string? rawLabel, DateTime? localTime,
        CaptureRack.DirectShow.Windows.FireWire.MsTape.FireWireMediaFormat format) =>
        BuildPrefix(rawLabel, localTime, $"CaptureRack_{format.FilePrefix}") + format.Extension;

    public static string BuildDirectShowPreviewName(string? rawLabel, string? videoTag, int? audioBitsPerSample,
        IReadOnlyList<string>? outputExtensions)
    {
        var prefix = BuildPrefix(rawLabel, localTime: null, "CaptureRack");
        var video = string.IsNullOrWhiteSpace(videoTag) ? "Video" : videoTag;
        var audio = audioBitsPerSample is > 0 ? $"_{audioBitsPerSample}bit-PCM" : "";
        var ext = outputExtensions is { Count: > 0 }
            ? outputExtensions[0]
            : ".mkv";
        var suffix = outputExtensions is { Count: > 1 }
            ? $" ({string.Join(" / ", outputExtensions)})"
            : "";
        return $"{prefix}_DirectShow_{video}{audio}{ext}{suffix}";
    }

    private static string BuildPrefix(string? rawLabel, DateTime? localTime, string capturePrefix)
    {
        var timestamp = localTime?.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture)
            ?? "YYYYMMDD_HHmmss_fff";
        var label = SanitizeLabel(rawLabel);
        return label.Length == 0 ? $"{capturePrefix}_{timestamp}" : $"{label}_{capturePrefix}_{timestamp}";
    }

    public static string BuildMovPath(string directory, string? rawLabel, DateTime localTime,
        string videoTag, string audioTag, Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        var prefix = BuildPrefix(rawLabel, localTime);
        var formats = $"{videoTag}_{audioTag}";
        var path = Path.Combine(directory, $"{prefix}_{formats}.mov");
        for (var suffix = 2; exists(path); suffix++)
            path = Path.Combine(directory, $"{prefix}_{suffix}_{formats}.mov");
        return path;
    }
}

public enum QcEntryKind { Mov, TransportStream, Dv, RfRamp }

public sealed record QcSessionHistoryEntry(
    Guid Id,
    string FilePath,
    string FileName,
    QcEntryKind Kind,
    DateTimeOffset CheckedAt,
    string TitleKey,
    string Summary,
    TelemetrySeverity Severity,
    IReadOnlyList<string> Warnings,
    string? SummaryKey = null,
    QcStage? LastStage = null,
    QcResult? MovResult = null,
    QcCaptureLog? CaptureLog = null,
    TsFileReport? TsReport = null,
    DvFileReport? DvReport = null,
    long? RfSampleCount = null);

public static class QcWarningFormatter
{
    public static IReadOnlyList<string> BuildMovWarnings(
        QcCaptureLog? capture,
        QcResult? result,
        Func<string, string> localize,
        IFormatProvider formatProvider)
    {
        List<string> warnings = [];
        if (capture?.Expected.VideoFrames == 0 || (capture is null && result is { DecodedVideoFrames: 0 }))
            warnings.Add(localize("QcWarnNoVideo"));
        if (capture?.Expected.AudioSampleFrames == 0 || (capture is null && result is { DecodedAudioSampleFrames: 0 }))
            warnings.Add(localize("QcWarnNoAudio"));
        if (capture?.CaptureError is { } captureError)
            warnings.Add(string.Format(formatProvider, localize("QcWarnCaptureEnded"), captureError));
        if (capture is { InputModeChanged: true })
            warnings.Add(localize("QcWarnInputModeChange"));
        if (capture is { VideoSignalLosses.Count: > 0 })
            warnings.Add(string.Format(formatProvider, localize("VideoLockSummary"), capture.VideoSignalLossCount,
                capture.VideoSignalLossDuration.TotalSeconds));
        if (result is not null)
        {
            if (capture is { NoInputFrames: > 0 })
                warnings.Add(string.Format(formatProvider, localize("QcWarnNoInput"), capture.NoInputFrames));
            if (capture is { UnreadableNoInputFrames: > 0 })
                warnings.Add(string.Format(formatProvider, localize("QcWarnUnreadableNoInput"), capture.UnreadableNoInputFrames));
            if (capture is { MissingVideoFrames: > 0 })
                warnings.Add(string.Format(formatProvider, localize("QcWarnMissing"), capture.MissingVideoFrames));
            if (capture?.Timing is { } videoTiming && videoTiming.VideoCorrections + videoTiming.VideoInvalidDurations > 0)
                warnings.Add(string.Format(formatProvider, localize("QcWarnVideoTiming"), videoTiming.VideoCorrections, videoTiming.VideoInvalidDurations));
            if (capture?.Timing is { FirstAudioIssue: not null } audioTiming)
                warnings.Add(string.Format(formatProvider, localize("QcWarnAudioTiming"), audioTiming.AudioDiscontinuities, audioTiming.MaximumAudioOffsetSamples));
            if (capture is { Timing: null, TimestampRegressions: > 0 })
                warnings.Add(string.Format(formatProvider, localize("QcWarnTimestamp"), capture.TimestampRegressions));
            if (capture is { LeftClips: > 0 } or { RightClips: > 0 } ||
                capture?.ChannelClips?.Any(count => count > 0) == true)
                warnings.Add(localize("QcWarnClipping"));
            if (result.VideoFramesWithoutFieldFlags > 0)
                warnings.Add(string.Format(formatProvider, localize("QcWarnFieldFlags"), result.VideoFramesWithoutFieldFlags));
        }
        return warnings;
    }
}

public sealed class QcSessionHistory(int maxEntries = 30)
{
    private readonly List<QcSessionHistoryEntry> entries = [];

    public IReadOnlyList<QcSessionHistoryEntry> Entries => entries;

    public void Record(QcSessionHistoryEntry entry)
    {
        entries.RemoveAll(existing => existing.Id == entry.Id);
        entries.Insert(0, entry);
        if (entries.Count > maxEntries)
            entries.RemoveRange(maxEntries, entries.Count - maxEntries);
    }

    public void TransformAll(Func<QcSessionHistoryEntry, QcSessionHistoryEntry> transform)
    {
        for (var i = 0; i < entries.Count; i++)
            entries[i] = transform(entries[i]);
    }

    public QcSessionHistoryEntry? Find(Guid id) =>
        entries.FirstOrDefault(entry => entry.Id == id);

    public void Clear() => entries.Clear();
}

public static class MonitoringSeverity
{
    public static TelemetrySeverity ClassifyLumaExcursion(double? outsideLegalFraction) => outsideLegalFraction switch
    {
        null or <= 0 => TelemetrySeverity.Normal,
        < 0.05 => TelemetrySeverity.Caution,
        _ => TelemetrySeverity.Danger
    };
}

public static class QcPresentationHelper
{
    public static string OpenMediaLabelKey(string? filePath)
    {
        var ext = string.IsNullOrWhiteSpace(filePath) ? "" : Path.GetExtension(filePath);
        return ext.ToLowerInvariant() switch
        {
            ".mkv" => "OpenRecordedMkv",
            ".ts" or ".ts192" or ".m2t" => "OpenRecordedTs",
            ".dv" => "OpenRecordedDv",
            _ => "OpenRecordedMov"
        };
    }

    public static string SaveReportLabelKey(QcEntryKind kind) => kind switch
    {
        QcEntryKind.Dv => "SaveDvJson",
        _ => "SaveTsJson"
    };

    public static bool ShouldShowStatusFileName(bool recordingActive, bool qcActive, string? statusTitle) =>
        recordingActive || qcActive ||
        (!string.IsNullOrEmpty(statusTitle) &&
         (statusTitle.StartsWith("Qc", StringComparison.Ordinal) ||
          statusTitle.StartsWith("Ts", StringComparison.Ordinal) ||
          statusTitle.StartsWith("Dv", StringComparison.Ordinal)));
}

public static class CameraControlEditHelper
{
    public static bool HasPendingChanges(bool currentIsAuto, int currentValue, bool editedIsAuto, int editedValue, bool supportsAuto)
    {
        if (supportsAuto && currentIsAuto != editedIsAuto)
            return true;
        return !editedIsAuto && currentValue != editedValue;
    }

    public static bool CanResetToDefault(int? defaultValue, int editedValue, bool editedIsAuto, bool supportsManual) =>
        defaultValue is { } target && supportsManual && (editedIsAuto || editedValue != target);
}

public readonly record struct WrapItemRect(double X, double Y, double Width, double Height);

public static class WrapLayoutCalculator
{
    public static double NormalizeChildAvailableWidth(double availableWidth) =>
        double.IsInfinity(availableWidth) || double.IsNaN(availableWidth)
            ? double.PositiveInfinity
            : Math.Max(0, availableWidth);

    public static (double Width, double Height) Measure(
        IReadOnlyList<(double Width, double Height)> items,
        double availableWidth,
        double horizontalSpacing,
        double verticalSpacing)
    {
        var maxWidth = double.IsInfinity(availableWidth) || double.IsNaN(availableWidth)
            ? double.PositiveInfinity
            : Math.Max(0, availableWidth);
        var hSpacing = Math.Max(0, horizontalSpacing);
        var vSpacing = Math.Max(0, verticalSpacing);

        double lineWidth = 0;
        double lineHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;
        var hasItemOnLine = false;

        foreach (var (rawW, rawH) in items)
        {
            var w = Math.Max(0, double.IsNaN(rawW) ? 0 : rawW);
            var h = Math.Max(0, double.IsNaN(rawH) ? 0 : rawH);
            var candidateWidth = hasItemOnLine ? lineWidth + hSpacing + w : w;

            if (hasItemOnLine && candidateWidth > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + vSpacing;
                lineWidth = w;
                lineHeight = h;
                hasItemOnLine = true;
            }
            else
            {
                lineWidth = candidateWidth;
                lineHeight = Math.Max(lineHeight, h);
                hasItemOnLine = true;
            }
        }

        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;
        return (totalWidth, totalHeight);
    }

    public static WrapItemRect[] Arrange(
        IReadOnlyList<(double Width, double Height)> items,
        double finalWidth,
        double horizontalSpacing,
        double verticalSpacing)
    {
        var maxWidth = double.IsInfinity(finalWidth) || double.IsNaN(finalWidth)
            ? double.PositiveInfinity
            : Math.Max(0, finalWidth);
        var hSpacing = Math.Max(0, horizontalSpacing);
        var vSpacing = Math.Max(0, verticalSpacing);

        var rects = new WrapItemRect[items.Count];
        double x = 0;
        double y = 0;
        double lineHeight = 0;
        var hasItemOnLine = false;

        for (var i = 0; i < items.Count; i++)
        {
            var w = Math.Max(0, double.IsNaN(items[i].Width) ? 0 : items[i].Width);
            var h = Math.Max(0, double.IsNaN(items[i].Height) ? 0 : items[i].Height);
            var startX = hasItemOnLine ? x + hSpacing : 0;

            if (hasItemOnLine && startX + w > maxWidth)
            {
                x = 0;
                y += lineHeight + vSpacing;
                lineHeight = 0;
                startX = 0;
                hasItemOnLine = false;
            }

            rects[i] = new WrapItemRect(startX, y, w, h);
            x = startX + w;
            lineHeight = Math.Max(lineHeight, h);
            hasItemOnLine = true;
        }

        return rects;
    }
}

internal sealed record DirectShowFormatSnapshot(
    string? PinId,
    bool IsInfrared,
    int? Width,
    int? Height,
    double? FramesPerSecond,
    string? Encoding,
    int Index);

internal static class DirectShowSettingsResolver
{
    public static string? ResolveRefreshTargetDeviceId(
        DirectShowSettings saved,
        string? currentlySelectedDeviceId,
        bool isFallbackSelection) =>
        isFallbackSelection ? saved.DeviceId : currentlySelectedDeviceId ?? saved.DeviceId;

    public static int? FindMatchingVideoFormatIndex(
        DirectShowSettings saved,
        IReadOnlyList<DirectShowFormatSnapshot> candidates)
    {
        if (candidates.Count == 0)
            return null;

        bool MatchesMetadata(DirectShowFormatSnapshot f) =>
            (saved.PinId is null || string.Equals(f.PinId, saved.PinId, StringComparison.OrdinalIgnoreCase)) &&
            (saved.IsInfrared is null || f.IsInfrared == saved.IsInfrared) &&
            (saved.Width is null || f.Width == saved.Width) &&
            (saved.Height is null || f.Height == saved.Height) &&
            (saved.FramesPerSecond is null || f.FramesPerSecond == saved.FramesPerSecond) &&
            (saved.Encoding is null || string.Equals(f.Encoding, saved.Encoding, StringComparison.OrdinalIgnoreCase));

        var hasMetadata = saved.Width is not null || saved.Height is not null ||
            saved.Encoding is not null || saved.FramesPerSecond is not null ||
            saved.PinId is not null || saved.IsInfrared is not null;

        if (saved.VideoFormatIndex is { } savedIdx && hasMetadata &&
            candidates.FirstOrDefault(f => f.Index == savedIdx && MatchesMetadata(f)) is { } exact)
            return exact.Index;

        if (hasMetadata && candidates.FirstOrDefault(MatchesMetadata) is { } fullMetadata)
            return fullMetadata.Index;

        if (saved.Width is not null && saved.Height is not null && saved.Encoding is not null &&
            candidates.FirstOrDefault(f => f.Width == saved.Width && f.Height == saved.Height &&
                string.Equals(f.Encoding, saved.Encoding, StringComparison.OrdinalIgnoreCase)) is { } loose)
            return loose.Index;

        if (!hasMetadata && saved.VideoFormatIndex is { } indexOnly &&
            candidates.FirstOrDefault(f => f.Index == indexOnly) is { } byIndex)
            return byIndex.Index;

        return null;
    }

    public static DirectShowSettings Export(
        DirectShowSettings saved,
        string? selectedDeviceId,
        bool isFallbackSelection,
        string? loadedCapabilitiesDeviceId,
        DirectShowFormatSnapshot? video,
        int? audioFormatIndex)
    {
        if (string.IsNullOrWhiteSpace(selectedDeviceId) || isFallbackSelection)
            return saved;

        if (!string.IsNullOrWhiteSpace(loadedCapabilitiesDeviceId) &&
            string.Equals(loadedCapabilitiesDeviceId, selectedDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return new DirectShowSettings
            {
                DeviceId = selectedDeviceId,
                PinId = video?.PinId,
                IsInfrared = video?.IsInfrared,
                Width = video?.Width,
                Height = video?.Height,
                FramesPerSecond = video?.FramesPerSecond,
                Encoding = video?.Encoding,
                VideoFormatIndex = video?.Index,
                AudioFormatIndex = audioFormatIndex
            };
        }

        if (string.Equals(saved.DeviceId, selectedDeviceId, StringComparison.OrdinalIgnoreCase))
            return saved;

        return new DirectShowSettings { DeviceId = selectedDeviceId };
    }
}

internal static class FireWireSettingsResolver
{
    public static string? ResolveRefreshTargetDeviceId(
        FireWireSettings saved,
        string? currentlySelectedDeviceId,
        bool isFallbackSelection) =>
        isFallbackSelection ? saved.DeviceId : currentlySelectedDeviceId ?? saved.DeviceId;

    public static FireWireSettings Export(
        FireWireSettings saved,
        string? selectedDeviceId,
        bool isFallbackSelection) =>
        string.IsNullOrWhiteSpace(selectedDeviceId) || isFallbackSelection
            ? saved
            : new FireWireSettings { DeviceId = selectedDeviceId };
}

public static class FullscreenRenderPolicy
{
    public static bool ShouldPauseMainWindowRendering(bool fullscreenActive, bool sameDisplayAsMainWindow) =>
        fullscreenActive && sameDisplayAsMainWindow;

    public static bool ShouldRenderVideoScopes(bool pauseMainWindowRendering, bool layoutShowsScopes) =>
        !pauseMainWindowRendering && layoutShowsScopes;

    public static bool ShouldUpdateMainWindowMeters(bool pauseMainWindowRendering) =>
        !pauseMainWindowRendering;

    public static bool ShouldRunMainWindowAnimations(bool pauseMainWindowRendering) =>
        !pauseMainWindowRendering;

    public static (double TopHeight, double BottomHeight) ComputeNtsc480GuideHeights(double surfaceHeight)
    {
        if (double.IsNaN(surfaceHeight) || double.IsInfinity(surfaceHeight) || surfaceHeight <= 0)
            return (0, 0);

        return (Math.Max(2, surfaceHeight * 4.0 / 486.0), Math.Max(1, surfaceHeight * 2.0 / 486.0));
    }

    public static (double Width, double Height) ComputePreviewSurfaceSize(
        double containerWidth,
        double containerHeight,
        double aspectRatio,
        double padding)
    {
        if (double.IsNaN(containerWidth) || double.IsInfinity(containerWidth) || containerWidth <= 0 ||
            double.IsNaN(containerHeight) || double.IsInfinity(containerHeight) || containerHeight <= 0)
            return (0, 0);

        var safeAspect = double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio) || aspectRatio <= 0
            ? 4d / 3
            : aspectRatio;
        var pad = double.IsNaN(padding) || double.IsInfinity(padding) ? 0 : Math.Max(0, padding);
        var availW = Math.Max(0, containerWidth - pad * 2);
        var availH = Math.Max(0, containerHeight - pad * 2);
        if (availW <= 0 || availH <= 0)
            return (0, 0);

        var height = Math.Min(availW / safeAspect, availH);
        var width = height * safeAspect;
        return (Math.Max(0, width), Math.Max(0, height));
    }
}

public sealed class BroadcastMeterBallistics
{
    private const double FloorDb = -60.0;
    private const double DecayPerFrame = 0.42;
    private const double PeakHoldDecayPerFrame = 0.22;
    private const int PeakHoldFrames = 72;

    private double leftTargetDb = FloorDb;
    private double rightTargetDb = FloorDb;
    private double leftDisplayedDb = FloorDb;
    private double rightDisplayedDb = FloorDb;
    private double leftPeakHoldDb = FloorDb;
    private double rightPeakHoldDb = FloorDb;
    private int leftHoldCounter;
    private int rightHoldCounter;

    private long leftRawClips;
    private long rightRawClips;
    private long leftClipBaseline;
    private long rightClipBaseline;

    public double LeftNormalized => Normalize(leftDisplayedDb);
    public double RightNormalized => Normalize(rightDisplayedDb);
    public double LeftPeakNormalized => Normalize(leftPeakHoldDb);
    public double RightPeakNormalized => Normalize(rightPeakHoldDb);
    public long VisibleLeftClips => Math.Max(0, leftRawClips - leftClipBaseline);
    public long VisibleRightClips => Math.Max(0, rightRawClips - rightClipBaseline);

    public void Reset()
    {
        leftTargetDb = rightTargetDb = FloorDb;
        leftDisplayedDb = rightDisplayedDb = FloorDb;
        leftPeakHoldDb = rightPeakHoldDb = FloorDb;
        leftHoldCounter = rightHoldCounter = 0;
        leftRawClips = rightRawClips = 0;
        leftClipBaseline = rightClipBaseline = 0;
    }

    public void UpdateInput(double leftPeakLinear, double rightPeakLinear, long totalLeftClips, long totalRightClips)
    {
        leftTargetDb = leftPeakLinear > 0 ? Math.Clamp(20 * Math.Log10(leftPeakLinear), FloorDb, 0) : FloorDb;
        rightTargetDb = rightPeakLinear > 0 ? Math.Clamp(20 * Math.Log10(rightPeakLinear), FloorDb, 0) : FloorDb;
        if (totalLeftClips < leftClipBaseline)
            leftClipBaseline = 0;
        if (totalRightClips < rightClipBaseline)
            rightClipBaseline = 0;
        leftRawClips = totalLeftClips;
        rightRawClips = totalRightClips;
    }

    public void ResetLeftClipLatch() => leftClipBaseline = leftRawClips;

    public void ResetRightClipLatch() => rightClipBaseline = rightRawClips;

    public void StepFrame()
    {
        StepChannel(leftTargetDb, ref leftDisplayedDb, ref leftPeakHoldDb, ref leftHoldCounter);
        StepChannel(rightTargetDb, ref rightDisplayedDb, ref rightPeakHoldDb, ref rightHoldCounter);
    }

    private static void StepChannel(double targetDb, ref double displayedDb, ref double peakHoldDb, ref int holdCounter)
    {
        displayedDb = targetDb >= displayedDb
            ? targetDb
            : Math.Max(targetDb, displayedDb - DecayPerFrame);

        if (displayedDb >= peakHoldDb)
        {
            peakHoldDb = displayedDb;
            holdCounter = PeakHoldFrames;
        }
        else if (holdCounter > 0)
        {
            holdCounter--;
        }
        else
        {
            peakHoldDb = Math.Max(displayedDb, peakHoldDb - PeakHoldDecayPerFrame);
        }
    }

    private static double Normalize(double db) => Math.Clamp((60.0 + db) / 60.0, 0.0, 1.0);
}

public enum AudioMeterDisplayMode
{
    Auto = 0,
    All = 1,
    MonitoredPair = 2
}

public static class MultiChannelMeterPolicy
{
    public const double ActivityThresholdLinear = 0.000251188643150958; // -72 dBFS
    public const int DefaultAttackFrames = 9;    // ~150 ms at 60 fps
    public const int DefaultHoldFrames = 600;    // ~10 s at 60 fps

    public static int[] ComputeVisibleChannels(
        int totalChannels,
        AudioMeterDisplayMode displayMode,
        AudioMonitorRoute route,
        ReadOnlySpan<bool> activePairs,
        ReadOnlySpan<bool> clippedPairs)
    {
        var safeChannels = Math.Clamp(totalChannels, 1, AudioMonitorRoute.MaxSupportedChannels);
        if (safeChannels == 1)
            return [0];
        if (safeChannels == 2)
            return [0, 1];

        var pairCount = (safeChannels + 1) / 2;
        var clampedRoute = route.ClampForChannels(safeChannels);

        if (displayMode == AudioMeterDisplayMode.All)
        {
            var all = new int[safeChannels];
            for (var i = 0; i < safeChannels; i++)
                all[i] = i;
            return all;
        }

        var visible = new List<int>(safeChannels);
        if (displayMode == AudioMeterDisplayMode.MonitoredPair && clampedRoute.Mode == AudioMonitorMode.Pair)
        {
            AppendPair(visible, clampedRoute.PairIndex, safeChannels);
            return [.. visible];
        }

        // Auto mode (and MonitoredPair fallback when route is Downmix):
        // Always include Ch 1-2, the monitored pair (if Pair mode), and every active or clipped pair.
        for (var pair = 0; pair < pairCount; pair++)
        {
            var isPrimary = pair == 0;
            var isMonitored = clampedRoute.Mode == AudioMonitorMode.Pair && clampedRoute.PairIndex == pair;
            var isActive = pair < activePairs.Length && activePairs[pair];
            var isClipped = pair < clippedPairs.Length && clippedPairs[pair];
            if (isPrimary || isMonitored || isActive || isClipped)
                AppendPair(visible, pair, safeChannels);
        }

        return [.. visible];
    }

    public static double ComputePanelWidth(int visibleChannelCount, bool multiChannel = false) => visibleChannelCount switch
    {
        <= 2 => multiChannel ? 104 : 86,
        <= 4 => 124,
        <= 6 => 152,
        <= 8 => 184,
        <= 12 => 248,
        _ => 312
    };

    private static void AppendPair(List<int> list, int pairIndex, int totalChannels)
    {
        var left = pairIndex * 2;
        if (left < totalChannels)
            list.Add(left);
        var right = left + 1;
        if (right < totalChannels)
            list.Add(right);
    }
}

public sealed class MultiChannelActivityTracker(
    int attackFrames = MultiChannelMeterPolicy.DefaultAttackFrames,
    int holdFrames = MultiChannelMeterPolicy.DefaultHoldFrames)
{
    private readonly int attack = Math.Max(1, attackFrames);
    private readonly int hold = Math.Max(1, holdFrames);
    private readonly int[] aboveCount = new int[AudioMonitorRoute.MaxSupportedPairs];
    private readonly int[] holdRemaining = new int[AudioMonitorRoute.MaxSupportedPairs];
    private readonly bool[] pairActive = new bool[AudioMonitorRoute.MaxSupportedPairs];

    public void Reset()
    {
        Array.Clear(aboveCount);
        Array.Clear(holdRemaining);
        Array.Clear(pairActive);
    }

    public void Observe(StereoLevels levels)
    {
        var channels = Math.Clamp(levels.ChannelCount, 1, AudioMonitorRoute.MaxSupportedChannels);
        var pairs = (channels + 1) / 2;
        for (var p = 0; p < pairs; p++)
        {
            var left = levels.PeakForChannel(p * 2);
            var right = p * 2 + 1 < channels ? levels.PeakForChannel(p * 2 + 1) : 0;
            var peak = Math.Max(left, right);
            if (peak > MultiChannelMeterPolicy.ActivityThresholdLinear)
            {
                aboveCount[p]++;
                if (aboveCount[p] >= attack)
                {
                    pairActive[p] = true;
                    holdRemaining[p] = hold;
                }
            }
            else
            {
                aboveCount[p] = 0;
                if (pairActive[p])
                {
                    if (holdRemaining[p] > 0)
                        holdRemaining[p]--;
                    if (holdRemaining[p] == 0)
                        pairActive[p] = false;
                }
            }
        }
        for (var p = pairs; p < pairActive.Length; p++)
        {
            aboveCount[p] = 0;
            holdRemaining[p] = 0;
            pairActive[p] = false;
        }
    }

    public int ActivePairMask(int totalChannels)
    {
        var pairs = (Math.Clamp(totalChannels, 1, AudioMonitorRoute.MaxSupportedChannels) + 1) / 2;
        var mask = 1;
        for (var p = 0; p < pairs; p++)
        {
            if (pairActive[p])
                mask |= 1 << p;
        }
        return mask;
    }

    public ReadOnlySpan<bool> ActivePairs => pairActive;
}

public sealed class MultiChannelMeterBallistics
{
    private const double FloorDb = -60.0;
    private const double DecayPerFrame = 0.42;
    private const double PeakHoldDecayPerFrame = 0.22;
    private const int PeakHoldFrames = 72;

    private readonly double[] targetDb = new double[AudioMonitorRoute.MaxSupportedChannels];
    private readonly double[] displayedDb = new double[AudioMonitorRoute.MaxSupportedChannels];
    private readonly double[] peakHoldDb = new double[AudioMonitorRoute.MaxSupportedChannels];
    private readonly int[] holdCounter = new int[AudioMonitorRoute.MaxSupportedChannels];
    private readonly long[] rawClips = new long[AudioMonitorRoute.MaxSupportedChannels];
    private readonly long[] clipBaseline = new long[AudioMonitorRoute.MaxSupportedChannels];

    public int ChannelCount { get; private set; } = 2;

    public MultiChannelMeterBallistics() => Reset();

    public void Reset()
    {
        ChannelCount = 2;
        Array.Fill(targetDb, FloorDb);
        Array.Fill(displayedDb, FloorDb);
        Array.Fill(peakHoldDb, FloorDb);
        Array.Clear(holdCounter);
        Array.Clear(rawClips);
        Array.Clear(clipBaseline);
    }

    public void UpdateInput(StereoLevels audio)
    {
        var count = Math.Clamp(audio.ChannelCount, 1, AudioMonitorRoute.MaxSupportedChannels);
        ChannelCount = count;
        for (var ch = 0; ch < count; ch++)
        {
            var peak = audio.PeakForChannel(ch);
            var clips = audio.ClipsForChannel(ch);
            targetDb[ch] = peak > 0 ? Math.Clamp(20 * Math.Log10(peak), FloorDb, 0) : FloorDb;
            if (clips < clipBaseline[ch])
                clipBaseline[ch] = 0;
            rawClips[ch] = clips;
        }
        for (var ch = count; ch < AudioMonitorRoute.MaxSupportedChannels; ch++)
        {
            targetDb[ch] = FloorDb;
            rawClips[ch] = 0;
            clipBaseline[ch] = 0;
        }
    }

    public void StepFrame()
    {
        for (var ch = 0; ch < ChannelCount; ch++)
        {
            displayedDb[ch] = targetDb[ch] >= displayedDb[ch]
                ? targetDb[ch]
                : Math.Max(targetDb[ch], displayedDb[ch] - DecayPerFrame);

            if (displayedDb[ch] >= peakHoldDb[ch])
            {
                peakHoldDb[ch] = displayedDb[ch];
                holdCounter[ch] = PeakHoldFrames;
            }
            else if (holdCounter[ch] > 0)
            {
                holdCounter[ch]--;
            }
            else
            {
                peakHoldDb[ch] = Math.Max(displayedDb[ch], peakHoldDb[ch] - PeakHoldDecayPerFrame);
            }
        }
    }

    public double NormalizedLevel(int channel) =>
        channel >= 0 && channel < AudioMonitorRoute.MaxSupportedChannels ? Normalize(displayedDb[channel]) : 0;

    public double NormalizedPeakHold(int channel) =>
        channel >= 0 && channel < AudioMonitorRoute.MaxSupportedChannels ? Normalize(peakHoldDb[channel]) : 0;

    public long VisibleClips(int channel) =>
        channel >= 0 && channel < AudioMonitorRoute.MaxSupportedChannels ? Math.Max(0, rawClips[channel] - clipBaseline[channel]) : 0;

    public void ResetClipLatch(int channel)
    {
        if (channel >= 0 && channel < AudioMonitorRoute.MaxSupportedChannels)
            clipBaseline[channel] = rawClips[channel];
    }

    public void FillClippedPairs(Span<bool> clippedPairs)
    {
        var pairs = Math.Min(clippedPairs.Length, (ChannelCount + 1) / 2);
        clippedPairs.Clear();
        for (var p = 0; p < pairs; p++)
        {
            var left = VisibleClips(p * 2) > 0;
            var right = p * 2 + 1 < ChannelCount && VisibleClips(p * 2 + 1) > 0;
            clippedPairs[p] = left || right;
        }
    }

    private static double Normalize(double db) => Math.Clamp((60.0 + db) / 60.0, 0.0, 1.0);
}

public sealed class RfPeakBallistics
{
    private const double DecayPerFrame = 0.022;
    private const double PeakHoldDecayPerFrame = 0.012;
    private const int PeakHoldFrames = 54; // ~0.9 s at 60 fps

    private double targetHigh;
    private double targetLow;
    private double displayedHigh;
    private double displayedLow;
    private double holdHigh;
    private double holdLow;
    private int holdHighCounter;
    private int holdLowCounter;

    private long rawPositiveClips;
    private long rawNegativeClips;
    private long positiveClipBaseline;
    private long negativeClipBaseline;

    public double PositiveNormalized => displayedHigh;
    public double NegativeNormalized => displayedLow;
    public double PositivePeakHold => holdHigh;
    public double NegativePeakHold => holdLow;
    public long VisiblePositiveClips => Math.Max(0, rawPositiveClips - positiveClipBaseline);
    public long VisibleNegativeClips => Math.Max(0, rawNegativeClips - negativeClipBaseline);

    public void Reset()
    {
        targetHigh = targetLow = displayedHigh = displayedLow = holdHigh = holdLow = 0;
        holdHighCounter = holdLowCounter = 0;
        rawPositiveClips = rawNegativeClips = positiveClipBaseline = negativeClipBaseline = 0;
    }

    public (bool NewPositiveClip, bool NewNegativeClip) UpdateInput(
        int minCode, int maxCode, int fullScale, long totalPositiveClips, long totalNegativeClips)
    {
        var safeScale = Math.Max(2, fullScale);
        targetHigh = Math.Clamp((double)Math.Max(0, maxCode) / (safeScale - 1), 0.0, 1.0);
        targetLow = Math.Clamp((double)Math.Max(0, -minCode) / safeScale, 0.0, 1.0);

        if (totalPositiveClips < positiveClipBaseline)
            positiveClipBaseline = 0;
        if (totalNegativeClips < negativeClipBaseline)
            negativeClipBaseline = 0;

        var newPos = totalPositiveClips > rawPositiveClips;
        var newNeg = totalNegativeClips > rawNegativeClips;
        rawPositiveClips = totalPositiveClips;
        rawNegativeClips = totalNegativeClips;

        // Immediately attack upwards on incoming measurement so fast peaks never miss.
        if (targetHigh >= displayedHigh)
        {
            displayedHigh = targetHigh;
            if (displayedHigh >= holdHigh)
            {
                holdHigh = displayedHigh;
                holdHighCounter = PeakHoldFrames;
            }
        }
        if (targetLow >= displayedLow)
        {
            displayedLow = targetLow;
            if (displayedLow >= holdLow)
            {
                holdLow = displayedLow;
                holdLowCounter = PeakHoldFrames;
            }
        }

        return (newPos, newNeg);
    }

    public void StepFrame()
    {
        StepRail(targetHigh, ref displayedHigh, ref holdHigh, ref holdHighCounter);
        StepRail(targetLow, ref displayedLow, ref holdLow, ref holdLowCounter);
    }

    public void ResetPositiveClipLatch() => positiveClipBaseline = rawPositiveClips;
    public void ResetNegativeClipLatch() => negativeClipBaseline = rawNegativeClips;

    private static void StepRail(double target, ref double displayed, ref double peakHold, ref int holdCounter)
    {
        displayed = target >= displayed
            ? target
            : Math.Max(target, displayed - DecayPerFrame);

        if (displayed >= peakHold)
        {
            peakHold = displayed;
            holdCounter = PeakHoldFrames;
        }
        else if (holdCounter > 0)
        {
            holdCounter--;
        }
        else
        {
            peakHold = Math.Max(displayed, peakHold - PeakHoldDecayPerFrame);
        }
    }
}

public static class RfDeviceSelectionHelper
{
    public const string PlaybackDeviceKey = "rf:playback";

    public static string BuildDddKey(string instanceId) => $"ddd:{instanceId}";
    public static string BuildRtlKey(uint index, string name) => $"rtl:{index}:{name}";

    public static int ResolveSelectionIndex(IReadOnlyList<string> candidateKeys, string? preferredKey, string? savedKey)
    {
        if (candidateKeys.Count == 0)
            return -1;

        if (!string.IsNullOrWhiteSpace(preferredKey))
        {
            var idx = FindKey(candidateKeys, preferredKey);
            if (idx >= 0)
                return idx;
        }

        if (!string.IsNullOrWhiteSpace(savedKey))
        {
            var idx = FindKey(candidateKeys, savedKey);
            if (idx >= 0)
                return idx;
        }

        return 0;
    }

    private static int FindKey(IReadOnlyList<string> keys, string target)
    {
        for (var i = 0; i < keys.Count; i++)
        {
            if (string.Equals(keys[i], target, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}

public static class EncodingUxHelper
{
    public const int MinBitrateKbps = 1;
    public const int MaxBitrateKbps = 1_000_000;

    public static (bool IsValid, int? ParsedKbps) ValidateBitrateText(string? rawText)
    {
        var trimmed = rawText?.Trim() ?? "";
        if (trimmed.Length == 0)
            return (true, null);

        if (int.TryParse(trimmed, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var kbps) &&
            kbps is >= MinBitrateKbps and <= MaxBitrateKbps)
            return (true, kbps);

        return (false, null);
    }

    public static bool RequiresProgressiveDeinterlaceWarning(
        CaptureRack.Recording.RecordingOutput output,
        bool? sourceIsInterlaced,
        bool softwareDeinterlaceEnabled) =>
        output is CaptureRack.Recording.RecordingOutput.H265 or CaptureRack.Recording.RecordingOutput.Av1 &&
        sourceIsInterlaced == true &&
        !softwareDeinterlaceEnabled;

    public static double? EstimateSoftwareBytesPerSecond(
        CaptureRack.Recording.RecordingOutput output,
        int? videoKbps,
        uint audioChannels,
        CaptureAudioFormat audioFormat)
    {
        if (output is CaptureRack.Recording.RecordingOutput.Native or CaptureRack.Recording.RecordingOutput.Ffv1)
            return null;
        if (videoKbps is not >= MinBitrateKbps)
            return null;

        // Target video bitrate + lossless PCM / FLAC audio ceiling (48 kHz) + ~3% container overhead.
        var videoBytesPerSec = videoKbps.Value * 1000.0 / 8.0;
        var audioBytesPerSec = 48_000.0 * CaptureAudio.BytesPerFrame(audioFormat, Math.Max(2u, audioChannels));
        return (videoBytesPerSec + audioBytesPerSec) * 1.03;
    }
}
