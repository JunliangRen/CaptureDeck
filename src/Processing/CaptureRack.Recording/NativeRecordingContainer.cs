using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CaptureRack.QualityControl;

namespace CaptureRack.Recording;

internal static class NativeRecordingContainer
{
    internal static string[] Extensions(bool compressed) => compressed
        ? [".ts", ".mov", ".mp4", ".mkv"]
        : [".mov", ".mp4", ".mkv"];

    internal static async Task<string> FinalizeAsync(string stagingPath, string mkvPath, bool compressed,
        (string? FfmpegPath, string? FfprobePath) tools, Action<NativeContainerAttempt> progress)
    {
        void Report(string container, NativeContainerStatus status, string? detail = null) =>
            progress(new(container.TrimStart('.').ToUpperInvariant(), status, detail));
        if (tools is { FfmpegPath: { } ffmpeg, FfprobePath: { } ffprobe })
        {
            MediaSignature? source = null;
            foreach (var extension in Extensions(compressed)[..^1])
            {
                var target = Path.ChangeExtension(mkvPath, extension);
                if (File.Exists(target))
                {
                    Report(extension, NativeContainerStatus.PathExists);
                    continue;
                }
                Report(extension, NativeContainerStatus.Trying);

                var candidate = target + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    var muxer = extension == ".ts" ? "mpegts" : extension[1..];
                    var result = await RunToolAsync(ffmpeg, ["-nostdin", "-v", "error", "-xerror", "-n",
                        "-copyts", "-i", stagingPath, "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy",
                        "-fflags", "-autobsf", "-avoid_negative_ts", "disabled", "-f", muxer, candidate], captureDiagnostics: true)
                        .ConfigureAwait(false);
                    if (!result.Success)
                    {
                        Report(extension, NativeContainerStatus.MuxFailed, result.Diagnostics);
                        continue;
                    }

                    Report(extension, NativeContainerStatus.Verifying);
                    source ??= await InspectAsync(ffmpeg, ffprobe, stagingPath).ConfigureAwait(false);
                    if (source is null)
                    {
                        Report(extension, NativeContainerStatus.SourceUnverifiable);
                        break;
                    }
                    var actual = await InspectAsync(ffmpeg, ffprobe, candidate).ConfigureAwait(false);
                    if (actual is null || source != actual)
                    {
                        Report(extension, actual is null ? NativeContainerStatus.CandidateUnverifiable : NativeContainerStatus.SignatureMismatch);
                        continue;
                    }

                    using (var output = new FileStream(candidate, FileMode.Open, FileAccess.Write))
                        output.Flush(flushToDisk: true);
                    File.Move(candidate, target);
                    TryDelete(stagingPath);
                    Report(extension, NativeContainerStatus.Selected);
                    return target;
                }
                catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or JsonException)
                {
                    Report(extension, NativeContainerStatus.IoFailed, error.Message);
                }
                finally { TryDelete(candidate); }
            }
        }

        else
            Report(".mkv", NativeContainerStatus.ToolsUnavailable);

        File.Move(stagingPath, mkvPath);
        Report(".mkv", NativeContainerStatus.Selected);
        return mkvPath;
    }

    private static async Task<MediaSignature?> InspectAsync(string ffmpeg, string ffprobe, string path)
    {
        var json = await RunAsync(ffprobe, "-v", "error", "-show_entries",
            "stream=codec_type,codec_name,pix_fmt,width,height,sample_rate,channels,bits_per_sample,field_order,sample_aspect_ratio",
            "-of", "json", path).ConfigureAwait(false);
        if (json is null)
            return null;

        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray()
            .Select(stream => stream.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.ToString()))
            .ToArray();
        if (streams.Length is < 1 or > 2 || !streams.Any(s => s.GetValueOrDefault("codec_type") == "video") ||
            streams.Any(s => s.GetValueOrDefault("codec_type") is not ("video" or "audio")
                || !s.ContainsKey("codec_name")))
            return null;

        // Compare stream payloads independently of container-specific packet boundaries (notably PCM).
        var hash = await RunAsync(ffmpeg, "-nostdin", "-v", "error", "-xerror", "-i", path, "-map", "0:v:0",
            "-map", "0:a:0?", "-c", "copy", "-f", "streamhash", "-hash", "sha256", "-").ConfigureAwait(false);
        if (hash is null || hash.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != streams.Length)
            return null;
        var layout = string.Join('\n', streams.Select(s => string.Join('|',
            s.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={Normalize(p.Key, p.Value)}"))));
        // Container metadata can change the decoded result without changing the stored bytes,
        // for example the row orientation of Windows RGB bitmaps.
        var decoded = await RunAsync(ffmpeg, "-nostdin", "-v", "error", "-xerror", "-i", path,
            "-map", "0:v:0", "-frames:v", "1", "-c:v", "rawvideo", "-threads:v", "1",
            "-f", "framehash", "-hash", "sha256", "-").ConfigureAwait(false);
        var frame = decoded?.Split('\n').FirstOrDefault(line => line.StartsWith("0,", StringComparison.Ordinal));
        return frame is null ? null : new MediaSignature(layout, hash.Trim(), frame[(frame.LastIndexOf(',') + 1)..].Trim());
    }

    private static string Normalize(string key, string value) =>
        key == "sample_aspect_ratio" && value == "N/A" ? "1:1" : value;

    private static async Task<string?> RunAsync(string executable, params string[] args)
    {
        var result = await RunToolAsync(executable, args, captureDiagnostics: false).ConfigureAwait(false);
        return result.Success ? result.Output : null;
    }

    private static async Task<ToolResult> RunToolAsync(string executable, string[] args, bool captureDiagnostics)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = ReadDiagnosticsAsync(process.StandardError, captureDiagnostics ? 2048 : 0);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new(process.ExitCode == 0, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task<string?> ReadDiagnosticsAsync(StreamReader reader, int limit)
    {
        var text = new StringBuilder(limit);
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            text.Append(buffer, 0, Math.Min(count, limit - text.Length));
        return text.Length == 0 ? null : text.ToString().Trim();
    }

    private sealed record ToolResult(bool Success, string Output, string? Diagnostics);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private sealed record MediaSignature(string Layout, string Hash, string FirstFrameHash);
}
