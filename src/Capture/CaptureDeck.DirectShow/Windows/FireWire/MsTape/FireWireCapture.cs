using System.Runtime.InteropServices;
using System.Text.Json;
using CaptureDeck.DirectShow.Windows.Interop;
using CaptureDeck.QualityControl;
using CaptureDeck.Recording.TransportStream;
using CaptureDeck.Recording.Windows;
using static CaptureDeck.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureDeck.DirectShow.Windows.FireWire.MsTape;

public sealed class FireWireException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public sealed record FireWireRequest(string DeviceId, string OutputPath, TimeSpan Duration, TimeSpan NoDataTimeout);

public sealed record FireWireProgress(double ElapsedSeconds, long ReceivedBytes, long WrittenBytes);

public sealed record FireWireResult
{
    public int SchemaVersion => 1;
    public string Type => "capture_result";
    public string SourceKind => "mstape";
    public int ExitCode { get; init; }
    public bool Success => ExitCode == 0;
    public string? Error { get; init; }
    public required string StopReason { get; init; }
    public required string RequestedFormat { get; init; }
    public string? OutputFormat { get; init; }
    public required string OutputPath { get; init; }
    public required string DataPath { get; init; }
    public required string ReportPath { get; init; }
    public bool DataFinalized { get; init; }
    public bool DataFileCreated { get; init; }
    public bool StreamStarted { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset EndedUtc { get; init; }
    public long ReceivedBytes { get; init; }
    public long ReceivedSamples { get; init; }
    public long WrittenBytes { get; init; }
    public long QueuedBytes { get; init; }
    public long NotQueuedBytes { get; init; }
    public long QueuePeakBytes { get; init; }
    public long ProcessedInputBytes { get; init; }
    public long SampleDiscontinuities { get; init; }
    public long SampleDiscontinuitiesAfterFirst { get; init; }
    public long SinkReceiveCalls { get; init; }
    public long SinkZeroLengthSamples { get; init; }
    public long SinkRejectedSamples { get; init; }
    public bool AnalysisComplete { get; init; }
    public bool StructuralValidationOk => Analysis.StructuralValidationOk;
    public bool QualityWarning => Analysis.SyncErrors != 0 || Analysis.MalformedPackets != 0 ||
        Analysis.TrailingBytes != 0 || Analysis.HasTransportWarnings || SampleDiscontinuitiesAfterFirst != 0;
    public required TsStatistics Analysis { get; init; }
}

public static unsafe class FireWireCapture
{
    private static readonly JsonSerializerOptions ReportJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static bool IsSupported => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public static IReadOnlyList<FireWireDevice> List()
    {
        CheckPlatform();
        try
        {
            using var apartment = new ComApartment();
            using var devices = MsTapeDevices.Scan();
            return devices.Descriptions;
        }
        catch (DirectShowException ex) { throw new FireWireException(3, ex.Message); }
    }

    public static IReadOnlyList<TsLayout> GetFormats(string deviceId)
    {
        CheckPlatform();
        try
        {
            using var apartment = new ComApartment();
            using var devices = MsTapeDevices.Scan();
            using var source = devices.Bind(deviceId);
            using var output = MsTapeDevices.SelectOutput(source.Value);
            return [output.Layout];
        }
        catch (FireWireException ex) when (ex.ExitCode == 4)
        {
            return [];
        }
        catch (DirectShowException ex) { throw new FireWireException(3, ex.Message); }
    }

    internal static void CheckPlatform()
    {
        if (!IsSupported)
            throw new FireWireException(3, "MSTape capture requires Windows x64.");
    }

