using CaptureDeck.Core;
using CaptureDeck.DeckLink;
using Xunit;

namespace CaptureDeck.UT;

public sealed class CaptureBackendTests
{
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
    public void UnsupportedPcmDepthIsRejectedBeforeOpeningDevice()
    {
        var request = new DeckLinkCaptureRequest(0, null, null) { AudioFormat = (CaptureAudioFormat)24 };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DeckLinkBackend().Run(request, _ => { }, CancellationToken.None));

        Assert.Equal("audioFormat", error.ParamName);
    }
}
