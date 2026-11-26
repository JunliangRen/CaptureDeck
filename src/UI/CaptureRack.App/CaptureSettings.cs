using System.Runtime.InteropServices;
using System.Text.Json;
using CaptureRack.DeckLink;
using CaptureRack.DeviceControl;
using CaptureRack.Recording;

namespace CaptureRack.App;

internal sealed record CaptureSettings
{
    public string CaptureBackendId { get; init; } = DeckLinkBackend.BackendId;
    public DeckLinkSettings DeckLink { get; init; } = new();
    public DirectShowSettings DirectShow { get; init; } = new();
    public FireWireSettings FireWire { get; init; } = new();
    public string TestSignalScenario { get; init; } = "Normal";
    public ControlDeviceAddress? ControlDevice { get; init; }
    public string OutputFolder { get; init; } = "";
    public string RecordingLabel { get; init; } = "";
    public RecordingEncoding RecordingEncoding { get; init; } = new();
    public string? LanguageOverride { get; init; }
    public string Theme { get; init; } = "System";
    public string WorkspaceLayout { get; init; } = "Split";
    public double SplitPreviewRatio { get; init; } = 0.63;
    public bool InspectorPinned { get; init; } = true;
    public bool ShowSafeArea { get; init; }
    public bool ShowNtsc480Guide { get; init; }
    public string PreviewDeinterlace { get; init; } = "BobWeave";
    public string PreviewAspectRatio { get; init; } = "Auto";
    public string PreviewColorMatrix { get; init; } = "Auto";
    public string ScopeWaveformMode { get; init; } = "Luma";
    public string ScopeLumaScale { get; init; } = "Code";
    public bool AudioMonitorEnabled { get; init; }
    public double AudioMonitorVolume { get; init; } = 0.8;
}

internal sealed record DirectShowSettings
{
    public string? DeviceId { get; init; }
    public string? PinId { get; init; }
    public bool? IsInfrared { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FramesPerSecond { get; init; }
    public string? Encoding { get; init; }
    public int? VideoFormatIndex { get; init; }
    public int? AudioFormatIndex { get; init; }
}

internal sealed record FireWireSettings
{
    public string? DeviceId { get; init; }
}

internal sealed record DeckLinkSettings
{
    public long? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public uint? VideoSource { get; init; }
    public uint? AudioSource { get; init; }
    public bool ModeSelected { get; init; }
    public uint? ModeId { get; init; }
    public uint? VideoFormat { get; init; }
    public uint? AudioFormat { get; init; }
    public uint AudioChannels { get; init; } = CaptureAudio.Channels;
    public string AnalogNtscIre { get; init; } = "Driver";
    public string AnalogComponentLevel { get; init; } = "Driver";
    public string AnalogAudioLevel { get; init; } = "Driver";
    public bool AnalogAudioGainOverride { get; init; }
    public double AnalogAudioGainLeftDb { get; init; }
    public double AnalogAudioGainRightDb { get; init; }
    public bool AnalogAudioGainLinked { get; init; } = true;
    public VancInputOptions VancInput { get; init; } = new();
}

internal static class CaptureSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaptureRack", "settings.json");

    public static CaptureSettings Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            return Read(stream);
        }
        catch (FileNotFoundException)
        {
            return new();
        }
        catch (DirectoryNotFoundException)
        {
            return new();
        }
    }

    internal static CaptureSettings Read(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(nameof(CaptureSettings.DeckLink), out var deckLink) ||
            deckLink.ValueKind != JsonValueKind.Object)
            throw new JsonException("Missing or invalid DeckLink settings.");

        var settings = root.Deserialize<CaptureSettings>() ?? throw new JsonException("Empty settings.");
        return settings.RecordingEncoding is { } encoding && Enum.IsDefined(encoding.Output)
            ? settings : settings with { RecordingEncoding = new() };
    }

    internal static string Serialize(CaptureSettings settings) =>
        JsonSerializer.Serialize(settings, JsonOptions).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";

    public static void Save(CaptureSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, Serialize(settings));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong availableBytes,
        out ulong totalBytes, out ulong freeBytes);

    public static ulong? AvailableBytes(string directory) =>
        DiskSpace(directory)?.AvailableBytes;

    public static (ulong AvailableBytes, ulong TotalBytes)? DiskSpace(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            return null;

        var path = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return GetDiskFreeSpaceEx(path, out var available, out var total, out _) ? (available, total) : null;
    }
}
