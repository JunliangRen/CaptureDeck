using System.Runtime.InteropServices;
using System.Text.Json;

namespace CaptureDeck.App;

internal sealed record CaptureSettings
{
    public long? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public uint? VideoSource { get; init; }
    public uint? AudioSource { get; init; }
    public bool ModeSelected { get; init; }
    public uint? ModeId { get; init; }
    public uint? VideoFormat { get; init; }
    public uint? AudioFormat { get; init; }
    public string OutputFolder { get; init; } = "";
    public string? LanguageOverride { get; init; }
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
            return JsonSerializer.Deserialize<CaptureSettings>(stream) ?? throw new JsonException("Empty settings.");
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

    public static void Save(CaptureSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions).Replace("\n", "\r\n", StringComparison.Ordinal);
            File.WriteAllText(temporary, json + "\r\n");
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
