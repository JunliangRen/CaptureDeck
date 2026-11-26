using CaptureRack.QualityControl;

namespace CaptureRack.Recording.TransportStream;

internal sealed class TsRecording : RawSampleRecording
{
    private readonly TsAnalyzer analyzer;

    public TsRecording(string path, TsLayout layout) : this(path, new TsAnalyzer(layout)) { }

    private TsRecording(string path, TsAnalyzer analyzer) : base(path, (bytes, _) => analyzer.Feed(bytes),
        analyzer.Finish, () => Validate(analyzer.Statistics)) => this.analyzer = analyzer;

    public TsStatistics Analysis => analyzer.Statistics;

    private static string? Validate(TsStatistics analysis) =>
        analysis.TrailingBytes != 0 || analysis.SyncErrors + analysis.MalformedPackets == analysis.Packets
            ? "Capture ended with an incomplete packet or no structurally valid TS packets." : null;
}
