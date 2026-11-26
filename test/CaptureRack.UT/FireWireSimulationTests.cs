using CaptureRack.DirectShow.Windows.FireWire.MsTape;
using CaptureRack.QualityControl;
using Xunit;

namespace CaptureRack.UT;

public sealed class FireWireSimulationTests
{
    [Theory]
    [InlineData("simulate:dv-ntsc", 720, 480, 120000)]
    [InlineData("simulate:dv-pal", 720, 576, 144000)]
    [InlineData("simulate:dv25-ntsc", 720, 480, 120000, DvEncoding.Dvcpro25)]
    [InlineData("simulate:dv25-pal", 720, 576, 144000, DvEncoding.Dvcpro25)]
    [InlineData("simulate:dv50-ntsc", 720, 480, 240000, DvEncoding.Dvcpro50)]
    [InlineData("simulate:dv50-pal", 720, 576, 288000, DvEncoding.Dvcpro50)]
    [InlineData("simulate:dvh1-1080i59", 1280, 1080, 480000, DvEncoding.DvcproHd)]
    [InlineData("simulate:dvh1-1080i50", 1440, 1080, 576000, DvEncoding.DvcproHd)]
    [InlineData("simulate:dvh1-720p59", 960, 720, 240000, DvEncoding.DvcproHd)]
    [InlineData("simulate:dvh1-720p50", 960, 720, 288000, DvEncoding.DvcproHd)]
    [InlineData("simulate:hdv-720p", 1280, 720, 188)]
    [InlineData("simulate:hdv-188", 1440, 1080, 188)]
    [InlineData("simulate:hdv-192", 1440, 1080, 192)]
    [Trait("Category", "FFmpegIntegration")]
    public async Task SelectableSimulationNegotiatesPreviewsAndRecordsNativeBytes(string id, int width, int height, int unitBytes,
        DvEncoding encoding = DvEncoding.Consumer)
    {
        if (!FireWireCapture.IsSupported || QcReports.FindTools().FfmpegPath is not { } ffmpeg)
            return;

        var device = Assert.Single(FireWireCapture.List(includeSimulated: true), candidate => candidate.Id == id);
        var format = Assert.IsType<FireWireMediaFormat>(device.SimulatedFormat);
        Assert.DoesNotContain(FireWireCapture.List(), candidate => candidate.Id == id);
        var directory = Directory.CreateTempSubdirectory("capturerack-simulated-ilink-");
        var path = Path.Combine(directory.FullName, $"Tape_CaptureRack_{format.FilePrefix}_20261125_120000_000{format.Extension}");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var expected = new MemoryStream();
        var session = new FireWireLiveSession();
        var decoder = new FireWirePreviewRecovery(device.IsDv);
        var frameReady = new TaskCompletionSource<FireWireFrameInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        long pcmBytes = 0;
        var decoding = decoder.RunAsync(ffmpeg, (_, frame) => frameReady.TrySetResult(frame),
            data => Interlocked.Add(ref pcmBytes, data.Length), () => { }, stop.Token);
        FireWireClipWriter? writer = null;
        DvStatistics? dv = null;
        FireWireTsInfo? ts = null;
        try
        {
            await Task.Run(() => session.Run(id, sample =>
            {
                decoder.Offer(sample);
                if (writer is null && session.CanRecord)
                {
                    writer = session.StartRecording(path);
                    return true;
                }
                if (writer is not null)
                    expected.Write(sample.Bytes);
                if (expected.Length >= 2 * 1024 * 1024 && frameReady.Task.IsCompletedSuccessfully && Interlocked.Read(ref pcmBytes) > 0)
                {
                    dv = session.DvInfo;
                    ts = session.StreamInfo;
                    session.StopRecording(writer!);
                    stop.Cancel();
                }
                return true;
            }, stop.Token, decoder.NotifyStreamChange, ffmpeg));

            Assert.Equal(format, session.Format);
            Assert.True(frameReady.Task.IsCompletedSuccessfully, $"Preview returned no frame for {id}.");
            var video = await frameReady.Task;
            Assert.Equal((width, height), (video.Width, video.Height));
            Assert.True(pcmBytes > 0);
            Assert.NotNull(writer);
            var clip = await writer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(clip.Error);
            Assert.Equal(expected.ToArray(), await File.ReadAllBytesAsync(path));
            Assert.Equal(0, clip.Bytes % unitBytes);
            if (device.IsDv)
            {
                Assert.NotNull(dv?.LastFrame);
                Assert.Equal(encoding, dv.LastFrame.Encoding);
                Assert.Equal((width, height, unitBytes), (dv.LastFrame.Width, dv.LastFrame.Height, dv.LastFrame.FrameBytes));
                if (encoding == DvEncoding.DvcproHd)
                {
                    Assert.Equal(height == 720, dv.LastFrame.Progressive);
                    Assert.Equal(height == 1080, video.TopFieldFirst.HasValue);
                }
                Assert.StartsWith("01:", dv.LastFrame.Timecode);
                Assert.Equal("2006-05-04", dv.LastFrame.RecordingDate);
                Assert.NotNull(dv.LastFrame.RecordingTime);
                Assert.Equal(48000, dv.LastFrame.AudioRate);
                Assert.Equal(16, dv.LastFrame.AudioBits);
                Assert.Equal(encoding switch { DvEncoding.DvcproHd => 8, DvEncoding.Dvcpro50 => 4, _ => 2 },
                    dv.LastFrame.AudioChannels);
                var report = DvFile.Analyze(path);
                Assert.True(report.Analysis.Success);
                Assert.Equal(encoding, report.Analysis.LastFrame?.Encoding);
            }
            else
            {
                Assert.Equal(height == 720 ? (bool?)null : true, video.TopFieldFirst);
                Assert.Contains(ts!.Programs.SelectMany(program => program.Tracks), track => track.StreamType == 2);
                Assert.Contains(ts.Programs.SelectMany(program => program.Tracks), track => track.StreamType is 3 or 4);
                var report = TsFile.Analyze(path);
                Assert.Equal($"ts{unitBytes}", report.Format);
                Assert.True(report.Success);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await decoding;
            if (writer is not null)
                await writer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            directory.Delete(recursive: true);
        }
    }
}
