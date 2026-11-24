using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CaptureRack.Core;
using CaptureRack.DirectShow.Windows.FireWire.MsTape;
using CaptureRack.DirectShow.Windows.Interop;
using CaptureRack.QualityControl;
using CaptureRack.Recording.TransportStream;
using Xunit;
using static CaptureRack.DirectShow.Windows.FireWire.MsTape.MsTapeMediaTypes;
using static CaptureRack.DirectShow.Windows.Interop.DirectShowInterop;

namespace CaptureRack.UT;

public sealed unsafe class FireWireTests
{
    [Theory]
    [InlineData(188)]
    [InlineData(192)]
    public void AnalyzerKeepsAlignmentAcrossChunks(int stride)
    {
        var layout = stride == 188 ? TsLayout.Ts188 : TsLayout.Ts192;
        var bytes = MakePackets(layout);
        Assert.Equal(layout, TsAnalyzer.Detect(bytes));
        bytes[stride + layout.Offset] = 0; // Break sync in packet 1 without shifting subsequent packets.
        var analyzer = new TsAnalyzer(layout);
        for (var offset = 0; offset < bytes.Length - 1; offset += 37)
            analyzer.Feed(bytes.AsSpan(offset, Math.Min(37, bytes.Length - 1 - offset)));
        analyzer.Finish();

        Assert.Equal(7, analyzer.Statistics.Packets);
        Assert.Equal(1, analyzer.Statistics.SyncErrors);
        Assert.Equal(1, analyzer.Statistics.FirstSyncErrorPacket);
        Assert.Equal(1, analyzer.Statistics.ContinuityErrors);
        Assert.Equal(2, analyzer.Statistics.FirstContinuityErrorPacket);
        Assert.Equal(stride - 1, analyzer.Statistics.TrailingBytes);
    }

