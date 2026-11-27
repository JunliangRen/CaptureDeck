using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using CaptureRack.DeckLink;
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
    private enum ScopeLumaScaleMode { Code, Ire }

    private IReadOnlyList<CaptureEvent>? visibleEvents;
    private long visibleSignalLossCount;
    private TimeSpan visibleSignalLossDuration;
    private VideoScopes? visibleScopes;
    private StereoLevels? visibleAudio;
    private EventFilterMode eventFilter = EventFilterMode.All;
    private VideoScopeWaveformMode scopeWaveformMode = VideoScopeWaveformMode.Luma;
    private ScopeLumaScaleMode scopeLumaScale = ScopeLumaScaleMode.Code;

    private float[]? spectrumPeakHoldValues;
    private long lastSpectrumPaint;
    private long nextMonitorFrame;
    private static readonly long MonitorFrameTicks = (long)Math.Round(Stopwatch.Frequency / 60d);
    private static readonly long RenderToleranceTicks = Stopwatch.Frequency / 1000;

    private Brush SpectrumGridBrush => ThemeBrush("StudioSpectrumGridBrush");
    private Brush SpectrumAxisBrush => ThemeBrush("StudioSpectrumAxisBrush");

    private void RefreshMonitoringTheme()
    {
        RenderEvents();
        RenderLumaRange(visibleScopes);
        PaintSpectrumScale(visibleAudio?.SpectrumFloorDbfs);
    }

    private void StartPreviewClock()
    {
        nextMonitorFrame = 0;
        CompositionTarget.Rendering += RenderMonitorFrame;
        previewTimer.Start();
    }

    private void StopPreviewClock()
    {
        CompositionTarget.Rendering -= RenderMonitorFrame;
        previewTimer.Stop();
    }

    private void RenderMonitorFrame(object? sender, object args)
    {
        if (!FullscreenRenderPolicy.ShouldUpdateMainWindowMeters(PauseMainWindowRendering))
        {
            if (preview is { } feed && feed.RenderScopes)
                feed.RenderScopes = false;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now + RenderToleranceTicks < nextMonitorFrame)
            return;
        nextMonitorFrame = now - nextMonitorFrame >= MonitorFrameTicks
            ? now + MonitorFrameTicks : nextMonitorFrame + MonitorFrameTicks;

        ShowVideoScopes();
        ShowAudioMonitoring(LiveMediaSource is { } media ? media.ReadAudioLevels() : previewRecorder?.ReadAudioLevels());
        UpdateMeterBallistics();
    }

    private void ResetMonitoring()
    {
        visibleEvents = null;
        visibleSignalLossCount = 0;
        visibleSignalLossDuration = TimeSpan.Zero;
        visibleAudio = null;
        visibleScopes = null;
        spectrumPeakHoldValues = null;
        lastSpectrumPaint = 0;

        EventList.ItemsSource = null;
        EventList.Visibility = Visibility.Collapsed;
        EventsEmptyState.Visibility = Visibility.Visible;
        EventsSummary.Text = L("EventsEmpty");
        ScopeStatus.Text = L("ScopeWaiting");
        WaveformPlot.Source = null;
        RenderScopeScaleAxis(null);
        LumaScaleDescription.Text = L("LumaWaiting");
        AutomationProperties.SetName(WaveformPlot, L("LumaHeading"));
        VectorPlot.Source = null;
        SpectrumTrace.Points.Clear();
        SpectrumFill.Points.Clear();
        SpectrumPeakHold.Points.Clear();
        PaintSpectrumScale(null);
        SpectrumMidFrequency.Text = SpectrumMaxFrequency.Text = "—";
        StereoMeter.Reset();
        ResetAudioMonitorRoute();
        UpdateSecondaryInstrumentHeader(GetDisplayedSecondaryInstrument());
    }

    private void ShowMonitoring(CaptureStats stats)
    {
        visibleSignalLossCount = stats.VideoSignalLossCount;
        visibleSignalLossDuration = stats.VideoSignalLossDuration;
        RenderLumaRange(visibleScopes);
        if (!ReferenceEquals(visibleEvents, stats.Events))
        {
            visibleEvents = stats.Events;
            RenderEvents();
        }
        else if (GetDisplayedSecondaryInstrument() == SplitSecondaryInstrument.Events)
        {
            UpdateSecondaryInstrumentHeader(SplitSecondaryInstrument.Events);
        }

    }

    private void ShowAudioMonitoring(StereoLevels? audio)
    {
        if (audio is null || ReferenceEquals(visibleAudio, audio))
            return;

        var previous = visibleAudio;
        var effectiveRoute = audioMonitorRoute.ClampForChannels(audio.ChannelCount);
        if (audioMonitorRoute != effectiveRoute || audio.MonitorRoute != effectiveRoute)
        {
            audioMonitorRoute = effectiveRoute;
            previewRecorder?.SetAudioMonitorRoute(effectiveRoute);
            LiveMediaSource?.SetAudioMonitorRoute(effectiveRoute);
        }

        visibleAudio = audio;
        StereoMeter.UpdateLevels(audio, settingsCulture, effectiveRoute);
        monitorOptionsBar.UpdateAudioMonitorRoute(
            audio.ChannelCount,
            audio.ChannelLayout,
            effectiveRoute,
            StereoMeter.ActivePairMask);
        if (previous?.SampleRate != audio.SampleRate || previous?.SampleBits != audio.SampleBits ||
            previous.SpectrumFloorDbfs != audio.SpectrumFloorDbfs ||
            previous.ChannelCount != audio.ChannelCount ||
            previous.MonitorRoute != effectiveRoute)
            RenderSpectrumLabels(audio);
        if (audio.Spectrum is { } spectrum && SpectrumBody.Visibility == Visibility.Visible &&
            EffectiveWorkspaceLayout is WorkspaceLayoutMode.Split or WorkspaceLayoutMode.Spectrum)
            PaintSpectrum(spectrum, audio.SpectrumFloorDbfs);
    }

    private void ScopeWaveformMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;

        scopeWaveformMode = tag == "RgbParade" ? VideoScopeWaveformMode.RgbParade : VideoScopeWaveformMode.Luma;
        if (preview is { } feed)
            feed.ScopeWaveformMode = scopeWaveformMode;
        UpdateScopeModeButtons();
        if (visibleScopes is { } scopes)
            RenderScopeLabels(scopes);
        SettingsChanged();
    }

    private void ScopeLumaScale_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;

        scopeLumaScale = tag == "Ire" ? ScopeLumaScaleMode.Ire : ScopeLumaScaleMode.Code;
        if (preview is { } feed)
            feed.ScopeIreScale = scopeLumaScale == ScopeLumaScaleMode.Ire;
        UpdateScopeModeButtons();
        if (visibleScopes is { } scopes)
            RenderScopeLabels(scopes);
        else
            RenderScopeScaleAxis(null);
        SettingsChanged();
    }

    private void UpdateScopeModeButtons()
    {
        ApplySegmentState(ScopeLumaBtn, scopeWaveformMode == VideoScopeWaveformMode.Luma);
        ApplySegmentState(ScopeParadeBtn, scopeWaveformMode == VideoScopeWaveformMode.RgbParade);
        ApplySegmentState(ScopeScaleCodeBtn, scopeLumaScale == ScopeLumaScaleMode.Code);
        ApplySegmentState(ScopeScaleIreBtn, scopeLumaScale == ScopeLumaScaleMode.Ire);
        LumaHeading.Text = L(scopeWaveformMode == VideoScopeWaveformMode.RgbParade ? "RgbParadeHeading" : "LumaHeading");
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
        Controls.StudioFeedback.FlashCopied(CopyEventsIcon, "\uE8C8", themeRoot: RootShell);
    }

    private string LocalEventDescription(CaptureEvent item) => item.Kind switch
    {
        CaptureEventKind.TimestampRegression => L("EventTimestampRegression"),
        CaptureEventKind.FrameGap => LocalFormat("EventFrameGap", item.MissingFrames),
        CaptureEventKind.NoInputAtStart => L("EventNoInputAtStart"),
        CaptureEventKind.SignalLost => L("EventSignalLost"),
        CaptureEventKind.SignalRestored when item.SignalLossDuration is { } duration =>
            LocalFormat("EventLockRestoredWithDuration", duration.TotalSeconds),
        CaptureEventKind.SignalRestored => L("EventSignalRestored"),
        _ => item.Description
    };

    private Brush EventAccentBrush(CaptureEventKind kind) => kind switch
    {
        CaptureEventKind.TimestampRegression or CaptureEventKind.FrameGap => ThemeBrush("StudioDangerBrush"),
        CaptureEventKind.NoInputAtStart or CaptureEventKind.SignalLost => ThemeBrush("StudioCautionBrush"),
        CaptureEventKind.SignalRestored => ThemeBrush("StudioSignalLockBrush"),
        _ => ThemeBrush("StudioInfoBrush")
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
        EventsSummary.Text = L("EventsEmpty");
        EventsEmptyState.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        EventList.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        EventList.ItemsSource = filtered;
        if (GetDisplayedSecondaryInstrument() == SplitSecondaryInstrument.Events)
            UpdateSecondaryInstrumentHeader(SplitSecondaryInstrument.Events);
    }

    private void RenderSpectrumLabels(StereoLevels audio)
    {
        SpectrumMinFrequency.Text = "0 Hz";
        SpectrumMidFrequency.Text = (audio.SampleRate / 4000d).ToString("0.##", settingsCulture) + " kHz";
        SpectrumMaxFrequency.Text = (audio.SampleRate / 2000d).ToString("0.##", settingsCulture) + " kHz";
        PaintSpectrumScale(audio.SpectrumFloorDbfs);
        if (GetDisplayedSecondaryInstrument() == SplitSecondaryInstrument.Spectrum)
            UpdateSecondaryInstrumentHeader(SplitSecondaryInstrument.Spectrum);
    }

    private void UpdateMeterBallistics() => StereoMeter.StepBallistics();

    private SplitSecondaryInstrument GetDisplayedSecondaryInstrument() => EffectiveWorkspaceLayout switch
    {
        WorkspaceLayoutMode.Scopes => SplitSecondaryInstrument.Vector,
        WorkspaceLayoutMode.Spectrum => SplitSecondaryInstrument.Spectrum,
        WorkspaceLayoutMode.Events => SplitSecondaryInstrument.Events,
        _ => EffectiveSplitInstrument
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
                if (visibleAudio is { } audio)
                {
                    var route = audioMonitorRoute.ClampForChannels(audio.ChannelCount);
                    var routeSuffix = audio.ChannelCount > 2
                        ? route.Mode == AudioMonitorMode.Downmix
                            ? $" · {L("AudioMonitorDownmixShort")}"
                            : $" · Ch {AudioChannelLayoutHelper.PairCompactLabel(route.PairIndex, audio.ChannelCount)}"
                        : "";
                    SecondaryInstrumentSubtitle.Text = $"{audio.SampleBits}-bit · {audio.SampleRate / 1000d:0.##} kHz{routeSuffix}";
                }
                else
                {
                    SecondaryInstrumentSubtitle.Text = "";
                }
                break;
            case SplitSecondaryInstrument.Events:
                SecondaryInstrumentTitle.Text = L("EventsTab");
                SecondaryInstrumentSubtitle.Text = visibleSignalLossCount > 0 || visibleSignalLossDuration > TimeSpan.Zero
                    ? LocalFormat("VideoLockSummary", visibleSignalLossCount, visibleSignalLossDuration.TotalSeconds)
                    : visibleEvents is { Count: > 0 } ? LocalFormat("EventsCountBadge", visibleEvents.Count) : "";
                break;
        }
    }

    private void RefreshLocalizedMonitoring()
    {
        UpdateScopeModeButtons();
        RenderEvents();
        if (visibleScopes is { } scopes)
            RenderScopeLabels(scopes);
        else
        {
            ScopeStatus.Text = L("ScopeWaiting");
            RenderScopeScaleAxis(null);
            LumaScaleDescription.Text = L("LumaWaiting");
            AutomationProperties.SetName(WaveformPlot, L("LumaHeading"));
        }
        StereoMeter.ApplyLanguage(L, settingsCulture);
        if (visibleAudio is { } audio)
            RenderSpectrumLabels(audio);
        UpdateSecondaryInstrumentHeader(GetDisplayedSecondaryInstrument());
    }

    private sealed record EventRow(string TimeLabel, string Description, Brush AccentBrush);

    private void ShowVideoScopes()
    {
        if (preview is not { } feed)
            return;

        feed.RenderScopes = FullscreenRenderPolicy.ShouldRenderVideoScopes(
            PauseMainWindowRendering,
            EffectiveWorkspaceLayout is WorkspaceLayoutMode.Split or WorkspaceLayoutMode.Scopes);
        if (!feed.RenderScopes)
            return;

        var scale = RootShell.XamlRoot?.RasterizationScale ?? 1;
        var waveformVisible = WaveformPlot.ActualWidth > 0 && WaveformPlot.ActualHeight > 0;
        var vectorSide = Math.Min(VectorPlot.ActualWidth, VectorPlot.ActualHeight);
        if (waveformVisible || vectorSide > 0)
        {
            feed.SetScopeDisplaySize(waveformVisible ? WaveformPlot.ActualWidth : VideoScopes.WaveformWidth / scale,
                waveformVisible ? WaveformPlot.ActualHeight : VideoScopes.WaveformHeight / scale,
                vectorSide > 0 ? vectorSide : VideoScopes.VectorSize / scale, scale);
        }
        feed.VisitScopes(PaintVideoScopes);
    }

    private void PaintVideoScopes(VideoScopes scopes)
    {
        if (ReferenceEquals(visibleScopes, scopes))
            return;

        if (visibleScopes?.LumaScaleBits != scopes.LumaScaleBits ||
            visibleScopes?.InputBits != scopes.InputBits ||
            visibleScopes?.NoInput != scopes.NoInput ||
            visibleScopes?.WaveformMode != scopes.WaveformMode)
            RenderScopeLabels(scopes);

        visibleScopes = scopes;
        RenderLumaRange(scopes);
        var (width, height, vectorSize) = scopes.Raster;
        PaintPlot(WaveformPlot, scopes.LumaWaveform, width, height);
        if (VectorBody.Visibility == Visibility.Visible)
            PaintPlot(VectorPlot, scopes.Vectorscope, vectorSize, vectorSize);
    }

    private void LumaAxis_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LumaScaleSetup is not null)
            PositionLumaAxis(visibleScopes);
    }

    private void PositionLumaAxis(VideoScopes? scopes)
    {
        var height = LumaAxis.ActualHeight;
        const double labelHeight = 12;
        var luma = scopeWaveformMode == VideoScopeWaveformMode.Luma;
        var ire = luma && scopeLumaScale == ScopeLumaScaleMode.Ire;
        var showCodeGuides = luma && scopeLumaScale == ScopeLumaScaleMode.Code && scopes is not null;
        var bits = scopes?.LumaScaleBits ?? 8;
        // Give full-range ticks and reference codes separate columns when the axis is short.
        var extraColumn = showCodeGuides && height < 300;
        var tickX = extraColumn ? -36 : 0;
        var width = new GridLength(extraColumn ? 72 : 38);
        if (LumaAxisColumn.Width != width)
            LumaAxisColumn.Width = width;
        double Row(double value) => VideoScopes.LumaIreYFraction(bits, value) * height;
        void Position(TextBlock label, double y, double x = 0) =>
            label.Translation = new Vector3((float)x, (float)Math.Clamp(y - labelHeight / 2, 0, Math.Max(0, height - labelHeight)), 0);

        Position(LumaScaleTop, ire ? Row(100) : 0, tickX);
        Position(LumaScaleHigh, ire ? Row(75) : height / 4, tickX);
        Position(LumaScaleMid, ire ? Row(50) : height / 2, tickX);
        Position(LumaScaleLow, ire ? Row(25) : height * 3 / 4, tickX);
        Position(LumaScaleZero, ire ? Row(0) : height, tickX);
        LumaCodeWhite.Visibility = LumaCodeBlack.Visibility = showCodeGuides ? Visibility.Visible : Visibility.Collapsed;
        LumaScaleSetup.Visibility = ire || showCodeGuides ? Visibility.Visible : Visibility.Collapsed;
        var setup = Row(VideoScopes.SetupIre);
        var black = Row(0);
        if (showCodeGuides)
        {
            Position(LumaCodeWhite, Row(100));
            Position(LumaCodeBlack, black);
            Position(LumaScaleSetup, setup, black - setup < labelHeight ? -16 : 0);
        }
        else if (ire)
        {
            Position(LumaScaleSetup, setup, black - setup < labelHeight ? -10 : 0);
        }
    }

    private void RenderScopeScaleAxis(VideoScopes? scopes)
    {
        PositionLumaAxis(scopes);
        if (scopeLumaScale == ScopeLumaScaleMode.Ire)
        {
            LumaScaleSetup.Text = VideoScopes.SetupIre.ToString("0.#", settingsCulture);
            LumaScaleTop.Text = "100";
            LumaScaleHigh.Text = "75";
            LumaScaleMid.Text = "50";
            LumaScaleLow.Text = "25";
            LumaScaleZero.Text = "0";
            return;
        }

        LumaScaleZero.Text = "0";
        if (scopes is null)
        {
            LumaScaleTop.Text = LumaScaleHigh.Text = LumaScaleMid.Text = LumaScaleLow.Text = "—";
            return;
        }

        var maximum = scopeWaveformMode == VideoScopeWaveformMode.RgbParade
            ? 255 : (1 << scopes.LumaScaleBits) - 1;
        if (scopeWaveformMode == VideoScopeWaveformMode.Luma)
        {
            string Code(double ire) => VideoScopes.LumaCodeAtIre(scopes.LumaScaleBits, ire).ToString("0.#", settingsCulture);
            LumaCodeWhite.Text = Code(100);
            LumaScaleSetup.Text = Code(VideoScopes.SetupIre);
            LumaCodeBlack.Text = Code(0);
        }
        var quarter = (maximum + 1) / 4;
        LumaScaleTop.Text = maximum.ToString(settingsCulture);
        LumaScaleHigh.Text = (quarter * 3).ToString(settingsCulture);
        LumaScaleMid.Text = (quarter * 2).ToString(settingsCulture);
        LumaScaleLow.Text = quarter.ToString(settingsCulture);
    }

    private void RenderScopeLabels(VideoScopes scopes)
    {
        RenderScopeScaleAxis(scopes);
        if (scopeWaveformMode == VideoScopeWaveformMode.RgbParade)
        {
            LumaScaleDescription.Text = scopeLumaScale == ScopeLumaScaleMode.Ire
                ? "R · G · B · 0–100 IRE" : "R · G · B · 0–255";
            AutomationProperties.SetName(WaveformPlot, L("RgbParadeHeading"));
        }
        else if (scopeLumaScale == ScopeLumaScaleMode.Ire)
        {
            LumaScaleDescription.Text = $"Y′ 0–100 IRE ({scopes.InputBits}-bit)";
            AutomationProperties.SetName(WaveformPlot, L("LumaHeading"));
        }
        else
        {
            var maximum = (1 << scopes.LumaScaleBits) - 1;
            LumaScaleDescription.Text = scopes.LumaScaleBits == scopes.InputBits
                ? LocalFormat("LumaNative", maximum, scopes.InputBits)
                : LocalFormat("LumaScaled", maximum, scopes.LumaScaleBits, scopes.InputBits);
            AutomationProperties.SetName(WaveformPlot, LocalFormat("LumaPlotRange", maximum));
        }
        RenderLumaRange(scopes);
    }

    private void RenderLumaRange(VideoScopes? scopes)
    {
        var noInput = displayedStats?.Signal == VideoSignal.NoInput || scopes?.NoInput == true;
        if (noInput)
        {
            ScopeStatus.Text = L("ScopeNoInput");
            ScopeStatus.Foreground = ThemeBrush("StudioCautionBrush");
            return;
        }
        if (scopes?.LumaOutsideLegalFraction is { } fraction)
        {
            ScopeStatus.Text = LocalFormat("ScopeLumaOutOfRange", fraction);
            ScopeStatus.Foreground = MonitoringSeverity.ClassifyLumaExcursion(fraction) switch
            {
                TelemetrySeverity.Danger => ThemeBrush("StudioDangerBrush"),
                TelemetrySeverity.Caution => ThemeBrush("StudioCautionBrush"),
                _ => ThemeBrush("StudioSignalLockBrush")
            };
            return;
        }
        ScopeStatus.Text = L("ScopeWaiting");
        ScopeStatus.Foreground = ThemeBrush("StudioTextMutedBrush");
    }

    private void SpectrumPlot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsRfActive && ShowRfPcmSpectrum) { RenderRfAudio(); return; }
        if (IsRfActive && displayedRfMeasurement is { } reading)
        {
            PaintSpectrumScale(-90);
            PaintRfSpectrum(reading);
            return;
        }
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
        var gridBrush = SpectrumGridBrush;
        var axisBrush = SpectrumAxisBrush;
        var labels = levels.Select(level => new TextBlock
        {
            Text = level.ToString("0.#", settingsCulture),
            FontSize = 10,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = axisBrush,
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
                X1 = 1, X2 = 1 + width, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1
            });

            var label = labels[i];
            label.Width = SpectrumLevelColumn.Width.Value - 8;
            Canvas.SetTop(label, Math.Max(0, y - label.DesiredSize.Height / 2));
            SpectrumLevelAxis.Children.Add(label);
        }

        for (var q = 1; q <= 3; q++)
        {
            var x = 1 + width * q / 4.0;
            SpectrumGridLines.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = x, X2 = x, Y1 = 1, Y2 = 1 + height, Stroke = gridBrush, StrokeThickness = 1, Opacity = 0.55
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

        var now = Stopwatch.GetTimestamp();
        var peakDecay = lastSpectrumPaint == 0 ? 0 : 5.4 * Stopwatch.GetElapsedTime(lastSpectrumPaint, now).TotalSeconds;
        lastSpectrumPaint = now;
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
            held = value >= held ? value : (float)Math.Max(floorDbfs, held - peakDecay);
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
