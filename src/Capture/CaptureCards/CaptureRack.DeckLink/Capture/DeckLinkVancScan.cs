using System.Runtime.InteropServices;

namespace CaptureRack.DeckLink;

public enum VancScanMethod { Direct, Mapping }
public enum VancLineKind { Waveform, Caption608, AncPacket }
public sealed record VancScanLine(int Line, VancLineKind Kind);
public sealed record VancScanResult(VideoMode Mode, VancScanMethod Method, IReadOnlyList<VancScanLine> Lines,
    bool Readable);
public readonly record struct VancScanProgress(int LinesScanned, int TotalLines);

public static partial class DeckLinkCapture
{
    private const uint DeviceBusyStatus = 0x62757379; // busy
    private const int LastNtscScanLine = 30;
    private static readonly TimeSpan ScanFrameTimeout = TimeSpan.FromSeconds(2);

    public static bool IsVancScanInput(uint connection) => connection is 1 or 4 or 8 or 16 or 32;

    public static VancScanResult ScanVanc(CaptureDevice selected, CaptureRouting routing, uint? requestedMode,
        AnalogInputOptions analogOptions, IProgress<VancScanProgress> progress, CancellationToken stop)
    {
        if (!IsVancScanInput(routing.Video))
            throw new ArgumentException("VANC/VBI scanning is unavailable on this input.", nameof(routing));

        VancScanResult? result = null;
        VisitDevices((index, device) =>
        {
            if (index != selected.Index)
                return true;

            stop.ThrowIfCancellationRequested();
            if (selected.PersistentId is { } id && (device is not IDeckLinkProfileAttributes attributes ||
                attributes.GetInt(0x70656964, out var actualId) != 0 || actualId != id))
                return false;
            if (device is not IDeckLinkInput input || device is not IDeckLinkConfiguration configuration)
                throw new InvalidOperationException("This DeckLink device does not expose video input.");

            var status = device as IDeckLinkStatus;
            if (status is not null && status.GetInt(DeviceBusyStatus, out var busy) == 0 && busy != 0)
                throw new InvalidOperationException("The DeckLink is already in use. Stop capture or playback before scanning.");

            using var route = new InputRoutingScope(device, routing with { VancInput = null }, analogOptions);
            var modes = ReadModes(input, routing.Video, CapturePixelFormat.V210);
            var mode = ResolveVancScanMode(modes, status, requestedMode, stop);
            var analog = routing.Video is 8 or 16 or 32;
            var ntsc = mode.Id == Ntsc && mode.Width == 720 && mode.Height == 486;
            if (analog && !ntsc)
                throw new InvalidOperationException("Analog VBI scanning currently requires 525i59.94 NTSC.");

            var direct = new DirectVancScan(ntsc, readPackets: !analog);
            progress.Report(new(0, 0));
            CaptureScanFrames(input, mode, direct.Observe, frames: 12, stop);
            var found = direct.GetLines();
            if (found.Count != 0 || !ntsc || !CanReadVancMappings(configuration))
            {
                result = new(mode, VancScanMethod.Direct, found, direct.Readable);
                return false;
            }

            result = ScanMappedVbi(input, configuration, mode, progress, stop);
            return false;
        });
        return result ?? throw new InvalidOperationException("The selected DeckLink device is no longer available.");
    }

