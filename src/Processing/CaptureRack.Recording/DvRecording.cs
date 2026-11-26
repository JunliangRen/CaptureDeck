using CaptureRack.QualityControl;

namespace CaptureRack.Recording;

internal sealed class DvRecording : RawSampleRecording
{
    private readonly DvStreamAnalyzer analyzer;

    public DvRecording(string path, DvEncoding? encoding = null) : this(path, new DvStreamAnalyzer(encoding)) { }

    private DvRecording(string path, DvStreamAnalyzer analyzer) : base(path, analyzer.Feed, () => { }, () => Validate(analyzer.Snapshot)) =>
        this.analyzer = analyzer;

    public DvStatistics Analysis => analyzer.Snapshot;

    private static string? Validate(DvStatistics analysis) => analysis.Frames == 0 || analysis.TrailingBytes != 0
        ? "Capture ended with an incomplete DIF frame or no valid DV frames." : null;
}
