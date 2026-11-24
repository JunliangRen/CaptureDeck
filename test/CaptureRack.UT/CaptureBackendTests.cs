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

    private sealed class AudioPacket : IDeckLinkAudioInputPacket, IDisposable
    {
        private GCHandle data = GCHandle.Alloc(new short[3204], GCHandleType.Pinned);
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
