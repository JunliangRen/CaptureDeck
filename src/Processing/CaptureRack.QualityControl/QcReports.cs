using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CaptureRack.Core;

namespace CaptureRack.QualityControl;

public sealed record QcExpectedMedia(long VideoFrames, long AudioSampleFrames, string FieldOrder,
    int AudioBits, string VideoTag, int Width, int Height);

public sealed record QcEvent(TimeSpan Elapsed, string Description);

public sealed record QcCaptureLog(
    string Device, string Driver, string VideoSource, string AudioSource, string InputMode,
    DateTimeOffset StartedUtc, DateTimeOffset StoppedUtc, QcExpectedMedia Expected,
    long ValidVideoFrames, long NoInputFrames, long MissingVideoFrames, long TimestampRegressions,
    long AudioPackets, long? LeftClips, long? RightClips, IReadOnlyList<QcEvent> RecentEvents)
{
    public bool InputModeChanged { get; init; }
    public RecordingTiming? Timing { get; init; }
    public string? CaptureError { get; init; }
    public long UnreadableNoInputFrames { get; init; }
    public long VideoSignalLossCount { get; init; }
    public TimeSpan VideoSignalLossDuration { get; init; }
    public IReadOnlyList<VideoSignalLoss> VideoSignalLosses { get; init; } = [];
}

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

    public static (string? FfmpegPath, string? FfprobePath) FindTools() =>
        (FindTool("ffmpeg"), FindTool("ffprobe"));

    public static bool SupportsFile(string path) =>
        Path.GetExtension(path).Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    public static async Task<QcResult> RunAsync(string inputPath, QcCaptureLog? capture = null,
        Action<string>? progress = null, CancellationToken cancellation = default, Action<QcStage>? stageChanged = null)
    {
        var path = Path.GetFullPath(inputPath);
        if (!SupportsFile(path))
            throw new ArgumentException("Select a MOV or MKV file.", nameof(inputPath));
        if (!File.Exists(path))
            throw new FileNotFoundException("The recording could not be found.", path);

        var isMov = Path.GetExtension(path).Equals(".mov", StringComparison.OrdinalIgnoreCase);
        var fileBytes = new FileInfo(path).Length;

        var output = ReportPaths.For(path);
        var began = DateTimeOffset.UtcNow;
        var problems = new List<string>();
        var warnings = CaptureWarnings(capture);
        NativeCaptureReport? native = null;
        MediaFacts? media = null;
        DecodedCounts? counts = null;
        QcToolsCounts? scopeCounts = null;
        string? hashesPath = null;
        string? scopesPath = null;
        var stage = "Inspecting media streams";
        var status = QcStatus.Passed;
        var ffmpeg = FindTool("ffmpeg");
        var ffprobe = FindTool("ffprobe");

        if (ffmpeg is null || ffprobe is null)
        {
            status = QcStatus.ToolsUnavailable;
            problems.Add("Install FFmpeg with ffmpeg.exe and ffprobe.exe available on PATH or beside CaptureRack.");
        }
        else
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                stageChanged?.Invoke(QcStage.Inspecting);
                progress?.Invoke(stage);
                if (capture is null)
                {
                    try { native = await NativeCaptureReport.ReadAsync(path, cancellation); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                    {
                        warnings.Add($"Could not read the DirectShow recording report: {ex.Message}");
                    }
                }

                if (native is not null)
                {
                    Compare("Recorded file bytes", fileBytes, native.FileBytes, problems);
                    if (native.Error is { } error)
                        problems.Add(error);
                    AddNativeWarnings(native.VideoCounts, "Video", native.Video.Encoding == "MPEG2", warnings);
                    if (native.AudioCounts is { } pcm)
                        AddNativeWarnings(pcm, "PCM", false, warnings);
                }

                var metadata = await InspectAsync(ffprobe, path, cancellation);
                media = ReadMediaFacts(metadata, isMov, capture is not null);
                CheckMedia(media, capture?.Expected, isMov, problems);
                if (native is not null)
                    CheckNativeMedia(media, native, problems);
                if (capture is null && native is null)
                {
                    if (!media.HasVideo)
                        warnings.Add("The file has no video track.");
                    if (!media.HasAudio)
                        warnings.Add("The file has no audio track.");
                    if ((media.HasVideo && media.VideoFrames is null) ||
                        (media.HasAudio && media.AudioSampleFrames is null))
                        warnings.Add("Some expected sample counts are unavailable. Full decoding cannot establish the original capture length.");
                }

                stage = "Decoding video and PCM and writing FrameMD5";
                stageChanged?.Invoke(QcStage.Decoding);
                progress?.Invoke(stage);
                counts = await WriteAtomicallyAsync(output.FrameMd5, stream =>
                    DecodeAndHashAsync(ffmpeg, path, media, stream, cancellation, native is not null || !isMov));
                hashesPath = output.FrameMd5;

                Compare("Decoded video frames", counts.VideoFrames, capture?.Expected.VideoFrames ?? native?.ExpectedVideoFrames ?? media.VideoFrames, problems);
                Compare("Decoded PCM sample frames", counts.AudioSampleFrames,
                    capture?.Expected.AudioSampleFrames ?? native?.PcmSampleFrames ?? media.AudioSampleFrames, problems);

                stage = "Analyzing video and audio for QCTools";
                stageChanged?.Invoke(QcStage.Analyzing);
                progress?.Invoke(stage);
                scopeCounts = await WriteAtomicallyAsync(output.QcTools, stream =>
                    WriteQcToolsAsync(ffprobe, path, metadata, media, stream, cancellation));
                scopesPath = output.QcTools;

                Compare("QCTools video frames", scopeCounts.VideoFrames, counts.VideoFrames, problems);
                if (media.HasAudio && scopeCounts.AudioFrames == 0)
                    problems.Add("The QCTools report contains no audio analysis frames.");
                if (scopeCounts.FirstFieldMismatch is { } mismatch)
                    problems.Add($"Decoded field flags disagree with the file at video frame {mismatch:N0}.");
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
                fileBytes,
                capture,
                nativeRecording = native,
                verificationBasis = capture is not null ? "CaptureRack recording settings and write counts" :
                    native is not null ? "DirectShow recording report and full decode" : "Media stream metadata and full decode; original capture settings unavailable",
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

    internal static List<string> CaptureWarnings(QcCaptureLog? capture)
    {
        List<string> warnings = [];
        if (capture is null)
        {
            warnings.Add("Original capture settings and signal events are unavailable for this file.");
            return warnings;
        }

        if (capture.Expected.VideoFrames == 0)
            warnings.Add("No video frames were captured.");
        if (capture.Expected.AudioSampleFrames == 0)
            warnings.Add("No audio samples were captured.");
        if (capture.InputModeChanged)
            warnings.Add("The input mode changed. Recording stopped before writing frames in the new mode.");
        if (capture.VideoSignalLosses.Count > 0)
            warnings.Add($"Video lock lost {capture.VideoSignalLossCount:N0} time(s). Total time unlocked: " +
                $"{capture.VideoSignalLossDuration.TotalSeconds:F3} s. See videoSignalLosses for recording-relative times.");
        if (capture.NoInputFrames > 0)
            warnings.Add($"The capture source marked {capture.NoInputFrames:N0} frames as no input.");
        if (capture.UnreadableNoInputFrames > 0)
            warnings.Add($"{capture.UnreadableNoInputFrames:N0} no-input frames had no writable video payload.");
        if (capture.MissingVideoFrames > 0)
            warnings.Add($"Video timestamp gaps suggest {capture.MissingVideoFrames:N0} missing frames.");
        if (capture.CaptureError is { } captureError)
            warnings.Add($"Capture ended before its requested stop. The writable portion was saved. {captureError}");
        if (capture.Timing is { } video && video.VideoCorrections + video.VideoInvalidDurations > 0)
            warnings.Add($"Video timing: {video.VideoCorrections:N0} timestamp intervals corrected and " +
                $"{video.VideoInvalidDurations:N0} invalid frame durations reported by the device.");
        if (capture.Timing is { FirstAudioIssue: not null } audio)
            warnings.Add($"Audio timestamp anomalies: {audio.AudioDiscontinuities:N0} packet discontinuities, " +
                $"{audio.AudioTimestampResets:N0} repeats or resets, {audio.ApparentAudioGapSamples:N0} apparent gap samples, " +
                $"{audio.ApparentAudioOverlapSamples:N0} apparent overlap samples, maximum cumulative offset {audio.MaximumAudioOffsetSamples:N0} samples. " +
                "PCM was written continuously in arrival order. Check A/V sync around the reported timestamp.");
        if (capture.Timing is null && capture.TimestampRegressions > 0)
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

    private static MediaFacts ReadMediaFacts(XElement metadata, bool isMov, bool deckLink)
    {
        var format = metadata.Element("format");
        var streams = metadata.Element("streams")?.Elements("stream").ToArray() ?? [];
        var container = isMov ? "mov" : "matroska";
        if (!Text(format, "format_name").Split(',').Contains(container, StringComparer.Ordinal))
            throw new InvalidOperationException($"The input was not recognized as {container}.");
        var video = streams.Where(stream => Text(stream, "codec_type") == "video").ToArray();
        var audio = streams.Where(stream => Text(stream, "codec_type") == "audio").ToArray();
        if (video.Length > 1 || audio.Length > 1 || video.Length + audio.Length == 0)
            throw new InvalidOperationException("QC expects at most one video track and one PCM track.");

        var v = video.SingleOrDefault();
        var a = audio.SingleOrDefault();
        var audioCodec = a is null ? "" : Text(a, "codec_name");
        var audioBits = (int)(a is null ? 0 : Number(a, audioCodec == "flac" ? "bits_per_raw_sample" : "bits_per_sample") ?? 0);
        var sampleRate = (int)(Number(a, "sample_rate") ?? 0);
        var channels = (int)(Number(a, "channels") ?? 0);
        if (a is not null && ((audioCodec, audioBits) is not
            (("pcm_u8", 8) or ("pcm_s16le", 16) or ("pcm_s24le", 24) or ("pcm_s32le", 32))
            && (isMov || audioCodec != "flac" || audioBits is not (8 or 16 or 24 or 32))
            || sampleRate <= 0 || channels <= 0))
            throw new InvalidOperationException("QC expects supported PCM or FLAC audio.");
        if (deckLink && a is not null && (audioBits is not (16 or 32) || sampleRate != 48000 || channels != 2))
            throw new InvalidOperationException("DeckLink QC expects 48 kHz, 16- or 32-bit signed stereo PCM.");

        return new(v is not null, a is not null, v is null ? "" : Text(v, "codec_tag_string"),
            v is null ? "" : Text(v, "field_order"), v is null ? 0 : Number(v, "nb_frames"),
            a is null ? 0 : Number(a, "nb_frames"), (int)(v is null ? 0 : Number(v, "width") ?? 0),
            (int)(v is null ? 0 : Number(v, "height") ?? 0), audioCodec, audioBits, sampleRate,
            channels, v is null ? "" : Text(v, "r_frame_rate"), Text(v, "codec_name"), Text(v, "pix_fmt"));
    }

    private static void CheckMedia(MediaFacts media, QcExpectedMedia? expected, bool isMov, List<string> problems)
    {
        if (expected is null)
            return;
        if (isMov && media.HasVideo)
        {
            if (media.VideoTag is not ("v210" or "2vuy"))
                problems.Add($"Unexpected video format '{media.VideoTag}' for a CaptureRack MOV.");
            if (media.FieldOrder is not ("tt" or "bb" or "tb" or "bt" or "progressive"))
                problems.Add("The MOV does not declare a recognized field order.");
            if (media.VideoFrames is null)
                problems.Add("The MOV video sample count is unavailable.");
        }
        if (isMov && media.HasAudio && media.AudioSampleFrames is null)
            problems.Add("The MOV audio sample count is unavailable.");

        if (isMov)
        {
            Compare("MOV index video frames", media.VideoFrames, expected.VideoFrames, problems);
            Compare("MOV index PCM sample frames", media.AudioSampleFrames, expected.AudioSampleFrames, problems);
        }
        else if (!media.HasVideo || expected.AudioSampleFrames > 0 && !media.HasAudio)
            problems.Add("A track recorded by the capture source is missing from the encoded file.");
        if (media.HasAudio)
            Compare("Audio bit depth", media.AudioBits, expected.AudioBits, problems);
        if (media.HasVideo)
        {
            Compare("Video width", media.Width, expected.Width, problems);
            Compare("Video height", media.Height, expected.Height, problems);
            if (isMov && media.VideoTag != expected.VideoTag)
                problems.Add($"Video format '{media.VideoTag}' differs from the recorded setting '{expected.VideoTag}'.");
            if (isMov ? media.FieldOrder != expected.FieldOrder : NormalizeFieldOrder(media.FieldOrder) != NormalizeFieldOrder(expected.FieldOrder))
                problems.Add($"Field order '{media.FieldOrder}' differs from the recorded setting '{expected.FieldOrder}'.");
        }
    }

    private static string NormalizeFieldOrder(string value) => value switch
    {
        "tt" or "tb" => "top-first",
        "bb" or "bt" => "bottom-first",
        _ => value
    };

    private static void CheckNativeMedia(MediaFacts media, NativeCaptureReport recording, List<string> problems)
    {
        if (!media.HasVideo)
            problems.Add("The recording is missing its video track.");
        Compare("Video width", media.Width, recording.Video.Width, problems);
        Compare("Video height", media.Height, recording.Video.Height, problems);
        var expectedEncoding = recording.Video.Encoding switch
        {
            "MJPG" or "MJPG_IR" => ("mjpeg", ""),
            "MPEG2" => ("mpeg2video", ""),
            "v210" => ("v210", "yuv422p10le"),
            "r210" => ("r210", "gbrp10le"),
            "YUY2" => ("rawvideo", "yuyv422"),
            "UYVY" or "HDYC" => ("rawvideo", "uyvy422"),
            "YVYU" => ("rawvideo", "yvyu422"),
            "NV12" => ("rawvideo", "nv12"),
            "I420" or "IYUV" or "YV12" => ("rawvideo", "yuv420p"),
            "RGB24" => ("rawvideo", "bgr24"),
            "RGB32" => ("rawvideo", ""),
            "L8_IR" or "Y800" or "GREY" => ("rawvideo", "gray"),
            "L16_IR" => ("rawvideo", "gray16le"),
            _ => ("", "")
        };
        if (expectedEncoding.Item1.Length > 0 && (media.VideoCodec != expectedEncoding.Item1 ||
            expectedEncoding.Item2.Length > 0 && media.PixelFormat != expectedEncoding.Item2))
            problems.Add($"Video encoding '{media.VideoCodec}/{media.PixelFormat}' differs from the negotiated '{recording.Video.Encoding}'.");
        var order = media.FieldOrder switch
        {
            "tt" or "tb" => "top-first",
            "bb" or "bt" => "bottom-first",
            _ => media.FieldOrder
        };
        if (recording.Video.FieldOrder is { } expectedOrder && order != expectedOrder)
            problems.Add($"Video field order '{media.FieldOrder}' differs from the negotiated '{expectedOrder}'.");
        if (media.HasAudio != (recording.Audio is not null))
            problems.Add("The audio track does not match the recording report.");
        if (recording.Audio is { } audio)
        {
            Compare("Audio sample rate", media.AudioRate, audio.SampleRate, problems);
            Compare("Audio channels", media.Channels, audio.Channels, problems);
            Compare("Audio bit depth", media.AudioBits, audio.BitsPerSample, problems);
        }
    }

    private static void AddNativeWarnings(NativeCaptureCounts counts, string stream, bool reordered, List<string> warnings)
    {
        if (counts.Discontinuities > 0)
            warnings.Add($"{stream}: the driver marked {counts.Discontinuities:N0} sample discontinuities.");
        if (counts.MissingStartTimes > 0)
            warnings.Add($"{stream}: {counts.MissingStartTimes:N0} samples have no driver timestamp.");
        if (counts.RepeatedStartTimes > 0 || !reordered && counts.RegressingStartTimes > 0)
            warnings.Add($"{stream}: {counts.RepeatedStartTimes:N0} repeated and {counts.RegressingStartTimes:N0} regressing driver timestamps.");
    }

    private static void Compare(string subject, long? observed, long? expected, List<string> problems)
    {
        if (expected is not null && observed != expected)
            problems.Add($"{subject}: expected {expected:N0}, found {observed?.ToString("N0") ?? "unknown"}.");
    }

    private static Task<DecodedCounts> DecodeAndHashAsync(string executable, string path, MediaFacts media,
        Stream target, CancellationToken cancellation, bool useDemuxTimeBase)
    {
        string[] timeBase = useDemuxTimeBase ? ["-enc_time_base:v", "demux"] : [];
        string[] videoArgs = media.HasVideo
            ? ["-map", "0:v:0", "-c:v", "rawvideo", "-threads:v", "1", "-fps_mode", "passthrough", .. timeBase] : [];
        var pcmCodec = media.AudioCodec == "flac" ? media.AudioBits == 8 ? "pcm_u8" : $"pcm_s{media.AudioBits}le" : media.AudioCodec;
        string[] audioArgs = media.HasAudio ? ["-map", "0:a:0", "-c:a", pcmCodec] : [];
        var audioStream = media.HasVideo ? "1" : "0";
        return RunProcessAsync(executable, Path.GetDirectoryName(path)!,
            ["-v", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-apply_cropping", "none", "-i", path,
                .. videoArgs, .. audioArgs, "-abort_on", "empty_output_stream", "-f", "framemd5", "-"],
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
                            case "0" when media.HasVideo:
                                videoFrames++;
                                break;
                            case var index when media.HasAudio && index == audioStream && size % audioFrameBytes == 0:
                                pcmFrames = checked(pcmFrames + size / audioFrameBytes);
                                break;
                            default:
                                throw new InvalidOperationException("FFmpeg returned an unexpected stream or PCM packet size.");
                        }
                    }

                    await output.WriteLineAsync(line.AsMemory(), cancellation);
                }
                await output.FlushAsync(cancellation);
                if ((media.HasVideo && videoFrames == 0) || (media.HasAudio && pcmFrames == 0))
                    throw new InvalidOperationException("Full decoding produced no samples for a declared track.");
                return new DecodedCounts(videoFrames, pcmFrames);
            }, cancellation);
    }

    private static Task<QcToolsCounts> WriteQcToolsAsync(string executable, string path, XElement metadata,
        MediaFacts media, Stream target, CancellationToken cancellation)
    {
        // Filter arguments are parsed first as a graph and then as AVOptions. A local relative
        // file name avoids drive-letter and protocol syntax, and is escaped through both layers.
        var filename = Escape(Escape($"./{Path.GetFileName(path)}", "\\':"), "\\'[],;");
        var selection = media.HasVideo && media.HasAudio ? "v+a[v][a]" : media.HasVideo ? "v[v]" : "a[a]";
        var videoGraph = media.HasVideo ? ";[v]signalstats=stat=tout+vrep+brng,cropdetect=reset=1:round=1[out0]" : "";
        var audioOutput = media.HasVideo ? "out1" : "out0";
        var audioGraph = media.HasAudio
            ? $";[a]aformat=sample_fmts=fltp,aphasemeter=video=0,astats=metadata=1:reset=1,ebur128=metadata=1[{audioOutput}]" : "";
        var graph = $"movie=filename={filename}:streams={selection}{videoGraph}{audioGraph}";
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
                var sourceVideo = streams.SingleOrDefault(stream => Text(stream, "codec_type") == "video");
                var sourceAudio = streams.SingleOrDefault(stream => Text(stream, "codec_type") == "audio");
                var fieldOrder = sourceVideo is null ? "" : Text(sourceVideo, "field_order");
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
                    if (Text(frame, "media_type") == "video" && sourceVideo is not null)
                    {
                        video++;
                        var matches = CheckFrameFields(frame, fieldOrder);
                        if (matches is null)
                            unknown++;
                        else if (!matches.Value)
                            mismatched ??= video;
                        frame.SetAttributeValue("stream_index", Text(sourceVideo, "index"));
                    }
                    else if (Text(frame, "media_type") == "audio" && sourceAudio is not null)
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

    private sealed record MediaFacts(bool HasVideo, bool HasAudio, string VideoTag, string FieldOrder, long? VideoFrames, long? AudioSampleFrames,
        int Width, int Height, string AudioCodec, int AudioBits, int AudioRate, int Channels, string FrameRate, string VideoCodec, string PixelFormat);
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
