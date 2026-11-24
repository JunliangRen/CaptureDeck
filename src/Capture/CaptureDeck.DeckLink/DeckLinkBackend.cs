using CaptureDeck.Core;

namespace CaptureDeck.DeckLink;

public sealed record DeckLinkCaptureRequest(int DeviceIndex, uint? ModeId, TimeSpan? Duration)
{
    public CaptureRouting? Routing { get; init; }
    public PreviewFeed? Preview { get; init; }
    public CapturePixelFormat PixelFormat { get; init; } = CapturePixelFormat.V210;
    public CaptureAudioFormat AudioFormat { get; init; } = CaptureAudioFormat.Pcm16;
    public string? RecordingPath { get; init; }
    public PreviewRecorder? PreviewRecorder { get; init; }
}

public readonly record struct DeckLinkProgress(VideoMode Mode, CaptureStats Stats);

public sealed class DeckLinkBackend
    : ICaptureBackend<CaptureDevice, DeckLinkCaptureRequest, DeckLinkProgress, CaptureStats>
{
    public const string BackendId = "blackmagic.decklink";

    public string Id => BackendId;
    public CaptureBackendCategory Category => CaptureBackendCategory.Video;

    public IReadOnlyList<CaptureDevice> GetDevices(bool includeCapabilities = false) =>
        DeckLinkCapture.GetDevices(includeSources: includeCapabilities);

    public CaptureStats Run(DeckLinkCaptureRequest request, Action<DeckLinkProgress> progress,
        CancellationToken cancellation) =>
        DeckLinkCapture.Run(request.DeviceIndex, request.ModeId, request.Duration,
            (mode, stats) => progress(new DeckLinkProgress(mode, stats)), cancellation,
            request.Routing, request.Preview, request.PixelFormat, request.RecordingPath,
            request.AudioFormat, request.PreviewRecorder);
}
