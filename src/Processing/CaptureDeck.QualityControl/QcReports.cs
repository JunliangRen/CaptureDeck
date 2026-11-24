using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CaptureDeck.QualityControl;

public sealed record QcExpectedMedia(long VideoFrames, long AudioSampleFrames, string FieldOrder,
    int AudioBits, string VideoTag, int Width, int Height);

public sealed record QcEvent(TimeSpan Elapsed, string Description);

public sealed record QcCaptureLog(
    string Device, string Driver, string VideoSource, string AudioSource, string InputMode,
    DateTimeOffset StartedUtc, DateTimeOffset StoppedUtc, QcExpectedMedia Expected,
    long ValidVideoFrames, long NoInputFrames, long MissingVideoFrames, long TimestampRegressions,
    long AudioPackets, long? LeftClips, long? RightClips, IReadOnlyList<QcEvent> RecentEvents);

public enum QcStage { Inspecting, Decoding, Analyzing }

public enum QcStatus { Passed, Failed, Cancelled, ToolsUnavailable }

public sealed record QcResult(QcStatus Status, string Summary, string CaptureLog,
    string? FrameMd5, string? QcToolsReport, IReadOnlyList<string> Warnings)
{
    public long? DecodedVideoFrames { get; init; }
    public long? DecodedAudioSampleFrames { get; init; }
    public long VideoFramesWithoutFieldFlags { get; init; }
}

