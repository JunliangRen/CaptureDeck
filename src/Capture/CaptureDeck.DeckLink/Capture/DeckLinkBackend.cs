using System.Runtime.InteropServices;
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

public enum DeckLinkAvailability { UnsupportedPlatform, DriverUnavailable, NoDevices, Available }

public sealed record DeckLinkDeviceScan(DeckLinkAvailability Availability, string? ApiVersion, IReadOnlyList<CaptureDevice> Devices);

public sealed class DeckLinkBackend
    : ICaptureBackend<CaptureDevice, DeckLinkCaptureRequest, DeckLinkProgress, CaptureStats>
{
    public const string BackendId = "blackmagic.decklink";

    public string Id => BackendId;
    public CaptureBackendCategory Category => CaptureBackendCategory.Video;

    public DeckLinkDeviceScan ScanDevices(bool includeCapabilities = false)
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return new(DeckLinkAvailability.UnsupportedPlatform, null, []);

        try
        {
            var version = DeckLinkCapture.GetApiVersion();
            var devices = GetDevices(includeCapabilities);
            var availability = devices.Count == 0 ? DeckLinkAvailability.NoDevices : DeckLinkAvailability.Available;
            return new(availability, version, devices);
        }
        catch (COMException ex) when (ex.HResult is unchecked((int)0x80040154) or // Class not registered
            unchecked((int)0x8007007E) or unchecked((int)0x800700C1)) // DLL missing or wrong architecture
        {
            return new(DeckLinkAvailability.DriverUnavailable, null, []);
        }
        catch (BadImageFormatException)
        {
            return new(DeckLinkAvailability.DriverUnavailable, null, []);
        }
    }

    public IReadOnlyList<CaptureDevice> GetDevices(bool includeCapabilities = false) =>
        DeckLinkCapture.GetDevices(includeSources: includeCapabilities);

    public CaptureStats Run(DeckLinkCaptureRequest request, Action<DeckLinkProgress> progress,
        CancellationToken cancellation) =>
        DeckLinkCapture.Run(request.DeviceIndex, request.ModeId, request.Duration,
            (mode, stats) => progress(new DeckLinkProgress(mode, stats)), cancellation,
            request.Routing, request.Preview, request.PixelFormat, request.RecordingPath,
            request.AudioFormat, request.PreviewRecorder);
}
