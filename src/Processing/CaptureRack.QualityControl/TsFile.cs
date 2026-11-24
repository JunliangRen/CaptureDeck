using System.Text.Json;

namespace CaptureRack.QualityControl;

public sealed record TsFileReport(string InputPath, string Format, TsStatistics Analysis)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly JsonSerializerOptions IndentedOptions = new(Options) { WriteIndented = true };

    public int SchemaVersion => 1;
    public string Type => "analysis_result";
    public bool Success => Analysis.StructuralValidationOk;
    public int ExitCode => Success ? 0 : 4;

    public string ToJson(bool indented = false) => JsonSerializer.Serialize(this, indented ? IndentedOptions : Options);
}

public static class TsFile
{
    public static TsFileReport Analyze(string path, TsLayout? layout = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        var count = stream.ReadAtLeast(buffer, buffer.Length, false);
        cancellation.ThrowIfCancellationRequested();
        var detected = layout ?? TsAnalyzer.Detect(buffer.AsSpan(0, count));
        var analyzer = new TsAnalyzer(detected);
        while (count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            analyzer.Feed(buffer.AsSpan(0, count));
            count = stream.Read(buffer);
        }
        cancellation.ThrowIfCancellationRequested();
        analyzer.Finish();
        return new TsFileReport(fullPath, detected.Format, analyzer.Statistics);
    }
}
