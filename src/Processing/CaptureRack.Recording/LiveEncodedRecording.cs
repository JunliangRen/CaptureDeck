using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaptureRack.QualityControl;
using CaptureRack.Recording.Matroska;
using CaptureRack.Recording.Windows;

namespace CaptureRack.Recording;

public enum RecordingOutput { Native, Ffv1, H264, H265, Av1, Mpeg2 }

public sealed record RecordingEncoding(RecordingOutput Output = RecordingOutput.Native, int? VideoKbps = null,
    string? Level = null, bool Deinterlace = false)
{
    public bool IsSoftware => Output != RecordingOutput.Native;

    public string FileTag => Output switch
    {
        RecordingOutput.Ffv1 => "Encoded_FFV1_FLAC8",
        RecordingOutput.H264 => "Encoded_H264",
        RecordingOutput.H265 => "Encoded_H265",
        RecordingOutput.Av1 => "Encoded_AV1",
        RecordingOutput.Mpeg2 => "Encoded_MPEG2",
        _ => "Native"
    };

    internal string[] Arguments(uint? fieldOrder, int? audioBits)
    {
        if (VideoKbps is <= 0 or > 1000000)
            throw new ArgumentException("Video bitrate must be between 1 and 1,000,000 kb/s.");
        if (Level is not null && !Regex.IsMatch(Level, @"\A(?:[0-9]{1,3}(?:\.[0-9])?|low|main|high|high1440)\z"))
            throw new ArgumentException("Enter a codec level such as 4.1, or main for MPEG-2.");

        var interlaced = fieldOrder is 1 or 6 && !AppliesDeinterlacing(fieldOrder);
        var parity = fieldOrder == 1 ? "tff" : "bff";
        List<string> args = [];
        if (AppliesDeinterlacing(fieldOrder))
            args.AddRange(["-vf", $"bwdif=mode=send_frame:parity={parity}:deint=all"]);
        var level = Level;
        switch (Output)
        {
            case RecordingOutput.Ffv1:
                args.AddRange(["-c:v", "ffv1", "-level:v", "3", "-slicecrc", "1", "-slices", "16", "-g", "1"]);
                break;
            case RecordingOutput.H264:
                args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-pix_fmt", "yuv420p"]);
                if (interlaced)
                    args.AddRange(["-vf", $"setfield={parity}", "-flags", "+ilme+ildct", "-x264-params", $"{parity}=1"]);
                break;
            case RecordingOutput.H265:
                if (interlaced)
                    throw new NotSupportedException("H.265 requires progressive input in this profile. Enable software deinterlacing or choose a progressive capture mode.");
                args.AddRange(["-c:v", "libx265", "-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p"]);
                if (level is not null)
                    args.AddRange(["-x265-params", $"level-idc={level}"]);
                level = null;
                break;
            case RecordingOutput.Av1:
                if (interlaced)
                    throw new NotSupportedException("AV1 requires progressive input. Enable software deinterlacing or choose a progressive capture mode.");
                args.AddRange(["-c:v", "libsvtav1", "-preset", "9", "-pix_fmt", "yuv420p",
                    "-svtav1-params", "pred-struct=1"]);
                if (level is not null)
                {
                    if (!decimal.TryParse(level, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var av1Level))
                        throw new ArgumentException("Enter an AV1 level such as 4.0.");
                    level = ((int)(av1Level < 10 ? av1Level * 10 : av1Level)).ToString(CultureInfo.InvariantCulture);
                }
                break;
            case RecordingOutput.Mpeg2:
                args.AddRange(["-c:v", "mpeg2video", "-pix_fmt", "yuv420p", "-bf", "0"]);
                if (level is not null)
                {
                    if (level is not ("low" or "main" or "high1440" or "high" or "10" or "8" or "6" or "4"))
                        throw new ArgumentException("Choose an MPEG-2 level of low, main, high1440, or high.");
                    args.AddRange(["-profile:v", "main"]);
                }
                if (interlaced)
                    args.AddRange(["-vf", $"setfield={parity}", "-flags", "+ilme+ildct"]);
                break;
            default:
                throw new InvalidOperationException("Select a software video encoder.");
        }
        if (Output != RecordingOutput.Ffv1)
        {
            var rate = VideoKbps ?? (Output == RecordingOutput.Mpeg2 ? 12000 : 8000);
            var kbps = rate.ToString(CultureInfo.InvariantCulture) + "k";
            args.AddRange(["-b:v", kbps, "-g", "30"]);
            if (Output == RecordingOutput.Av1)
                args.AddRange(["-minrate", kbps, "-maxrate", kbps, "-bufsize", kbps]);
            if (level is not null)
                args.AddRange(["-level:v", level]);
        }
        if (audioBits is { } bits)
        {
            args.AddRange(Output == RecordingOutput.Ffv1
                ? ["-c:a", "flac", "-compression_level:a", "8", "-bits_per_raw_sample:a", Math.Max(16, bits).ToString(CultureInfo.InvariantCulture)]
                : ["-c:a", "copy"]);
            if (Output == RecordingOutput.Ffv1 && bits == 32)
                args.AddRange(["-strict:a", "experimental"]);
        }
        return [.. args];
    }

    internal bool AppliesDeinterlacing(uint? fieldOrder) =>
        Deinterlace && Output is not (RecordingOutput.Native or RecordingOutput.Ffv1) && fieldOrder is 1 or 6;
}

