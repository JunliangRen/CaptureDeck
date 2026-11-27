using CaptureRack.Audio;
using System.Runtime.InteropServices;
using CaptureRack.Core;
using CaptureRack.DeckLink;
using Xunit;

namespace CaptureRack.UT;

public sealed class CaptureBackendTests
{
    private static readonly VideoMode Ntsc = new(0x6E747363, "NTSC", 720, 486, 1001, 30000, 0);
    private const uint Pal = 0x70616C20;

    [Fact]
    public void ResolvesRegisteredBackendThroughCaptureContract()
    {
        var backend = new DeckLinkBackend();
        var registry = new CaptureBackendRegistry(backend);

        var selected = registry.Get<
            ICaptureBackend<CaptureDevice, DeckLinkCaptureRequest, DeckLinkProgress, CaptureStats>>(
            DeckLinkBackend.BackendId);

        Assert.Same(backend, selected);
    }

    [Theory]
    [InlineData("windows.directshow")]
    [InlineData("windows.firewire")]
    [InlineData("signal-capture.test")]
    [InlineData("audio.test")]
    public void ReservedBackendCannotBeStarted(string id)
    {
        var registry = new CaptureBackendRegistry(new DeckLinkBackend());

        Assert.Throws<NotSupportedException>(() => registry.Get<ICaptureBackend>(id));
    }

    [Fact]
    public void VideoBackendsWithDifferentIdsCanCoexist()
    {
        var deckLink = new DeckLinkBackend();
        var otherVideo = new TestVideoBackend();
        var registry = new CaptureBackendRegistry(deckLink, otherVideo);

        Assert.Equal(deckLink.Category, otherVideo.Category);
        Assert.Same(deckLink, registry.Get<ICaptureBackend>(deckLink.Id));
        Assert.Same(otherVideo, registry.Get<ICaptureBackend>(otherVideo.Id));
    }

    private sealed class TestVideoBackend : ICaptureBackend
    {
        public string Id => "test.video";
        public CaptureBackendCategory Category => CaptureBackendCategory.Video;
    }

