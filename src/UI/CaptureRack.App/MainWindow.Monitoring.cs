using System.Runtime.InteropServices.WindowsRuntime;
using CaptureRack.DeckLink;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private enum EventFilterMode { All, Drops, Signal }

    private IReadOnlyList<CaptureEvent>? visibleEvents;
    private VideoScopes? visibleScopes;
    private StereoLevels? visibleAudio;
    private EventFilterMode eventFilter = EventFilterMode.All;

    private readonly SolidColorBrush spectrumGridBrush = new(Windows.UI.Color.FromArgb(255, 42, 49, 61));
    private readonly SolidColorBrush spectrumAxisBrush = new(Windows.UI.Color.FromArgb(255, 155, 165, 184));
    private readonly SolidColorBrush eventDangerBrush = new(Windows.UI.Color.FromArgb(255, 239, 68, 68));
    private readonly SolidColorBrush eventCautionBrush = new(Windows.UI.Color.FromArgb(255, 245, 158, 11));
    private readonly SolidColorBrush eventLockBrush = new(Windows.UI.Color.FromArgb(255, 34, 197, 94));
    private readonly SolidColorBrush eventInfoBrush = new(Windows.UI.Color.FromArgb(255, 56, 189, 248));

    // Broadcast meter ballistics state (60Hz smooth decay & peak hold)
    private double leftTargetDb = -60;
    private double rightTargetDb = -60;
    private double leftDisplayedDb = -60;
    private double rightDisplayedDb = -60;
    private double leftPeakHoldDb = -60;
    private double rightPeakHoldDb = -60;
    private int leftPeakHoldFrames;
    private int rightPeakHoldFrames;
    private double meterRailHeight;
    private double? displayedPhase;
    private float[]? spectrumPeakHoldValues;

    private void ResetMonitoring()
    {
        visibleEvents = null;
        visibleAudio = null;
        visibleScopes = null;
        displayedPhase = null;
        spectrumPeakHoldValues = null;
        leftTargetDb = rightTargetDb = -60;
        leftDisplayedDb = rightDisplayedDb = -60;
        leftPeakHoldDb = rightPeakHoldDb = -60;
        leftPeakHoldFrames = rightPeakHoldFrames = 0;

        EventList.ItemsSource = null;
        EventList.Visibility = Visibility.Collapsed;
        EventsEmptyState.Visibility = Visibility.Visible;
        EventsSummary.Text = L("EventsEmpty");
        ScopeStatus.Text = L("ScopeWaiting");
        WaveformPlot.Source = null;
        LumaScaleTop.Text = LumaScaleHigh.Text = LumaScaleMid.Text = LumaScaleLow.Text = "—";
        LumaScaleDescription.Text = L("LumaWaiting");
        AutomationProperties.SetName(WaveformPlot, L("LumaHeading"));
        VectorPlot.Source = null;
        SpectrumTrace.Points.Clear();
        SpectrumFill.Points.Clear();
        SpectrumPeakHold.Points.Clear();
        PaintSpectrumScale(null);
        SpectrumMidFrequency.Text = SpectrumMaxFrequency.Text = "—";
        LeftMeterFill.ScaleY = RightMeterFill.ScaleY = 0;
        LeftPeakHoldBar.Opacity = RightPeakHoldBar.Opacity = 0;
        LeftLevel.Text = RightLevel.Text = "—";
        AutomationProperties.SetName(LeftLevel, LocalFormat("PeakUnavailable", L("LeftChannel")));
        AutomationProperties.SetName(RightLevel, LocalFormat("PeakUnavailable", L("RightChannel")));
        UpdateClipBadge(LeftClipBadge, LeftChannel, "L", 0);
        UpdateClipBadge(RightClipBadge, RightChannel, "R", 0);
        PhaseLevel.Text = "—";
        UpdatePhaseBar(null);
        UpdateSecondaryInstrumentHeader(GetDisplayedSecondaryInstrument());
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

    private void EventsFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<EventFilterMode>(tag, out var filter))
        {
            eventFilter = filter;
            UpdateEventFilterButtons();
            RenderEvents();
        }
    }

    private void UpdateEventFilterButtons()
    {
        ApplySegmentState(EventsFilterAllBtn, eventFilter == EventFilterMode.All);
        ApplySegmentState(EventsFilterDropsBtn, eventFilter == EventFilterMode.Drops);
        ApplySegmentState(EventsFilterSignalBtn, eventFilter == EventFilterMode.Signal);
    }

    private void CopyEvents_Click(object sender, RoutedEventArgs e)
    {
        if (visibleEvents is not { Count: > 0 })
            return;

        var lines = visibleEvents.Select(item => $"{item.TimeLabel}\t{LocalEventDescription(item)}");
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, lines));
        Clipboard.SetContent(package);
    }

    private string LocalEventDescription(CaptureEvent item) => item.Kind switch
    {
        CaptureEventKind.TimestampRegression => L("EventTimestampRegression"),
        CaptureEventKind.FrameGap => LocalFormat("EventFrameGap", item.MissingFrames),
        CaptureEventKind.NoInputAtStart => L("EventNoInputAtStart"),
        CaptureEventKind.SignalLost => L("EventSignalLost"),
        CaptureEventKind.SignalRestored => L("EventSignalRestored"),
        _ => item.Description
    };

    private Brush EventAccentBrush(CaptureEventKind kind) => kind switch
    {
        CaptureEventKind.TimestampRegression or CaptureEventKind.FrameGap => eventDangerBrush,
        CaptureEventKind.NoInputAtStart or CaptureEventKind.SignalLost => eventCautionBrush,
        CaptureEventKind.SignalRestored => eventLockBrush,
        _ => eventInfoBrush
    };

    private void RenderEvents()
    {
        UpdateEventFilterButtons();
        var filtered = visibleEvents?.Where(item => eventFilter switch
        {
            EventFilterMode.Drops => item.Kind is CaptureEventKind.FrameGap or CaptureEventKind.TimestampRegression,
            EventFilterMode.Signal => item.Kind is CaptureEventKind.NoInputAtStart or CaptureEventKind.SignalLost or CaptureEventKind.SignalRestored,
            _ => true
        }).Select(item => new EventRow(item.TimeLabel, LocalEventDescription(item), EventAccentBrush(item.Kind))).ToArray();

        var hasRows = filtered is { Length: > 0 };
        EventsSummary.Text = L(visibleEvents is { Count: > 0 } ? "EventsRecent" : "EventsEmpty");
        EventsEmptyState.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        EventList.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        EventList.ItemsSource = filtered;
        if (GetDisplayedSecondaryInstrument() == SplitSecondaryInstrument.Events)
            UpdateSecondaryInstrumentHeader(SplitSecondaryInstrument.Events);
    }

    private void RenderStereoLevels(StereoLevels audio)
    {
        SpectrumMidFrequency.Text = (audio.SampleRate / 4000d).ToString("0.##", settingsCulture) + " kHz";
        SpectrumMaxFrequency.Text = (audio.SampleRate / 2000d).ToString("0.##", settingsCulture) + " kHz";

        leftTargetDb = audio.LeftPeak > 0 ? Math.Clamp(20 * Math.Log10(audio.LeftPeak), -60, 0) : -60;
        rightTargetDb = audio.RightPeak > 0 ? Math.Clamp(20 * Math.Log10(audio.RightPeak), -60, 0) : -60;

        LeftLevel.Text = audio.LeftPeak > 0 ? (20 * Math.Log10(audio.LeftPeak)).ToString("0.0", settingsCulture) : "−∞";
        RightLevel.Text = audio.RightPeak > 0 ? (20 * Math.Log10(audio.RightPeak)).ToString("0.0", settingsCulture) : "−∞";
        AutomationProperties.SetName(LeftLevel, LocalFormat("PeakValue", L("LeftChannel"), LeftLevel.Text));
        AutomationProperties.SetName(RightLevel, LocalFormat("PeakValue", L("RightChannel"), RightLevel.Text));

        UpdateClipBadge(LeftClipBadge, LeftChannel, "L", audio.LeftClips);
        UpdateClipBadge(RightClipBadge, RightChannel, "R", audio.RightClips);

        displayedPhase = audio.PhaseCorrelation;
        PhaseLevel.Text = audio.PhaseCorrelation?.ToString("+0.00;-0.00;0.00", settingsCulture) ?? "—";
        UpdatePhaseBar(audio.PhaseCorrelation);

        if (audio.Spectrum is { } spectrum)
            PaintSpectrum(spectrum, audio.SpectrumFloorDbfs);

        if (GetDisplayedSecondaryInstrument() == SplitSecondaryInstrument.Spectrum)
            UpdateSecondaryInstrumentHeader(SplitSecondaryInstrument.Spectrum);
    }

    private void UpdateClipBadge(Border badge, TextBlock label, string channelCode, long clips)
    {
        if (clips > 0)
        {
            badge.Background = ThemeBrush("StudioDangerBrush");
            badge.BorderBrush = ThemeBrush("StudioDangerBrush");
            label.Foreground = new SolidColorBrush(Colors.White);
            label.Text = "CLIP";
            var tip = LocalFormat("ClipCount", L(channelCode == "L" ? "LeftChannel" : "RightChannel"), clips);
            ToolTipService.SetToolTip(badge, tip);
        }
        else
        {
            badge.Background = ThemeBrush("StudioMeterTrackBrush");
            badge.BorderBrush = ThemeBrush("StudioSubtleBorderBrush");
            label.Foreground = ThemeBrush("StudioTextSecondaryBrush");
            label.Text = channelCode;
            ToolTipService.SetToolTip(badge, L(channelCode == "L" ? "LeftChannel" : "RightChannel"));
        }
    }

    private void MeterRail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        meterRailHeight = Math.Max(0, e.NewSize.Height - 4);
        ApplyMeterTransforms();
    }

    private void PhaseBarRail_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdatePhaseBar(displayedPhase);

    private void UpdatePhaseBar(double? correlation)
    {
        var width = PhaseBarRail.ActualWidth;
        if (width <= 2 || correlation is not { } value)
        {
            PhaseIndicatorFill.Width = 0;
            return;
        }

        var clamped = Math.Clamp(value, -1, 1);
        var center = width / 2;
        var span = Math.Max(2, Math.Abs(clamped) * (center - 1));
        PhaseIndicatorFill.Width = span;
        PhaseIndicatorFill.Margin = clamped >= 0
            ? new Thickness(center, 0, 0, 0)
            : new Thickness(center - span, 0, 0, 0);
        PhaseIndicatorFill.Background = clamped >= 0.25
            ? ThemeBrush("StudioSignalLockBrush")
            : clamped >= -0.15
                ? ThemeBrush("StudioCautionBrush")
                : ThemeBrush("StudioDangerBrush");
    }

    private void UpdateMeterBallistics()
    {
        // Fast attack, broadcast ~24 dB/s gravity decay, 1.2s peak hold (~72 frames at 60Hz)
        const double decayPerFrame = 0.42;
        const double peakHoldDecay = 0.22;
        const int holdFrames = 72;

        StepChannel(leftTargetDb, ref leftDisplayedDb, ref leftPeakHoldDb, ref leftPeakHoldFrames, decayPerFrame, peakHoldDecay, holdFrames);
        StepChannel(rightTargetDb, ref rightDisplayedDb, ref rightPeakHoldDb, ref rightPeakHoldFrames, decayPerFrame, peakHoldDecay, holdFrames);
        ApplyMeterTransforms();
    }

    private static void StepChannel(double targetDb, ref double displayedDb, ref double peakHoldDb, ref int peakHoldFrames,
        double decayPerFrame, double peakHoldDecay, int holdFrames)
    {
        displayedDb = targetDb >= displayedDb
            ? targetDb
            : Math.Max(targetDb, displayedDb - decayPerFrame);

        if (displayedDb >= peakHoldDb)
        {
            peakHoldDb = displayedDb;
            peakHoldFrames = holdFrames;
        }
        else if (peakHoldFrames > 0)
        {
            peakHoldFrames--;
        }
        else
        {
            peakHoldDb = Math.Max(displayedDb, peakHoldDb - peakHoldDecay);
        }
    }

    private void ApplyMeterTransforms()
    {
        var leftNorm = Math.Clamp((60 + leftDisplayedDb) / 60, 0, 1);
        var rightNorm = Math.Clamp((60 + rightDisplayedDb) / 60, 0, 1);
        LeftMeterFill.ScaleY = leftNorm;
        RightMeterFill.ScaleY = rightNorm;

        var leftPeakNorm = Math.Clamp((60 + leftPeakHoldDb) / 60, 0, 1);
        var rightPeakNorm = Math.Clamp((60 + rightPeakHoldDb) / 60, 0, 1);
        LeftPeakHoldBar.Opacity = leftPeakNorm > 0.01 ? 0.92 : 0;
        RightPeakHoldBar.Opacity = rightPeakNorm > 0.01 ? 0.92 : 0;
        LeftPeakHoldOffset.Y = -leftPeakNorm * meterRailHeight;
        RightPeakHoldOffset.Y = -rightPeakNorm * meterRailHeight;
    }

    private SplitSecondaryInstrument GetDisplayedSecondaryInstrument() => workspaceLayout switch
    {
        WorkspaceLayoutMode.Scopes => SplitSecondaryInstrument.Vector,
        WorkspaceLayoutMode.Spectrum => SplitSecondaryInstrument.Spectrum,
        WorkspaceLayoutMode.Events => SplitSecondaryInstrument.Events,
        _ => splitSecondary
    };

    private void UpdateSecondaryInstrumentHeader(SplitSecondaryInstrument instrument)
    {
        switch (instrument)
        {
            case SplitSecondaryInstrument.Vector:
                SecondaryInstrumentTitle.Text = L("VectorHeading");
                SecondaryInstrumentSubtitle.Text = L("VectorAxes");
                break;
            case SplitSecondaryInstrument.Spectrum:
                SecondaryInstrumentTitle.Text = L("SpectrumTitle");
                SecondaryInstrumentSubtitle.Text = visibleAudio is { } audio
                    ? $"{audio.SampleBits}-bit · {audio.SampleRate / 1000d:0.##} kHz"
                    : "";
                break;
            case SplitSecondaryInstrument.Events:
                SecondaryInstrumentTitle.Text = L("EventsTab");
                SecondaryInstrumentSubtitle.Text = visibleEvents is { Count: > 0 }
                    ? LocalFormat("EventsCountBadge", visibleEvents.Count)
                    : "";
                break;
        }
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
            PhaseLevel.Text = "—";
            AutomationProperties.SetName(LeftLevel, LocalFormat("PeakUnavailable", L("LeftChannel")));
            AutomationProperties.SetName(RightLevel, LocalFormat("PeakUnavailable", L("RightChannel")));
            UpdateClipBadge(LeftClipBadge, LeftChannel, "L", 0);
            UpdateClipBadge(RightClipBadge, RightChannel, "R", 0);
        }
        UpdateSecondaryInstrumentHeader(GetDisplayedSecondaryInstrument());
    }

    private sealed record EventRow(string TimeLabel, string Description, Brush AccentBrush);

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

        var floor = floorDbfs.Value;
        var step = Math.Max(1, Math.Round(-floor / 30) * 10);
        var levels = new[] { 0, -step, -2 * step, floor };
        var labels = levels.Select(level => new TextBlock
        {
            Text = level.ToString("0.#", settingsCulture),
            FontSize = 10,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = spectrumAxisBrush,
            TextAlignment = TextAlignment.Right
        }).ToArray();
        foreach (var label in labels)
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var labelWidth = Math.Ceiling(labels.Max(label => label.DesiredSize.Width));
        SpectrumLevelColumn.Width = new GridLength(Math.Max(34, labelWidth + 10));

        for (var i = 0; i < levels.Length; i++)
        {
            var y = SpectrumLevelY(levels[i], floor, height);
            SpectrumGridLines.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 1, X2 = 1 + width, Y1 = y, Y2 = y, Stroke = spectrumGridBrush, StrokeThickness = 1
            });

            var label = labels[i];
            label.Width = SpectrumLevelColumn.Width.Value - 8;
            Canvas.SetTop(label, Math.Max(0, y - label.DesiredSize.Height / 2));
            SpectrumLevelAxis.Children.Add(label);
        }

        // Subtle vertical quarter frequency guides
        for (var q = 1; q <= 3; q++)
        {
            var x = 1 + width * q / 4.0;
            SpectrumGridLines.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = x, X2 = x, Y1 = 1, Y2 = 1 + height, Stroke = spectrumGridBrush, StrokeThickness = 1, Opacity = 0.55
            });
        }
    }

    private static double SpectrumLevelY(double dbfs, double floorDbfs, double height) =>
        1 + Math.Clamp(dbfs / floorDbfs, 0, 1) * height;

    private void PaintSpectrum(IReadOnlyList<float> spectrum, double floorDbfs)
    {
        var width = SpectrumPlot.ActualWidth - 2;
        var height = SpectrumPlot.ActualHeight - 2;
        if (width <= 0 || height <= 0 || spectrum.Count < 2)
            return;

        if (spectrumPeakHoldValues is null || spectrumPeakHoldValues.Length != spectrum.Count)
        {
            spectrumPeakHoldValues = new float[spectrum.Count];
            Array.Fill(spectrumPeakHoldValues, (float)floorDbfs);
        }

        var tracePoints = new PointCollection();
        var fillPoints = new PointCollection();
        var peakPoints = new PointCollection();
        var bottomY = 1 + height;
        fillPoints.Add(new Point(1, bottomY));

        for (var bin = 0; bin < spectrum.Count; bin++)
        {
            var x = 1 + bin * width / (spectrum.Count - 1);
            var value = spectrum[bin];
            var y = SpectrumLevelY(value, floorDbfs, height);
            tracePoints.Add(new Point(x, y));
            fillPoints.Add(new Point(x, y));

            var held = spectrumPeakHoldValues[bin];
            held = value >= held ? value : (float)Math.Max(floorDbfs, held - 0.65f);
            spectrumPeakHoldValues[bin] = held;
            peakPoints.Add(new Point(x, SpectrumLevelY(held, floorDbfs, height)));
        }

        fillPoints.Add(new Point(1 + width, bottomY));
        SpectrumTrace.Points = tracePoints;
        SpectrumFill.Points = fillPoints;
        SpectrumPeakHold.Points = peakPoints;
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
