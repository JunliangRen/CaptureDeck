namespace CaptureDeck.Core;

public enum CaptureBackendCategory { Video, FireWire, RF, Audio }

public interface ICaptureBackend
{
    string Id { get; }
    CaptureBackendCategory Category { get; }
}

/// <summary>
/// Discovers devices and runs a capture session. Each backend owns its native
/// format negotiation, request options, progress payload and result.
/// </summary>
public interface ICaptureBackend<TDevice, in TRequest, out TProgress, out TResult> : ICaptureBackend
{
    IReadOnlyList<TDevice> GetDevices(bool includeCapabilities = false);

    // Runs on a capture worker. Progress can originate on a native callback thread.
    // On return, the backend has finished processing the session and released the device.
    TResult Run(TRequest request, Action<TProgress> progress, CancellationToken cancellation);
}

public sealed class CaptureBackendRegistry(params ICaptureBackend[] backends)
{
    private readonly Dictionary<string, ICaptureBackend> backendsById =
        backends.ToDictionary(backend => backend.Id, StringComparer.Ordinal);

    public TBackend Get<TBackend>(string id) where TBackend : class, ICaptureBackend
    {
        if (!backendsById.TryGetValue(id, out var backend))
            throw new NotSupportedException($"The {id} capture backend is not registered.");

        return backend as TBackend
            ?? throw new InvalidOperationException($"The {id} capture backend does not implement {typeof(TBackend).Name}.");
    }
}
