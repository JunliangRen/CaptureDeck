using System.Buffers.Binary;
using System.Diagnostics;
using CaptureRack.QualityControl;

namespace CaptureRack.DirectShow.Windows.FireWire.MsTape;

internal sealed record FireWireSimulation(string Id, string Label, FireWireMediaFormat Format, bool Pal = false, bool Progressive = false)
{
    private const int ClipSeconds = 6;
    private int TransportBitRate => Progressive ? 19700000 : 25000000;
    internal static IReadOnlyList<FireWireSimulation> All { get; } =
    [
        new("simulate:dv-ntsc", "MiniDV NTSC", FireWireMediaFormat.Dv),
        new("simulate:dv-pal", "MiniDV PAL", FireWireMediaFormat.Dv, Pal: true),
        new("simulate:dv25-ntsc", "DVCPRO 25 NTSC", new(null, DvEncoding.Dvcpro25)),
        new("simulate:dv25-pal", "DVCPRO 25 PAL", new(null, DvEncoding.Dvcpro25), Pal: true),
        new("simulate:dv50-ntsc", "DVCPRO50 NTSC", new(null, DvEncoding.Dvcpro50)),
        new("simulate:dv50-pal", "DVCPRO50 PAL", new(null, DvEncoding.Dvcpro50), Pal: true),
        new("simulate:dvh1-1080i59", "DVCPRO HD 1080i59.94", new(null, DvEncoding.DvcproHd)),
        new("simulate:dvh1-1080i50", "DVCPRO HD 1080i50", new(null, DvEncoding.DvcproHd), Pal: true),
        new("simulate:dvh1-720p59", "DVCPRO HD 720p59.94", new(null, DvEncoding.DvcproHd), Progressive: true),
        new("simulate:dvh1-720p50", "DVCPRO HD 720p50", new(null, DvEncoding.DvcproHd), Pal: true, Progressive: true),
        new("simulate:hdv-720p", "HDV 720p · TS188", new(TsLayout.Ts188), Progressive: true),
        new("simulate:hdv-188", "HDV 1080i · TS188", new(TsLayout.Ts188)),
        new("simulate:hdv-192", "HDV 1080i · TS192", new(TsLayout.Ts192))
    ];

    internal FireWireDevice Device => new(Id, Label, "", Label, "") { IsDv = Format.IsDv, SimulatedFormat = Format };

    private bool Professional => Format.IsDv && Format.Encoding != DvEncoding.Consumer;
    private bool Hd => Format.Encoding == DvEncoding.DvcproHd;
    private int DifChannels => Hd && !Progressive ? 4 : Format.Encoding == DvEncoding.Dvcpro50 || Hd ? 2 : 1;
    internal int UnitBytes => Format.Transport?.Stride ?? (Pal ? 144000 : 120000) * DifChannels;
    internal int SampleUnits => Format.IsDv ? 1 : 128;
    internal double SecondsPerUnit => Format.IsDv
        ? (Pal ? 1d / 25 : 1001d / 30000) / (Progressive ? 2 : 1)
        : 188d * 8 / TransportBitRate;

    internal async Task<byte[]> MakeFixtureAsync(string executable, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        var video = Format.IsDv ? DvVideo() :
            Progressive ? "1280x720:rate=30000/1001" : "1440x1080:rate=30000/1001";
        var videoBitRate = Progressive ? "18M" : "22M";
        string[] scan = Progressive ? [] : ["-vf", "setfield=tff", "-flags", "+ilme+ildct"];
        string[] common = ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", $"testsrc2=size={video}",
            "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000", "-t", Professional ? "3" : ClipSeconds.ToString(), "-ac", "2"];
        string[] options = Format.IsDv
            ? [.. DvEncodingOptions(), "-threads:v", "2", "-timecode", "01:00:00:00",
                "-metadata", "creation_time=2006-05-04T03:02:01Z", "-f", "dv"]
            : ["-c:v", "mpeg2video", "-threads:v", "2", "-pix_fmt", "yuv420p", .. scan,
                "-g", "15", "-b:v", videoBitRate, "-maxrate", Progressive ? "19M" : "25M", "-bufsize", "1835k", "-aspect", "16:9",
                "-c:a", "mp2", "-b:a", "384k", "-ar", "48000", "-muxrate", TransportBitRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-metadata", "service_name=CaptureRack simulated HDV", "-f", "mpegts"];
        foreach (var argument in common.Concat(options).Append("pipe:1"))
            start.ArgumentList.Add(argument);
        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("Could not start FFmpeg for the i.LINK simulation.");
        using var stop = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
        });
        using var output = new MemoryStream();
        var diagnostic = process.StandardError.ReadToEndAsync(cancellation);
        await process.StandardOutput.BaseStream.CopyToAsync(output, cancellation).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        var error = await diagnostic.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException($"Could not generate the i.LINK simulation: {error.Trim()}");
        var bytes = output.ToArray();
        var unit = Format.IsDv ? UnitBytes : 188;
        if (bytes.Length < unit || bytes.Length % unit != 0)
            throw new InvalidDataException("FFmpeg produced an incomplete i.LINK simulation fixture.");
        return Format.Transport == TsLayout.Ts192 ? AddSimulatedPacketPrefixes(bytes) : bytes;
    }

    private string DvVideo()
    {
        if (!Hd)
            return Pal ? "720x576:rate=25" : "720x480:rate=30000/1001";
        if (Progressive)
            return Pal ? "960x720:rate=50" : "960x720:rate=60000/1001";
        return Pal ? "1440x1080:rate=25" : "1280x1080:rate=30000/1001";
    }

    private string[] DvEncodingOptions()
    {
        if (!Professional)
            return ["-target", Pal ? "pal-dv" : "ntsc-dv"];
        string[] secondPair = DifChannels > 1 ? ["-map", "1:a"] : [];
        // FFmpeg's DV muxer takes one 25/29.97 Hz PCM block per coded 720p frame.
        string[] audioPacing = Hd && Progressive ? ["-af", "asetpts=PTS/2"] : [];
        return ["-map", "0:v", "-map", "1:a", .. secondPair, "-c:v", "dvvideo", "-pix_fmt",
            Format.Encoding == DvEncoding.Dvcpro25 ? "yuv411p" : "yuv422p", "-c:a", "pcm_s16le", .. audioPacing];
    }

    private byte[] AddSimulatedPacketPrefixes(byte[] ts)
    {
        var result = new byte[ts.Length / 188 * 192];
        for (int i = 0; i < result.Length / 192; i++)
        {
            // Simulate an arrival timestamp. Device-specific interpretation is outside the replay contract.
            var target = result.AsSpan(i * 192, 192);
            var arrival = (uint)((long)i * 188 * 8 * 27000000 / TransportBitRate) & 0x3fffffff;
            BinaryPrimitives.WriteUInt32BigEndian(target, arrival);
            ts.AsSpan(i * 188, 188).CopyTo(target[4..]);
        }
        return result;
    }
}
