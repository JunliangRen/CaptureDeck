using CaptureRack.Core;
using CaptureRack.QualityControl;
using CaptureRack.SignalCapture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private readonly Views.RfInspectorView rfInspector = new();
    private bool IsRfActive => activeModule.Descriptor.Category == CaptureBackendCategory.SignalCapture;
    private CancellationTokenSource? rfStop;
    private Task? rfTask;
    private bool rfWriterActive;
    private RfMeasurement? latestRfMeasurement;
    private RfMeasurement? displayedRfMeasurement;
    private RfRecording? measuredSource;

    private CancellationTokenSource? rfPreviewStop;
    private Task? rfPreviewTask;
    private RfPreviewSession? rfPreview;
    private bool rfPreviewReady;

    private RfPreviewAudio? rfAudio;
    private RfPreviewType? meterPreviewType;
    private long displayedAudioGeneration = -1;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer rfAudioTimer;

    private void UpdateAudioMeterVisibility(double width)
    {
        bool enabled = IsRfActive ? rfAudio is not null : HasLiveCapture && ActiveWorkspaceCapabilities.SupportsAudioMeters && width >= 680;
        StereoMeter.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfigureRfAudioMeter(bool newSession = false)
    {
        RfPreviewType? type = IsRfActive ? rfInspector.PreviewType : null;
        if (newSession || type != meterPreviewType)
        {
            meterPreviewType = type;
            bool supported = type is RfPreviewType.BetamaxHifi or RfPreviewType.LaserDisc;
            rfAudio = supported ? new RfPreviewAudio() : null;
            displayedAudioGeneration = -1;
            StereoMeter.Reset();
            if (supported) rfAudioTimer.Start();
            else rfAudioTimer.Stop();
        }
        if (rfAudio is null) StereoMeter.SetPreviewTrackStatus(null);
        else RenderRfAudio();
        UpdateAudioMeterVisibility(lastWindowWidth);
    }

    private void RenderRfAudio()
    {
        if (rfAudio is null) return;
        var audio = rfAudio.Read();
        if (audio.Generation != displayedAudioGeneration)
        {
            StereoMeter.Reset();
            displayedAudioGeneration = audio.Generation;
        }
        string? status = audio.Locked ? audio.Track : string.IsNullOrEmpty(audio.Track)
            ? L("RfWaitingAudio") : $"{audio.Track} · {L("RfWaitingAudio")}";
        StereoMeter.SetPreviewTrackStatus(status, waiting: !audio.Locked);
        if (audio.Levels is { } levels) StereoMeter.UpdateLevels(levels, settingsCulture);
        StereoMeter.StepBallistics();
    }

    private void ResetRfMonitor()
    {
        latestRfMeasurement = displayedRfMeasurement = null;
        RfTraceA.Points.Clear();
        RfTraceB.Points.Clear();
        RfMeterA.Reset();
        RfMeterB.Reset();
        RfMeterB.Visibility = rfInspector.Selection?.Rtl is null ? Visibility.Collapsed : Visibility.Visible;
        RfReading.Text = RfFile.Text = "";
        rfInspector.SetTelemetry("");
    }

    private async Task PreviewRfAsync(RfRecording request)
    {
        using var stop = new CancellationTokenSource();
        rfPreviewStop = stop;
        var session = rfPreview = new RfPreviewSession(request);
        var vga = rfInspector.VgaReader();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += (_, _) =>
        {
            RenderRfMeasurement();
            if (!rfPreviewReady && displayedRfMeasurement is not null)
            {
                rfPreviewReady = true;
                UpdateControls();
            }
        };
        try
        {
            ConfigureRfAudioMeter(newSession: true);
            ResetRfMonitor();
            rfInspector.SetRecording(true);
            RenderSourceRackList();
            UpdateControls();
            timer.Start();
            await Task.Run(() => session.Run(vga, m => Volatile.Write(ref latestRfMeasurement, m), stop.Token));
            SetStatus("PreviewStopped", "RfReadyHint");
        }
        catch (Exception ex) { SetStatus("PreviewFailed", () => ex.Message, InfoBarSeverity.Error); }
        finally
        {
            timer.Stop();
            rfPreviewReady = false;
            rfPreview = null;
            ConfigureRfAudioMeter(newSession: true);
            rfPreviewStop = null;
            rfInspector.SetRecording(rfStop is not null);
            RenderSourceRackList();
            UpdateControls();
        }
    }

    private TelemetryMetric[] RfTelemetry()
    {
        var selection = rfInspector.Selection;
        if (measuredSource is { } source && (source.Ddd != selection?.Ddd || source.Rtl != selection?.Rtl ||
            source.HardwareRate != selection?.HardwareRate || source.Rtl is not null && source.Frequency != selection?.Frequency))
        {
            measuredSource = null;
            ResetRfMonitor();
        }
        var m = displayedRfMeasurement;
        RfMeasureLabel.Text = L("RfPeakSpan");
        RfPeakSpan.Text = m is null ? "—" : m.B is null ? $"{m.A.Max - m.A.Min}" : $"{m.A.Max - m.A.Min} / {m.B.Max - m.B.Min}";
        if (RfMeasureFlyout.IsOpen) RenderRfDetails();
        long clips = (m?.A.Clips ?? 0) + (m?.B?.Clips ?? 0);
        return [
            new("rf-rate", L("RfMetricSampleRate"), selection is null ? "—" : $"{selection.Rate / 1e6:G} MSPS"),
            new("rf-clips", L("RfMetricAdcClips"), m is null ? "—" : clips.ToString("N0"),
                clips > 0 ? TelemetrySeverity.Caution : TelemetrySeverity.Normal)
        ];
    }

    private async Task RecordRfAsync(RfRecording request)
    {
        using var stop = new CancellationTokenSource();
        rfStop = stop;
        var preview = rfPreview;
        string prefix = Path.Combine(OutputPath.Text.Trim(), RecordingFileNaming.BuildPrefix(GetRecordingLabel(), DateTime.Now));
        string path = prefix + request.Suffix;
        var vga = rfInspector.VgaReader();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        bool received = false;
        void ReportWriter(RfMeasurement m)
        {
            if (preview is null) Volatile.Write(ref latestRfMeasurement, m);
            Volatile.Write(ref received, true);
        }
        timer.Tick += (_, _) =>
        {
            if (!rfWriterActive && Volatile.Read(ref received))
            {
                rfWriterActive = true;
                RenderSourceRackList();
            }
            if (preview is null) RenderRfMeasurement();
            UpdateRecordingClock();
        };
        try
        {
            rfInspector.SetRecording(true);
            RenderSourceRackList();
            if (preview is null) ResetRfMonitor();
            RenderCounts(null);
            RfFile.Text = path;
            timer.Start();
            displayedRecordingSeconds = -1;
            recordingClock.Restart();
            UpdateRecordingClock();
            UpdateControls();
            UpdateRecordingIndicator(true);
            SetStatus("RecordingActive", () => path);
            var ffmpeg = request.Flac ? QcReports.FindTools().FfmpegPath : null;
            var qc = await Task.Run(() => preview is not null ? preview.Record(prefix, ffmpeg, stop.Token, ReportWriter) : request.Capture(prefix, ffmpeg, vga, ReportWriter, stop.Token));
            if (qc is null)
                SetStatus("RecordingSaved", () => path, InfoBarSeverity.Success);
            else
            {
                rfQcTail = RunRfQcAsync(rfQcTail, qc);
                SetStatus("RfQcQueued", () => path);
            }
        }
        catch (Exception ex)
        {
            RfFile.Text = ex.Message;
            SetStatus("RecordingFailed", () => ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            timer.Stop();
            RenderRfMeasurement();
            recordingClock.Reset();
            UpdateRecordingIndicator(false);
            rfWriterActive = false;
            rfStop = null;
            rfInspector.SetRecording(rfPreview is not null);
            RenderSourceRackList();
            UpdateControls();
        }
    }

    private Task rfQcTail = Task.CompletedTask;

    private async Task RunRfQcAsync(Task previous, RfRampQc qc)
    {
        var entry = new QcSessionHistoryEntry(Guid.NewGuid(), qc.FinalPath + ".partial", Path.GetFileName(qc.FinalPath),
            QcEntryKind.RfRamp, DateTimeOffset.Now, "RfQcQueued", "", TelemetrySeverity.Info, []);
        void Show(string titleKey, TelemetrySeverity severity, string detail, string path)
        {
            entry = entry with { TitleKey = titleKey, Severity = severity, Summary = detail, FilePath = path };
            var selected = QcCenterPage.DisplayedEntry;
            qcHistory.Record(entry);
            latestQcEntry = entry;
            if (qcStop is null && (selected is null || selected.Id == entry.Id))
                QcCenterPage.RenderEntry(entry);
            QcCenterPage.RenderHistory(qcHistory.Entries, QcCenterPage.DisplayedEntry?.Id);
            RenderQcSummaryBadge();
            UpdateQcNavBadge();
        }

        string samples = LocalFormat("RfQcSamples", qc.FileSamples);
        Show("RfQcQueued", TelemetrySeverity.Info, samples, entry.FilePath);
        await previous;
        Show("RfVerifyRamp", TelemetrySeverity.Info, samples, entry.FilePath);
        try
        {
            await Task.Run(qc.VerifyAndPublish);
            Show("QcPassed", TelemetrySeverity.Normal, samples, qc.FinalPath);
        }
        catch (Exception ex)
        {
            Show("QcFailed", TelemetrySeverity.Danger, ex.Message, entry.FilePath);
        }
    }

    private void RfMeasureFlyout_Opened(object sender, object args) => RenderRfDetails();

    private void RenderRfDetails()
    {
        var m = displayedRfMeasurement;
        bool iq = m is null ? rfInspector.Selection?.Rtl is not null : m.B is not null;
        RfMeasureHeading.Text = L("RfMeasureHeading");
        RfChannelA.Text = iq ? "I" : "ADC";
        RfChannelB.Text = iq ? "Q" : "";
        void Column(RfLevels? ch, TextBlock min, TextBlock max, TextBlock pp, TextBlock rms, TextBlock clips, string empty)
        {
            min.Text = ch?.Min.ToString(settingsCulture) ?? empty;
            max.Text = ch?.Max.ToString(settingsCulture) ?? empty;
            pp.Text = ch is null ? empty : (ch.Max - ch.Min).ToString(settingsCulture);
            rms.Text = ch?.Rms.ToString("F1", settingsCulture) ?? empty;
            clips.Text = ch?.Clips.ToString("N0", settingsCulture) ?? empty;
        }
        Column(m?.A, RfMinA, RfMaxA, RfPpA, RfRmsA, RfClipsA, "—");
        Column(m?.B, RfMinB, RfMaxB, RfPpB, RfRmsB, RfClipsB, iq ? "—" : "");
        RfReading.Text = LocalFormat("RfInputSamples", m?.Frames.ToString("N0", settingsCulture) ?? "—",
            (rfInspector.Selection?.HardwareRate / 1e6)?.ToString("G", settingsCulture) ?? "—");
        RfFile.Visibility = string.IsNullOrEmpty(RfFile.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderRfMeasurement()
    {
        var m = Interlocked.Exchange(ref latestRfMeasurement, null);
        if (m is null) return;
        measuredSource = rfInspector.Selection;
        displayedRfMeasurement = m;
        RenderCounts(null);
        PointCollection Trace(RfLevels ch)
        {
            var points = new PointCollection();
            for (int i = 0; i < ch.Trace.Length; i++)
                points.Add(new Point(i, 50 - 49d * ch.Trace[i] / m.FullScale));
            return points;
        }
        RfMeterA.Show(m.B is null ? "ADC" : "I", m.A, m.FullScale);
        if (m.B is not null) RfMeterB.Show("Q", m.B, m.FullScale);
        RfTraceA.Points = Trace(m.A);
        if (m.B is not null) RfTraceB.Points = Trace(m.B);
        var telemetry = new List<string>();
        if (m.Fifo is { } f)
        {
            string limit = f.CoverageLimited || m.FifoDisabled ? "≥" : "";
            telemetry.Add(LocalFormat("RfFifoLevels", f.UsedWords, f.DepthWords, f.RunPeakWords,
                100d * f.RunPeakWords / f.DepthWords, limit, f.NearFullSamples));
        }
        if (m.LegacyMarkerState is { } state) telemetry.Add(L("RfMarker" + state));
        if (m.FifoDisabled) telemetry.Add(L("RfFifoStopped"));
        rfInspector.SetTelemetry(string.Join(Environment.NewLine, telemetry));
    }
}