    public static FireWireResult Run(FireWireRequest request, Action<FireWireProgress>? progress,
        Action? started, CancellationToken cancellationToken)
    {
        CheckPlatform();
        var path = Path.GetFullPath(request.OutputPath);
        var report = path + ".report.json";
        var reportPartial = report + ".partial";
        var startedUtc = DateTimeOffset.UtcNow;
        var endReason = "error";
        var streamStarted = false;
        string? outputFormat = null;
        TsRecording? recording = null;
        (int Code, string Message)? error = null;
        (long Calls, long Empty, long Rejected) diagnostics = default;
        FileStream? reportStream = null;
        try
        {
            foreach (var candidate in new[] { path, path + ".partial", report, reportPartial })
            {
                if (Path.Exists(candidate))
                    throw new IOException($"Output already exists: {candidate}.");
            }
            reportStream = new FileStream(reportPartial, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var apartment = new ComApartment();
            using var devices = MsTapeDevices.Scan();
            using var source = devices.Bind(request.DeviceId);
            using var selected = MsTapeDevices.SelectOutput(source.Value);
            outputFormat = selected.Layout.Format;
            recording = new TsRecording(path, selected.Layout);
            Action<string> reportFailure = message => recording.Fail(6, message);
            using var sink = new DirectShowSink(selected.MediaType, recording.TryReceive, reportFailure);
            using var graph = new FireWireGraph();
            try
            {
                graph.Connect(source.Value, selected, sink);
                if (cancellationToken.IsCancellationRequested)
                {
                    endReason = "cancelled";
                }
                else
                {
                    graph.Start();
                    streamStarted = true;
                    started?.Invoke();
                    endReason = Monitor(graph, sink, recording, request, progress, reportFailure, cancellationToken);
                }
            }
            finally
            {
                graph.Stop(sink, reportFailure);
                diagnostics = (sink.ReceiveCalls, sink.ZeroLengthSamples, sink.RejectedSamples);
            }
        }
        catch (FireWireException ex) { error = (ex.ExitCode, ex.Message); }
        catch (DirectShowException ex) { error = (3, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = (5, ex.Message); }
        catch (Exception ex) { error = (6, ex.Message); }

        if (recording is not null)
        {
            using (recording)
            {
                if (error is { } fault)
                    recording.Fail(fault.Code, fault.Message);
                recording.Complete();
                error = recording.Failure;
            }
        }

        var result = new FireWireResult
        {
            ExitCode = error?.Code ?? 0,
            Error = error?.Message,
            StopReason = error is null ? endReason : "error",
            RequestedFormat = TsLayout.Ts192.Format,
            OutputFormat = outputFormat,
            OutputPath = path,
            DataPath = recording?.Finalized == true ? path : path + ".partial",
            ReportPath = report,
            DataFinalized = recording?.Finalized == true,
            DataFileCreated = recording is not null,
            StreamStarted = streamStarted,
            StartedUtc = startedUtc,
            EndedUtc = DateTimeOffset.UtcNow,
            ReceivedBytes = recording?.ReceivedBytes ?? 0,
            ReceivedSamples = recording?.ReceivedSamples ?? 0,
            WrittenBytes = recording?.WrittenBytes ?? 0,
            QueuedBytes = recording?.QueuedBytes ?? 0,
            NotQueuedBytes = recording?.RejectedBytes ?? 0,
            QueuePeakBytes = recording?.PeakQueueBytes ?? 0,
            ProcessedInputBytes = recording?.ProcessedBytes ?? 0,
            SampleDiscontinuities = recording?.DiscontinuitySamples ?? 0,
            SampleDiscontinuitiesAfterFirst = recording is null ? 0 :
                recording.DiscontinuitySamples - (recording.InitialDiscontinuity ? 1 : 0),
            SinkReceiveCalls = diagnostics.Calls,
            SinkZeroLengthSamples = diagnostics.Empty,
            SinkRejectedSamples = diagnostics.Rejected,
            AnalysisComplete = recording is not null && error is null && recording.ProcessedBytes == recording.ReceivedBytes,
            Analysis = recording?.Analysis ?? new TsStatistics()
        };
        if (reportStream is not null)
        {
            try
            {
                using (reportStream)
                {
                    JsonSerializer.Serialize(reportStream, result, ReportJson);
                    reportStream.Write("\r\n"u8);
                    reportStream.Flush(flushToDisk: true);
                }
                WindowsFile.MoveWriteThrough(reportPartial, report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var message = $"Cannot save report: {ex.Message}";
                result = result with { ExitCode = error?.Code ?? 5, StopReason = "error",
                    Error = error is null ? message : $"{error.Value.Message} {message}" };
            }
        }
        return result;
    }

    private static string Monitor(FireWireGraph graph, DirectShowSink sink, TsRecording recording,
        FireWireRequest request, Action<FireWireProgress>? progress, Action<string> reportFailure, CancellationToken cancellationToken)
    {
        var startup = recording.Elapsed;
        var nextProgress = startup;
        while (recording.Failure is null)
        {
            if (cancellationToken.IsCancellationRequested)
                return "ctrl_c";
            var complete = graph.ReadEvents(reportFailure);
            if (recording.Failure is not null)
                break;
            var now = recording.Elapsed;
            var idle = recording.HasSamples ? recording.SinceLastSample : now - startup;
            if (idle >= request.NoDataTimeout)
            {
                recording.Fail(7, $"No samples received for {request.NoDataTimeout.TotalSeconds:g} seconds. Check tape playback.");
                break;
            }
            var elapsed = recording.HasSamples ? recording.SinceFirstSample : TimeSpan.Zero;
            if (recording.HasSamples && request.Duration > TimeSpan.Zero && elapsed >= request.Duration)
                return "duration";
            if (complete || sink.Ended)
                return "end_of_stream";
            if (now >= nextProgress)
            {
                progress?.Invoke(new FireWireProgress(elapsed.TotalSeconds, recording.ReceivedBytes, recording.WrittenBytes));
                nextProgress = now + TimeSpan.FromSeconds(1);
            }
            cancellationToken.WaitHandle.WaitOne(50);
        }
        return "error";
    }
}

internal sealed unsafe class FireWireGraph : IDisposable
{
    private static readonly Guid FilterGraph = new("E436EBB3-524F-11CE-9F53-0020AF0BA770");
    private static readonly Guid GraphBuilder = new("56A868A9-0AD4-11CE-B03A-0020AF0BA770");
    private static readonly Guid MediaControl = new("56A868B1-0AD4-11CE-B03A-0020AF0BA770");
    private static readonly Guid MediaEvent = new("56A868B6-0AD4-11CE-B03A-0020AF0BA770");
    private readonly ComReference graph = Create(FilterGraph, GraphBuilder, "Create DirectShow filter graph");
    private ComReference? control;
    private ComReference? events;
    private ComReference? sinkFilter;
    private ComReference? sinkPin;
    private nint sourcePin;
    private nint sourceFilter;
    private bool connected;

    internal void Connect(nint source, MsTapeOutput selected, DirectShowSink sink)
    {
        AddFilter(source, "MSTape Source");
        sourceFilter = source;
        sinkFilter = new ComReference(Export(sink, BaseFilter));
        AddFilter(sinkFilter.Value, "CaptureDeck FireWire Sink");
        sinkPin = new ComReference(Export(sink, Pin));
        sourcePin = selected.Pin;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint, DirectShowMediaType*, int>)Method(graph.Value, 7))(
            graph.Value, sourcePin, sinkPin.Value, selected.MediaType); // IFilterGraph::ConnectDirect
        if (hr is TypeNotAccepted or unchecked((int)0x80040207) or unchecked((int)0x80040217) or
            unchecked((int)0x80040200) or unchecked((int)0x80040248))
            throw new FireWireException(4, $"MSTape rejected the selected TS format (0x{hr:X8}).");
        Check(hr, "Connect MSTape directly to the sink");
        connected = true;
        control = Query(graph.Value, MediaControl, "Get IMediaControl");
        events = Query(graph.Value, MediaEvent, "Get IMediaEvent");
        Check(NoArgs(graph.Value, 10), "Set default DirectShow graph clock");
    }

    private void AddFilter(nint filter, string name)
    {
        fixed (char* text = name)
            Check(((delegate* unmanaged[Stdcall]<nint, nint, char*, int>)Method(graph.Value, 3))(
                graph.Value, filter, text), "Add filter to DirectShow graph");
    }

    internal void Start() => Check(NoArgs(control!.Value, 7), "Run DirectShow graph"); // IMediaControl::Run

    internal bool ReadEvents(Action<string> reportFailure)
    {
        var source = events!.Value;
        var get = (delegate* unmanaged[Stdcall]<nint, int*, nint*, nint*, int, int>)Method(source, 8);
        var free = (delegate* unmanaged[Stdcall]<nint, int, nint, nint, int>)Method(source, 12);
        var complete = false;
        int code;
        nint first, second;
        while (get(source, &code, &first, &second, 0) == Ok)
        {
            try
            {
                switch (code)
                {
                    case 1: // EC_COMPLETE
                        complete = true;
                        break;
                    case 3: // EC_ERRORABORT
                        reportFailure($"DirectShow error (0x{unchecked((uint)first):X8}).");
                        break;
                    case 0x1f when second == 0: // EC_DEVICE_LOST
                        reportFailure("Capture device disconnected.");
                        break;
                }
            }
            finally { free(source, code, first, second); }
        }
        return complete;
    }

    internal void Stop(DirectShowSink sink, Action<string> reportFailure)
    {
        if (control is not null)
        {
            var hr = NoArgs(control.Value, 9); // IMediaControl::Stop
            if (hr < 0)
                reportFailure($"Stop DirectShow graph failed (0x{hr:X8}).");
        }
        sink.Stop(); // Serializes the state change with in-flight Receive calls.
    }

    public void Dispose()
    {
        if (connected)
        {
            var disconnect = (delegate* unmanaged[Stdcall]<nint, nint, int>)Method(graph.Value, 9);
            disconnect(graph.Value, sourcePin);
            disconnect(graph.Value, sinkPin!.Value);
        }
        var remove = (delegate* unmanaged[Stdcall]<nint, nint, int>)Method(graph.Value, 4);
        if (sinkFilter is not null)
            remove(graph.Value, sinkFilter.Value);
        if (sourceFilter != 0)
            remove(graph.Value, sourceFilter);
        events?.Dispose();
        control?.Dispose();
        graph.Dispose();
        sinkPin?.Dispose();
        sinkFilter?.Dispose();
    }
}
