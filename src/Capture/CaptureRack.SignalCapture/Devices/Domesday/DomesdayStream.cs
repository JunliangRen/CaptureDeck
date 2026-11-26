using System.Runtime.InteropServices;

namespace CaptureRack.SignalCapture;

internal sealed class DomesdayStream(bool legacy, DomesdayRamp? ramp = null, DomesdayDiagnostics? diagnostics = null)
{
    private int marker = -1, runLength, learnedLength;
    private long samples;
    private bool initial = true;

    public void Convert(Span<byte> bytes)
    {
        var words = MemoryMarshal.Cast<byte, ushort>(bytes);
        if (legacy)
        {
            ConvertLegacy(words);
            return;
        }
        int i = 0;
        try
        {
            for (; i < words.Length; i++)
            {
                int word = words[i], next = word >> 10;
                if (next == 63) throw new InvalidDataException("Invalid DdD sequence marker.");
                if (marker != next)
                {
                    if (marker >= 0)
                    {
                        if (next != (marker + 1) % 63)
                            throw new InvalidDataException($"DdD sequence skipped from {marker} to {next} after {runLength} samples. Capture data was lost.");
                        if (!initial)
                        {
                            if (runLength is not (65535 or 65536) || learnedLength != 0 && learnedLength != runLength)
                                throw new InvalidDataException("Incomplete DdD sequence. Capture data was lost.");
                            learnedLength = runLength;
                        }
                        initial = false;
                    }
                    marker = next;
                    runLength = 0;
                }
                if (++runLength > 65536) throw new InvalidDataException("DdD marker failed to advance.");
                ramp?.Check(word & 1023);
                words[i] = unchecked((ushort)(((word & 1023) - 512) << 6));
            }
        }
        catch (InvalidDataException)
        {
            if (diagnostics is not null)
            {
                int? expected = marker < 0 ? null : (marker + 1) % 63;
                if (!initial && learnedLength != 0 && runLength < learnedLength) expected = marker;
                diagnostics.Position = new(samples + i, marker < 0 ? null : marker, expected, words[i] >> 10, runLength);
            }
            throw;
        }
        samples += words.Length;
        if (diagnostics is not null) diagnostics.Position = new(samples, marker, null, null, runLength);
    }

    private void ConvertLegacy(Span<ushort> words)
    {
        if (diagnostics is not null && diagnostics.LegacyMarkerState is null)
            diagnostics.LegacyMarkerState = "Unlocked";
        for (int i = 0; i < words.Length; i++)
        {
            int word = words[i], next = word >> 10;
            if (marker >= 0)
            {
                // Before the first transition the initial run may be partial, or the
                // firmware may emit no markers at all. Only a transition establishes phase.
                int expected = (marker + 1) & 63;
                if (!initial && runLength < 65536) expected = marker;
                if ((next != marker || !initial) && next != expected)
                {
                    if (diagnostics is not null)
                    {
                        diagnostics.LegacyMarkerState = "Lost";
                        diagnostics.Position = new(samples + i, marker, expected, next, runLength);
                    }
                    throw new InvalidDataException($"Legacy DdD marker lock lost at sample {samples + i}: expected {expected}, received {next}.");
                }
                if (initial && next != marker)
                {
                    initial = false;
                    if (diagnostics is not null)
                    {
                        diagnostics.LegacyMarkerState = "Locked";
                        diagnostics.LegacyMarkerLockSample = samples + i;
                    }
                }
            }
            if (next != marker) runLength = 0;
            marker = next;
            // Cap the unbounded opening run of a markerless recording.
            runLength = Math.Min(65537, runLength + 1);
            try { ramp?.Check(word & 1023); }
            catch (InvalidDataException)
            {
                if (diagnostics is not null) diagnostics.Position = new(samples + i, marker, marker, next, runLength);
                throw;
            }
            words[i] = unchecked((ushort)(((word & 1023) - 512) << 6));
        }
        samples += words.Length;
        if (diagnostics is not null) diagnostics.Position = new(samples, marker, null, null, runLength);
    }

    public static void WithDevice(RfUsbDevice device, Action<WinUsbDevice, RfUsbProbeResult, DomesdaySession> record)
    {
        var path = RfUsbDiscovery.GetInterfacePath(device.InstanceId);
        using var usb = WinUsbDevice.Open(path);
        var session = new DomesdaySession(path);
        usb.SetControlTimeout(1000);
        try { record(usb, WinUsbRfProbe.Inspect(device, usb), session); }
        catch (Exception ex)
        {
            // Acquisition already combined its error with any rollback failures.
            var cleanup = new DomesdaySession(path);
            cleanup.Fail(ex);
            if (!usb.HasUnreapedRequests) cleanup.Cleanup("Restore alternate setting", usb.RestoreAlternate);
            cleanup.ThrowIfFailed();
        }
    }

    public static RampResult? Read(WinUsbDevice usb, RfUsbProbeResult probe, DomesdaySession session,
        bool halfRate, bool testMode, DomesdayDiagnostics diagnostics, Action<byte[], int> consume, CancellationToken stop)
    {
        diagnostics.StreamEndpoint = probe.StreamEndpoint;
        diagnostics.LinkSpeedHint = probe.LinkSpeedHint;
        bool legacy = probe.Firmware == RfUsbFirmware.Legacy;
        if (legacy) diagnostics.LegacyMarkerState = "Unlocked";
        var ramp = testMode ? new DomesdayRamp(probe.GatewareCommit is "bb2e11a" or "bb2e11aa") : null;
        var decoder = new DomesdayStream(legacy, ramp, diagnostics);
        int discard = legacy ? 0 : 8 * 1024 * 1024;
        void Register(byte address, byte value)
        {
            usb.Command(0xb8, (ushort)((address << 8) | value));
            Span<byte> readback = stackalloc byte[1];
            if (usb.ReadVendorControl(0xb7, readback, "Read DdD register", address) != 1 || readback[0] != value)
                throw new IOException($"DdD register 0x{address:X2} verification failed.");
        }
        try
        {
            if (legacy)
            {
                if (halfRate) throw new ArgumentException("Legacy DdD requires 40 MSPS.");
                usb.Command(0xb6, testMode ? (ushort)1 : (ushort)0);
                usb.ClearLegacyHalt(probe.StreamEndpoint.Address);
            }
            else
            {
                Register(0x10, testMode ? (byte)1 : (byte)0);
                Register(0x12, halfRate ? (byte)2 : (byte)1);
                usb.Command(0xb5, 1);
            }
            usb.Stream(probe.StreamEndpoint, probe.MaximumTransferBytes, (buffer, count) =>
            {
                int skip = Math.Min(discard, count);
                discard -= skip;
                if (skip == count) return false;
                if (skip != 0) Buffer.BlockCopy(buffer, skip, buffer, 0, count - skip);
                count -= skip;
                decoder.Convert(buffer.AsSpan(0, count));
                consume(buffer, count);
                return true;
            }, stop, legacy ? null : diagnostics, session, diagnostics);
        }
        catch (Exception ex) { session.Fail(ex); }

        // An unreaped control request could race a rollback. Keep the handles and
        // pinned buffers alive, and refuse further operations on this device path.
        if (!usb.HasUnreapedRequests)
        {
            session.Rollback(legacy, testMode, usb.Command, Register);
            session.Cleanup("Restore alternate setting", usb.RestoreAlternate);
        }
        session.ThrowIfFailed();
        return ramp?.Result;
    }
}
