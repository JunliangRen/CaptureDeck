using System.Runtime.InteropServices.WindowsRuntime;
using CaptureRack.DeckLink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private IReadOnlyList<CaptureEvent>? visibleEvents;
    private VideoScopes? visibleScopes;
    private StereoLevels? visibleAudio;
    private readonly SolidColorBrush spectrumGridBrush = new(Windows.UI.Color.FromArgb(255, 57, 60, 66));
    private readonly SolidColorBrush spectrumAxisBrush = new(Windows.UI.Color.FromArgb(255, 204, 211, 220));

    private void ResetMonitoring()
    {
        visibleEvents = null;
        visibleAudio = null;
        visibleScopes = null;
        EventList.ItemsSource = null;
        EventsSummary.Text = L("EventsEmpty");
        ScopeStatus.Text = L("ScopeWaiting");
        WaveformPlot.Source = null;
        LumaScaleTop.Text = LumaScaleHigh.Text = LumaScaleMid.Text = LumaScaleLow.Text = "—";
        LumaScaleDescription.Text = L("LumaWaiting");
        AutomationProperties.SetName(WaveformPlot, L("LumaHeading"));
        VectorPlot.Source = null;
        SpectrumTrace.Points.Clear();
        PaintSpectrumScale(null);
        SpectrumTitle.Text = L("SpectrumTitle");
        SpectrumMidFrequency.Text = SpectrumMaxFrequency.Text = "—";
        LeftMeterFill.ScaleY = RightMeterFill.ScaleY = 0;
        LeftLevel.Text = RightLevel.Text = "—";
        AutomationProperties.SetName(LeftLevel, LocalFormat("PeakUnavailable", L("LeftChannel")));
        AutomationProperties.SetName(RightLevel, LocalFormat("PeakUnavailable", L("RightChannel")));
        ClipPanel.Visibility = LeftClip.Visibility = RightClip.Visibility = Visibility.Collapsed;
        PhaseLevel.Text = LocalFormat("Phase", "—");
    }

    private void ShowMonitoring(CaptureStats stats)
    {
        if (!ReferenceEquals(visibleEvents, stats.Events))
        {
            visibleEvents = stats.Events;
            RenderEvents();
        }

        if (stats.AudioLevels is not { } audio || ReferenceEquals(visibleAudio, audio))
            return;

        var spectrumScaleChanged = visibleAudio?.SpectrumFloorDbfs != audio.SpectrumFloorDbfs;
        visibleAudio = audio;
        RenderStereoLevels(audio);
        if (spectrumScaleChanged)
            PaintSpectrumScale(audio.SpectrumFloorDbfs);
    }

    private void RenderEvents()
    {
        EventsSummary.Text = L(visibleEvents is { Count: > 0 } ? "EventsRecent" : "EventsEmpty");
        EventList.ItemsSource = visibleEvents?.Select(item => new EventRow(item.TimeLabel, item.Kind switch
        {
            CaptureEventKind.TimestampRegression => L("EventTimestampRegression"),
            CaptureEventKind.FrameGap => LocalFormat("EventFrameGap", item.MissingFrames),
            CaptureEventKind.NoInputAtStart => L("EventNoInputAtStart"),
            CaptureEventKind.SignalLost => L("EventSignalLost"),
            CaptureEventKind.SignalRestored => L("EventSignalRestored"),
            _ => item.Description
        })).ToArray();
    }

    private void RenderStereoLevels(StereoLevels audio)
    {
        SpectrumTitle.Text = LocalFormat("SpectrumInput", audio.SampleBits, audio.SampleRate / 1000d);
        SpectrumMidFrequency.Text = (audio.SampleRate / 4000d).ToString("0.##", settingsCulture) + " kHz";
        SpectrumMaxFrequency.Text = (audio.SampleRate / 2000d).ToString("0.##", settingsCulture) + " kHz";
        ShowLevel("LeftChannel", audio.LeftPeak, audio.LeftClips, LeftMeterFill, LeftLevel, LeftClip);
        ShowLevel("RightChannel", audio.RightPeak, audio.RightClips, RightMeterFill, RightLevel, RightClip);
        ClipPanel.Visibility = audio.LeftClips > 0 || audio.RightClips > 0
            ? Visibility.Visible : Visibility.Collapsed;
        PhaseLevel.Text = LocalFormat("Phase", audio.PhaseCorrelation?.ToString("+0.00;-0.00;0.00", settingsCulture) ?? "—");

        if (audio.Spectrum is { } spectrum)
            PaintSpectrum(spectrum, audio.SpectrumFloorDbfs);
    }

    private void RefreshLocalizedMonitoring()
    {
        RenderEvents();
        if (visibleScopes is { } scopes)
            RenderScopeLabels(scopes);
        else
        {
            ScopeStatus.Text = L("ScopeWaiting");
            LumaScaleDescription.Text = L("LumaWaiting");
            AutomationProperties.SetName(WaveformPlot, L("LumaHeading"));
        }
        if (visibleAudio is { } audio)
        {
            RenderStereoLevels(audio);
            PaintSpectrumScale(audio.SpectrumFloorDbfs);
        }
        else
        {
            SpectrumTitle.Text = L("SpectrumTitle");
            PhaseLevel.Text = LocalFormat("Phase", "—");
            AutomationProperties.SetName(LeftLevel, LocalFormat("PeakUnavailable", L("LeftChannel")));
            AutomationProperties.SetName(RightLevel, LocalFormat("PeakUnavailable", L("RightChannel")));
        }
    }

    private sealed record EventRow(string TimeLabel, string Description);

    private void ShowVideoScopes()
    {
        if (preview?.Scopes is not { } scopes || ReferenceEquals(visibleScopes, scopes))
            return;

        if (visibleScopes?.LumaScaleBits != scopes.LumaScaleBits ||
            visibleScopes?.InputBits != scopes.InputBits || visibleScopes?.NoInput != scopes.NoInput)
            RenderScopeLabels(scopes);

        visibleScopes = scopes;
        PaintPlot(WaveformPlot, scopes.LumaWaveform, VideoScopes.WaveformWidth, VideoScopes.WaveformHeight);
        PaintPlot(VectorPlot, scopes.Vectorscope, VideoScopes.VectorSize, VideoScopes.VectorSize);
    }

    private void RenderScopeLabels(VideoScopes scopes)
    {
        var maximum = (1 << scopes.LumaScaleBits) - 1;
        var quarter = (maximum + 1) / 4;
        LumaScaleTop.Text = maximum.ToString(settingsCulture);
        LumaScaleHigh.Text = (quarter * 3).ToString(settingsCulture);
        LumaScaleMid.Text = (quarter * 2).ToString(settingsCulture);
        LumaScaleLow.Text = quarter.ToString(settingsCulture);
        LumaScaleDescription.Text = scopes.LumaScaleBits == scopes.InputBits
            ? LocalFormat("LumaNative", maximum, scopes.InputBits)
            : LocalFormat("LumaScaled", maximum, scopes.LumaScaleBits, scopes.InputBits);
        AutomationProperties.SetName(WaveformPlot, LocalFormat("LumaPlotRange", maximum));
        ScopeStatus.Text = L(scopes.NoInput ? "ScopeNoInput" : "ScopeLive");
    }

    private void ShowLevel(string channel, double peak, long clips, ScaleTransform meter, TextBlock label, TextBlock clip)
    {
        var db = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        meter.ScaleY = Math.Clamp((60 + db) / 60, 0, 1);
        label.Text = peak > 0 ? db.ToString("0.0", settingsCulture) : "−∞";
        AutomationProperties.SetName(label, LocalFormat("PeakValue", L(channel), label.Text));
        clip.Visibility = clips > 0 ? Visibility.Visible : Visibility.Collapsed;
        clip.Text = LocalFormat("ClipCount", L(channel), clips);
        ToolTipService.SetToolTip(clip, clip.Text);
    }

    private void SpectrumPlot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PaintSpectrumScale(visibleAudio?.SpectrumFloorDbfs);
        if (visibleAudio is { Spectrum: { } spectrum } audio)
            PaintSpectrum(spectrum, audio.SpectrumFloorDbfs);
    }

    private void PaintSpectrumScale(double? floorDbfs)
    {
        SpectrumLevelAxis.Children.Clear();
        SpectrumGridLines.Children.Clear();
        var width = SpectrumPlot.ActualWidth - 2;
        var height = SpectrumPlot.ActualHeight - 2;
        if (floorDbfs is not < 0 || width <= 0 || height <= 0)
            return;

        // Use round intermediate ticks and preserve the actual one-LSB floor.
        var floor = floorDbfs.Value;
        var step = Math.Max(1, Math.Round(-floor / 30) * 10);
        var levels = new[] { 0, -step, -2 * step, floor };
        var labels = levels.Select(level => new TextBlock
        {
            Text = level.ToString("0.#", settingsCulture),
            FontSize = 12,
            Foreground = spectrumAxisBrush,
            TextAlignment = TextAlignment.Right
        }).ToArray();
        foreach (var label in labels)
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var labelWidth = Math.Ceiling(labels.Max(label => label.DesiredSize.Width));
        // Include the axis canvas margins and enough space for the dBFS unit underneath.
        SpectrumLevelColumn.Width = new GridLength(Math.Max(32, labelWidth + 10));

        for (var i = 0; i < levels.Length; i++)
        {
            var y = SpectrumLevelY(levels[i], floor, height);
            SpectrumGridLines.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 1, X2 = 1 + width, Y1 = y, Y2 = y, Stroke = spectrumGridBrush
            });

            var label = labels[i];
            label.Width = SpectrumLevelColumn.Width.Value - 10;
            Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
            SpectrumLevelAxis.Children.Add(label);
        }
    }

    private static double SpectrumLevelY(double dbfs, double floorDbfs, double height) =>
        1 + Math.Clamp(dbfs / floorDbfs, 0, 1) * height;

    private void PaintSpectrum(IReadOnlyList<float> spectrum, double floorDbfs)
    {
        var width = SpectrumPlot.ActualWidth - 2;
        var height = SpectrumPlot.ActualHeight - 2;
        if (width <= 0 || height <= 0)
            return;

        var points = new PointCollection();
        for (var bin = 0; bin < spectrum.Count; bin++)
        {
            var x = 1 + bin * width / (spectrum.Count - 1);
            var y = SpectrumLevelY(spectrum[bin], floorDbfs, height);
            points.Add(new Point(x, y));
        }
        SpectrumTrace.Points = points;
    }

    private static void PaintPlot(Image target, byte[] pixels, int width, int height)
    {
        if (target.Source is not WriteableBitmap bitmap || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
        {
            bitmap = new WriteableBitmap(width, height);
            target.Source = bitmap;
        }
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels);
        bitmap.Invalidate();
    }
}
