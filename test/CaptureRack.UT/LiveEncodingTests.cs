using System.Buffers.Binary;
using System.Diagnostics;
using CaptureRack.App;
using CaptureRack.DeckLink;
using CaptureRack.QualityControl;
using CaptureRack.Recording;
using CaptureRack.Recording.Matroska;
using Xunit;

namespace CaptureRack.UT;

public sealed class LiveEncodingTests
{
    [Fact]
    public void NewSettingsUseNativeOutput()
    {
        Assert.Equal(RecordingOutput.Native, new CaptureSettings().RecordingEncoding.Output);
    }

    [Theory]
    [InlineData("{\"DeckLink\":{}}", RecordingOutput.Native)]
    [InlineData("{\"DeckLink\":{},\"RecordingEncoding\":null}", RecordingOutput.Native)]
    [InlineData("{\"DeckLink\":{},\"RecordingEncoding\":{\"Output\":99}}", RecordingOutput.Native)]
    [InlineData("{\"DeckLink\":{},\"RecordingEncoding\":{\"Output\":1}}", RecordingOutput.Ffv1)]
    public void SettingsMigrationOnlyRestoresValidSelectedEncoding(string json, RecordingOutput expected)
    {
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(expected, CaptureSettingsStore.Read(input).RecordingEncoding.Output);
    }

