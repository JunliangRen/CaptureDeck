using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CaptureRack.Core;

namespace CaptureRack.DirectShow.Windows.FireWire.MsTape;

public readonly record struct FireWireFrameInfo(int Width, int Height, bool? TopFieldFirst,
    double DisplayAspectRatio, double Timestamp, string? VideoCodec, string PixelFormat)
{
    public int PixelWidth => Math.Min(Width, FireWirePreviewDecoder.MaxWidth);
    public int PixelHeight => Math.Min(Height, FireWirePreviewDecoder.MaxHeight);
}

public sealed class FireWirePreviewDecoder(bool dv = false)
{
    public const int AudioRate = 48000;
    internal const int MaxWidth = 1920, MaxHeight = 1080;
    private const int PipeStride = MaxWidth * 4;
    private const int FrameBytes = PipeStride * MaxHeight;
    private const int MaxQueuedBytes = 16 * 1024 * 1024;
    // FFmpeg fixes AVI's raster when it writes the header. Pad to a stable raster so an SD/HD
    // format change keeps the full source pixels without restarting the preview process.
    private const string VideoFilter = "showinfo=checksum=0,scale='min(iw,1920)':'min(ih,1080)':interl=-1," +
        "format=bgra,pad=1920:1080:0:0";
    private static readonly Regex VideoMapping = new(
        @"^\s*Stream #(?<stream>\d+:\d+) -> #0:0 \((?<codec>[\w-]+) ",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SourcePid = new(
        @"^\s*Stream #(?<stream>\d+:\d+)\[0x(?<pid>[\da-fA-F]+)\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AudioMapping = new(
        @"^\s*Stream #(?<stream>\d+:\d+) -> #0:1 ",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AudioStreamLog = new(
        @"^\s*Stream #(?<stream>\d+:\d+).*?: Audio: (?<codec>[\w-]+)(?: \((?<profile>[^)]+)\))?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AudioRateAndLayout = new(
        @",\s*(?<rate>\d+) Hz(?:,\s*(?<layout>[^,]+))?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AudioBitRate = new(
        @",\s*(?<rate>\d+(?:\.\d+)?) (?<unit>[kM]?)b/s\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DecodedAudioLog = new(
        @"channels:(?<channels>\d+)\s+chlayout:(?<layout>.+?)\s+rate:(?<rate>\d+)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PcmDepth = new(@"^pcm_[suf](\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FrameLog = new(
        @"pts_time:(?<time>\S+).*?fmt:(?<format>\S+).*?sar:(?<sarN>\d+)/(?<sarD>\d+)\s+s:(?<width>\d+)x(?<height>\d+)\s+i:(?<field>[PTB])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly Channel<(byte[] Buffer, int Length)> queue =
        Channel.CreateUnbounded<(byte[], int)>(new UnboundedChannelOptions { SingleReader = true });
    private int accepting = 1;
    private int queuedBytes;
    private string? pipelineError;
    private long decodedFrames;
    private CaptureAudioInfo? audioInfo;
    private string? audioChannelLayout;
    private int videoPid = -1, audioPid = -1;
    public int? VideoPid => Volatile.Read(ref videoPid) is var pid && pid >= 0 ? pid : null;
    public int? AudioPid => Volatile.Read(ref audioPid) is var pid && pid >= 0 ? pid : null;

    public long DecodedFrames => Interlocked.Read(ref decodedFrames);
    public CaptureAudioInfo? AudioInfo => Volatile.Read(ref audioInfo);
    public string? AudioChannelLayout => Volatile.Read(ref audioChannelLayout);

    // Never wait for FFmpeg in the DirectShow callback or let preview backpressure reach the recording.
    public bool Offer(CaptureSample sample)
    {
        if (Volatile.Read(ref accepting) == 0)
            return true;
        var size = sample.Bytes.Length;
        if (Interlocked.Add(ref queuedBytes, size) > MaxQueuedBytes)
        {
            Interlocked.Add(ref queuedBytes, -size);
            FailInput("Preview decoder could not keep up with the i.LINK stream.");
            return true;
        }

        byte[] buffer;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(size);
        }
        catch (OutOfMemoryException)
        {
            Interlocked.Add(ref queuedBytes, -size);
            FailInput("There was not enough memory to buffer the i.LINK preview.");
            return true;
        }
        sample.Bytes.CopyTo(buffer);
        if (!queue.Writer.TryWrite((buffer, size)))
        {
            Interlocked.Add(ref queuedBytes, -size);
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return true;
    }

    public void Complete() => CloseInput(null);

    private void FailInput(string message)
    {
        Interlocked.CompareExchange(ref pipelineError, message, null);
        CloseInput(new IOException(message));
    }

    private void CloseInput(Exception? error)
    {
        if (Interlocked.Exchange(ref accepting, 0) != 0)
            queue.Writer.TryComplete(error);
    }

    public Task RunAsync(string ffmpeg, Action<ReadOnlyMemory<byte>, FireWireFrameInfo> video,
        Action<ReadOnlyMemory<byte>> audio, CancellationToken stop) =>
        RunAsync(ffmpeg, video, (pcm, _, _) => audio(pcm), stop);

    public async Task RunAsync(string ffmpeg, Action<ReadOnlyMemory<byte>, FireWireFrameInfo> video,
        Action<ReadOnlyMemory<byte>, int, string?> audio, CancellationToken stop)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        List<string> arguments = ["-hide_banner", "-loglevel", "info", "-nostats", "-nostdin", "-f", dv ? "dv" : "mpegts", "-probesize", "1048576",
            "-analyzeduration", "1000000", "-i", "pipe:0", "-map", "0:v:0", "-map", "0:a:0?"];
        if (dv)
        {
            arguments.Add("-map");
            arguments.Add("0:a:1?");
        }
        arguments.AddRange([
            "-vf", VideoFilter, "-fps_mode", "passthrough", "-c:v", "rawvideo", "-pix_fmt", "bgra", "-threads:v", "1",
            "-af", "ashowinfo", "-c:a", "pcm_s16le", "-ar", "48000", "-flush_packets", "1",
            "-max_interleave_delta", "100000", "-f", "avi", "pipe:1"]);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var input = Task.CompletedTask;
        var output = Task.CompletedTask;
        Task<string>? errors = null;
        try
        {
            stop.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new IOException("FFmpeg preview decoder could not start.");

            var token = lifetime.Token;
            await using var terminate = token.Register(() => Kill(process));
            var fields = Channel.CreateBounded<FireWireFrameInfo>(new BoundedChannelOptions(512)
            {
                SingleReader = true, SingleWriter = true
            });
            errors = DrainErrors(process.StandardError, fields.Writer);
            input = WriteInput(process.StandardInput.BaseStream, token);
            output = ReadFrames(process.StandardOutput.BaseStream, fields.Reader, video, audio, token);

            var first = await Task.WhenAny(input, output).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            await output.ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var diagnostic = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException(diagnostic.Length == 0 ? $"FFmpeg exited with code {process.ExitCode}." : diagnostic);
        }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or IOException) { }
        catch (IOException ex)
        {
            var diagnostic = errors is { IsCompletedSuccessfully: true } ? errors.Result : null;
            throw new IOException(Volatile.Read(ref pipelineError) ?? (string.IsNullOrWhiteSpace(diagnostic) ? ex.Message : diagnostic), ex);
        }
        finally
        {
            Complete();
            await lifetime.CancelAsync().ConfigureAwait(false);
            // Stop either side even if the other failed while blocked on a pipe.
            Kill(process);
            try { await Task.WhenAll(input, output).ConfigureAwait(false); }
            catch (Exception)
            {
                // ignored
            }

            if (errors is not null)
                await errors.ConfigureAwait(false);
            while (queue.Reader.TryRead(out var block))
                ArrayPool<byte>.Shared.Return(block.Buffer);
        }
    }

    private async Task WriteInput(Stream destination, CancellationToken stop)
    {
        await using (destination)
        {
            await foreach (var block in queue.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                try { await destination.WriteAsync(block.Buffer.AsMemory(0, block.Length), stop).ConfigureAwait(false); }
                finally
                {
                    Interlocked.Add(ref queuedBytes, -block.Length);
                    ArrayPool<byte>.Shared.Return(block.Buffer);
                }
            }
        }
    }

    // FFmpeg's streaming AVI muxer puts all raw video and optional PCM in one interleaved pipe.
    private async Task ReadFrames(Stream source, ChannelReader<FireWireFrameInfo> fields,
        Action<ReadOnlyMemory<byte>, FireWireFrameInfo> video, Action<ReadOnlyMemory<byte>, int, string?> audio, CancellationToken stop)
    {
        var header = new byte[12];
        if (!await ReadHeader(source, header, 12, stop).ConfigureAwait(false))
            return;
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("AVI "u8))
            throw new IOException("Unexpected FFmpeg preview stream format.");

        var scratch = new byte[16384];
        var inMovie = false;
        var currentStreamIndex = -1;
        var currentStreamIsAudio = false;
        var audioStreams = new Dictionary<int, int>(); // AVI stream index -> channels
        MultiStreamAudioInterleaver? multiStreamInterleaver = null;
        while (await ReadHeader(source, header, 8, stop).ConfigureAwait(false))
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (header.AsSpan(0, 4).SequenceEqual("LIST"u8))
            {
                await source.ReadExactlyAsync(header.AsMemory(8, 4), stop).ConfigureAwait(false);
                var listType = header.AsSpan(8, 4);
                if (listType.SequenceEqual("movi"u8))
                {
                    inMovie = true;
                    if (audioStreams.Count > 1)
                        multiStreamInterleaver = new MultiStreamAudioInterleaver(audioStreams);
                    continue;
                }
                if (listType.SequenceEqual("hdrl"u8) || listType.SequenceEqual("strl"u8))
                {
                    if (listType.SequenceEqual("strl"u8))
                    {
                        currentStreamIndex++;
                        currentStreamIsAudio = false;
                    }
                    continue;
                }
                if (size < 4)
                    throw new IOException("Invalid AVI list in the preview stream.");
                size -= 4;
            }
            else switch (inMovie)
            {
                case false when header.AsSpan(0, 4).SequenceEqual("strh"u8) && size >= 4 && size <= scratch.Length:
                {
                    var padded = size + (size & 1);
                    await source.ReadExactlyAsync(scratch.AsMemory(0, (int)padded), stop).ConfigureAwait(false);
                    currentStreamIsAudio = scratch.AsSpan(0, 4).SequenceEqual("auds"u8);
                    continue;
                }
                case false when currentStreamIsAudio && header.AsSpan(0, 4).SequenceEqual("strf"u8) && size >= 16 && size <= scratch.Length:
                {
                    var padded = size + (size & 1);
                    await source.ReadExactlyAsync(scratch.AsMemory(0, (int)padded), stop).ConfigureAwait(false);
                    var ch = BinaryPrimitives.ReadUInt16LittleEndian(scratch.AsSpan(2, 2));
                    if (ch is >= 1 and <= 16 && currentStreamIndex >= 0)
                        audioStreams[currentStreamIndex] = ch;
                    continue;
                }
                case true when (header.AsSpan(0, 4).SequenceEqual("00dc"u8) ||
                                header.AsSpan(0, 4).SequenceEqual("00db"u8)):
                {
                    if (size == 0) // AVI can mark a repeated frame with an empty chunk.
                        continue;
                    if (size != FrameBytes)
                        throw new IOException("Unexpected FFmpeg preview frame size.");
                    var buffer = ArrayPool<byte>.Shared.Rent(FrameBytes);
                    try
                    {
                        await source.ReadExactlyAsync(buffer.AsMemory(0, FrameBytes), stop).ConfigureAwait(false);
                        var frame = await fields.ReadAsync(stop).ConfigureAwait(false);
                        var stride = frame.PixelWidth * 4;
                        if (stride != PipeStride)
                            for (var row = 1; row < frame.PixelHeight; row++)
                                buffer.AsSpan(row * PipeStride, stride).CopyTo(buffer.AsSpan(row * stride, stride));
                        video(buffer.AsMemory(0, stride * frame.PixelHeight), frame);
                        Interlocked.Increment(ref decodedFrames);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                    continue;
                }
                case true when TryParseAudioChunkStream(header.AsSpan(0, 4), out var audioStreamIdx):
                {
                    var streamChannels = audioStreams.TryGetValue(audioStreamIdx, out var ch) ? ch : 2;
                    var frameBytes = streamChannels * 2;
                    if (size % frameBytes != 0)
                        throw new IOException("Unaligned PCM in the FFmpeg preview stream.");
                    var maxRead = (scratch.Length / frameBytes) * frameBytes;
                    var remaining = size;
                    while (remaining > 0)
                    {
                        var length = (int)Math.Min(remaining, maxRead);
                        await source.ReadExactlyAsync(scratch.AsMemory(0, length), stop).ConfigureAwait(false);
                        if (multiStreamInterleaver is not null)
                        {
                            multiStreamInterleaver.Push(audioStreamIdx, scratch.AsSpan(0, length),
                                (merged, totalChannels) => audio(merged, totalChannels, null));
                        }
                        else
                        {
                            audio(scratch.AsMemory(0, length), streamChannels, AudioChannelLayout);
                        }
                        remaining -= (uint)length;
                    }
                    if ((size & 1) != 0)
                        await source.ReadExactlyAsync(scratch.AsMemory(0, 1), stop).ConfigureAwait(false);
                    continue;
                }
            }

            if (size > MaxQueuedBytes)
                throw new IOException("Unexpected FFmpeg preview chunk size.");
            size += size & 1;
            while (size > 0)
            {
                var length = (int)Math.Min(size, scratch.Length);
                await source.ReadExactlyAsync(scratch.AsMemory(0, length), stop).ConfigureAwait(false);
                size -= (uint)length;
            }
        }
    }

    private static bool TryParseAudioChunkStream(ReadOnlySpan<byte> tag, out int streamIndex)
    {
        streamIndex = -1;
        if (tag.Length != 4 || tag[2] != (byte)'w' || tag[3] != (byte)'b')
            return false;
        var hi = HexNibble(tag[0]);
        var lo = HexNibble(tag[1]);
        if (hi < 0 || lo < 0)
            return false;
        streamIndex = (hi << 4) | lo;
        return true;
    }

    private static int HexNibble(byte ch) => ch switch
    {
        >= (byte)'0' and <= (byte)'9' => ch - '0',
        >= (byte)'a' and <= (byte)'f' => ch - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => ch - 'A' + 10,
        _ => -1
    };

    private sealed class MultiStreamAudioInterleaver
    {
        private const int MaxBufferedFrames = 16384;
        private const int SilentStreamFlushFrames = 4096;
        private readonly int[] streamOrder;
        private readonly int[] streamChannels;
        private readonly Queue<short>[] queues;
        private readonly int totalChannels;
        private byte[] outputBuffer = [];

        public MultiStreamAudioInterleaver(Dictionary<int, int> streams)
        {
            var ordered = streams.OrderBy(pair => pair.Key).ToArray();
            var list = new List<(int Id, int Channels)>();
            var sum = 0;
            foreach (var pair in ordered)
            {
                if (sum + pair.Value > 16)
                    break;
                list.Add((pair.Key, pair.Value));
                sum += pair.Value;
            }
            streamOrder = list.Select(x => x.Id).ToArray();
            streamChannels = list.Select(x => x.Channels).ToArray();
            queues = Enumerable.Range(0, list.Count).Select(_ => new Queue<short>()).ToArray();
            totalChannels = Math.Max(1, sum);
        }

        public void Push(int streamId, ReadOnlySpan<byte> pcmBytes, Action<ReadOnlyMemory<byte>, int> emit)
        {
            var slot = Array.IndexOf(streamOrder, streamId);
            if (slot < 0)
                return;

            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcmBytes);
            var q = queues[slot];
            var maxSamples = MaxBufferedFrames * streamChannels[slot];
            foreach (var t in samples)
                q.Enqueue(t);

            while (q.Count > maxSamples)
            {
                for (var c = 0; c < streamChannels[slot] && q.Count > 0; c++)
                    q.Dequeue();
            }

            // Lockstep sync when all streams have queued frames; if any stream is absent or drops out
            // mid-stream, flush active streams once SilentStreamFlushFrames accumulate so audio never stalls.
            var minAllFrames = int.MaxValue;
            var minNonEmptyFrames = int.MaxValue;
            var maxQueuedFrames = 0;
            for (var i = 0; i < queues.Length; i++)
            {
                var queuedFrames = queues[i].Count / streamChannels[i];
                if (queuedFrames > maxQueuedFrames)
                    maxQueuedFrames = queuedFrames;
                if (queuedFrames < minAllFrames)
                    minAllFrames = queuedFrames;
                if (queuedFrames > 0 && queuedFrames < minNonEmptyFrames)
                    minNonEmptyFrames = queuedFrames;
            }

            var readyFrames = minAllFrames > 0
                ? minAllFrames
                : maxQueuedFrames >= SilentStreamFlushFrames && minNonEmptyFrames < int.MaxValue
                    ? minNonEmptyFrames
                    : 0;
            if (readyFrames is <= 0 or int.MaxValue)
                return;

            var neededBytes = readyFrames * totalChannels * 2;
            if (outputBuffer.Length < neededBytes)
                outputBuffer = new byte[neededBytes];
            var dest = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(outputBuffer.AsSpan(0, neededBytes));
            var outIdx = 0;
            for (var f = 0; f < readyFrames; f++)
            {
                for (var s = 0; s < queues.Length; s++)
                {
                    var sq = queues[s];
                    var chCount = streamChannels[s];
                    if (sq.Count >= chCount)
                    {
                        for (var c = 0; c < chCount; c++)
                            dest[outIdx++] = sq.Dequeue();
                    }
                    else
                    {
                        for (var c = 0; c < chCount; c++)
                            dest[outIdx++] = 0;
                    }
                }
            }
            emit(outputBuffer.AsMemory(0, neededBytes), totalChannels);
        }
    }

    private static async ValueTask<bool> ReadHeader(Stream stream, byte[] buffer, int size, CancellationToken stop)
    {
        var first = await stream.ReadAsync(buffer.AsMemory(0, size), stop).ConfigureAwait(false);
        if (first == 0)
            return false;
        await stream.ReadExactlyAsync(buffer.AsMemory(first, size - first), stop).ConfigureAwait(false);
        return true;
    }

    private async Task<string> DrainErrors(StreamReader reader, ChannelWriter<FireWireFrameInfo> fields)
    {
        var last = "";
        string? codec = null;
        var sourceAudio = new Dictionary<string, CaptureAudioInfo>(StringComparer.Ordinal);
        var readingInput = true;
        var sourcePids = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Contains("Parsed_ashowinfo", StringComparison.Ordinal))
                {
                    var decoded = DecodedAudioLog.Match(line);
                    if (decoded.Success)
                    {
                        var layout = decoded.Groups["layout"].Value;
                        if (!string.IsNullOrWhiteSpace(layout))
                            Volatile.Write(ref audioChannelLayout, layout);
                        if (AudioInfo is { } current &&
                            int.TryParse(decoded.Groups["channels"].Value, CultureInfo.InvariantCulture, out var channels) && channels > 0 &&
                            int.TryParse(decoded.Groups["rate"].Value, CultureInfo.InvariantCulture, out var rate) && rate > 0 &&
                            (current.SampleRateHz != rate || current.Channels != channels))
                        {
                            var bitRate = current.BitDepth is { } bits ? (long)bits * rate * channels : (long?)null;
                            Volatile.Write(ref audioInfo, current with { SampleRateHz = rate, Channels = channels, BitRate = bitRate });
                        }
                    }
                    continue;
                }
                if (!line.Contains("Parsed_showinfo", StringComparison.Ordinal))
                {
                    if (line.TrimStart().StartsWith("Stream mapping:", StringComparison.Ordinal))
                        readingInput = false;
                    if (readingInput)
                    {
                        var pid = SourcePid.Match(line);
                        if (pid.Success && int.TryParse(pid.Groups["pid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
                            sourcePids[pid.Groups["stream"].Value] = parsed;
                        var stream = AudioStreamLog.Match(line);
                        if (stream.Success)
                            sourceAudio[stream.Groups["stream"].Value] = ReadSourceAudio(line, stream);
                    }

                    var mapping = VideoMapping.Match(line);
                    if (mapping.Success)
                    {
                        codec = mapping.Groups["codec"].Value;
                        if (sourcePids.TryGetValue(mapping.Groups["stream"].Value, out var pid))
                            Volatile.Write(ref videoPid, pid);
                    }
                    var audioMapping = AudioMapping.Match(line);
                    if (audioMapping.Success)
                    {
                        var index = audioMapping.Groups["stream"].Value;
                        if (sourceAudio.TryGetValue(index, out var selected))
                            Volatile.Write(ref audioInfo, selected);
                        if (sourcePids.TryGetValue(index, out var pid))
                            Volatile.Write(ref audioPid, pid);
                    }
                    if (!string.IsNullOrWhiteSpace(line))
                        last = line;
                    continue;
                }
                var match = FrameLog.Match(line);
                if (!match.Success)
                    continue;
                var width = int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture);
                var height = int.Parse(match.Groups["height"].Value, CultureInfo.InvariantCulture);
                var sarN = int.Parse(match.Groups["sarN"].Value, CultureInfo.InvariantCulture);
                var sarD = int.Parse(match.Groups["sarD"].Value, CultureInfo.InvariantCulture);
                var timestamp = double.TryParse(match.Groups["time"].Value, CultureInfo.InvariantCulture, out var pts)
                    ? pts : double.NaN;
                bool? field = match.Groups["field"].Value[0] switch { 'T' => true, 'B' => false, _ => null };
                var aspect = sarN > 0 && sarD > 0 ? (double)width * sarN / (height * sarD) : (double)width / height;
                if (!fields.TryWrite(new(width, height, field, aspect, timestamp, codec, match.Groups["format"].Value)))
                    fields.TryComplete(new IOException("FFmpeg preview metadata exceeded its buffer."));
            }
            return last;
        }
        finally
        {
            fields.TryComplete();
        }
    }

