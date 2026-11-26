using CaptureRack.DeckLink;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? vancScanStop;
    private Task<VancScanResult>? vancScanTask;

    private async Task ScanVancAsync()
    {
        if (vancScanStop is { } running)
        {
            running.Cancel();
            deckLinkInspector.ShowVancScanStatus(L("VancScanCancelling"), scanning: true);
            return;
        }

        if (!IsDeckLinkActive || closing || loading || pickingOutput || captureTask is not null ||
            qcStop is not null || recordingSession || recordingTransition || restartPreviewRequested ||
            deckLinkInspector.SelectedDevice is not { } device ||
            deckLinkInspector.SelectedVideoSource is not { } video ||
            deckLinkInspector.SelectedAudioSource is not { } audio ||
            deckLinkInspector.SelectedMode is not { } mode ||
            !DeckLinkCapture.IsVancScanInput(video.Id))
            return;

        using var stop = new CancellationTokenSource();
        vancScanStop = stop;
        var routing = new CaptureRouting(video.Id, audio.Id);
        var analogOptions = deckLinkInspector.BuildAnalogInputOptions();
        var progress = new Progress<VancScanProgress>(update =>
        {
            if (vancScanStop != stop || stop.IsCancellationRequested || closing)
                return;
            var message = update.TotalLines == 0
                ? L("VancScanReading")
                : LocalFormat("VancScanMapping", update.LinesScanned, update.TotalLines);
            deckLinkInspector.ShowVancScanStatus(message, scanning: true);
        });
        var task = Task.Run(() => DeckLinkCapture.ScanVanc(device, routing, mode.Id, analogOptions, progress, stop.Token));
        vancScanTask = task;
        deckLinkInspector.ShowVancScanStatus(L("VancScanReading"), scanning: true);
        UpdateControls();

        try
        {
            var result = await task;
            stop.Token.ThrowIfCancellationRequested();
            if (!closing)
            {
                var status = FormatVancScanResult(result);
                if (device.SupportsVancInputMapping && deckLinkInspector.VancInput.WithDetectedLines(result.Lines) is { } filled)
                {
                    deckLinkInspector.FillVancInputLines(filled);
                    status += "\n" + LocalFormat("VancScanFilled", filled.Line1, filled.Line2, filled.Line3);
                }
                var severity = result.Readable && result.Lines.Count > 0
                    ? TelemetrySeverity.Info
                    : TelemetrySeverity.Caution;
                deckLinkInspector.ShowVancScanStatus(status, scanning: false, severity);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (!closing)
                deckLinkInspector.ShowVancScanStatus(L("VancScanCancelled"), scanning: false, TelemetrySeverity.Caution);
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            if (!closing)
                deckLinkInspector.ShowVancScanStatus(LocalFormat("VancScanFailed", ex.Message), scanning: false, TelemetrySeverity.Danger);
        }
        finally
        {
            vancScanStop = null;
            vancScanTask = null;
            if (!closing)
                UpdateControls();
        }
    }

    private string FormatVancScanResult(VancScanResult result)
    {
        var method = L(result.Method == VancScanMethod.Direct ? "VancScanDirectResult" : "VancScanMappedResult");
        if (!result.Readable)
            return LocalFormat("VancScanUnreadable", result.Mode.Name);
        if (result.Lines.Count == 0)
            return LocalFormat("VancScanNoLines", result.Mode.Name, method);

        var rows = result.Lines.GroupBy(line => line.Kind).OrderByDescending(group => group.Key)
            .Select(group => LocalFormat(group.Key switch
            {
                VancLineKind.AncPacket => "VancScanAncLines",
                VancLineKind.Caption608 => "VancScanCaptionLines",
                _ => "VancScanWaveformLines"
            }, string.Join(", ", group.Select(line => line.Line))));
        return LocalFormat("VancScanFound", result.Mode.Name, method) + "\n" + string.Join("\n", rows);
    }
}