internal sealed class LiveEncodedRecording : IDisposable
{
    private readonly Process encoder;
    private readonly NativeMatroskaWriter input;
    private readonly Task<string> diagnostics;
    private readonly Task<long> writing;
    private readonly string path;
    private readonly string partial;
    private readonly string checkpoint;
    private readonly RecordingEncoding options;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly NativeVideoTrack video;
    private readonly NativePcmTrack? audio;
    private readonly LiveMatroskaRecovery recovery = new();
    private readonly Timer watchdog;
    private long writeStarted;
    private bool finished;
    private bool interruptedCheckpointPending;
    private string? interruption;
    private long videoPackets, audioPackets, durablePosition;
    private long longestEncoderWrite, longestDiskWrite, longestDiskFlush;
    private readonly Action? beforeEncoderWrite;
    private readonly Func<Task>? beforeDiskWrite;
    internal LiveEncodingDiagnostics CaptureDiagnostics { get; } = new();
    internal bool Deinterlaced => options.AppliesDeinterlacing(video.FieldOrder);
    internal string PartialPath => partial;
    internal Task OutputEnded => writing;

    internal void RecordInterruption(string reason) =>
        Interlocked.CompareExchange(ref interruption, reason, null);

    internal void CheckHealth()
    {
        if (writing.IsFaulted)
        {
            var error = writing.Exception?.InnerException;
            throw new IOException($"Could not write the encoded output. {error?.Message} Recording prefix: {partial}.", error);
        }
        if (writing.IsCompleted)
        {
            var detail = diagnostics.IsCompletedSuccessfully ? diagnostics.Result.Trim() : "";
            throw new IOException($"The live encoder exited before capture stopped. Recording prefix: {partial}. {detail}".TrimEnd());
        }
    }

