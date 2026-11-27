using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CaptureRack.SignalCapture;

// USB-thread wall time. GC pause is process-wide evidence and may overlap a stage.
internal enum CallbackStage { Other, Convert, Rent, Copy, QueueLock, Enqueue }

internal readonly record struct CallbackSample(long Number, int Slot, long FirstSample, long EndSample,
    double TotalMs, double ConvertMs, double RentMs, double CopyMs, double QueueLockMs,
    double EnqueueMs, double OtherMs, double? ThreadCpuMs, double GcPauseMs, double? SincePreviousEndMs, int? QueueBlocks);

internal sealed partial class DomesdayCallbackTiming
{
    // Keep one request ring so a fault can also identify a delayed preceding transfer.
    private readonly CallbackSample[] recent = new CallbackSample[16];
    private readonly long[] ticks = new long[6];
    private long count, start, boundary, previousEnd, firstSample;
    private int slot;
    private int? queueBlocks;
    private CallbackStage phase;
    private TimeSpan gcStart;
    private long? cpuStart;

    // Windows charges CPU time at scheduler granularity. Useful on a long stall, noisy on a short callback.
    [LibraryImport("kernel32.dll")]
    private static partial int GetThreadTimes(nint thread, out long created, out long exited, out long kernel, out long user);

    private static long? ThreadCpuTicks() => OperatingSystem.IsWindows() &&
        GetThreadTimes(-2, out _, out _, out var kernel, out var user) != 0 ? kernel + user : null;

    public CallbackSample? Longest { get; private set; }
    public CallbackSample[] Recent
    {
        get
        {
            var result = new CallbackSample[(int)Math.Min(count, recent.Length)];
            for (int i = 0; i < result.Length; i++)
                result[i] = recent[(count - result.Length + i) % recent.Length];
            return result;
        }
    }

    internal void Begin(int requestSlot, long sample)
    {
        Array.Clear(ticks);
        slot = requestSlot;
        firstSample = sample;
        queueBlocks = null;
        phase = CallbackStage.Other;
        cpuStart = ThreadCpuTicks();
        gcStart = GC.GetTotalPauseDuration();
        start = boundary = Stopwatch.GetTimestamp();
    }

    internal void Stage(CallbackStage next)
    {
        if (start == 0) return;
        var now = Stopwatch.GetTimestamp();
        ticks[(int)phase] += now - boundary;
        boundary = now;
        phase = next;
    }

    internal void Queued(int depth)
    {
        queueBlocks = depth;
        Stage(CallbackStage.Other);
    }

    internal long End(long endSample)
    {
        var end = Stopwatch.GetTimestamp();
        ticks[(int)phase] += end - boundary;
        long elapsed = end - start;
        static double Ms(long value) => value * 1000d / Stopwatch.Frequency;
        double Duration(CallbackStage stage) => Ms(ticks[(int)stage]);
        var sample = new CallbackSample(++count, slot, firstSample, endSample, Ms(elapsed),
            Duration(CallbackStage.Convert), Duration(CallbackStage.Rent), Duration(CallbackStage.Copy),
            Duration(CallbackStage.QueueLock), Duration(CallbackStage.Enqueue), Duration(CallbackStage.Other),
            (ThreadCpuTicks() - cpuStart) / (double)TimeSpan.TicksPerMillisecond,
            (GC.GetTotalPauseDuration() - gcStart).TotalMilliseconds,
            previousEnd == 0 ? null : Ms(start - previousEnd), queueBlocks);
        recent[(count - 1) % recent.Length] = sample;
        if (Longest is null || sample.TotalMs > Longest.Value.TotalMs) Longest = sample;
        previousEnd = end;
        start = 0;
        return elapsed;
    }
}
