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

    IReadOnlyList<TelemetryMetric> BuildTelemetry(
        CaptureStats? stats,
        long previewSkippedFrames,
        Func<string, string> localize,
        IFormatProvider formatProvider);
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

    public IReadOnlyList<TelemetryMetric> BuildTelemetry(
        CaptureStats? stats,
        long previewSkippedFrames,
        Func<string, string> localize,
        IFormatProvider formatProvider)
    {
        return BuildVideoTelemetry(stats, previewSkippedFrames, localize, formatProvider);
    }

    internal static IReadOnlyList<TelemetryMetric> BuildVideoTelemetry(CaptureStats? stats, long previewSkippedFrames,
        Func<string, string> localize, IFormatProvider formatProvider)
    {
        var valid = stats?.VideoFrames ?? 0;
        var noInput = stats?.NoInputFrames ?? 0;
        var missing = stats?.MissingVideoFrames ?? 0;
        var skipped = Math.Max(0, previewSkippedFrames);
        var audio = stats?.AudioPackets ?? 0;
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

    public IReadOnlyList<TelemetryMetric> BuildTelemetry(CaptureStats? stats, long previewSkippedFrames,
        Func<string, string> localize, IFormatProvider formatProvider) =>
        DeckLinkWorkspaceModule.BuildVideoTelemetry(stats, previewSkippedFrames, localize, formatProvider);
}

public sealed class PlannedWorkspaceModule(
    CaptureSourceDescriptor descriptor,
    Func<Func<string, string>, TelemetryMetric[]> telemetryFactory) : ICaptureWorkspaceModule
{
    public CaptureSourceDescriptor Descriptor { get; } = descriptor;

    public IReadOnlyList<TelemetryMetric> BuildTelemetry(
        CaptureStats? stats,
        long previewSkippedFrames,
        Func<string, string> localize,
        IFormatProvider formatProvider) =>
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
            new PlannedWorkspaceModule(
                new("windows.directshow", CaptureBackendCategory.Video,
                    "BackendDirectShowName", "BackendDirectShowDesc", IsImplementedInUi: false,
                    new(true, true, true, true, true, true, true, false, true),
                    IconGlyph: "\uE722",
                    CliCommandHint: "CaptureRack.Probe.exe directshow list"),
                loc =>
                [
                    new("ds-state", loc("SourceRackStatusLabel"), loc("SourceStatusPlanned"), TelemetrySeverity.Info),
                    new("ds-frames", loc("MetricValidFrames"), "—"),
                    new("ds-dropped", loc("MetricMissing"), "—")
                ]),
            new PlannedWorkspaceModule(
                new("windows.firewire", CaptureBackendCategory.FireWire,
                    "BackendFireWireName", "BackendFireWireDesc", IsImplementedInUi: false,
                    new(false, false, false, false, true, false, false, true, false),
                    IconGlyph: "\uE88E",
                    CliCommandHint: "CaptureRack.Probe.exe firewire list"),
                loc =>
                [
                    new("ts-packets", loc("TsMetricPackets"), "—"),
                    new("ts-sync", loc("TsMetricSync"), "—"),
                    new("ts-cc", loc("TsMetricContinuity"), "—")
                ]),
            new PlannedWorkspaceModule(
                new("windows.signalcapture", CaptureBackendCategory.SignalCapture,
                    "BackendRfName", "BackendRfDesc", IsImplementedInUi: false,
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

    public static string BuildPrefix(string? rawLabel, DateTime localTime)
    {
        var timestamp = localTime.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture);
        var label = SanitizeLabel(rawLabel);
        return label.Length == 0 ? $"CaptureRack_{timestamp}" : $"{label}_CaptureRack_{timestamp}";
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

public enum QcEntryKind { Mov, TransportStream }

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
    TsFileReport? TsReport = null);

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
            if (capture is { LeftClips: > 0 } or { RightClips: > 0 })
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
