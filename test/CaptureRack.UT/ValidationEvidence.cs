using System.Text.Json;

namespace CaptureRack.UT;

internal static class ValidationEvidence
{
    internal static void Save(string name, object data)
    {
        if (Environment.GetEnvironmentVariable("CAPTURERACK_VALIDATION_OUTPUT") is { Length: > 0 } directory)
            File.WriteAllText(Path.Combine(directory, $"measurement-{name}.json"), JsonSerializer.Serialize(data));
    }
}
