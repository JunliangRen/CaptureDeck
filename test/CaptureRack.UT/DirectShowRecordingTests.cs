using System.Buffers.Binary;
using CaptureRack.DirectShow.Windows.Capture;
using Xunit;
using CaptureRack.QualityControl;
using System.Text.Json;
using CaptureRack.Recording;

namespace CaptureRack.UT;

public sealed class DirectShowRecordingTests
{
    [Fact]
    public async Task StopRequestsWaitForOwnerCleanupOnlyOnce()
    {
        using var operation = new CaptureRack.Core.CaptureOperation();
        int stops = 0;
        using var registration = operation.Token.Register(() => stops++);
        var first = operation.StopAsync();
        var second = operation.StopAsync();
        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        Assert.True(operation.IsStopping);
        Assert.Equal(1, stops);
        operation.Dispose();
        await first.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await operation.StopAsync();
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task SimulatedGraphCanReplacePreviewFramesAndRecordTwoCompleteClips()
    {
        var graph = new DirectShowSampleSession();
        var format = Video(4, 2);
        graph.SetFormats(format, Audio());
        var folder = Directory.CreateTempSubdirectory();
        byte[] pixels = new byte[16];
        Array.Fill(pixels, (byte)128);
        void Frame(byte luma)
        {
            pixels[0] = luma;
            graph.ReceiveVideo(new(pixels, false));
        }
        void CheckPreview(byte luma)
        {
            Assert.True(graph.Frames.TryRead(out var raw));
            using (raw)
            {
                Assert.Equal(luma, raw.Buffer[0]);
                Assert.True(DirectShowPreviewVideo.TryConvert(format, graph.Encoding!,
                    raw.Buffer.AsSpan(0, raw.Length), new byte[4 * 2 * 4], 4, 2));
            }
        }
        try
        {
            Frame(16);
            CheckPreview(16);
            for (int clip = 1; clip <= 2; clip++)
            {
                var path = Path.Combine(folder.FullName, $"clip{clip}.mkv");
                var writer = graph.StartRecording(path);
                var expected = new List<byte>();
                for (byte frame = 1; frame <= clip * 3; frame++)
                {
                    Frame((byte)(16 + frame));
                    expected.AddRange(pixels);
                    graph.ReceiveAudio(new(new byte[6], false));
                }
                graph.FinishRecording(writer);
                CheckPreview((byte)(16 + clip * 3));
                Assert.Equal(path, await writer.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(NativeCaptureReport.PathFor(path), TestContext.Current.CancellationToken));
                Assert.Equal(clip * 3, report.RootElement.GetProperty("expectedVideoFrames").GetInt64());
                Assert.Equal(clip * 3, report.RootElement.GetProperty("pcmSampleFrames").GetInt64());
                Assert.Equal(0, writer.RejectedPackets);
                if (Environment.GetEnvironmentVariable("CAPTURERACK_TEST_SIGNAL_QC") == "1")
                    await AssertDecodedPixelsAsync(path, expected);
                Frame(48);
                CheckPreview(48);
            }
            Assert.Equal(7, graph.SkippedBeforeDecode); // Two of three and five of six.
            Assert.Equal(12, graph.Received);
            graph.End(null);
            Assert.Throws<InvalidOperationException>(() => graph.StartRecording(Path.Combine(folder.FullName, "late.mkv")));
        }
        finally
        {
            graph.End(null);
            while (graph.Frames.TryRead(out var raw)) raw.Dispose();
            folder.Delete(true);
        }
    }

    private static async Task AssertDecodedPixelsAsync(string path, List<byte> expected)
    {
        var tools = QcReports.FindTools();
        Assert.NotNull(tools.FfmpegPath);
        var start = new System.Diagnostics.ProcessStartInfo(tools.FfmpegPath)
        {
            UseShellExecute = false, RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "-v", "error", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough",
            "-threads", "1", "-pix_fmt", "yuyv422", "-f", "rawvideo", "pipe:1" })
            start.ArgumentList.Add(arg);
        using var ffmpeg = System.Diagnostics.Process.Start(start)!;
        using var decoded = new MemoryStream();
        await ffmpeg.StandardOutput.BaseStream.CopyToAsync(decoded);
        await ffmpeg.WaitForExitAsync();
        Assert.Equal(0, ffmpeg.ExitCode);
        Assert.Equal(expected.ToArray(), decoded.ToArray());
    }

