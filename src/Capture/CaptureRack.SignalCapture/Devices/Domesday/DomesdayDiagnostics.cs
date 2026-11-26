using System.Text.Json;
using System.Diagnostics;

namespace CaptureRack.SignalCapture;

internal sealed record MarkerPosition(long SampleIndex, int? PreviousMarker, int? ExpectedMarker,
    int? ActualMarker, int RunLength);
internal sealed record UsbRequestState(string Operation, byte Endpoint, int Slot, int SubmittedBulkRequests,
    int ExpectedBytes, int? ActualBytes, bool FifoRequestPending, int? Win32Error = null);
internal sealed record DomesdayFault(string Error, MarkerPosition? Sequence, UsbRequestState? Usb,
    FifoReading? LatestFifo, long WrittenSamples, int WriteQueuePeakBlocks);

internal sealed class DomesdayDiagnostics : DomesdayFifoTelemetry
{
    public Guid? PreviewSessionId { get; internal set; }
    public long? FirstPreviewSample { get; internal set; }
    public long InputSamples { get; internal set; }
    public long PositiveClips { get; internal set; }
    public long NegativeClips { get; internal set; }
    public DomesdaySource? Source { get; internal set; }
    public RfUsbEndpoint? StreamEndpoint { get; internal set; }
    public string? LinkSpeedHint { get; internal set; }
    private string? legacyState;
    public string? LegacyMarkerState
    {
        get => Volatile.Read(ref legacyState);
        internal set => Volatile.Write(ref legacyState, value);
    }
    public long? LegacyMarkerLockSample { get; internal set; }
    internal volatile MarkerPosition? Position;
    internal volatile UsbRequestState? UsbState;
    private DomesdayFault? fault;
    private long written;
    private int queuePeak;
    public DomesdayFault? Fault => fault;
    public long WrittenSamples => Interlocked.Read(ref written);
    public int WriteQueueCapacityBlocks => RfRecording.DddWriteQueueBlocks;
    public int BulkTransferBytes { get; internal set; }
    public int WriteQueuePeakBlocks => Volatile.Read(ref queuePeak);
    public string? Error { get; private set; }

    private long callbackTicks, longestCallbackTicks;
    public long UsbCallbackCount { get; private set; }
    public double UsbCallbackMaxMs => longestCallbackTicks * 1000d / Stopwatch.Frequency;
    public double UsbCallbackMeanMs => UsbCallbackCount == 0 ? 0 : callbackTicks * 1000d / Stopwatch.Frequency / UsbCallbackCount;

    // Only the USB thread updates these, and the report is serialized after it returns.
    public void ObserveCallback(long start)
    {
        long elapsed = Stopwatch.GetTimestamp() - start;
        UsbCallbackCount++;
        callbackTicks += elapsed;
        longestCallbackTicks = Math.Max(longestCallbackTicks, elapsed);
    }

    public void Wrote(int samples) => Interlocked.Add(ref written, samples);
    public void ObserveQueue(int blocks)
    {
        if (blocks > queuePeak) Volatile.Write(ref queuePeak, blocks);
    }

    public void CaptureFault(Exception error) => Interlocked.CompareExchange(ref fault,
        new(error.Message, Position, UsbState, Latest, WrittenSamples, WriteQueuePeakBlocks), null);

    public void WithReport(string path, Action capture)
    {
        using var report = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try { capture(); }
        catch (Exception ex)
        {
            CaptureFault(ex);
            Error = ex.Message;
            try { JsonSerializer.Serialize(report, this); report.Flush(); }
            catch (IOException writeError) { throw new IOException($"{Error} [Report: {writeError.Message}]", ex); }
            throw;
        }
        JsonSerializer.Serialize(report, this);
    }
}
