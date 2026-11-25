using System.Buffers.Binary;
using CaptureRack.DirectShow.Windows.Capture;
using Xunit;

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
        }
        finally { Cleanup(path); }
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

    private static DirectShowFormat Video()
    {
        var bitmap = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(bitmap, 40);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(8), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(14), 16);
        "YUY2"u8.CopyTo(bitmap.AsSpan(16));
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(20), 8);
        return new(0, "Video", "Video", "video", Guid.Empty, Guid.Empty, Guid.Empty)
        {
            Width = 2, Height = 2, FrameInterval100ns = 333_333, FourCc = "YUY2",
            BitmapHeaderBase64 = Convert.ToBase64String(bitmap)
        };
    }

    private static DirectShowFormat Audio() => new(1, "Audio", "Audio", "audio", Guid.Empty, Guid.Empty, Guid.Empty)
        { SampleRate = 48_000, Channels = 2, WaveFormatTag = 1, BitsPerSample = 24, BlockAlign = 6 };

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"CaptureRack-{Guid.NewGuid():N}.mkv");

    private static void Cleanup(string path)
    {
        File.Delete(path + ".partial");
        File.Delete(path);
    }
}