public static class QcReports
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static async Task<QcResult> RunAsync(string inputPath, QcCaptureLog? capture = null,
        Action<string>? progress = null, CancellationToken cancellation = default, Action<QcStage>? stageChanged = null)
    {
        var path = Path.GetFullPath(inputPath);
        if (!Path.GetExtension(path).Equals(".mov", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select a MOV file.", nameof(inputPath));
        if (!File.Exists(path))
            throw new FileNotFoundException("The MOV could not be found.", path);

        var output = ReportPaths.For(path);
        var began = DateTimeOffset.UtcNow;
        var problems = new List<string>();
        var warnings = CaptureWarnings(capture);
        MediaFacts? media = null;
        DecodedCounts? counts = null;
        QcToolsCounts? scopeCounts = null;
        string? hashesPath = null;
        string? scopesPath = null;
        var stage = "Inspecting MOV streams";
        var status = QcStatus.Passed;
        var ffmpeg = FindTool("ffmpeg");
        var ffprobe = FindTool("ffprobe");

        if (ffmpeg is null || ffprobe is null)
        {
            status = QcStatus.ToolsUnavailable;
            problems.Add("Install FFmpeg with ffmpeg.exe and ffprobe.exe available on PATH or beside CaptureDeck.");
        }
        else
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                stageChanged?.Invoke(QcStage.Inspecting);
                progress?.Invoke(stage);
                var metadata = await InspectAsync(ffprobe, path, cancellation);
                media = ReadMediaFacts(metadata);
                CheckMedia(media, capture?.Expected, problems);

                stage = "Decoding video and PCM and writing FrameMD5";
                stageChanged?.Invoke(QcStage.Decoding);
                progress?.Invoke(stage);
                counts = await WriteAtomicallyAsync(output.FrameMd5, stream =>
                    DecodeAndHashAsync(ffmpeg, path, media, stream, cancellation));
                hashesPath = output.FrameMd5;

                Compare("Decoded video frames", counts.VideoFrames, capture?.Expected.VideoFrames ?? media.VideoFrames, problems);
                Compare("Decoded PCM sample frames", counts.AudioSampleFrames,
                    capture?.Expected.AudioSampleFrames ?? media.AudioSampleFrames, problems);

                stage = "Analyzing video and audio for QCTools";
                stageChanged?.Invoke(QcStage.Analyzing);
                progress?.Invoke(stage);
                scopeCounts = await WriteAtomicallyAsync(output.QcTools, stream =>
                    WriteQcToolsAsync(ffprobe, path, metadata, stream, cancellation));
                scopesPath = output.QcTools;

                Compare("QCTools video frames", scopeCounts.VideoFrames, counts.VideoFrames, problems);
                if (scopeCounts.AudioFrames == 0)
                    problems.Add("The QCTools report contains no audio analysis frames.");
                if (scopeCounts.FirstFieldMismatch is { } mismatch)
                    problems.Add($"Decoded field flags disagree with the MOV at video frame {mismatch:N0}.");
                if (scopeCounts.VideoFramesWithoutFieldFlags > 0)
                    warnings.Add($"{scopeCounts.VideoFramesWithoutFieldFlags:N0} decoded frames did not expose field flags.");

                status = problems.Count == 0 ? QcStatus.Passed : QcStatus.Failed;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                status = QcStatus.Cancelled;
                problems.Add($"Cancelled during {stage.ToLowerInvariant()}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception or XmlException or FormatException or OverflowException)
            {
                status = QcStatus.Failed;
                problems.Add($"{stage}: {ex.Message}");
            }
        }

        var summary = status switch
        {
            QcStatus.Passed => $"Decoded {counts!.VideoFrames:N0} video frames and {counts.AudioSampleFrames:N0} PCM sample frames.",
            _ => problems[0]
        };
        await WriteAtomicallyAsync(output.Log, async stream =>
        {
            var log = new
            {
                file = path,
                fileBytes = new FileInfo(path).Length,
                capture,
                verificationBasis = capture is null ? "MOV sample tables; original capture settings unavailable" : "CaptureDeck recording settings and write counts",
                qc = new
                {
                    status,
                    startedUtc = began,
                    completedUtc = DateTimeOffset.UtcNow,
                    ffmpeg,
                    ffprobe,
                    media,
                    decoded = counts,
                    qcTools = scopeCounts,
                    artifacts = new { frameMd5 = hashesPath, qcToolsReport = scopesPath },
                    warnings,
                    problems
                }
            };
            var json = JsonSerializer.Serialize(log, JsonOptions).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(json));
            return true;
        });

        return new(status, summary, output.Log, hashesPath, scopesPath, warnings)
        {
            DecodedVideoFrames = counts?.VideoFrames,
            DecodedAudioSampleFrames = counts?.AudioSampleFrames,
            VideoFramesWithoutFieldFlags = scopeCounts?.VideoFramesWithoutFieldFlags ?? 0
        };
    }

    private static List<string> CaptureWarnings(QcCaptureLog? capture)
    {
        List<string> warnings = [];
        if (capture is null)
        {
            warnings.Add("Original capture settings and signal events are unavailable for this file.");
            return warnings;
        }

        if (capture.NoInputFrames > 0)
            warnings.Add($"{capture.NoInputFrames:N0} recorded frames were marked no input by DeckLink.");
        if (capture.MissingVideoFrames > 0)
            warnings.Add($"Capture detected gaps corresponding to {capture.MissingVideoFrames:N0} missing video frames.");
        if (capture.TimestampRegressions > 0)
            warnings.Add($"Capture detected {capture.TimestampRegressions:N0} audio or video timestamp regressions.");
        if (capture.LeftClips > 0 || capture.RightClips > 0)
            warnings.Add("Audio reached the capture monitor's clipping threshold.");
        return warnings;
    }

    private static Task<XElement> InspectAsync(string executable, string path, CancellationToken cancellation) =>
        RunProcessAsync(executable, Path.GetDirectoryName(path)!,
            ["-v", "error", "-show_program_version", "-show_library_versions", "-show_format", "-show_streams",
                "-of", "xml", "-i", path],
            async reader =>
            {
                using var xml = CreateXmlReader(reader);
                return await XElement.LoadAsync(xml, LoadOptions.None, cancellation);
            }, cancellation);

    private static MediaFacts ReadMediaFacts(XElement metadata)
    {
        var format = metadata.Element("format");
        var streams = metadata.Element("streams")?.Elements("stream").ToArray() ?? [];
        if (!Text(format, "format_name").Split(',').Contains("mov", StringComparer.Ordinal))
            throw new InvalidOperationException("The input was not recognized as QuickTime MOV.");
        var video = streams.Where(stream => Text(stream, "codec_type") == "video").ToArray();
        var audio = streams.Where(stream => Text(stream, "codec_type") == "audio").ToArray();
        if (video.Length != 1 || audio.Length != 1)
            throw new InvalidOperationException("QC expects one video track and one stereo PCM track.");

        var v = video[0];
        var a = audio[0];
        var audioCodec = Text(a, "codec_name");
        var audioBits = (int)(Number(a, "bits_per_sample") ?? 0);
        if ((audioCodec, audioBits) is not (("pcm_s16le", 16) or ("pcm_s32le", 32)) ||
            Number(a, "sample_rate") != 48000 || Number(a, "channels") != 2)
            throw new InvalidOperationException("The MOV must contain 48 kHz, 16- or 32-bit signed stereo PCM.");

        return new(Text(v, "codec_tag_string"), Text(v, "field_order"), Number(v, "nb_frames"),
            Number(a, "nb_frames"), (int)(Number(v, "width") ?? 0), (int)(Number(v, "height") ?? 0),
            audioCodec, audioBits, 48000, 2, Text(v, "r_frame_rate"));
    }

    private static void CheckMedia(MediaFacts media, QcExpectedMedia? expected, List<string> problems)
    {
        if (media.VideoTag is not ("v210" or "2vuy"))
            problems.Add($"Unexpected video format '{media.VideoTag}' for a CaptureDeck MOV.");
        if (media.FieldOrder is not ("tt" or "bb" or "tb" or "bt" or "progressive"))
            problems.Add("The MOV does not declare a recognized field order.");
        if (media.VideoFrames is null)
            problems.Add("The MOV video sample count is unavailable.");
        if (media.AudioSampleFrames is null)
            problems.Add("The MOV audio sample count is unavailable.");

        if (expected is null)
            return;

        Compare("MOV index video frames", media.VideoFrames, expected.VideoFrames, problems);
        Compare("MOV index PCM sample frames", media.AudioSampleFrames, expected.AudioSampleFrames, problems);
        Compare("Audio bit depth", media.AudioBits, expected.AudioBits, problems);
        Compare("Video width", media.Width, expected.Width, problems);
        Compare("Video height", media.Height, expected.Height, problems);
        if (media.VideoTag != expected.VideoTag)
            problems.Add($"Video format '{media.VideoTag}' differs from the recorded setting '{expected.VideoTag}'.");
        if (media.FieldOrder != expected.FieldOrder)
            problems.Add($"Field order '{media.FieldOrder}' differs from the recorded setting '{expected.FieldOrder}'.");
    }

    private static void Compare(string subject, long? observed, long? expected, List<string> problems)
    {
        if (expected is not null && observed != expected)
            problems.Add($"{subject}: expected {expected:N0}, found {observed?.ToString("N0") ?? "unknown"}.");
    }

    private static Task<DecodedCounts> DecodeAndHashAsync(string executable, string path, MediaFacts media,
        Stream target, CancellationToken cancellation) =>
        RunProcessAsync(executable, Path.GetDirectoryName(path)!,
            ["-v", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-apply_cropping", "none", "-i", path,
                "-map", "0:v:0", "-map", "0:a:0", "-c:v", "rawvideo", "-threads:v", "1",
                "-c:a", media.AudioCodec, "-fps_mode", "passthrough",
                "-abort_on", "empty_output_stream", "-f", "framemd5", "-"],
            async reader =>
            {
                using var output = new StreamWriter(target, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n" };
                long videoFrames = 0;
                long pcmFrames = 0;
                var audioFrameBytes = media.AudioBits / 8 * media.Channels;
                while (await reader.ReadLineAsync(cancellation) is { } line)
                {
                    if (line.Length > 0 && line[0] != '#')
                    {
                        var columns = line.Split(',', 6, StringSplitOptions.TrimEntries);
                        if (columns.Length != 6 || !long.TryParse(columns[4], out var size) || size <= 0 ||
                            columns[5].Length != 32 || !columns[5].All(Uri.IsHexDigit))
                            throw new InvalidOperationException("FFmpeg returned an invalid FrameMD5 entry.");

                        switch (columns[0])
                        {
                            case "0":
                                videoFrames++;
                                break;
                            case "1" when size % audioFrameBytes == 0:
                                pcmFrames = checked(pcmFrames + size / audioFrameBytes);
                                break;
                            default:
                                throw new InvalidOperationException("FFmpeg returned an unexpected stream or PCM packet size.");
                        }
                    }

                    await output.WriteLineAsync(line.AsMemory(), cancellation);
                }
                await output.FlushAsync(cancellation);
                if (videoFrames == 0 || pcmFrames == 0)
                    throw new InvalidOperationException("Full decoding produced no video frames or PCM samples.");
                return new DecodedCounts(videoFrames, pcmFrames);
            }, cancellation);

    private static Task<QcToolsCounts> WriteQcToolsAsync(string executable, string path, XElement metadata,
        Stream target, CancellationToken cancellation)
    {
        // Filter arguments are parsed first as a graph and then as AVOptions. A local relative
        // file name avoids drive-letter and protocol syntax, and is escaped through both layers.
        var filename = Escape(Escape($"./{Path.GetFileName(path)}", "\\':"), "\\'[],;");
        var graph = $"movie=filename={filename}:streams=v+a[v][a];" +
            "[v]signalstats=stat=tout+vrep+brng,cropdetect=reset=1:round=1[out0];" +
            "[a]aformat=sample_fmts=fltp,aphasemeter=video=0,astats=metadata=1:reset=1,ebur128=metadata=1[out1]";
        return RunProcessAsync(executable, Path.GetDirectoryName(path)!,
            ["-v", "error", "-f", "lavfi", "-i", graph, "-show_frames", "-of", "xml", "-noprivate"],
            async reader =>
            {
                using var xml = CreateXmlReader(reader);
                await using var gzip = new GZipStream(target, CompressionLevel.Fastest, leaveOpen: true);
                using var report = XmlWriter.Create(gzip, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
                report.WriteStartDocument();
                report.WriteStartElement("ffprobe", "ffprobe", "http://www.ffmpeg.org/schema/ffprobe");
                foreach (var name in new[] { "program_version", "library_versions" })
                    metadata.Element(name)?.WriteTo(report);
                report.WriteStartElement("frames");

                var streams = metadata.Element("streams")!.Elements("stream");
                var sourceVideo = streams.Single(stream => Text(stream, "codec_type") == "video");
                var sourceAudio = streams.Single(stream => Text(stream, "codec_type") == "audio");
                var fieldOrder = Text(sourceVideo, "field_order");
                long video = 0, audio = 0, unknown = 0;
                long? mismatched = null;
                while (await xml.ReadAsync())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (xml.NodeType != XmlNodeType.Element || xml.Depth != 2 || xml.LocalName != "frame")
                        continue;

                    using var subtree = xml.ReadSubtree();
                    var frame = await XElement.LoadAsync(subtree, LoadOptions.None, cancellation);
                    AddAlias(frame, "pkt_pts", "pts");
                    AddAlias(frame, "pkt_pts_time", "pts_time");
                    AddAlias(frame, "pkt_duration", "duration");
                    AddAlias(frame, "pkt_duration_time", "duration_time");
                    if (Text(frame, "media_type") == "video")
                    {
                        video++;
                        var matches = CheckFrameFields(frame, fieldOrder);
                        if (matches is null)
                            unknown++;
                        else if (!matches.Value)
                            mismatched ??= video;
                        frame.SetAttributeValue("stream_index", Text(sourceVideo, "index"));
                    }
                    else if (Text(frame, "media_type") == "audio")
                    {
                        audio++;
                        frame.SetAttributeValue("stream_index", Text(sourceAudio, "index"));
                    }

                    // QCTools reads frame tags directly, while recent ffprobe wraps them in <tags>.
                    foreach (var tags in frame.Elements("tags").ToList())
                        tags.ReplaceWith(tags.Elements("tag").ToList());
                    frame.WriteTo(report);
                }
                report.WriteEndElement();
                metadata.Element("streams")?.WriteTo(report);
                metadata.Element("format")?.WriteTo(report);
                report.WriteEndElement();
                report.WriteEndDocument();
                return new QcToolsCounts(video, audio, mismatched, unknown);
            }, cancellation);
    }

    private static bool? CheckFrameFields(XElement frame, string declaredOrder)
    {
        if (frame.Attribute("interlaced_frame") is not { } interlaced)
            return null;
        if (declaredOrder == "progressive")
            return interlaced.Value == "0";
        if (declaredOrder is not ("tt" or "bt" or "bb" or "tb") ||
            frame.Attribute("top_field_first") is not { } first)
            return null;
        // For the spatially woven QuickTime layouts, ffprobe emits tb for top-first
        // and bt for bottom-first; compare the same decoder's per-frame flags.
        var topFirst = declaredOrder is "tt" or "tb";
        return interlaced.Value == "1" && first.Value == (topFirst ? "1" : "0");
    }

    private static void AddAlias(XElement frame, string name, string source)
    {
        if (frame.Attribute(name) is null && frame.Attribute(source) is { } value)
            frame.SetAttributeValue(name, value.Value);
    }

    private static XmlReader CreateXmlReader(TextReader reader) => XmlReader.Create(reader,
        new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit });

    private static string Text(XElement? element, string attribute) => (string?)element?.Attribute(attribute) ?? "";
    private static long? Number(XElement? element, string attribute) =>
        long.TryParse(Text(element, attribute), out var number) ? number : null;

    private static string Escape(string value, string special)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (special.Contains(c))
                result.Append('\\');
            result.Append(c);
        }
        return result.ToString();
    }

    private static string? FindTool(string name)
    {
        var filename = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var folders = path.Split(Path.PathSeparator).Prepend(AppContext.BaseDirectory);
        // Finder-launched apps do not inherit the interactive shell's Homebrew PATH.
        if (OperatingSystem.IsMacOS())
            folders = folders.Append("/opt/homebrew/bin").Append("/usr/local/bin");

        foreach (var entry in folders)
        {
            var folder = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(folder))
                continue;

            var executable = Path.Combine(folder, filename);
            if (File.Exists(executable))
                return executable;
        }
        return null;
    }

    private static async Task<T> RunProcessAsync<T>(string executable, string workingDirectory, string[] arguments,
        Func<StreamReader, Task<T>> consume, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();

        using var registration = cancellation.Register(() => StopProcess(process));
        var diagnostics = ReadDiagnosticsAsync(process.StandardError);
        try
        {
            var result = await consume(process.StandardOutput);
            await process.WaitForExitAsync();
            var errors = await diagnostics;
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || errors.Length != 0)
                throw ProcessError(executable, process.ExitCode, errors);
            return result;
        }
        catch
        {
            StopProcess(process);
            await process.WaitForExitAsync();
            var errors = await diagnostics;
            cancellation.ThrowIfCancellationRequested();
            if (errors.Length != 0)
                throw ProcessError(executable, process.ExitCode, errors);
            throw;
        }
    }

    private static InvalidOperationException ProcessError(string executable, int code, string diagnostics) =>
        new((code == 0 ? $"{Path.GetFileName(executable)} reported an error."
            : $"{Path.GetFileName(executable)} exited with code {code}.") +
            (diagnostics.Length == 0 ? "" : $" {diagnostics}"));

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static async Task<string> ReadDiagnosticsAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        var result = new StringBuilder(4096);
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
            result.Append(buffer, 0, Math.Min(count, 4096 - result.Length));
        return result.ToString().Trim();
    }

    private static async Task<T> WriteAtomicallyAsync<T>(string path, Func<Stream, Task<T>> write)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.partial";
        var created = false;
        try
        {
            T result;
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, useAsync: true))
            {
                created = true;
                result = await write(stream);
                await stream.FlushAsync();
            }

            File.Move(temp, path);
            created = false;
            return result;
        }
        finally
        {
            if (created)
                File.Delete(temp);
        }
    }

    private sealed record MediaFacts(string VideoTag, string FieldOrder, long? VideoFrames, long? AudioSampleFrames,
        int Width, int Height, string AudioCodec, int AudioBits, int AudioRate, int Channels, string FrameRate);
    private sealed record DecodedCounts(long VideoFrames, long AudioSampleFrames);
    private sealed record QcToolsCounts(long VideoFrames, long AudioFrames, long? FirstFieldMismatch,
        long VideoFramesWithoutFieldFlags);

    private sealed record ReportPaths(string Log, string FrameMd5, string QcTools)
    {
        public static ReportPaths For(string path)
        {
            var name = Path.ChangeExtension(path, null);
            for (var run = 1; ; run++)
            {
                var prefix = run == 1 ? name : $"{name}-qc{run}";
                var reportName = run == 1 ? path : $"{prefix}{Path.GetExtension(path)}";
                var files = new ReportPaths($"{prefix}.capture.json", $"{prefix}.framemd5",
                    $"{reportName}.qctools.xml.gz");
                if (!File.Exists(files.Log) && !File.Exists(files.FrameMd5) && !File.Exists(files.QcTools))
                    return files;
            }
        }
    }
}
