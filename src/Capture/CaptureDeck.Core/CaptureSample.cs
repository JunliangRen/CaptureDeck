namespace CaptureDeck.Core;

/// <summary>A sample in the stream format negotiated when capture started.</summary>
/// <remarks>
/// Bytes are borrowed from the source and valid only during the receive callback.
/// Times are relative to the source's stream clock and may be absent.
/// </remarks>
public readonly ref struct CaptureSample(
    ReadOnlySpan<byte> bytes, bool discontinuity, TimeSpan? startTime = null, TimeSpan? endTime = null)
{
    public ReadOnlySpan<byte> Bytes { get; } = bytes;
    public bool Discontinuity { get; } = discontinuity;
    public TimeSpan? StartTime { get; } = startTime;
    public TimeSpan? EndTime { get; } = endTime;
}

/// <summary>
/// Consumes a borrowed sample synchronously on the source thread. Return false to reject delivery.
/// Copy any bytes that must outlive the callback into consumer-owned storage.
/// </summary>
public delegate bool CaptureSampleHandler(CaptureSample sample);