    [Fact]
    [Trait("Category", "FFmpegIntegration")]
    public async Task Ffv1PreservesFullRangeDecodedMjpegPixels()
    {
        if (QcReports.FindTools() is not (string ffmpeg, string ffprobe))
            return;
        var folder = Directory.CreateTempSubdirectory("capturerack-live-mjpeg-");
        try
        {
            var jpeg = Path.Combine(folder.FullName, "input.jpg");
            await Run(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=128x96:rate=25",
                "-frames:v", "1", "-c:v", "mjpeg", "-f", "mjpeg", jpeg);
            var frame = await File.ReadAllBytesAsync(jpeg);
            var bitmap = new byte[40];
            BinaryPrimitives.WriteInt32LittleEndian(bitmap, 40);
            BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(4), 128);
            BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(8), 96);
            BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(12), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(14), 24);
            "MJPG"u8.CopyTo(bitmap.AsSpan(16));
            var path = Path.Combine(folder.FullName, "encoded.mkv");
            using (var recording = new LiveEncodedRecording(path, new(RecordingOutput.Ffv1), new(bitmap, 128, 96, 400000, 0, null), null, ffmpeg))
            {
                recording.Write(true, 0, 400000, frame);
                recording.Finish();
            }
            Task<string> Hash(string file) => Run(ffmpeg, "-v", "error", "-i", file,
                "-vf", "scale=in_range=full:out_range=full", "-c:v", "rawvideo", "-pix_fmt", "yuv420p",
                "-f", "hash", "-hash", "sha256", "pipe:1");
            Assert.Equal(await Hash(jpeg), await Hash(path));
            Assert.Equal("pc", (await Run(ffprobe, "-v", "error", "-show_entries",
                "stream=color_range", "-of", "csv=p=0", path)).Trim());
        }
        finally { folder.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(RecordingOutput.Ffv1, "ffv1", null)]
    [InlineData(RecordingOutput.Ffv1, "ffv1", null, 32)]
    [InlineData(RecordingOutput.H264, "h264", "3.0")]
    [InlineData(RecordingOutput.H265, "hevc", "3.0")]
    [InlineData(RecordingOutput.Av1, "av1", "3.0")]
    [InlineData(RecordingOutput.Mpeg2, "mpeg2video", "main")]
    [Trait("Category", "FFmpegIntegration")]
    public async Task EncodesLiveAndRecoversCompleteClusters(RecordingOutput output, string expected, string? level, int bits = 16)
    {
        if (QcReports.FindTools() is not (string ffmpeg, string ffprobe))
            return;
        var folder = Directory.CreateTempSubdirectory("capturerack-live-encoding-");
        try
        {
            const int width = 128, height = 96, frameTicks = 400000;
            var bitmap = new byte[40];
            BinaryPrimitives.WriteInt32LittleEndian(bitmap, 40);
            BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(8), height);
            BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(12), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(14), 16);
            "UYVY"u8.CopyTo(bitmap.AsSpan(16));
            var video = new NativeVideoTrack(bitmap, width, height, frameTicks, 0, null);
            var audioTrack = new NativePcmTrack(48000, 2, bits, bits / 4);
            var path = Path.Combine(folder.FullName, "encoded.mkv");
            var partial = path + ".partial.mkv";
            var frame = new byte[width * height * 2];
            var audio = new byte[1920 * audioTrack.BlockAlign];
            using (var recording = new LiveEncodedRecording(path, new(output, 500, level), video, audioTrack, ffmpeg))
            {
                for (var i = 0; i < 80; i++)
                {
                    var start = (long)i * frameTicks;
                    recording.Write(true, start, start + frameTicks, frame);
                    recording.Write(false, start, start + frameTicks, audio);
                }

                var end = Stopwatch.StartNew();
                while (new FileInfo(partial).Length == 0 && end.Elapsed < TimeSpan.FromSeconds(10))
                    await Task.Delay(50);
                Assert.True(new FileInfo(partial).Length > 0, "Expected encoded bytes before stop.");
                recording.Finish();
            }

            var streams = await Probe(ffprobe, path);
            Assert.Contains(expected, streams);
            Assert.Contains(output == RecordingOutput.Ffv1 ? "flac" : "pcm_s16le", streams);
            if (output == RecordingOutput.Ffv1)
                Assert.Contains($"flac,{bits}", streams);
            Assert.True(File.Exists(path + ".encoding.json"));

            var bytes = await File.ReadAllBytesAsync(path);
            var broken = Path.Combine(folder.FullName, "broken.mkv");
            var recovered = Path.Combine(folder.FullName, "recovered.mkv");
            using var evidence = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path + ".encoding.json"));
            var truncation = evidence.RootElement.GetProperty("completeClusterBytes").GetInt32() - 17;
            await File.WriteAllBytesAsync(broken, bytes[..truncation]);
            var count = await LiveMatroskaRecovery.RecoverAsync(broken, recovered);
            Assert.True(count > 0);
            Assert.True(count < truncation);
            Assert.Contains(expected, await Probe(ffprobe, recovered));
            Assert.Equal(truncation, new FileInfo(broken).Length);

            if (output == RecordingOutput.Ffv1 && bits == 16)
            {
                // Flip a byte inside the last complete cluster. The outer lengths are unchanged.
                var damaged = Path.Combine(folder.FullName, "bad-crc.mkv");
                var crcRecovery = Path.Combine(folder.FullName, "without-bad-cluster.mkv");
                bytes[truncation + 16] ^= 0x01;
                await File.WriteAllBytesAsync(damaged, bytes);
                Assert.Equal(count, await LiveMatroskaRecovery.RecoverAsync(damaged, crcRecovery));

                var crash = Path.Combine(folder.FullName, "interrupted.mkv");
                using var live = new LiveEncodedRecording(crash, new(output), video, audioTrack, ffmpeg);
                for (var i = 0; i < 80; i++)
                {
                    var start = (long)i * frameTicks;
                    live.Write(true, start, start + frameTicks, frame);
                    live.Write(false, start, start + frameTicks, audio);
                }
                var journal = crash + ".partial.mkv.checkpoint.json";
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(journal) && deadline.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(50);
                Assert.True(File.Exists(journal));
                live.AbortEncoder();
                Assert.Throws<IOException>(() => live.Finish());
                Assert.False(File.Exists(crash));
                using var checkpoint = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(journal));
                Assert.NotNull(checkpoint.RootElement.GetProperty("error").GetString());
                var repaired = Path.Combine(folder.FullName, "repaired.mkv");
                Assert.True(await LiveMatroskaRecovery.RecoverAsync(crash + ".partial.mkv", repaired) > 0);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var qc = await QcReports.RunAsync(repaired, cancellation: timeout.Token);
                Assert.True(qc.Status == QcStatus.Passed, qc.Summary);
                Assert.True(qc.DecodedVideoFrames > 0);
                Assert.True(qc.DecodedAudioSampleFrames > 0);
            }
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    [Trait("Category", "FFmpegIntegration")]
    public async Task DeckLinkInterruptedRecordingLeavesAnExplicitlyMarkedPartial()
    {
        if (QcReports.FindTools() is not (string, string ffprobe))
            return;
        var folder = Directory.CreateTempSubdirectory("capturerack-live-overrun-");
        try
        {
            var path = Path.Combine(folder.FullName, "capture.mkv");
            var recorder = new PreviewRecorder();
            using var input = new TestSignal.Session(TestSignalScenario.Normal, new PreviewFeed(), recorder);
            input.Emit(0);
            recorder.Start(path, encoding: new(RecordingOutput.Ffv1));
            for (var frame = 1; frame <= 32; frame++)
            {
                await Task.Delay(34);
                input.Emit(frame);
            }
            const string error = "Simulated encoder overload";
            Assert.Contains(error, Assert.Throws<IOException>(() => recorder.Finish(error)).Message);
            Assert.False(File.Exists(path));
            var partial = path + ".partial.mkv";
            Assert.Contains("ffv1", await Probe(ffprobe, partial));
            using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(partial + ".checkpoint.json"));
            Assert.Equal(error, journal.RootElement.GetProperty("error").GetString());
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    [Trait("Category", "FFmpegIntegration")]
    public async Task ASoftwareEncoderFailureDoesNotStopDeckLinkPreview()
    {
        if (QcReports.FindTools().FfmpegPath is null)
            return;
        var folder = Directory.CreateTempSubdirectory("capturerack-live-failed-encoder-");
        try
        {
            var path = Path.Combine(folder.FullName, "capture.mkv");
            var feed = new PreviewFeed();
            var recorder = new PreviewRecorder();
            using var input = new TestSignal.Session(TestSignalScenario.Normal, feed, recorder);
            input.Emit(0);
            // Syntactically valid, but rejected by x264 when the encoder opens.
            recorder.Start(path, encoding: new(RecordingOutput.H264, 500, "9.9"));
            string? failure = null;
            for (var i = 1; i <= 50 && failure is null; i++)
            {
                await Task.Delay(34);
                input.Emit(i);
                var stats = input.Snapshot();
                Assert.Null(stats.Error);
                failure = stats.RecordingError;
            }
            Assert.NotNull(failure);
            Assert.Throws<IOException>(() => recorder.Finish());
            var before = input.Snapshot().VideoFrames;
            input.Emit(51);
            var current = input.Snapshot();
            Assert.Null(current.Error);
            Assert.True(current.CanStartRecording);
            Assert.True(current.VideoFrames > before);
            Assert.False(File.Exists(path));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(RecordingOutput.Ffv1, false, "ffv1,yuv422p10le,bt")]
    [InlineData(RecordingOutput.H265, true, "hevc,yuv420p,progressive")]
    [Trait("Category", "FFmpegIntegration")]
    public async Task EncodesTheDeckLinkTestSignalWithExpectedScanAndAudioCount(RecordingOutput output,
        bool deinterlace, string expectedVideo)
    {
        if (QcReports.FindTools() is not (string ffmpeg, string ffprobe))
            return;
        var folder = Directory.CreateTempSubdirectory("capturerack-live-decklink-");
        try
        {
            var path = Path.Combine(folder.FullName, "encoded.mkv");
            var recorder = new PreviewRecorder();
            using var session = new TestSignal.Session(TestSignalScenario.Normal, new PreviewFeed(), recorder);
            session.Emit(0);
            var started = recorder.Start(path, encoding: new(output, Deinterlace: deinterlace));
            for (var i = 1; i <= 45; i++)
            {
                await Task.Delay(34);
                session.Emit(i);
            }
            var stats = recorder.Finish();
            Assert.Equal(45, stats.Recording!.VideoFrames);
            var streams = await Probe(ffprobe, path);
            Assert.Contains(expectedVideo, streams);
            Assert.Contains(output == RecordingOutput.Ffv1 ? "flac" : "pcm_s16le", streams);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var log = CaptureQcLog.Create(stats, started, DateTimeOffset.UtcNow,
                "CaptureRack test signal", "Built-in", "SD interlaced test pattern", "Stereo test tone");
            var qc = await QcReports.RunAsync(path, log, cancellation: timeout.Token);
            Assert.True(qc.Status == QcStatus.Passed, qc.Summary);
            Assert.Equal(45, qc.DecodedVideoFrames);
            Assert.Equal(72072, qc.DecodedAudioSampleFrames);

            if (output == RecordingOutput.Ffv1)
            {
                var nativePath = Path.Combine(folder.FullName, "device-original.mov");
                var nativeRecorder = new PreviewRecorder();
                using var native = new TestSignal.Session(TestSignalScenario.Normal, new PreviewFeed(), nativeRecorder);
                native.Emit(0);
                nativeRecorder.Start(nativePath);
                for (var i = 1; i <= 45; i++)
                {
                    await Task.Delay(34);
                    native.Emit(i);
                }
                nativeRecorder.Finish();
                Assert.Equal(await HashDecoded(ffmpeg, nativePath), await HashDecoded(ffmpeg, path));
            }
        }
        finally { folder.Delete(recursive: true); }
    }

    private static async Task<string> Probe(string executable, string path)
        => await Run(executable, "-v", "error", "-show_entries",
            "stream=codec_name,field_order,pix_fmt,bits_per_raw_sample", "-of", "csv=p=0", path);

    private static Task<string> HashDecoded(string ffmpeg, string path) => Run(ffmpeg, "-v", "error", "-apply_cropping", "0", "-i", path,
        "-map", "0:v:0", "-map", "0:a:0", "-fps_mode", "passthrough", "-c:v", "rawvideo", "-pix_fmt", "yuv422p10le",
        "-c:a", "pcm_s16le", "-f", "streamhash", "-hash", "sha256", "pipe:1");

    private static async Task<string> Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var diagnostics = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.ExitCode == 0, await diagnostics);
        return await output;
    }
}
