using System.Text;
using System.Text.Json;

namespace CaptureRack.QualityControl;

public sealed record NativeCaptureFormat(string Pin, string Encoding, Guid Subtype, int? Width, int? Height,
    long? FrameInterval100ns, string? FieldOrder, int? SampleRate, int? Channels, int? BitsPerSample);

public sealed record NativeCaptureCounts(long ReceivedPackets, long WrittenPackets, long WrittenBytes,
    long MissingStartTimes, long RepeatedStartTimes, long RegressingStartTimes, long Discontinuities);

public enum NativeContainerStatus
{
    Trying, Verifying, PathExists, MuxFailed, SourceUnverifiable, CandidateUnverifiable, SignatureMismatch,
    IoFailed, ToolsUnavailable, Selected
}

public sealed record NativeContainerAttempt(string Container, NativeContainerStatus Status, string? Detail = null);

public sealed record NativeCaptureReport(
    string FileName, long FileBytes, string? Device, DateTimeOffset StartedUtc, DateTimeOffset StoppedUtc,
    NativeCaptureFormat Video, NativeCaptureFormat? Audio, NativeCaptureCounts VideoCounts, NativeCaptureCounts? AudioCounts,
    long? ExpectedVideoFrames, long PcmSampleFrames, IReadOnlyList<NativeContainerAttempt> ContainerAttempts, string? Error)
{
    public long? DriverDroppedVideoFrames { get; init; }

    public static string PathFor(string mediaPath) => mediaPath + ".recording.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public async Task SaveAsync(string mediaPath)
    {
        var report = PathFor(mediaPath);
        var pending = $"{report}.{Guid.NewGuid():N}.partial";
        try
        {
            var json = JsonSerializer.Serialize(this, Options).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
            await File.WriteAllTextAsync(pending, json, new UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(pending, report);
        }
        finally
        {
            if (File.Exists(pending))
                File.Delete(pending);
        }
    }

    internal static async Task<NativeCaptureReport?> ReadAsync(string mediaPath, CancellationToken cancellation)
    {
        var path = PathFor(mediaPath);
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        var report = await JsonSerializer.DeserializeAsync<NativeCaptureReport>(stream, Options, cancellation)
            .ConfigureAwait(false);
        if (report is null || !string.Equals(report.FileName, Path.GetFileName(mediaPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            report.Video is null || report.VideoCounts is null || report.ContainerAttempts is null || report.FileBytes <= 0)
            throw new InvalidDataException("The recording report does not match the selected media file.");
        return report;
    }
}