    [Theory]
    [InlineData(188)]
    [InlineData(192)]
    public void LiveGraphRequires192AndDrainsEveryByte(int stride)
    {
        if (!FireWireCapture.IsSupported)
            return;

        var layout = stride == 188 ? TsLayout.Ts188 : TsLayout.Ts192;
        var bytes = MakePackets(layout);
        var directory = Directory.CreateTempSubdirectory("capturerack-firewire-");
        try
        {
            var path = Path.Combine(directory.FullName, layout.Format);
            using var apartment = new ComApartment();
            using var writer = new TsRecording(path, layout);
            uint* format = stackalloc uint[] { (uint)layout.Offset, 188, (uint)layout.Stride };
            var type = new DirectShowMediaType
            {
                Major = StreamType, Subtype = Mpeg2TransportStride,
                FixedSizeSamples = 1, SampleSize = (uint)(stride * 256),
                FormatSize = stride == 192 ? 12u : 0, Format = stride == 192 ? (byte*)format : null
            };
            var timestamps = new List<(TimeSpan? Start, TimeSpan? End)>();
            bool Receive(CaptureSample sample)
            {
                timestamps.Add((sample.StartTime, sample.EndTime));
                return writer.TryReceive(sample);
            }
            void ReportFailure(string message) => writer.Fail(6, message);

            using var sink = new DirectShowSink(&type, Receive, ReportFailure);
            using var source = new TestTsSource(sink);
            using var filter = new ComReference(Export(source, BaseFilter));
            if (stride == 188)
            {
                Assert.Equal(4, Assert.Throws<FireWireException>(() => MsTapeDevices.SelectOutput(filter.Value)).ExitCode);
                return;
            }
            using var selected = MsTapeDevices.SelectOutput(filter.Value);
            Assert.Equal(TsLayout.Ts192, selected.Layout);
            using (var graph = new FireWireGraph())
            {
                graph.Connect(filter.Value, selected, sink);
                try
                {
                    graph.Start();
                    // Keep the graph apartment on this thread while a separate
                    // MTA thread behaves like the DirectShow delivery thread.
                    Exception? deliveryError = null;
                    var delivery = new Thread(() => deliveryError = Record.Exception(() =>
                    {
                        using var callbackApartment = new ComApartment();
                        Assert.Equal(Ok, source.Send(bytes.AsSpan(0, 31), discontinuity: true, start: 0, end: 100));
                        Assert.Equal(Ok, source.Send(bytes.AsSpan(31, 29), discontinuity: false, start: 100));
                        Assert.Equal(Ok, source.Send(bytes.AsSpan(60), discontinuity: false));
                    })) { IsBackground = true };
                    delivery.Start();
                    Assert.True(delivery.Join(TimeSpan.FromSeconds(5)), "Synthetic delivery did not return.");
                    Assert.Null(deliveryError);
                    Assert.False(graph.ReadEvents(ReportFailure));
                    using var input = new ComReference(Export(sink, Pin));
                    Assert.Equal(Ok, NoArgs(input.Value, 15)); // IPin::BeginFlush
                    Assert.Equal(False, source.Send(bytes, discontinuity: false));
                    Assert.Equal(Ok, NoArgs(input.Value, 16)); // IPin::EndFlush
                    Assert.Equal(Ok, NoArgs(input.Value, 14)); // IPin::EndOfStream
                    Assert.True(sink.Ended);
                    Assert.Equal(RejectedEos, source.Send(bytes, discontinuity: false));
                }
                finally { graph.Stop(sink, ReportFailure); }
                Assert.Equal(WrongState, source.Send(bytes, discontinuity: false));
            }
            writer.Complete();
            Assert.True(writer.Finalized, writer.Failure?.Message);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.False(File.Exists(writer.PartialPath));
            Assert.Equal(bytes.Length, writer.ReceivedBytes);
            Assert.Equal(bytes.Length, writer.WrittenBytes);
            Assert.Equal(8, writer.Analysis.Packets);
            Assert.Equal(1, writer.DiscontinuitySamples);
            Assert.True(writer.InitialDiscontinuity);
            Assert.Equal(new (TimeSpan?, TimeSpan?)[]
            {
                (TimeSpan.Zero, TimeSpan.FromTicks(100)),
                (TimeSpan.FromTicks(100), null),
                (null, null)
            }, timestamps);
            Assert.Equal(6, sink.ReceiveCalls);
            Assert.Equal(3, sink.RejectedSamples);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void IncompletePacketRetainsOriginalPartial()
    {
        var directory = Directory.CreateTempSubdirectory("capturerack-firewire-");
        try
        {
            var path = Path.Combine(directory.FullName, "incomplete.ts192");
            using var writer = new TsRecording(path, TsLayout.Ts192);
            var bytes = MakePackets(TsLayout.Ts192)[..^1];
            Assert.True(writer.TryReceive(new CaptureSample(bytes, discontinuity: false)));
            writer.Complete();
            Assert.Equal(4, writer.Failure?.Code);
            Assert.False(File.Exists(path));
            Assert.Equal(bytes, File.ReadAllBytes(writer.PartialPath));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(188)]
    [InlineData(192)]
    public void FileReplayUsesTheSameSampleContract(int stride)
    {
        var layout = stride == 188 ? TsLayout.Ts188 : TsLayout.Ts192;
        var bytes = MakePackets(layout);
        var directory = Directory.CreateTempSubdirectory("capturerack-ts-replay-");
        try
        {
            var inputPath = Path.Combine(directory.FullName, "input.ts");
            var outputPath = Path.Combine(directory.FullName, "output.ts");
            File.WriteAllBytes(inputPath, bytes);
            using var input = File.OpenRead(inputPath);
            using var writer = new TsRecording(outputPath, layout);
            CaptureSampleHandler receive = writer.TryReceive;
            Span<byte> buffer = stackalloc byte[37];
            int read;
            while ((read = input.Read(buffer)) != 0)
                Assert.True(receive(new CaptureSample(buffer[..read], discontinuity: false)));

            writer.Complete();
            Assert.True(writer.Finalized, writer.Failure?.Message);
            Assert.Equal(bytes, File.ReadAllBytes(outputPath));
            Assert.Equal(8, writer.Analysis.Packets);
            Assert.Equal(0, writer.DiscontinuitySamples);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static byte[] MakePackets(TsLayout layout)
    {
        var bytes = new byte[layout.Stride * 8];
        Array.Fill(bytes, (byte)0xff);
        for (var index = 0; index < 8; index++)
        {
            var start = index * layout.Stride;
            var header = start + layout.Offset;
            if (layout.Offset != 0)
                bytes[start] = (byte)index;
            bytes[header] = 0x47;
            bytes[header + 1] = 0x01;
            bytes[header + 2] = 0x00;
            bytes[header + 3] = (byte)(0x10 | index);
        }
        return bytes;
    }
}

[GeneratedComClass]
internal sealed unsafe partial class TestTsSource(DirectShowSink typeSource) : IDirectShowFilter, IDirectShowPin, IDisposable
{
    private int state;
    private nint graph;
    private nint peer;
    private nint allocator;
    private nint receiver;
    private DirectShowMediaType type;

    public int GetClassID(Guid* result) { *result = new Guid("16595445-233a-4133-9941-31376530811a"); return Ok; }
    public int Stop() { state = 0; return Ok; }
    public int Pause() { state = 1; return Ok; }
    public int Run(long start) { state = 2; return Ok; }
    public int GetState(uint timeout, int* result) { *result = state; return Ok; }
    public int SetSyncSource(nint clock) => Ok;
    public int GetSyncSource(nint* clock) { *clock = 0; return Ok; }
    public int EnumPins(nint* pins) { *pins = Export(new TestPinEnumerator(this), DirectShowInterop.EnumPins); return Ok; }
    public int FindPin(char* id, nint* pin)
    {
        *pin = id != null && new string(id) == "Output" ? Export(this, Pin) : 0;
        return *pin != 0 ? Ok : NotFound;
    }
    public int QueryFilterInfo(DirectShowFilterInfo* info)
    {
        *info = default;
        info->Graph = graph;
        if (graph != 0)
            Marshal.AddRef(graph);
        "Synthetic TS source".AsSpan().CopyTo(new Span<char>(info->Name, 128));
        return Ok;
    }
    public int JoinFilterGraph(nint owner, char* name) { graph = owner; return Ok; }
    public int QueryVendorInfo(nint* vendor) { *vendor = 0; return NotImplemented; }

    public int Connect(nint pin, DirectShowMediaType* media)
    {
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint, DirectShowMediaType*, int>)Method(pin, 4))(
            pin, ExportOutput(), media);
        if (hr != Ok) return hr;
        Marshal.AddRef(pin);
        peer = pin;
        type = media->Clone();
        var memInput = MemInputPin;
        hr = Marshal.QueryInterface(pin, in memInput, out receiver);
        if (hr < 0) return hr;
        nint value = 0;
        hr = PointerOut(receiver, 3, &value);
        allocator = value;
        if (hr < 0) return hr;
        int* request = stackalloc int[] { 1, 4096, 1, 0 };
        int* properties = stackalloc int[4];
        hr = ((delegate* unmanaged[Stdcall]<nint, int*, int*, int>)Method(allocator, 3))(
            allocator, request, properties);
        if (hr < 0) return hr;
        hr = ((delegate* unmanaged[Stdcall]<nint, nint, int, int>)Method(receiver, 4))(receiver, allocator, 0);
        return hr < 0 ? hr : NoArgs(allocator, 5);
    }

    private nint exportedPin;
    private nint ExportOutput() => exportedPin != 0 ? exportedPin : exportedPin = Export(this, Pin);

    public int Send(ReadOnlySpan<byte> bytes, bool discontinuity, long? start = null, long? end = null)
    {
        nint sample = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, nint, nint, uint, int>)Method(allocator, 7))(
            allocator, &sample, 0, 0, 0), "Get synthetic media sample");
        using var sampleRef = new ComReference(sample);
        var startTicks = start.GetValueOrDefault();
        var endTicks = end.GetValueOrDefault();
        Check(((delegate* unmanaged[Stdcall]<nint, long*, long*, int>)Method(sample, 6))(
            sample, start.HasValue ? &startTicks : null, end.HasValue ? &endTicks : null), "Set synthetic sample times");
        nint data = 0;
        Check(PointerOut(sample, 3, &data), "Read synthetic sample buffer");
        bytes.CopyTo(new Span<byte>((void*)data, bytes.Length));
        Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Method(sample, 12))(
            sample, bytes.Length), "Set synthetic sample length");
        Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Method(sample, 16))(
            sample, discontinuity ? 1 : 0), "Set synthetic sample discontinuity");
        return ((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(receiver, 6))(receiver, sample);
    }

    public int ReceiveConnection(nint pin, DirectShowMediaType* media) => InvalidDirection;
    public int Disconnect()
    {
        if (allocator != 0) { NoArgs(allocator, 6); Release(ref allocator); }
        Release(ref receiver);
        Release(ref peer);
        fixed (DirectShowMediaType* media = &type) DirectShowMediaType.Clear(media);
        return Ok;
    }
    public int ConnectedTo(nint* pin)
    {
        *pin = peer;
        if (peer == 0) return NotConnected;
        Marshal.AddRef(peer);
        return Ok;
    }
    public int ConnectionMediaType(DirectShowMediaType* media)
    {
        *media = peer == 0 ? default : type.Clone();
        return peer == 0 ? NotConnected : Ok;
    }
    public int QueryPinInfo(DirectShowPinInfo* info)
    {
        *info = default;
        info->Filter = Export(this, BaseFilter);
        info->Direction = 1;
        "Output".AsSpan().CopyTo(new Span<char>(info->Name, 128));
        return Ok;
    }
    public int QueryDirection(int* direction) { *direction = 1; return Ok; }
    public int QueryId(nint* id) { *id = Marshal.StringToCoTaskMemUni("Output"); return Ok; }
    public int QueryAccept(DirectShowMediaType* media) => typeSource.QueryAccept(media);
    public int EnumMediaTypes(nint* enumeration) => typeSource.EnumMediaTypes(enumeration);
    public int QueryInternalConnections(nint* pins, uint* count) => NotImplemented;
    public int EndOfStream() => Unexpected;
    public int BeginFlush() => Unexpected;
    public int EndFlush() => Unexpected;
    public int NewSegment(long start, long stop, double rate) => Unexpected;

    private static void Release(ref nint pointer)
    {
        if (pointer != 0) Marshal.Release(pointer);
        pointer = 0;
    }

    public void Dispose()
    {
        Disconnect();
        Release(ref exportedPin);
    }
}

[GeneratedComClass]
internal sealed unsafe partial class TestPinEnumerator(TestTsSource source) : SingleComItem, IDirectShowEnumPins
{
    protected override nint CreateItem() => Export(source, Pin);
    public int Next(uint count, nint* pins, uint* fetched) => NextItem(count, pins, fetched);
    public int Clone(nint* result)
    {
        *result = Export(new TestPinEnumerator(source) { consumed = consumed }, DirectShowInterop.EnumPins);
        return Ok;
    }
}