    private static VideoMode ResolveVancScanMode(IReadOnlyList<VideoMode> modes, IDeckLinkStatus? status,
        uint? requestedMode, CancellationToken stop)
    {
        if (requestedMode is { } id)
            return modes.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new InvalidOperationException("The selected mode does not support 10-bit YUV scanning.");

        if (status is not null)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (status.GetFlag(VideoInputSignalLocked, out var locked) == 0 && locked != 0 &&
                    status.GetInt(DetectedInputMode, out var detected) == 0 &&
                    modes.FirstOrDefault(candidate => candidate.Id == (uint)detected) is { } mode)
                    return mode;
                if (stop.WaitHandle.WaitOne(100))
                    stop.ThrowIfCancellationRequested();
            }
        }

        return modes.FirstOrDefault(candidate => candidate.Id == Ntsc)
            ?? throw new InvalidOperationException("No scan mode was detected. Select a fixed video mode before scanning.");
    }

    private static VancScanResult ScanMappedVbi(IDeckLinkInput input, IDeckLinkConfiguration configuration,
        VideoMode mode, IProgress<VancScanProgress> progress, CancellationToken stop)
    {
        using var mapping = new VancMappingScope(configuration);
        progress.Report(new(0, LastNtscScanLine));
        mapping.Apply([0, 0, 0]);
        var originalRows = new List<byte[]>[] { [], [], [] };
        CaptureScanFrames(input, mode, frame =>
        {
            ReadMappedRows(frame, (row, luma) => originalRows[row].Add(luma.ToArray()));
        }, frames: 6, stop);

        var found = new List<VancScanLine>();
        for (var first = 1; first <= LastNtscScanLine; first += VancMappingCount)
        {
            stop.ThrowIfCancellationRequested();
            mapping.Apply([first, first + 1, first + 2]);
            var evidence = new VbiLineEvidence[] { new(), new(), new() };
            CaptureScanFrames(input, mode, frame =>
            {
                ReadMappedRows(frame, (row, luma) => evidence[row].Observe(first + row, luma, originalRows[row]));
            }, frames: 6, stop);
            for (var row = 0; row < VancMappingCount; row++)
            {
                if (evidence[row].Kind is { } kind)
                    found.Add(new(first + row, kind));
            }

            progress.Report(new(first + VancMappingCount - 1, LastNtscScanLine));
        }

        return new(mode, VancScanMethod.Mapping, found, Readable: true);
    }

    private delegate void ScanRow(int index, ReadOnlySpan<byte> luma);

    private static unsafe void ReadMappedRows(IDeckLinkVideoInputFrame frame, ScanRow inspect)
    {
        const uint bufferRead = 1;
        var buffer = (IDeckLinkVideoBuffer)frame;
        Check(buffer.StartAccess(bufferRead), "Access the mapped video frame");
        try
        {
            Check(buffer.GetBytes(out var pixels), "Get the mapped video frame");
            Check(buffer.GetSize(out var length), "Get the mapped video frame size");
            var stride = frame.GetRowBytes();
            const int width = 720;
            var rowLength = (width + 5) / 6 * 16;
            if (pixels == 0 || stride < rowLength || length < (ulong)stride * VancMappingCount)
                throw new InvalidOperationException("The mapped video frame buffer is incomplete.");

            Span<byte> luma = stackalloc byte[width];
            for (var row = 0; row < VancMappingCount; row++)
            {
                var bytes = new ReadOnlySpan<byte>((byte*)pixels + row * stride, rowLength);
                ReadVbiLuma(bytes, CapturePixelFormat.V210, luma);
                inspect(row, luma);
            }
        }
        finally
        {
            Check(buffer.EndAccess(bufferRead), "Release the mapped video buffer");
        }
    }

    private static void ReadVbiLuma(ReadOnlySpan<byte> bytes, CapturePixelFormat format, Span<byte> luma)
    {
        var words = MemoryMarshal.Cast<byte, uint>(bytes);
        for (var x = 0; x < luma.Length; x++)
        {
            PreviewFeed.ReadYuv(bytes, words, x, format, out var y, out _, out _);
            luma[x] = (byte)(y >> 2);
        }
    }

    private static void CaptureScanFrames(IDeckLinkInput input, VideoMode mode, Action<IDeckLinkVideoInputFrame> inspect,
        int frames, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        var collector = new VancScanCallback(mode, inspect, frames);
        Check(input.SetCallback(collector), "Set VANC scan callback");
        try
        {
            Check(input.EnableVideoInput(mode.Id, (uint)CapturePixelFormat.V210, 0), "Enable 10-bit VANC scan input");
            try
            {
                Check(input.StartStreams(), "Start VANC scan");
                try
                {
                    collector.Completed.WaitAsync(ScanFrameTimeout, stop).GetAwaiter().GetResult();
                }
                catch (TimeoutException ex)
                {
                    throw new InvalidOperationException("No usable frames arrived during the scan. Check input lock and video mode.", ex);
                }
                finally
                {
                    Check(input.StopStreams(), "Stop VANC scan stream");
                }
            }
            finally
            {
                Check(input.DisableVideoInput(), "Disable VANC scan input");
            }
        }
        finally
        {
            Check(input.SetCallback(null), "Clear VANC scan callback");
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class VancScanCallback(VideoMode mode, Action<IDeckLinkVideoInputFrame> inspect, int frames)
        : IDeckLinkInputCallback
    {
        private readonly object gate = new();
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int validFrames;

        public Task Completed => done.Task;

        public int VideoInputFormatChanged(uint events, IntPtr displayMode, uint flags) => 0;

        public int VideoInputFrameArrived(IntPtr video, IntPtr audio)
        {
            lock (gate)
            {
                if (video == 0 || done.Task.IsCompleted)
                    return 0;

                try
                {
                    var frame = (IDeckLinkVideoInputFrame)Marshal.GetObjectForIUnknown(video);
                    try
                    {
                        if ((frame.GetFlags() & (1u << 31)) != 0 || frame.GetWidth() != mode.Width ||
                            frame.GetHeight() != mode.Height || frame.GetPixelFormat() != (uint)CapturePixelFormat.V210)
                            return 0;

                        // Skip initial frames while the input settles after a routing or mapping change.
                        if (++validFrames > 2)
                            inspect(frame);
                        if (validFrames >= frames + 2)
                            done.TrySetResult();
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(frame);
                    }
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }

                return 0;
            }
        }
    }

    private sealed class DirectVancScan(bool ntsc, bool readPackets)
    {
        private readonly SortedSet<int> packetLines = [];
        private readonly SortedDictionary<int, VbiLineEvidence> rawLines = [];
        public bool Readable { get; private set; }

        public void Observe(IDeckLinkVideoInputFrame frame)
        {
            if (readPackets)
                ReadPacketLines(frame);
            if (ntsc)
                ReadRawNtscLines(frame);
        }

        public IReadOnlyList<VancScanLine> GetLines()
        {
            var results = new SortedDictionary<int, VancLineKind>();
            foreach (var (line, evidence) in rawLines)
            {
                if (evidence.Kind is { } kind)
                    results[line] = kind;
            }
            foreach (var line in packetLines)
                results[line] = VancLineKind.AncPacket;
            return results.Select(pair => new VancScanLine(pair.Key, pair.Value)).ToArray();
        }

        private void ReadPacketLines(IDeckLinkVideoInputFrame frame)
        {
            if (frame is not IDeckLinkScanAncillaryPackets packets ||
                packets.GetPacketIterator(out var iterator) != 0 || iterator is null)
                return;

            try
            {
                Readable = true;
                while (iterator.Next(out var packet) == 0 && packet is not null)
                {
                    try
                    {
                        const uint verticalDataSpace = 0;
                        var line = packet.GetLineNumber();
                        if (packet.GetDataSpace() == verticalDataSpace && line is > 0 and <= int.MaxValue)
                            packetLines.Add((int)line);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(packet);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(iterator);
            }
        }

        private unsafe void ReadRawNtscLines(IDeckLinkVideoInputFrame frame)
        {
            if (frame.GetAncillaryData(out var pointer) != 0 || pointer == 0)
                return;

            try
            {
                var ancillary = (IDeckLinkScanAncillary)Marshal.GetObjectForIUnknown(pointer);
                try
                {
                    var format = (CapturePixelFormat)ancillary.GetPixelFormat();
                    if (format is not (CapturePixelFormat.V210 or CapturePixelFormat.Uyvy))
                        return;
                    const int width = 720;
                    var length = format == CapturePixelFormat.V210 ? (width + 5) / 6 * 16 : width * 2;
                    Span<byte> luma = stackalloc byte[width];
                    for (var line = 1; line <= LastNtscScanLine; line++)
                    {
                        if (ancillary.GetBufferForVerticalBlankingLine((uint)line, out var pixels) != 0 || pixels == 0)
                            continue;
                        Readable = true;
                        ReadVbiLuma(new ReadOnlySpan<byte>((void*)pixels, length), format, luma);
                        if (!rawLines.TryGetValue(line, out var evidence))
                            rawLines.Add(line, evidence = new());
                        evidence.Observe(line, luma);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(ancillary);
                }
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }
    }
}
