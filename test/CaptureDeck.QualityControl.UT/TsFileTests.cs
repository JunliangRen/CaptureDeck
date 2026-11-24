using System.Text.Json;
using CaptureDeck.QualityControl;
using Xunit;

namespace CaptureDeck.QualityControl.UT;

public sealed class TsFileTests
{
    [Theory]
    [InlineData(188, ".ts")]
    [InlineData(192, ".ts192")]
    [InlineData(188, ".m2t")]
    public void DetectsLayoutAndExportsReport(int stride, string extension)
    {
        var packets = MakePackets(stride);
        var directory = Directory.CreateTempSubdirectory("capturedeck-ts-");
        try
        {
            var path = Path.Combine(directory.FullName, "sample" + extension);
            File.WriteAllBytes(path, packets);
            var report = TsFile.Analyze(path);
            Assert.Equal($"ts{stride}", report.Format);
            Assert.True(report.Success);
            Assert.Equal(8192, report.Analysis.Packets);
            Assert.Equal(packets.Length, report.Analysis.InputBytes);

            using var json = JsonDocument.Parse(report.ToJson(indented: true));
            var root = json.RootElement;
            Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
            Assert.Equal("analysis_result", root.GetProperty("type").GetString());
            Assert.Equal(0, root.GetProperty("exit_code").GetInt32());
            Assert.Equal(path, root.GetProperty("input_path").GetString());
            var analysis = root.GetProperty("analysis");
            Assert.Equal(0, analysis.GetProperty("continuity_errors").GetInt64());
            Assert.Equal(256, analysis.GetProperty("pids")[0].GetProperty("pid").GetInt32());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(188)]
    [InlineData(192)]
    public void ReportsTruncationAndFirstAffectedPacket(int stride)
    {
        var packets = MakePackets(stride);
        var offset = stride == 192 ? 4 : 0;
        packets[70 * stride + offset] = 0;
        var directory = Directory.CreateTempSubdirectory("capturedeck-ts-");
        try
        {
            var path = Path.Combine(directory.FullName, "truncated.ts");
            File.WriteAllBytes(path, packets[..^1]);
            var report = TsFile.Analyze(path);
            Assert.False(report.Success);
            Assert.Equal(4, report.ExitCode);
            Assert.Equal(1, report.Analysis.SyncErrors);
            Assert.Equal(70, report.Analysis.FirstSyncErrorPacket);
            Assert.Equal(71, report.Analysis.FirstContinuityErrorPacket);
            Assert.Equal(stride - 1, report.Analysis.TrailingBytes);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void CancelledAnalysisDoesNotOpenInput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            TsFile.Analyze("missing.ts", cancellation: cancellation.Token));
    }

    private static byte[] MakePackets(int stride)
    {
        var bytes = new byte[stride * 8192];
        var offset = stride == 192 ? 4 : 0;
        for (var index = 0; index < 8192; index++)
        {
            var start = index * stride;
            bytes.AsSpan(start, stride).Fill(0xff);
            if (offset != 0)
                bytes[start] = (byte)(index % 256);
            var header = start + offset;
            bytes[header] = 0x47;
            bytes[header + 1] = 0x01;
            bytes[header + 2] = 0x00;
            bytes[header + 3] = (byte)(0x10 | (index % 16));
        }
        return bytes;
    }
}
