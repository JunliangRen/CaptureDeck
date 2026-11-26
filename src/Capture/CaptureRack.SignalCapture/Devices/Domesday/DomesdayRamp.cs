using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CaptureRack.SignalCapture;

public sealed record RampResult(long Samples, int FirstCode, int Period);

// New gateware cycles through 0..1020. Older generators can wrap at 1024.
internal sealed class DomesdayRamp(bool require1021)
{
    private int first = -1, expected, period = require1021 ? 1021 : 0;
    private long count;
    public void Check(int code)
    {
        if (count == 0)
        {
            first = code;
            expected = code;
        }
        else if (period == 0 && code == 0 && expected is 1021 or 1024)
        {
            period = expected;
            expected = 0;
        }
        if (code != expected || (period != 0 && code >= period))
            throw new InvalidDataException($"FPGA ramp at sample {count}: expected {expected}, received {code}.");
        expected = code + 1;
        if (expected == period) expected = 0;
        count++;
    }
    public RampResult Result => new(count, first, period);
}

public static class DomesdayRampFile
{
    // Exact PCM comparison also checks all six padding bits and the initial phase.
    internal static void VerifyPcm(Stream stream, RampResult live, int factor = 1)
    {
        if (live.Samples <= 0 || live.FirstCode < 0) throw new InvalidDataException("Empty test capture.");
        // A short legacy capture can finish before its first wrap.
        int period = live.Period == 0 ? 1024 : live.Period;
        if (factor > 1)
        {
            var decimator = new DomesdayDecimator(factor);
            var input = new short[8192];
            var output = new short[input.Length];
            var actual = new byte[input.Length * 2];
            void Compare(int count)
            {
                stream.ReadExactly(actual.AsSpan(0, count * 2));
                if (!actual.AsSpan(0, count * 2).SequenceEqual(MemoryMarshal.AsBytes(output.AsSpan(0, count))))
                    throw new InvalidDataException("Resampled ramp differs from expected PCM.");
            }
            int code = live.FirstCode;
            for (long offset = 0; offset < live.Samples; offset += input.Length)
            {
                int size = (int)Math.Min(input.Length, live.Samples - offset);
                for (int i = 0; i < size; i++)
                {
                    input[i] = (short)((code - 512) << 6);
                    if (++code == period) code = 0;
                }
                Compare(decimator.Process(input.AsSpan(0, size), output));
            }
            Compare(decimator.Finish(output));
            if (stream.ReadByte() != -1) throw new InvalidDataException("Extra resampled ramp data.");
            return;
        }
        var cycle = new short[period];
        for (int i = 0; i < cycle.Length; i++) cycle[i] = (short)((((live.FirstCode + i) % period) - 512) << 6);
        var expected = MemoryMarshal.AsBytes(cycle.AsSpan());
        var buffer = new byte[expected.Length * 128];
        long bytes = 0;
        int length;
        while ((length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
        {
            for (int offset = 0; offset < length; offset += expected.Length)
            {
                var slice = buffer.AsSpan(offset, Math.Min(expected.Length, length - offset));
                if (slice.SequenceEqual(expected[..slice.Length])) continue;
                int i = 0;
                while (slice[i] == expected[i]) i++;
                throw new InvalidDataException($"Recorded ramp differs at sample {(bytes + offset + i) / 2}.");
            }
            bytes += length;
        }
        if (bytes != checked(live.Samples * 2))
            throw new InvalidDataException($"Recorded ramp length {bytes} bytes, expected {live.Samples * 2}.");
    }

    public static void Verify(string path, bool flac, string? ffmpeg, RampResult live, int factor = 1)
    {
        if (!flac)
        {
            using var stream = File.OpenRead(path);
            VerifyPcm(stream, live, factor);
            return;
        }
        var start = new ProcessStartInfo(ffmpeg ?? throw new IOException("FFmpeg required for FLAC verification."))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-nostdin", "-nostats", "-v", "error", "-xerror", "-err_detect", "crccheck+explode",
            "-i", path, "-map", "0:a:0", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not launch FFmpeg.");
        var errors = process.StandardError.ReadToEndAsync();
        try { VerifyPcm(process.StandardOutput.BaseStream, live, factor); }
        finally
        {
            process.StandardOutput.Close();
            if (!process.HasExited && !process.WaitForExit(5000)) process.Kill();
            process.WaitForExit();
        }
        if (process.ExitCode != 0) throw new IOException($"FLAC verification failed: {errors.GetAwaiter().GetResult()}");
    }
}

public sealed record RfRampQc(string FinalPath, bool Flac, string? Ffmpeg, RampResult Live, int SoftwareDecimation = 1)
{
    public long FileSamples => (Live.Samples + SoftwareDecimation - 1) / SoftwareDecimation;
    public void VerifyAndPublish()
    {
        string partial = FinalPath + ".partial";
        DomesdayRampFile.Verify(partial, Flac, Ffmpeg, Live, SoftwareDecimation);
        using (var summary = new FileStream(FinalPath + ".ramp.json", FileMode.CreateNew))
            System.Text.Json.JsonSerializer.Serialize(summary, new { Live, VerifiedFileSamples = FileSamples });
        File.Move(partial, FinalPath);
    }
}