    [Fact]
    public void DuplicateBackendRegistrationIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new CaptureBackendRegistry(new DeckLinkBackend(), new DeckLinkBackend()));
    }

    [Fact]
    public void DirectMovRecordingRequiresFixedMode()
    {
        var request = new DeckLinkCaptureRequest(0, null, null) { RecordingPath = "test.mov" };

        Assert.Throws<InvalidOperationException>(() =>
            new DeckLinkBackend().Run(request, _ => { }, CancellationToken.None));
    }

    [Fact]
    public void LiveRecordingRequiresPreviewFeed()
    {
        var request = new DeckLinkCaptureRequest(0, 0x6E747363, null) { PreviewRecorder = new PreviewRecorder() };

        var error = Assert.Throws<ArgumentException>(() =>
            new DeckLinkBackend().Run(request, _ => { }, CancellationToken.None));

        Assert.Equal("previewRecorder", error.ParamName);
    }

    [Fact]
    public void AutoPreviewRequiresAReadableFrameInTheActiveMode()
    {
        var callback = CreatePreviewCallback(automaticMode: true);
        Assert.False(callback.Snapshot(false).CanStartRecording);
        Assert.Throws<InvalidOperationException>(() => callback.AttachRecording("unused.mov"));

        callback.ObserveDetectedMode(Ntsc.Id);
        using var frame = new VideoFrame(Ntsc, noInput: true);
        frame.Readable = false;
        callback.CountVideo(frame);
        Assert.False(callback.Snapshot(false).CanStartRecording);
        using var wrongMode = new VideoFrame(Ntsc with { Height = 576 }, noInput: false);
        callback.CountVideo(wrongMode);
        Assert.False(callback.Snapshot(false).CanStartRecording);

        frame.Readable = true;
        callback.CountVideo(frame);
        Assert.True(callback.Snapshot(false).CanStartRecording);
        callback.ObserveDetectedMode(Pal);
        Assert.False(callback.Snapshot(false).CanStartRecording);
        Assert.True(callback.TryBeginModeChange());
        Assert.Equal(Pal, callback.Snapshot(false).NextMode);
    }

    [Fact]
    public void AutoPreviewRecordsNoInputAndStopsWritingBeforeSwitchingModes()
    {
        var directory = Directory.CreateTempSubdirectory("capturerack-auto-mode-");
        try
        {
            var callback = CreatePreviewCallback(automaticMode: true);
            using var video = new VideoFrame(Ntsc, noInput: true);
            callback.CountVideo(video);
            Assert.Equal(VideoSignal.NoInput, callback.Snapshot(false).Signal);
            Assert.True(callback.Snapshot(false).CanStartRecording);

            var path = Path.Combine(directory.FullName, "auto.mov");
            var recorder = new PreviewRecorder();
            recorder.Connect(callback);
            recorder.Start(path);
            using var audio = new AudioPacket();
            callback.CountVideo(video);
            callback.CountAudio(audio);

            callback.ObserveDetectedMode(Pal);
            Assert.True(callback.Snapshot(false).RecordingModeChanged);
            Assert.False(callback.TryBeginModeChange());

            var stats = recorder.Finish();
            Assert.True(stats.RecordingModeChanged);
            Assert.Equal(1, stats.NoInputFrames);
            Assert.Equal(0, stats.VideoSignalLossCount);
            var initialLoss = Assert.Single(stats.VideoSignalLosses);
            Assert.True(initialLoss.StartedUnlocked);
            Assert.False(initialLoss.Restored);
            Assert.Equal(TimeSpan.Zero, initialLoss.Elapsed);
            Assert.Equal(0, stats.VideoFrames);
            Assert.Equal(Ntsc, stats.Recording!.Mode);
            Assert.Equal(1, stats.Recording.VideoFrames);
            Assert.Equal(1602, stats.Recording.AudioSampleFrames);
            Assert.True(File.Exists(path));
            Assert.True(callback.TryBeginModeChange());
            Assert.Equal(Pal, callback.Snapshot(false).NextMode);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void FixedModePreviewCanRecordWithoutInput()
    {
        var callback = CreatePreviewCallback(automaticMode: false);
        using var frame = new VideoFrame(Ntsc, noInput: true);
        callback.CountVideo(frame);
        Assert.True(callback.Snapshot(false).CanStartRecording);
    }

    [Fact]
    public void PublishesVideoAlreadyQueuedWhenPreviewDisconnects()
    {
        var folder = Directory.CreateTempSubdirectory("capturerack-disconnect-");
        var path = Path.Combine(folder.FullName, "interrupted.mov");
        var callback = CreatePreviewCallback(automaticMode: false);
        var recorder = new PreviewRecorder();
        recorder.Connect(callback);
        using var frame = new VideoFrame(Ntsc, noInput: false);
        try
        {
            recorder.Start(path);
            callback.CountVideo(frame);
            recorder.Disconnect();
            Assert.True(File.Exists(path));
            var result = recorder.Finish();
            Assert.Equal(1, result.Recording!.VideoFrames);
            Assert.Equal(0, result.Recording.AudioSampleFrames);
            Assert.Contains("disconnected", result.Error);
            Assert.Throws<InvalidOperationException>(() => recorder.Finish());
        }
        finally
        {
            recorder.Disconnect();
            File.Delete(path);
            folder.Delete();
        }
    }

    [Fact]
    public void KeepsFinalizedRecordingIfAnExistingOutputPreventsPublishing()
    {
        var folder = Directory.CreateTempSubdirectory("capturerack-publish-collision-");
        var path = Path.Combine(folder.FullName, "recording.mov");
        var callback = CreatePreviewCallback(automaticMode: false);
        var recorder = new PreviewRecorder();
        recorder.Connect(callback);
        using var frame = new VideoFrame(Ntsc, noInput: false);
        try
        {
            recorder.Start(path);
            callback.CountVideo(frame);
            File.WriteAllText(path, "existing");
            var error = Assert.Throws<IOException>(() => recorder.Finish());
            var saved = Assert.Single(Directory.GetFiles(folder.FullName, "*.partial"));
            Assert.Contains(saved, error.Message);
            Assert.Equal("existing", File.ReadAllText(path));
            Assert.True(File.ReadAllBytes(saved).AsSpan().IndexOf("moov"u8) > 0);
        }
        finally
        {
            recorder.Disconnect();
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void UnreadableNoSignalFrameDoesNotPreventWritingLaterFrames()
    {
        var folder = Directory.CreateTempSubdirectory("capturerack-unreadable-");
        var path = Path.Combine(folder.FullName, "recover.mov");
        var callback = CreatePreviewCallback(automaticMode: false);
        var recorder = new PreviewRecorder();
        recorder.Connect(callback);
        using var frame = new VideoFrame(Ntsc, noInput: true);
        using var audio = new AudioPacket();
        try
        {
            recorder.Start(path);
            frame.Readable = false;
            callback.CountVideo(frame);
            callback.CountAudio(audio);
            frame.Readable = true;
            callback.CountVideo(frame);
            var stats = recorder.Finish();
            Assert.Equal(2, stats.NoInputFrames);
            Assert.Equal(1, stats.UnreadableNoInputFrames);
            Assert.Equal(1, stats.Recording!.VideoFrames);
            Assert.Equal(1602, stats.Recording.AudioSampleFrames);
            Assert.Null(stats.Error);
        }
        finally
        {
            recorder.Disconnect();
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void PreviewCanRecordSuccessiveClipsWithDifferentPcmDepths()
    {
        var folder = Directory.CreateTempSubdirectory("capturerack-audio-format-");
        var callback = CreatePreviewCallback(automaticMode: false);
        callback.UseMonitoringAudioFormat(CaptureAudioFormat.Pcm32);
        var recorder = new PreviewRecorder();
        recorder.Connect(callback);
        using var audio = new AudioPacket(pcm32: true);
        try
        {
            Assert.True(callback.Snapshot(false).SupportsRecordingAudioFormat(CaptureAudioFormat.Pcm32));
            var wide = Path.Combine(folder.FullName, "wide.mov");
            recorder.Start(wide, CaptureAudioFormat.Pcm32);
            callback.CountAudio(audio);
            var native = recorder.Finish().Recording!;
            Assert.Equal(CaptureAudioFormat.Pcm32, native.AudioFormat);
            Assert.Equal(1602, native.AudioSampleFrames);
            AssertPcmPayload(wide, [0x78, 0x56, 0x34, 0x12]);

            var narrow = Path.Combine(folder.FullName, "narrow.mov");
            recorder.Start(narrow, CaptureAudioFormat.Pcm16);
            callback.CountAudio(audio);
            var converted = recorder.Finish().Recording!;
            Assert.Equal(CaptureAudioFormat.Pcm16, converted.AudioFormat);
            Assert.Equal(1602, converted.AudioSampleFrames);
            AssertPcmPayload(narrow, [0x34, 0x12]);
            Assert.Equal(CaptureAudioFormat.Pcm32, callback.Snapshot(false).InputAudioFormat);
        }
        finally
        {
            recorder.Disconnect();
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void PreviewRejectsUnsupportedPcmDepthBeforeCreatingOutput()
    {
        var folder = Directory.CreateTempSubdirectory("capturerack-pcm16-only-");
        var callback = CreatePreviewCallback(automaticMode: false);
        var recorder = new PreviewRecorder();
        recorder.Connect(callback);
        try
        {
            Assert.False(callback.Snapshot(false).SupportsRecordingAudioFormat(CaptureAudioFormat.Pcm32));
            Assert.Throws<InvalidOperationException>(() =>
                recorder.Start(Path.Combine(folder.FullName, "wide.mov"), CaptureAudioFormat.Pcm32));
            Assert.Empty(folder.EnumerateFileSystemInfos());

            using var audio = new AudioPacket();
            recorder.Start(Path.Combine(folder.FullName, "narrow.mov"), CaptureAudioFormat.Pcm16);
            callback.CountAudio(audio);
            Assert.Equal(CaptureAudioFormat.Pcm16, recorder.Finish().Recording!.AudioFormat);
        }
        finally
        {
            recorder.Disconnect();
            folder.Delete(recursive: true);
        }
    }

    private static void AssertPcmPayload(string path, byte[] expected)
    {
        var file = File.ReadAllBytes(path);
        var media = file.AsSpan().IndexOf("mdat"u8);
        Assert.True(media >= 0);
        Assert.Equal(expected, file.AsSpan(media + 12, expected.Length).ToArray());
    }

    [Fact]
    public void HardwareStatusFillsCallbackDroughtAndRecordingClipsOpenLossAtStop()
    {
        var folder = Directory.CreateTempSubdirectory("capturerack-video-lock-");
        var elapsed = TimeSpan.Zero;
        var callback = new CaptureCallback(new PreviewFeed(), CapturePixelFormat.V210, CaptureAudioFormat.Pcm16,
            Ntsc, null, automaticMode: false, signalClock: () => elapsed);
        var recorder = new PreviewRecorder();
        recorder.Connect(callback);
        using var frame = new VideoFrame(Ntsc, noInput: false);
        try
        {
            callback.CountVideo(frame);
            recorder.Start(Path.Combine(folder.FullName, "lock.mov"));
            callback.CountVideo(frame);

            elapsed = TimeSpan.FromMilliseconds(50);
            callback.ObserveHardwareLock(locked: false);
            Assert.Equal(VideoSignal.Present, callback.Snapshot(true).Signal);
            elapsed = TimeSpan.FromMilliseconds(150);
            callback.ObserveHardwareLock(locked: false);
            Assert.Equal(VideoSignal.NoInput, callback.Snapshot(true).Signal);
            elapsed = TimeSpan.FromMilliseconds(350);
            callback.ObserveHardwareLock(locked: true);
            elapsed = TimeSpan.FromMilliseconds(400);
            callback.ObserveHardwareLock(locked: false);
            elapsed = TimeSpan.FromMilliseconds(600);

            var stats = recorder.Finish();
            Assert.Equal(0, stats.NoInputFrames);
            Assert.Equal(2, stats.VideoSignalLossCount);
            Assert.Equal(TimeSpan.FromMilliseconds(400), stats.VideoSignalLossDuration);
            Assert.Collection(stats.VideoSignalLosses,
                first => Assert.Equal(new CaptureRack.Core.VideoSignalLoss(TimeSpan.FromMilliseconds(150),
                    TimeSpan.FromMilliseconds(200), StartedUnlocked: false, Restored: true), first),
                second => Assert.Equal(new CaptureRack.Core.VideoSignalLoss(TimeSpan.FromMilliseconds(400),
                    TimeSpan.FromMilliseconds(200), StartedUnlocked: false, Restored: false), second));
        }
        finally
        {
            recorder.Disconnect();
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void UyvyLumaRangeCountsOnlyPixelsOutsideInclusiveThresholds()
    {
        var frame = new PreviewFrame { Format = CapturePixelFormat.Uyvy };
        frame.Resize(4, 1, 12);
        new byte[] { 0, 15, 255, 16, 0, 235, 255, 236 }.CopyTo(frame.Pixels, 0);
        Assert.Equal(0.5, VideoScopes.MeasureLumaExcursions(frame));
        frame.NoInput = true;
        Assert.Null(VideoScopes.Analyze(frame, 8).LumaOutsideLegalFraction);
    }

    [Fact]
    public void V210LumaRangeIgnoresChromaAndPaddingAndPreservesFullPrecision()
    {
        var frame = new PreviewFrame { Format = CapturePixelFormat.V210 };
        frame.Resize(8, 1, 128);
        var words = MemoryMarshal.Cast<byte, uint>(frame.Pixels);
        uint[] luma = [63, 64, 940, 941, 64, 940, 940, 941, 0, 0, 0, 0];
        for (var i = 0; i < 2; i++)
        {
            var y = luma.AsSpan(i * 6, 6);
            words[i * 4] = 1023u | (y[0] << 10);
            words[i * 4 + 1] = y[1] | (1023u << 10) | (y[2] << 20);
            words[i * 4 + 2] = (y[3] << 10) | (1023u << 20);
            words[i * 4 + 3] = y[4] | (y[5] << 20);
        }
        Assert.Equal(3.0 / 8, VideoScopes.MeasureLumaExcursions(frame));
    }

    private static CaptureCallback CreatePreviewCallback(bool automaticMode) =>
        new(new PreviewFeed(), CapturePixelFormat.V210, CaptureAudioFormat.Pcm16, Ntsc, null, automaticMode);

    private sealed class VideoFrame(VideoMode mode, bool noInput) : IDeckLinkVideoInputFrame, IDeckLinkVideoBuffer, IDisposable
    {
        private readonly int stride = MovRecording.StoredRowBytes(mode.Width, CapturePixelFormat.V210);
        private GCHandle data = GCHandle.Alloc(
            new byte[MovRecording.StoredRowBytes(mode.Width, CapturePixelFormat.V210) * mode.Height], GCHandleType.Pinned);

        public bool Readable { get; set; } = true;
        public int GetWidth() => mode.Width;
        public int GetHeight() => mode.Height;
        public int GetRowBytes() => stride;
        public uint GetPixelFormat() => (uint)CapturePixelFormat.V210;
        public uint GetFlags() => noInput ? 1u << 31 : 0;
        public int GetTimecode(uint format, out IntPtr timecode) { timecode = 0; return 1; }
        public int GetAncillaryData(out IntPtr buffer) { buffer = 0; return 1; }
        public int GetStreamTime(out long time, out long duration, long scale)
        {
            time = 0;
            duration = mode.FrameDuration * scale / mode.TimeScale;
            return 0;
        }
        public int GetBytes(out IntPtr bytes) { bytes = data.AddrOfPinnedObject(); return 0; }
        public int GetSize(out ulong size) { size = (ulong)(stride * mode.Height); return 0; }
        public int StartAccess(uint flags) => Readable ? 0 : unchecked((int)0x80004005);
        public int EndAccess(uint flags) => 0;
        public void Dispose() => data.Free();
    }

    private sealed class AudioPacket(bool pcm32 = false) : IDeckLinkAudioInputPacket, IDisposable
    {
        private GCHandle data = GCHandle.Alloc(pcm32 ? (Array)Enumerable.Repeat(0x12345678, 3204).ToArray() : new short[3204],
            GCHandleType.Pinned);
        public int GetSampleFrameCount() => 1602;
        public int GetBytes(out IntPtr bytes) { bytes = data.AddrOfPinnedObject(); return 0; }
        public int GetPacketTime(out long time, long scale) { time = 0; return 0; }
        public void Dispose() => data.Free();
    }

    [Fact]
    public void UnsupportedPcmDepthIsRejectedBeforeOpeningDevice()
    {
        var request = new DeckLinkCaptureRequest(0, null, null) { AudioFormat = (CaptureAudioFormat)24 };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DeckLinkBackend().Run(request, _ => { }, CancellationToken.None));

        Assert.Equal("audioFormat", error.ParamName);
    }
}
