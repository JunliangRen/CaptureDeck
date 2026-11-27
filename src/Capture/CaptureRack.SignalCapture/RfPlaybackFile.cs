using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CaptureRack.SignalCapture;

public sealed record RfPlaybackFile(string Path, long Samples, int Rate, bool IsIq, int ContainerRate = 0, uint CenterFrequency = 0, bool IsHifiRecording = false)
{
    public static readonly string[] Extensions = [".flac", ".ldf", ".s16", ".raw", ".cu8"];
    public bool SupportsHifi => !IsIq && Rate is 8_000_000 or 8_192_000 or 10_000_000;
    public bool SupportsVideo => !IsIq && Rate is 20_000_000 or 40_000_000;

    public static RfPlaybackFile Open(string path, string? ffprobe)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(ext)) throw new InvalidDataException("Unsupported RF file format.");
        var match = Regex.Match(System.IO.Path.GetFileName(path), @"(\d+(?:\.\d+)?)msps", RegexOptions.RightToLeft | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool hifiName = Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path),
            @"(?:^|[_-])hifi(?:[_-]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var tuning = Regex.Match(System.IO.Path.GetFileName(path), @"RTL-(\d+)Hz", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        uint.TryParse(tuning.Groups[1].Value, out uint center);
        int namedRate = match.Success ? checked((int)Math.Round(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 1e6)) : 0;
        if (ext is ".s16" or ".raw" or ".cu8")
        {
            long bytes = new FileInfo(path).Length;
            if (bytes == 0 || bytes % 2 != 0) throw new InvalidDataException("Incomplete RF sample file.");
            return new(path, bytes / 2, namedRate > 0 ? namedRate : ext == ".cu8" ? RfRecording.DefaultRtlSampleRate : 40_000_000, ext == ".cu8", CenterFrequency: center, IsHifiRecording: hifiName);
        }

        using var probe = Start(ffprobe, ["-v","error","-select_streams","a:0","-show_streams","-show_format","-of","json",path]);
        var errors = probe.StandardError.ReadToEndAsync();
        using var json = JsonDocument.Parse(probe.StandardOutput.BaseStream);
        probe.WaitForExit();
        if (probe.ExitCode != 0) throw new IOException(errors.GetAwaiter().GetResult());
        var streams = json.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() == 0) throw new InvalidDataException("Missing RF stream.");
        var stream = streams[0];
        int channels = stream.GetProperty("channels").GetInt32();
        if (stream.GetProperty("codec_name").GetString() != "flac" || channels is not (1 or 2))
            throw new InvalidDataException("Expected mono RF or stereo I/Q FLAC.");
        int containerRate = int.Parse(stream.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture);
        var format = json.RootElement.GetProperty("format");
        string? Tag(string key)
        {
            foreach (var section in new[] { stream, format })
                if (section.TryGetProperty("tags", out var tags))
                    foreach (var prop in tags.EnumerateObject())
                        if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return prop.Value.GetString();
            return null;
        }
        string? rateTag = Tag("RF_SAMPLE_RATE_HZ") ?? Tag("RF_SAMPLE_RATE");
        int rate = int.TryParse(rateTag, out int tagged) && tagged > 0 ? tagged :
            namedRate > 0 ? namedRate : containerRate * 1000;
        long samples;
        // MISRC FLAC can scale both STREAMINFO rate and duration_ts by 1000.
        // RF_TOTAL_SAMPLES retains the exact number of samples in the frames.
        if (!long.TryParse(Tag("RF_TOTAL_SAMPLES"), out samples) || samples <= 0)
        {
            if (stream.TryGetProperty("duration_ts", out var ticks) && stream.GetProperty("time_base").GetString() == $"1/{containerRate}")
                samples = ticks.GetInt64();
            else if (format.TryGetProperty("duration", out var duration))
                samples = checked((long)Math.Round(decimal.Parse(duration.GetString()!, CultureInfo.InvariantCulture) * containerRate));
        }
        if (samples <= 0 || rate <= 0) throw new InvalidDataException("Missing RF duration or sample rate.");
        if (uint.TryParse(Tag("SOURCE_CENTER_FREQUENCY"), out uint taggedCenter)) center = taggedCenter;
        string? source = Tag("RF_SOURCE");
        bool hifi = source is null ? hifiName : source.Equals("RTLSDR_HIFI", StringComparison.OrdinalIgnoreCase);
        return new(path, samples, rate, channels == 2, containerRate, center, hifi);
    }

    private static Process Start(string? executable, string[] args)
    {
        var info = new ProcessStartInfo(executable ?? throw new IOException("FFmpeg and FFprobe are required for RF FLAC playback."))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Cannot start RF file reader.");
    }

    public void Play(long first, string? ffmpeg, Action<RfMeasurement> report, Action<byte[],int,long>? offer, CancellationToken stop)
    {
        if (first < 0 || first > Samples) throw new ArgumentOutOfRangeException(nameof(first));
        if (first == Samples) return;
        using var process = ContainerRate == 0 ? null : Start(ffmpeg, ["-nostdin","-v","error","-xerror","-ss",
            ((decimal)first / ContainerRate).ToString("0.#########", CultureInfo.InvariantCulture), "-i",Path,"-map","0:a:0",
            "-f",IsIq ? "u8" : "s16le","pipe:1"]);
        var errors = process?.StandardError.ReadToEndAsync();
        void Terminate()
        {
            if (process is null) return;
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
        }
        using var registration = stop.Register(Terminate);
        try
        {
            using Stream input = process?.StandardOutput.BaseStream ?? File.OpenRead(Path);
            if (process is null) input.Position = checked(first * 2);
            var a = new RfRecording.LevelWindow();
            var b = new RfRecording.LevelWindow();
            var buffer = new byte[Math.Clamp(Rate / 50, 2, 262144) & ~1];
            var clock = Stopwatch.StartNew();
            long nextReport = 0;
            long pos = first;
            while (pos < Samples)
            {
                stop.ThrowIfCancellationRequested();
                int count = (int)Math.Min(buffer.Length / 2, Samples - pos) * 2;
                input.ReadExactly(buffer, 0, count);
                if (IsIq) { a.AddIq(buffer.AsSpan(0,count), 0); b.AddIq(buffer.AsSpan(0,count), 1); }
                else a.AddDdd(MemoryMarshal.Cast<byte,short>(buffer.AsSpan(0,count)));
                offer?.Invoke(buffer,count,pos);
                pos += count / 2;
                int wait = (int)Math.Max(0, (pos-first)*1000d/Rate - clock.Elapsed.TotalMilliseconds);
                if (wait > 0 && stop.WaitHandle.WaitOne(wait)) stop.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds >= nextReport || pos == Samples)
                {
                    report(new(pos, IsIq ? 128 : 512, a.Take(), IsIq ? b.Take() : null));
                    nextReport = clock.ElapsedMilliseconds + 100;
                }
            }
            if (process is not null)
            {
                // Drain the pipe before waiting, including any container rounding at EOF.
                input.CopyTo(Stream.Null);
                process.WaitForExit();
                if (process.ExitCode != 0) throw new IOException(errors!.GetAwaiter().GetResult());
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { throw new OperationCanceledException(stop); }
        finally { Terminate(); }
    }
}