    internal LiveEncodedRecording(string outputPath, RecordingEncoding options, NativeVideoTrack video, NativePcmTrack? audio,
        string? ffmpeg = null, Action? beforeEncoderWrite = null, Func<Task>? beforeDiskWrite = null)
    {
        path = outputPath;
        partial = outputPath + ".partial.mkv";
        checkpoint = partial + ".checkpoint.json";
        this.options = options;
        this.video = video;
        this.audio = audio;
        this.beforeEncoderWrite = beforeEncoderWrite;
        this.beforeDiskWrite = beforeDiskWrite;
        if (File.Exists(path) || File.Exists(checkpoint) || File.Exists(path + ".encoding.json"))
            throw new IOException($"The recording destination already exists: {path}.");
        var executable = ffmpeg ?? QcReports.FindTools().FfmpegPath ??
            throw new InvalidOperationException("Software encoding requires FFmpeg.");
        var codecArgs = options.Arguments(video.FieldOrder, audio?.Bits);
        // The MJPEG decoder exposes full-range yuvj formats. Convert the layout without rescaling the signal.
        if (options.Output == RecordingOutput.Ffv1 && video.BitmapHeader.AsSpan().Length >= 20 &&
            video.BitmapHeader.AsSpan(16, 4).SequenceEqual("MJPG"u8))
            codecArgs = [..codecArgs, "-vf", "scale=in_range=full:out_range=full", "-color_range", "pc"];
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        string[] arguments = ["-hide_banner", "-nostdin", "-loglevel", "error", "-xerror",
            "-probesize", "1048576", "-analyzeduration", "500000", "-f", "matroska", "-i", "pipe:0",
            "-map", "0:v:0", "-map", "0:a?", "-fps_mode", "passthrough", ..codecArgs,
            "-f", "matroska", "-live", "1", "-cluster_time_limit", "500", "-write_crc32", "1", "-flush_packets", "1", "pipe:1"];
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.SequentialScan);
        Process? process = null;
        Task<long>? outputTask = null;
        try
        {
            process = Process.Start(start) ?? throw new IOException("FFmpeg could not start.");
            encoder = process;
            diagnostics = encoder.StandardError.ReadToEndAsync();
            writing = outputTask = PersistAsync(file);
            input = new NativeMatroskaWriter(encoder.StandardInput.BaseStream, video, audio, streaming: true);
            watchdog = new Timer(_ => AbortStalledEncoder(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        catch
        {
            if (process is null)
                file.Dispose();
            else
            {
                using (process)
                {
                    AbortEncoder();
                    if (outputTask is null)
                        file.Dispose();
                    else
                    {
                        try { outputTask.GetAwaiter().GetResult(); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
                    }
                }
            }
            try
            {
                if (new FileInfo(partial) is { Exists: true, Length: 0 })
                    File.Delete(partial);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    internal void Write(bool isVideo, long start, long end, ReadOnlySpan<byte> bytes, bool keyframe = true)
    {
        CheckHealth();
        if (encoder.HasExited)
            throw Failure();
        var startTimestamp = Stopwatch.GetTimestamp();
        Volatile.Write(ref writeStarted, startTimestamp);
        try
        {
            try
            {
                beforeEncoderWrite?.Invoke();
                input.Write(isVideo, start, end, bytes, keyframe);
            }
            finally
            {
                Volatile.Write(ref writeStarted, 0);
                RecordDuration(ref longestEncoderWrite, startTimestamp);
            }
            if (isVideo)
                Interlocked.Increment(ref videoPackets);
            else
                Interlocked.Increment(ref audioPackets);
        }
        catch (IOException error)
        {
            if (encoder.WaitForExit(100))
                throw Failure();
            throw new IOException($"The live encoder stopped accepting samples. Recording prefix: {partial}.", error);
        }
    }

    internal void Finish(bool publish = true)
    {
        if (finished)
            return;
        finished = true;
        var published = false;
        if (!publish)
            RecordInterruption("Recording was interrupted.");
        try
        {
            encoder.StandardInput.BaseStream.Close();
            if (!encoder.WaitForExit(30000))
            {
                encoder.Kill();
                throw new IOException($"The live encoder stalled while closing. Recording prefix: {partial}.");
            }
            var bytes = writing.GetAwaiter().GetResult();
            if (encoder.ExitCode != 0 || bytes == 0 || recovery.CompletePrefix == 0)
                throw Failure();
            if (!publish)
            {
                TrySaveInterruptedCheckpoint();
                return;
            }
            if (interruption is { } reason)
                throw new IOException(reason);
            if (!recovery.IsComplete(bytes))
                throw new IOException("The live encoder left an incomplete Matroska element.");
            using (var output = new FileStream(partial, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                recovery.AppendIndex(output);
                output.Flush(flushToDisk: true);
            }
            WindowsFile.MoveWriteThrough(partial, path);
            published = true;
            if (File.Exists(checkpoint))
                WindowsFile.MoveWriteThrough(checkpoint, path + ".encoding.json");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (published)
                throw new IOException($"Recording saved to '{path}'. The encoding report remains at '{checkpoint}'. {error.Message}", error);
            RecordInterruption(error.Message);
            interruptedCheckpointPending = !writing.IsCompleted;
            if (!interruptedCheckpointPending)
                TrySaveInterruptedCheckpoint();
            if (error.Message.Contains(partial, StringComparison.Ordinal))
                throw;
            throw new IOException($"{error.Message} Recording prefix: {partial}.", error);
        }
    }

    private async Task<long> PersistAsync(FileStream file)
    {
        using (file)
        {
            var buffer = new byte[128 * 1024];
            var flushTick = Task.Delay(1000);
            long flushed = 0, writtenPosition = 0;
            try
            {
                var output = encoder.StandardOutput.BaseStream;
                while (true)
                {
                    var next = output.ReadAsync(buffer.AsMemory()).AsTask();
                    if (file.Position != flushed &&
                        await Task.WhenAny(next, flushTick).ConfigureAwait(false) != next)
                    {
                        Checkpoint(file);
                        flushed = file.Position;
                        flushTick = Task.Delay(1000);
                    }
                    var read = await next.ConfigureAwait(false);
                    if (read == 0)
                        break;
                    var startedWriting = Stopwatch.GetTimestamp();
                    try
                    {
                        if (beforeDiskWrite is not null)
                            await beforeDiskWrite().ConfigureAwait(false);
                        await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                        writtenPosition = file.Position;
                    }
                    finally { RecordDuration(ref longestDiskWrite, startedWriting); }
                    if (flushTick.IsCompleted)
                    {
                        Checkpoint(file);
                        flushed = file.Position;
                        flushTick = Task.Delay(1000);
                    }
                }
                Checkpoint(file);
                return file.Length;
            }
            catch (Exception error)
            {
                RecordInterruption(error.Message);
                AbortEncoder();
                if (writtenPosition > durablePosition)
                {
                    // A failing write may have left a partial buffer. Scan only acknowledged writes.
                    try { Checkpoint(file, writtenPosition); }
                    catch (Exception checkpointError) when (checkpointError is IOException or UnauthorizedAccessException or InvalidDataException) { }
                }
                throw;
            }
        }
    }

    private void Checkpoint(FileStream file) => Checkpoint(file, file.Position);

    private void Checkpoint(FileStream file, long writtenPosition)
    {
        var startedFlushing = Stopwatch.GetTimestamp();
        try { file.Flush(flushToDisk: true); }
        finally { RecordDuration(ref longestDiskFlush, startedFlushing); }
        var flushedPosition = Math.Min(writtenPosition, file.Length);
        if (flushedPosition == 0)
            return;
        durablePosition = flushedPosition;
        using (var media = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            recovery.Scan(media, durablePosition);
        WriteCheckpoint(durablePosition);
    }

    private void TrySaveInterruptedCheckpoint()
    {
        if (!writing.IsCompleted || durablePosition == 0)
            return;
        try { WriteCheckpoint(durablePosition); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void WriteCheckpoint(long durableBytes)
    {
        var queue = CaptureDiagnostics.Queue;
        var videoSent = Interlocked.Read(ref videoPackets);
        var diskWrite = ReadMilliseconds(ref longestDiskWrite);
        var diskFlush = ReadMilliseconds(ref longestDiskFlush);
        var state = JsonSerializer.SerializeToUtf8Bytes(new
        {
            mode = "software_realtime",
            codec = options.Output.ToString(),
            options.VideoKbps,
            options.Level,
            deinterlaced = Deinterlaced,
            flacLevel = audio is not null && options.Output == RecordingOutput.Ffv1 ? 8 : (int?)null,
            outputAudioBits = audio is null ? (int?)null : options.Output == RecordingOutput.Ffv1 ? Math.Max(16, audio.Bits) : audio.Bits,
            queuePeakPackets = queue.PeakPackets,
            queuePeakBytes = queue.PeakBytes,
            queueRejectedPackets = queue.RejectedPackets,
            queueRejectedVideoPackets = queue.RejectedVideoPackets,
            captureVideoPackets = queue.CapturedVideoPackets,
            captureEstimatedMissingVideoFrames = queue.ComparableVideoIntervals > 0 ? queue.EstimatedMissingFrames : (long?)null,
            captureComparableVideoIntervals = queue.ComparableVideoIntervals,
            captureVideoDiscontinuities = queue.CaptureDiscontinuities,
            captureVideoTimestampsMissing = queue.TimestampsMissing,
            captureVideoTimestampsRepeated = queue.TimestampsRepeated,
            captureVideoTimestampsRegressed = queue.TimestampsRegressed,
            videoPacketsToEncoder = videoSent,
            videoFramesToEncoder = video.Mpeg2 ? (long?)null : videoSent,
            decodedVideoFrames = (long?)null,
            decodedAudioSampleFrames = (long?)null,
            decodeStatus = "pending",
            longestEncoderWriteMilliseconds = ReadMilliseconds(ref longestEncoderWrite),
            longestDiskWriteMilliseconds = diskWrite,
            longestDiskFlushMilliseconds = diskFlush,
            longestDiskStallMilliseconds = Math.Max(diskWrite, diskFlush),
            audioPacketsToEncoder = Interlocked.Read(ref audioPackets),
            started,
            error = Volatile.Read(ref interruption),
            durableBytes,
            completeClusterBytes = recovery.CompletePrefix,
            lastCompleteClusterTimestampMilliseconds = recovery.LastCompleteClusterTimestampMilliseconds,
            input = new { video.Width, video.Height, video.FrameTicks, video.FieldOrder, audio }
        });
        var pending = checkpoint + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var journal = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                journal.Write(state);
                journal.Flush(flushToDisk: true);
            }
            File.Move(pending, checkpoint, overwrite: true);
        }
        finally
        {
            try { File.Delete(pending); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void RecordDuration(ref long peak, long timestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(timestamp).Ticks;
        // Each peak has one writer. Checkpoint serialization can read the encoder peak concurrently.
        if (elapsed > Interlocked.Read(ref peak))
            Interlocked.Exchange(ref peak, elapsed);
    }

    private static double ReadMilliseconds(ref long peak) =>
        TimeSpan.FromTicks(Interlocked.Read(ref peak)).TotalMilliseconds;

    private IOException Failure()
    {
        var reason = Volatile.Read(ref interruption) ?? "Live FFmpeg encoding failed.";
        return new IOException($"{reason} Recording prefix: {partial}.{Environment.NewLine}{diagnostics.GetAwaiter().GetResult().Trim()}");
    }

    private void AbortStalledEncoder()
    {
        var start = Volatile.Read(ref writeStarted);
        if (start == 0 || Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(15))
            return;
        if (Volatile.Read(ref writeStarted) != start)
            return;
        RecordInterruption("The software encoder stopped accepting input for 15 seconds.");
        AbortEncoder();
    }

    internal void AbortEncoder()
    {
        try
        {
            if (!encoder.HasExited)
                encoder.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        try
        {
            if (!finished)
                Finish(publish: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { /* The live write or explicit finish reports the original failure. */ }
        finally
        {
            watchdog.Dispose();
            AbortEncoder();
            try { writing.GetAwaiter().GetResult(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            if (interruptedCheckpointPending)
                TrySaveInterruptedCheckpoint();
            encoder.Dispose();
        }
    }
}
