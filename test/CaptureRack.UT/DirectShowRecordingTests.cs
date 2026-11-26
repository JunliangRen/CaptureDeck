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
            var data = await File.ReadAllBytesAsync(path);
            Assert.True(data.AsSpan().IndexOf(new byte[] { 0x10, 0x80, 0x42, 0x80, 0x7f, 0x80, 0xeb, 0x80 }) >= 0);
            Assert.True(data.AsSpan().IndexOf(new byte[] { 0x11, 0xBB, 0x70, 0xEF, 0x07, 0x81 }) >= 0);
            Assert.False(File.Exists(record.PartialPath));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(NativeCaptureReport.PathFor(path)));
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
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(NativeCaptureReport.PathFor(record.PartialPath)));
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
            var result = await QcReports.RunAsync(path);
            Assert.True(result.Status == QcStatus.Passed, result.Summary);
            Assert.Equal(25, result.DecodedVideoFrames);
            Assert.Equal(48000, result.DecodedAudioSampleFrames);
            using var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(path + ".encoding.json"));
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
            Assert.False(File.Exists(path));
            using var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(recording.PartialPath + ".checkpoint.json"));
            Assert.Equal(35, checkpoint.RootElement.GetProperty("videoPacketsToEncoder").GetInt64());
            Assert.Equal(36, checkpoint.RootElement.GetProperty("captureVideoPackets").GetInt64());
            Assert.Equal(1, checkpoint.RootElement.GetProperty("queueRejectedVideoPackets").GetInt64());
            Assert.Equal(0, checkpoint.RootElement.GetProperty("captureEstimatedMissingVideoFrames").GetInt64());
            Assert.True(checkpoint.RootElement.GetProperty("queuePeakBytes").GetInt64() > 0);
            Assert.Contains("did not keep up", checkpoint.RootElement.GetProperty("error").GetString());
            var recovered = Path.Combine(folder.FullName, "recovered.mkv");
            Assert.True(await LiveMatroskaRecovery.RecoverAsync(recording.PartialPath, recovered) > 0);
            var qc = await QcReports.RunAsync(recovered);
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
            await Assert.ThrowsAsync<IOException>(() => recording.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
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
            await File.WriteAllTextAsync(path, "existing");
            var record = DirectShowRecording.Start(path, Video(), null);
            record.Accept(true, new(new byte[8], false));
            record.Complete();
            await Assert.ThrowsAnyAsync<IOException>(async () => await record.Completion);
            Assert.Equal("existing", await File.ReadAllTextAsync(path));
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
            var saved = await File.ReadAllBytesAsync(path);
            var result = await QcReports.RunAsync(path);
            Assert.True(result.Status == QcStatus.Passed, result.Summary);
            Assert.Equal(videoFrames, result.DecodedVideoFrames);
            Assert.Equal(withAudio ? videoFrames * samplesPerPacket : 0, result.DecodedAudioSampleFrames);
            Assert.DoesNotContain(result.Warnings, warning => warning.Contains("no audio track", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(result.FrameMd5));
            Assert.True(File.Exists(result.QcToolsReport));
            using (var report = JsonDocument.Parse(await File.ReadAllTextAsync(result.CaptureLog)))
                Assert.Equal("Synthetic DirectShow", report.RootElement.GetProperty("nativeRecording").GetProperty("device").GetString());
            Assert.Equal(saved, await File.ReadAllBytesAsync(path));

            var truncated = saved[..(saved.Length / 2)];
            await File.WriteAllBytesAsync(path, truncated);
            var damaged = await QcReports.RunAsync(path);
            Assert.Equal(QcStatus.Failed, damaged.Status);
            Assert.Contains("Recorded file bytes", damaged.Summary);
            Assert.Equal(truncated, await File.ReadAllBytesAsync(path));
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
