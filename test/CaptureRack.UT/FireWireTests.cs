using CaptureRack.Core;
using CaptureRack.DirectShow.Windows.FireWire.MsTape;
using CaptureRack.DirectShow.Windows.Interop;
using CaptureRack.QualityControl;
using CaptureRack.Recording;
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

    [Fact]
    public void TruncatedDvRetainsTheExactPartialBytes()
    {
        var directory = Directory.CreateTempSubdirectory("capturerack-dv-partial-");
        try
        {
            var path = Path.Combine(directory.FullName, "incomplete.dv");
            var bytes = new byte[640];
            bytes[0] = 0x1f;
            bytes[1] = 0x07;
            using var writer = new DvRecording(path);
            Assert.True(writer.TryReceive(new CaptureSample(bytes.AsSpan(0, 3), false)));
            Assert.True(writer.TryReceive(new CaptureSample(bytes.AsSpan(3), false)));
            writer.Complete();
            Assert.Equal(4, writer.Failure?.Code);
            Assert.False(File.Exists(path));
            Assert.Equal(bytes, File.ReadAllBytes(writer.PartialPath));
            Assert.Equal(bytes.Length, writer.Analysis.TrailingBytes);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void DvGraphAcceptsMetadataChangesWithinTheNegotiatedSubtype()
    {
        if (!FireWireCapture.IsSupported)
            return;

        using var apartment = new ComApartment();
        uint* info = stackalloc uint[8];
        new Span<uint>(info, 8).Clear();
        var type = new DirectShowMediaType
        {
            Major = Interleaved, Subtype = Dv50, FormatType = DvInfo,
            Format = (byte*)info, FormatSize = 32
        };
        using var descriptor = new DirectShowSink(&type, _ => true, _ => { });
        using var replay = new FireWireReplayFilter(descriptor);
        using var source = new ComReference(Export(replay, BaseFilter));
        using var selected = MsTapeDevices.SelectOutput(source.Value, dv: true);
        List<byte> received = [];
        List<string> failures = [];
        using var sink = FireWireGraph.CreateSink(selected, sample =>
        {
            received.AddRange(sample.Bytes.ToArray());
            return true;
        }, failures.Add);
        using var graph = new FireWireGraph();
        graph.Connect(source.Value, selected, sink);
        graph.Start();
        try
        {
            byte[] bytes = [1, 2, 3, 4];
            info[0] = 1; // Simulate an updated DVINFO pack.
            Assert.Equal(Ok, replay.Send(bytes, false, media: &type));
            Assert.Equal(bytes, received);
            Assert.Empty(failures);

            type.Subtype = Dvh1;
            Assert.Equal(TypeNotAccepted, replay.Send(bytes, false, media: &type));
            Assert.Single(failures);
            Assert.Equal(bytes, received);
        }
        finally { graph.Stop(sink, failures.Add); }
    }

    [Theory]
    [InlineData(188)]
    [InlineData(192)]
    public void LiveGraphNegotiatesBothTsLayoutsAndDrainsEveryByte(int stride)
    {
        if (!FireWireCapture.IsSupported)
            return;

        var layout = stride == 188 ? TsLayout.Ts188 : TsLayout.Ts192;
        var bytes = MakePackets(layout);
        var directory = Directory.CreateTempSubdirectory("capturerack-firewire-");
        try
        {
            var path = Path.Combine(directory.FullName, $"{layout.Format}_sample.ts");
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
            using var source = new FireWireReplayFilter(sink);
            using var filter = new ComReference(Export(source, BaseFilter));
            using var selected = MsTapeDevices.SelectOutput(filter.Value);
            Assert.Equal(layout, selected.Layout);
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
            Parallel.Invoke(writer.Complete, writer.Complete);
            Assert.True(writer.Finalized, writer.Failure?.Message);
            Assert.Null(writer.Failure);
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