    private static CaptureAudioInfo ReadSourceAudio(string line, Match stream)
    {
        var codec = stream.Groups["codec"].Value;
        var profile = stream.Groups["profile"];
        var label = codec switch
        {
            "aac" when profile.Success => $"AAC ({profile.Value})",
            "aac" => "AAC",
            "mp2" => "MP2",
            "mp3" => "MP3",
            "ac3" => "AC-3",
            "eac3" => "E-AC-3",
            "flac" => "FLAC",
            _ => codec
        };
        var audio = AudioRateAndLayout.Match(line);
        int? sampleRate = int.TryParse(audio.Groups["rate"].Value, CultureInfo.InvariantCulture, out var hz) && hz > 0 ? hz : null;
        var channels = ReadChannelCount(audio.Groups["layout"].Value);
        var pcm = PcmDepth.Match(codec);
        int? depth = pcm.Success && int.TryParse(pcm.Groups[1].Value, CultureInfo.InvariantCulture, out var bits) ? bits : null;
        long? bitRate = depth is { } pcmBits && sampleRate is { } rate && channels is { } count
            ? (long)pcmBits * rate * count : null;
        var reportedRate = AudioBitRate.Match(line);
        if (bitRate is null && decimal.TryParse(reportedRate.Groups["rate"].Value, CultureInfo.InvariantCulture, out var reported))
        {
            var multiplier = reportedRate.Groups["unit"].Value switch { "M" => 1_000_000, "k" => 1_000, _ => 1 };
            if (reported > 0 && reported <= long.MaxValue / multiplier)
                bitRate = (long)(reported * multiplier);
        }
        return new CaptureAudioInfo(label, depth, sampleRate, channels, bitRate, BitRateEstimated: depth is null && bitRate is not null);
    }

    private static int? ReadChannelCount(string layout)
    {
        switch (layout)
        {
            case "mono": return 1;
            case "stereo": return 2;
            case "surround": return 3;
            case "quad": return 4;
            case "hexagonal": return 6;
            case "octagonal": return 8;
        }
        var name = layout.Split(' ', '(')[0];
        if (layout.EndsWith(" channels", StringComparison.Ordinal) &&
            int.TryParse(name, CultureInfo.InvariantCulture, out var channels) && channels > 0)
            return channels;
        if (!name.Contains('.'))
            return null;
        var count = 0;
        foreach (var component in name.Split('.'))
        {
            if (!int.TryParse(component, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 64)
                return null;
            count += value;
        }
        return count > 0 ? count : null;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
