using System.Threading.Channels;

namespace CaptureRack.Recording;

internal sealed class LiveEncodingDiagnostics
{
    private readonly object gate = new();
    private long queueBytes, queuePeakBytes, queueRejectedPackets, queueRejectedVideoPackets;
    private int queuePackets, queuePeakPackets;
    private long capturedVideoPackets, estimatedMissingFrames, captureDiscontinuities, comparableVideoIntervals;
    private long timestampsMissing, timestampsRepeated, timestampsRegressed;
    private long? lastVideoTimestamp;
    private bool queueCompleted;

    internal void ObserveVideo(long? timestamp, long nominalDuration, bool discontinuity = false)
    {
        lock (gate)
        {
            capturedVideoPackets++;
            if (discontinuity && capturedVideoPackets > 1)
                captureDiscontinuities++;
            if (timestamp is null)
                timestampsMissing++;
            else if (lastVideoTimestamp is { } previous)
            {
                if (timestamp < previous)
                    timestampsRegressed++;
                else if (timestamp == previous)
                    timestampsRepeated++;
                else if (nominalDuration > 0)
                {
                    comparableVideoIntervals++;
                    var interval = ((decimal)timestamp.Value - previous) / nominalDuration;
                    var missing = Math.Max(0, Math.Round(interval, MidpointRounding.AwayFromZero) - 1);
                    estimatedMissingFrames += (long)Math.Min(missing, long.MaxValue - estimatedMissingFrames);
                }
            }
            lastVideoTimestamp = timestamp;
        }
    }

    internal bool TryEnqueue<T>(ChannelWriter<T> writer, T packet, int bytes, bool video)
    {
        lock (gate)
        {
            if (queueCompleted)
                throw new InvalidOperationException("The recording queue is closed.");
            if (!writer.TryWrite(packet))
            {
                Reject(video);
                return false;
            }
            queuePackets++;
            queueBytes += bytes;
            queuePeakPackets = Math.Max(queuePeakPackets, queuePackets);
            queuePeakBytes = Math.Max(queuePeakBytes, queueBytes);
            return true;
        }
    }

    internal void Reject(bool video)
    {
        lock (gate)
        {
            queueRejectedPackets++;
            if (video)
                queueRejectedVideoPackets++;
        }
    }

    internal void Complete<T>(ChannelWriter<T> writer)
    {
        lock (gate)
        {
            queueCompleted = true;
            writer.TryComplete();
        }
    }

    internal async IAsyncEnumerable<T> ReadAllAsync<T>(ChannelReader<T> reader, Func<T, int> size)
    {
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (TryDequeue(reader, size, out var packet))
                yield return packet;
        }
    }

    internal bool TryDequeue<T>(ChannelReader<T> reader, Func<T, int> size, out T packet)
    {
        lock (gate)
        {
            if (!reader.TryRead(out packet!))
                return false;
            queuePackets--;
            queueBytes -= size(packet);
            return true;
        }
    }

    internal QueueSnapshot Queue
    {
        get
        {
            lock (gate)
                return new(queuePeakPackets, queuePeakBytes, queueRejectedPackets, queueRejectedVideoPackets,
                    capturedVideoPackets, estimatedMissingFrames, captureDiscontinuities, comparableVideoIntervals,
                    timestampsMissing, timestampsRepeated, timestampsRegressed);
        }
    }

    internal readonly record struct QueueSnapshot(int PeakPackets, long PeakBytes, long RejectedPackets,
        long RejectedVideoPackets, long CapturedVideoPackets, long EstimatedMissingFrames, long CaptureDiscontinuities,
        long ComparableVideoIntervals, long TimestampsMissing, long TimestampsRepeated, long TimestampsRegressed);
}
