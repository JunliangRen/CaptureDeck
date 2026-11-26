using System.Buffers.Binary;

namespace CaptureRack.SignalCapture;

public sealed record FifoReading(DateTimeOffset TimeUtc, byte Latch, byte Status, int UsedWords, int PeakWords,
    int LifetimePeakWords, int OverflowEvents, int DroppedWords, int PacketsRead, int NearFullUnits,
    int DepthWords, int PacketWords, int NearFullWords, int RunPeakWords, long TotalNearFullUnits, bool CoverageLimited)
{
    public long NearFullSamples => TotalNearFullUnits * 256;
}

// One instance per protocol-v1 collection. Only the acquisition thread reads or updates it.
internal class DomesdayFifoTelemetry
{
    private FifoReading? latest;
    public FifoReading? Latest => Volatile.Read(ref latest);
    public long TotalNearFullUnits { get; private set; }
    public int RunPeakWords { get; private set; }
    private bool pending;
    private long nextPoll;
    private byte? lastLatch;
    public int ReadFailures { get; private set; }
    public bool SessionTelemetryUnavailable { get; internal set; }
    public bool PollingDisabled => SessionTelemetryUnavailable || ReadFailures >= 2;
    public long UnrecognizedReadings { get; private set; }
    public long ValidIntervals { get; private set; }
    public long OverflowEvents { get; private set; }
    public long DroppedSamples { get; private set; }
    public bool CounterSaturated { get; private set; }
    public bool MissedIntervals { get; private set; }

    internal Action<byte[]>? IntervalReceived;
    internal Action? FailedRead;
    internal void CountFailure() { ReadFailures++; FailedRead?.Invoke(); }

    public void Tick(long now, Action submit, Func<int?> tryComplete, byte[] block)
    {
        if (PollingDisabled) return;
        try
        {
            if (!pending)
            {
                if (now < nextPoll) return;
                nextPoll = now + 250;
                submit();
                pending = true;
            }
            int? length = tryComplete();
            if (!length.HasValue) return;
            pending = false;
            if (length != 23) CountFailure();
            else Accept(block);
        }
        catch (IOException)
        {
            pending = false;
            CountFailure();
        }
    }

    internal void Accept(byte[] block)
    {
        IntervalReceived?.Invoke(block);
        ushort Word(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(offset));
        int packet = Word(19);
        if (block[0] != 0xbd || (block[1] & 15) != 1 || packet == 0 || Word(17) <= packet)
        {
            UnrecognizedReadings++;
            return;
        }

        byte latch = block[2];
        if (lastLatch == latch) return;
        var previous = lastLatch;
        lastLatch = latch;
        // Reading 0x40 latches and clears the interval. The first one may predate collection.
        if (previous.HasValue)
        {
            MissedIntervals |= latch != unchecked((byte)(previous.Value + 1));
            CounterSaturated |= (block[1] & 0x20) != 0;
            ValidIntervals++;
            OverflowEvents += Word(9);
            DroppedSamples += Word(11);
            TotalNearFullUnits += Word(15);
            RunPeakWords = Math.Max(RunPeakWords, Word(5));
        }
        Volatile.Write(ref latest, new(DateTimeOffset.UtcNow, latch, block[1], Word(3), Word(5), Word(7),
            Word(9), Word(11), Word(13), Word(15), Word(17), packet, Word(21), RunPeakWords, TotalNearFullUnits,
            MissedIntervals || CounterSaturated || ReadFailures > 0));
    }

}
