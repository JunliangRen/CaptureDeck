using System.Runtime.InteropServices;
using System.Text.Json;
using CaptureDeck.DeckLink;
using CaptureDeck.DeviceControl;

namespace CaptureDeck.App;

internal sealed record CaptureSettings
{
    public string CaptureBackendId { get; init; } = DeckLinkBackend.BackendId;
    public DeckLinkSettings DeckLink { get; init; } = new();
    public ControlDeviceAddress? ControlDevice { get; init; }
    public string OutputFolder { get; init; } = "";
    public string? LanguageOverride { get; init; }
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
}

internal static class CaptureSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaptureDeck", "settings.json");

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
        var settings = root.Deserialize<CaptureSettings>() ?? throw new JsonException("Empty settings.");
        if (!root.TryGetProperty(nameof(CaptureSettings.DeckLink), out _))
            return settings with { DeckLink = root.Deserialize<DeckLinkSettings>()! };

        return settings.DeckLink is not null ? settings : throw new JsonException("Missing DeckLink settings.");
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

    public static ulong? AvailableBytes(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            return null;

        var path = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return GetDiskFreeSpaceEx(path, out var bytes, out _, out _) ? bytes : null;
    }
}