    [Fact]
    public void CompressedFormatsTryTransportStreamFirst()
    {
        Assert.Equal(new[] { ".mov", ".mp4", ".mkv" }, DirectShowRecording.OutputExtensions(Video()));
        var compressed = new[] { Video() with { FourCc = "MJPG" }, Video() with { Subtype = DirectShowFormat.Mpeg2Video } };
        foreach (var video in compressed)
            Assert.Equal(new[] { ".ts", ".mov", ".mp4", ".mkv" }, DirectShowRecording.OutputExtensions(video));
    }

    [Fact]
    public async Task MkvRetainsVideoAndPcmBytesAfterBorrowedBuffersChange()
    {
        var path = TemporaryPath();
        byte[] video = [0x10, 0x80, 0x42, 0x80, 0x7f, 0x80, 0xeb, 0x80];
        byte[] audio = [0x11, 0xBB, 0x70, 0xEF, 0x07, 0x81];
        var record = DirectShowRecording.Start(path, Video(), Audio());
        try
        {
            record.Accept(true, new(video, false, TimeSpan.FromSeconds(10)));
            record.Accept(false, new(audio, false, TimeSpan.FromSeconds(10)));
            Array.Clear(video);
            Array.Clear(audio);
            record.Complete();
            Assert.Equal(path, await record.Completion);
            var data = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.True(data.AsSpan().IndexOf(new byte[] { 0x10, 0x80, 0x42, 0x80, 0x7f, 0x80, 0xeb, 0x80 }) >= 0);
            Assert.True(data.AsSpan().IndexOf(new byte[] { 0x11, 0xBB, 0x70, 0xEF, 0x07, 0x81 }) >= 0);
            Assert.False(File.Exists(record.PartialPath));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(NativeCaptureReport.PathFor(path), TestContext.Current.CancellationToken));
            Assert.Equal(1, report.RootElement.GetProperty("expectedVideoFrames").GetInt64());
            Assert.Equal(1, report.RootElement.GetProperty("pcmSampleFrames").GetInt64());
            Assert.Equal("ToolsUnavailable", report.RootElement.GetProperty("containerAttempts")[0].GetProperty("status").GetString());
            Assert.Equal("Selected", report.RootElement.GetProperty("containerAttempts")[1].GetProperty("status").GetString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task IncompletePcmLeavesPartialAndDoesNotPublishFinalPath()
    {
        var path = TemporaryPath();
        var record = DirectShowRecording.Start(path, Video(), Audio());
        try
        {
            record.Accept(true, new(new byte[8], false));
            record.Accept(false, new(new byte[5], false));
            await Assert.ThrowsAsync<IOException>(async () => await record.Completion);
            Assert.True(File.Exists(record.PartialPath));
            Assert.False(File.Exists(path));
            Assert.Equal(1, record.RejectedPackets);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(NativeCaptureReport.PathFor(record.PartialPath), TestContext.Current.CancellationToken));
            Assert.Equal(1, report.RootElement.GetProperty("writerRejectedPackets").GetInt64());
            Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("driverDroppedVideoFrames").ValueKind);
            Assert.NotNull(report.RootElement.GetProperty("error").GetString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    [Trait("Category", "FFmpegIntegration")]
    public async Task DirectShowUsesTheSharedLiveEncoder()
    {
        var tools = QcReports.FindTools();
        if (tools is not (string, string))
            return;
        var folder = Directory.CreateTempSubdirectory("capturerack-directshow-encoded-");
        try
        {
            var path = Path.Combine(folder.FullName, "encoded.mkv");
            var video = new byte[128 * 96 * 2];
            var audio = new byte[1920 * 6];
            var recording = DirectShowRecording.Start(path, Video(128, 96) with { FrameInterval100ns = 400000 },
                Audio(), tools, encoding: new(RecordingOutput.Ffv1));
            for (var i = 0; i < 25; i++)
            {
                var time = TimeSpan.FromMilliseconds(i * 40);
                var videoTime = i < 10 ? time : time + TimeSpan.FromMilliseconds(40);
                recording.Accept(true, new(video, i == 10, videoTime));
                recording.Accept(false, new(audio, false, time));
            }
            recording.Complete();
            Assert.Equal(path, await recording.Completion);
            var result = await QcReports.RunAsync(path, cancellation: TestContext.Current.CancellationToken);
            Assert.True(result.Status == QcStatus.Passed, result.Summary);
            Assert.Equal(25, result.DecodedVideoFrames);
            Assert.Equal(48000, result.DecodedAudioSampleFrames);
            using var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(path + ".encoding.json", TestContext.Current.CancellationToken));
            Assert.Equal(25, sidecar.RootElement.GetProperty("captureVideoPackets").GetInt64());
            Assert.Equal(1, sidecar.RootElement.GetProperty("captureEstimatedMissingVideoFrames").GetInt64());
            Assert.Equal(1, sidecar.RootElement.GetProperty("captureVideoDiscontinuities").GetInt64());
            Assert.Equal(25, sidecar.RootElement.GetProperty("videoFramesToEncoder").GetInt64());
            Assert.Equal(25, sidecar.RootElement.GetProperty("decodedVideoFrames").GetInt64());
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    [Trait("Category", "FFmpegIntegration")]
    public async Task SoftwareQueueOverrunRejectsFurtherSamplesAndKeepsThePrefix()
    {
        var tools = QcReports.FindTools();
        if (tools is not (string, string))
            return;
        var folder = Directory.CreateTempSubdirectory("capturerack-directshow-overrun-");
        try
        {
            var path = Path.Combine(folder.FullName, "capture.mkv");
            var recording = DirectShowRecording.Start(path, Video(128, 96) with { FrameInterval100ns = 400000 },
                null, tools, encoding: new(RecordingOutput.Ffv1));
            var bytes = new byte[128 * 96 * 2];
            for (var frame = 0; frame < 35; frame++)
                recording.Accept(true, new(bytes, false, TimeSpan.FromMilliseconds(frame * 40)));
            recording.Accept(true, new(new byte[128 * 1024 * 1024 + 1], false));
            recording.Accept(true, new(bytes, false, TimeSpan.FromMilliseconds(1440)));
            var failure = await Assert.ThrowsAsync<IOException>(() => recording.Completion);
            Assert.Contains("did not keep up", failure.Message);
            Assert.Equal(1, recording.RejectedPackets);
            Assert.False(File.Exists(path));
            using var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(recording.PartialPath + ".checkpoint.json", TestContext.Current.CancellationToken));
            Assert.Equal(35, checkpoint.RootElement.GetProperty("videoPacketsToEncoder").GetInt64());
            Assert.Equal(36, checkpoint.RootElement.GetProperty("captureVideoPackets").GetInt64());
            Assert.Equal(1, checkpoint.RootElement.GetProperty("queueRejectedVideoPackets").GetInt64());
            Assert.Equal(0, checkpoint.RootElement.GetProperty("captureEstimatedMissingVideoFrames").GetInt64());
            Assert.True(checkpoint.RootElement.GetProperty("queuePeakBytes").GetInt64() > 0);
            Assert.Contains("did not keep up", checkpoint.RootElement.GetProperty("error").GetString());
            var recovered = Path.Combine(folder.FullName, "recovered.mkv");
            Assert.True(await LiveMatroskaRecovery.RecoverAsync(recording.PartialPath, recovered, TestContext.Current.CancellationToken) > 0);
            var qc = await QcReports.RunAsync(recovered, cancellation: TestContext.Current.CancellationToken);
            Assert.True(qc.Status == QcStatus.Passed, qc.Summary);
            Assert.Equal(35, qc.DecodedVideoFrames);
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    [Trait("Category", "FFmpegIntegration")]
    public async Task AnExitedEncoderCompletesTheSessionEvenWhenInputStops()
    {
        var tools = QcReports.FindTools();
        if (tools.FfmpegPath is null)
            return;
        var folder = Directory.CreateTempSubdirectory("capturerack-directshow-encoder-exit-");
        try
        {
            var path = Path.Combine(folder.FullName, "capture.mkv");
            var recording = DirectShowRecording.Start(path, Video(128, 96) with { FrameInterval100ns = 400000 },
                null, tools, encoding: new(RecordingOutput.H264, 500, "9.9"));
            var bytes = new byte[128 * 96 * 2];
            for (var frame = 0; frame < 35; frame++)
                recording.Accept(true, new(bytes, false, TimeSpan.FromMilliseconds(frame * 40)));
            await Assert.ThrowsAsync<IOException>(() => recording.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(File.Exists(path));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExistingOutputIsNeverOverwritten()
    {
        var path = TemporaryPath();
        try
        {
            await File.WriteAllTextAsync(path, "existing", TestContext.Current.CancellationToken);
            var record = DirectShowRecording.Start(path, Video(), null);
            record.Accept(true, new(new byte[8], false));
            record.Complete();
            await Assert.ThrowsAnyAsync<IOException>(async () => await record.Completion);
            Assert.Equal("existing", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.True(File.Exists(record.PartialPath));
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task MediaQcChecksNativeCountsAndRejectsTruncation(bool mov, bool withAudio)
    {
        if (Environment.GetEnvironmentVariable("CAPTURERACK_TEST_SIGNAL_QC") != "1")
            return;

        var folder = Directory.CreateTempSubdirectory("capturerack-directshow-qc-");
        try
        {
            const int videoFrames = 12;
            const int samplesPerPacket = 1920;
            var tools = QcReports.FindTools();
            Assert.NotNull(tools.FfmpegPath);
            Assert.NotNull(tools.FfprobePath);
            var progress = new List<NativeContainerAttempt>();
            var record = DirectShowRecording.Start(Path.Combine(folder.FullName, "fixture.mkv"),
                Video(64, 48) with { FrameInterval100ns = 400_000 }, withAudio ? Audio() : null,
                mov ? tools : default, "Synthetic DirectShow", progress.Add);
            var video = new byte[64 * 48 * 2];
            var audio = new byte[samplesPerPacket * 6];
            Array.Fill(video, (byte)128);
            for (int i = 0; i < videoFrames; i++)
            {
                var time = TimeSpan.FromMilliseconds(i * 40);
                record.Accept(true, new(video, false, time));
                if (withAudio)
                    record.Accept(false, new(audio, false, time));
            }
            record.Complete();

            var path = await record.Completion;
            Assert.Equal(mov ? ".mov" : ".mkv", Path.GetExtension(path));
            Assert.Equal(NativeContainerStatus.Selected, progress[^1].Status);
            var saved = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            var result = await QcReports.RunAsync(path, cancellation: TestContext.Current.CancellationToken);
            Assert.True(result.Status == QcStatus.Passed, result.Summary);
            Assert.Equal(videoFrames, result.DecodedVideoFrames);
            Assert.Equal(withAudio ? videoFrames * samplesPerPacket : 0, result.DecodedAudioSampleFrames);
            Assert.DoesNotContain(result.Warnings, warning => warning.Contains("no audio track", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(result.FrameMd5));
            Assert.True(File.Exists(result.QcToolsReport));
            using (var report = JsonDocument.Parse(await File.ReadAllTextAsync(result.CaptureLog, TestContext.Current.CancellationToken)))
                Assert.Equal("Synthetic DirectShow", report.RootElement.GetProperty("nativeRecording").GetProperty("device").GetString());
            Assert.Equal(saved, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

            var truncated = saved[..(saved.Length / 2)];
            await File.WriteAllBytesAsync(path, truncated, TestContext.Current.CancellationToken);
            var damaged = await QcReports.RunAsync(path, cancellation: TestContext.Current.CancellationToken);
            Assert.Equal(QcStatus.Failed, damaged.Status);
            Assert.Contains("Recorded file bytes", damaged.Summary);
            Assert.Equal(truncated, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally { folder.Delete(recursive: true); }
    }

    private static DirectShowFormat Video(int width = 2, int height = 2)
    {
        var bitmap = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(bitmap, 40);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(8), height);
        BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(14), 16);
        "YUY2"u8.CopyTo(bitmap.AsSpan(16));
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(20), width * height * 2);
        return new(0, "Video", "Video", "video", Guid.Empty, Guid.Empty, Guid.Empty)
        {
            Width = width, Height = height, FrameInterval100ns = 333_333, FourCc = "YUY2",
            BitmapHeaderBase64 = Convert.ToBase64String(bitmap)
        };
    }

    private static DirectShowFormat Audio() => new(1, "Audio", "Audio", "audio", Guid.Empty, Guid.Empty, Guid.Empty)
        { SampleRate = 48_000, Channels = 2, WaveFormatTag = 1, BitsPerSample = 24, BlockAlign = 6 };

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"CaptureRack-{Guid.NewGuid():N}.mkv");

    private static void Cleanup(string path)
    {
        File.Delete(NativeCaptureReport.PathFor(path + ".partial"));
        File.Delete(NativeCaptureReport.PathFor(path));
        File.Delete(path + ".partial");
        File.Delete(path);
    }
}
