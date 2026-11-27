using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CaptureRack.SignalCapture;

// The acquisition thread owns the device and level windows. Each recorder borrows
// complete blocks until its own cancellation and drains its own bounded writer.
public sealed class RfPreviewSession(RfRecording request)
{
    private readonly object gate = new();
    private readonly Guid id = Guid.NewGuid();
    private readonly DomesdayDiagnostics live = new();
    private DomesdayDiagnostics? clip;
    private Action<byte[], int>? deliver;
    private Exception? failure;
    private bool ended;
    private long frames;

    public void Run(Func<int?> vga, Action<RfMeasurement> report, CancellationToken stop,
        Action<byte[], int, long>? previewSamples = null) =>
        Run((consume, token) => ReadDevice(vga, consume, token), report, stop, previewSamples);

    internal void Run(Action<Action<byte[], int>, CancellationToken> read, Action<RfMeasurement> report,
        CancellationToken stop, Action<byte[], int, long>? previewSamples = null)
    {
        var a = new RfRecording.LevelWindow();
        var b = new RfRecording.LevelWindow();
        var clock = Stopwatch.StartNew();
        bool iq = request.Rtl is not null;
        void Consume(byte[] bytes, int size)
        {
            long start = Stopwatch.GetTimestamp();
            if (iq) { a.AddIq(bytes.AsSpan(0, size), 0); b.AddIq(bytes.AsSpan(0, size), 1); }
            else a.AddDdd(MemoryMarshal.Cast<byte, short>(bytes.AsSpan(0, size)));
            previewSamples?.Invoke(bytes, size, frames);
            lock (gate)
            {
                if (clip is not null)
                {
                    clip.FirstPreviewSample ??= frames;
                    CopyPosition(clip);
                }
                deliver?.Invoke(bytes, size);
                clip?.ObserveCallback(start);
                frames += size / 2;
            }
            if (clock.ElapsedMilliseconds < 100) return;
            report(new(frames, iq ? 128 : 512, a.Take(), iq ? b.Take() : null,
                live.Latest, live.PollingDisabled, live.LegacyMarkerState));
            clock.Restart();
        }

        try
        {
            read(Consume, stop);
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                failure = ex;
                if (clip is not null) { CopyPosition(clip); clip.CaptureFault(ex); }
            }
            throw;
        }
        finally { lock (gate) { ended = true; Monitor.PulseAll(gate); } }
    }

    private void ReadDevice(Func<int?> vga, Action<byte[], int> consume, CancellationToken stop)
    {
        if (request.Ddd is not { } device)
            RtlSdrProbe.Stream(request.Rtl!, new(request.Frequency, (uint)request.RtlSampleRate, 0, VgaIndex: vga()), vga, consume, stop);
        else DomesdayStream.WithDevice(device, (usb, probe, session) =>
        {
            live.Source = new(device.UsbId, device.InstanceId, probe.Firmware.ToString(), probe.FirmwareRelease,
                probe.Fx3Commit, probe.GatewareCommit, request.Rate, request.HalfRate ? 2 : 1,
                request.TestRamp, request.Sw401, request.FormatName, request.SoftwareDecimation);
            live.IntervalReceived = block => { lock (gate) clip?.Accept(block); };
            live.FailedRead = () =>
            {
                lock (gate)
                {
                    clip?.CountFailure();
                    if (live.PollingDisabled && clip is not null) clip.SessionTelemetryUnavailable = true;
                }
            };
            DomesdayStream.Read(usb, probe, session, request.HalfRate, request.TestRamp, live, consume, stop);
        });
    }

    private void CopyPosition(DomesdayDiagnostics target)
    {
        if (live.Position is { } position && target.FirstPreviewSample is long first)
            target.Position = position with { SampleIndex = position.SampleIndex - first };
        target.UsbState = live.UsbState;
        target.LegacyMarkerState = live.LegacyMarkerState;
        if (live.LegacyMarkerLockSample is long locked && target.FirstPreviewSample is long offset)
            target.LegacyMarkerLockSample = Math.Max(0, locked - offset);
    }

    public RfRampQc? Record(string prefix, string? ffmpeg, CancellationToken stop, Action<RfMeasurement>? report = null)
    {
        var diagnostics = request.Ddd is null ? null : new DomesdayDiagnostics
        {
            Source = live.Source, StreamEndpoint = live.StreamEndpoint, LinkSpeedHint = live.LinkSpeedHint,
            BulkTransferBytes = live.BulkTransferBytes, PreviewSessionId = id
        };
        var ramp = request.TestRamp ? new DomesdayRamp(live.Source?.GatewareCommit is "bb2e11a" or "bb2e11aa") : null;
        Exception? writerFailure = null;
        void RecordClip() => request.RecordSamples(prefix, ffmpeg, report ?? (_ => { }), stop, (enqueue, token) =>
        {
            using var wake = token.Register(() => { lock (gate) Monitor.PulseAll(gate); });
            lock (gate)
            {
                if (ended || deliver is not null) throw new IOException("RF preview session unavailable.", failure);
                clip = diagnostics;
                if (live.PollingDisabled && clip is not null) clip.SessionTelemetryUnavailable = true;
                deliver = (bytes, count) =>
                {
                    if (writerFailure is not null) return;
                    if (ramp is not null)
                        foreach (var word in MemoryMarshal.Cast<byte, short>(bytes.AsSpan(0, count))) ramp.Check((word >> 6) + 512);
                    try { enqueue(bytes, count); }
                    catch (IOException ex)
                    {
                        writerFailure = ex;
                        clip?.CaptureFault(ex);
                        Monitor.PulseAll(gate);
                    }
                };
                try
                {
                    while (!token.IsCancellationRequested && !ended && writerFailure is null) Monitor.Wait(gate);
                    if (writerFailure is not null) throw writerFailure;
                    if (failure is not null) throw new IOException(failure.Message, failure);
                    if (ended && !token.IsCancellationRequested) throw new IOException("RF preview ended during recording.");
                }
                finally { deliver = null; clip = null; }
            }
        }, inspect: ramp is null ? null : (_, count) =>
        {
            if (count != ramp.Result.Samples) throw new InvalidDataException("Live ramp and writer counts differ.");
        }, deferPublish: ramp is not null, diagnostics: diagnostics);
        if (diagnostics is null) RecordClip();
        else diagnostics.WithReport(prefix + request.Suffix + ".fifo.json", RecordClip);
        return ramp is null ? null : new RfRampQc(prefix + request.Suffix, request.Flac, ffmpeg, ramp.Result, request.SoftwareDecimation);
    }
}
