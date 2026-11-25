using static CaptureRack.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureRack.DirectShow.Windows.Interop;

internal sealed unsafe class DirectShowGraph : IDisposable
{
    private static readonly Guid FilterGraph = new("E436EBB3-524F-11CE-9F53-0020AF0BA770");
    private static readonly Guid GraphBuilder = new("56A868A9-0AD4-11CE-B03A-0020AF0BA770");
    private static readonly Guid MediaControl = new("56A868B1-0AD4-11CE-B03A-0020AF0BA770");
    private static readonly Guid SystemClock = new("E436EBB1-524F-11CE-9F53-0020AF0BA770");
    private static readonly Guid ReferenceClock = new("56A86897-0AD4-11CE-B03A-0020AF0BA770");
    private static readonly Guid MediaEvent = new("56A868B6-0AD4-11CE-B03A-0020AF0BA770");
    private readonly ComReference graph = Create(FilterGraph, GraphBuilder, "Create DirectShow filter graph");
    private readonly List<(nint SourcePin, ComReference SinkFilter, ComReference SinkPin)> connections = [];
    private ComReference? control;
    private ComReference? events;
    private nint sourceFilter;

    internal void AddSource(nint source, string name)
    {
        AddFilter(source, name);
        sourceFilter = source;
    }

    internal int Connect(nint sourcePin, DirectShowMediaType* mediaType, DirectShowSink sink, string name)
    {
        using var filter = new ComReference(Export(sink, BaseFilter));
        using var pin = new ComReference(Export(sink, Pin));
        AddFilter(filter.Value, name);
        connections.Add((sourcePin, new ComReference(filter.Detach()), new ComReference(pin.Detach())));
        return ((delegate* unmanaged[Stdcall]<nint, nint, nint, DirectShowMediaType*, int>)Method(graph.Value, 7))(
            graph.Value, sourcePin, connections[^1].SinkPin.Value, mediaType); // IFilterGraph::ConnectDirect
    }

    private void AddFilter(nint filter, string name)
    {
        fixed (char* text = name)
            Check(((delegate* unmanaged[Stdcall]<nint, nint, char*, int>)Method(graph.Value, 3))(
                graph.Value, filter, text), "Add filter to DirectShow graph");
    }

    internal void Start(bool useSystemClock = false, Action<string>? trace = null)
    {
        control = Query(graph.Value, MediaControl, "Get IMediaControl");
        events = Query(graph.Value, MediaEvent, "Get IMediaEvent");
        if (useSystemClock)
        {
            trace?.Invoke("Set the DirectShow System Reference Clock.");
            // This diagnostic graph has no renderer and should not query a live source for its clock.
            using var clock = Create(SystemClock, ReferenceClock, "Create DirectShow System Reference Clock");
            using var mediaFilter = Query(graph.Value, typeof(IDirectShowMediaFilter).GUID, "Get IMediaFilter");
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(mediaFilter.Value, 8))(
                mediaFilter.Value, clock.Value), "Set DirectShow System Reference Clock");
        }
        else
        {
            Check(NoArgs(graph.Value, 10), "Set default DirectShow graph clock");
        }
        trace?.Invoke("Run the DirectShow graph.");
        Check(NoArgs(control.Value, 7), "Run DirectShow graph");
    }

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

    internal void Stop(Action<string> reportFailure)
    {
        if (control is not null)
        {
            var hr = NoArgs(control.Value, 9);
            if (hr < 0)
                reportFailure($"Stop DirectShow graph failed (0x{hr:X8}).");
        }
    }

    public void Dispose()
    {
        var disconnect = (delegate* unmanaged[Stdcall]<nint, nint, int>)Method(graph.Value, 9);
        var remove = (delegate* unmanaged[Stdcall]<nint, nint, int>)Method(graph.Value, 4);
        foreach (var (source, filter, pin) in connections)
        {
            disconnect(graph.Value, source);
            disconnect(graph.Value, pin.Value);
            remove(graph.Value, filter.Value);
            pin.Dispose();
            filter.Dispose();
        }
        if (sourceFilter != 0)
            remove(graph.Value, sourceFilter);
        events?.Dispose();
        control?.Dispose();
        graph.Dispose();
    }
}
