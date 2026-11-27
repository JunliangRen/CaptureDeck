using CaptureRack.Audio;
using System.Globalization;
using CaptureRack.DeckLink;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CaptureRack.App.Controls;

public sealed partial class BroadcastMeterControl : UserControl
{
    private static readonly SolidColorBrush WhiteBrush = new(Colors.White);

    private readonly MultiChannelMeterBallistics ballistics = new();
    private readonly MultiChannelActivityTracker activityTracker = new();
    private readonly List<ChannelStrip> strips = [];
    private readonly List<PairRouteButton> pairButtons = [];
    private readonly List<TextBlock> scaleTickLabels = [];
    private int[] visibleChannels = [0, 1];
    private int totalChannels = 2;
    private bool lastMultiChannelMode;
    private AudioChannelLayout channelLayout = AudioChannelLayout.Stereo;
    private AudioMonitorRoute monitorRoute = AudioMonitorRoute.Default;
    private AudioMeterDisplayMode displayMode = AudioMeterDisplayMode.Auto;
    private double meterRailHeight;
    private double? displayedPhase;
    private Func<string, string> localize = key => key;
    private CultureInfo culture = CultureInfo.InvariantCulture;

    public event EventHandler<AudioMonitorRoute>? MonitorRouteSelected;

    public int ActivePairMask => activityTracker.ActivePairMask(totalChannels);

    public BroadcastMeterControl()
    {
        InitializeComponent();
        RebuildMeterColumns([0, 1], force: true);
    }

    public void ApplyLanguage(Func<string, string> localizer, CultureInfo currentCulture)
    {
        localize = localizer;
        culture = currentCulture;
        StereoHeading.Text = localize("AudioShort");
        AutomationProperties.SetName(StereoMeterGrid, localize("StereoMeter"));
        ToolTipService.SetToolTip(LeftLevel, localize("LeftPeak"));
        ToolTipService.SetToolTip(RightLevel, localize("RightPeak"));
        ToolTipService.SetToolTip(PhaseLevel, localize("PhaseHelp"));
        MeterModeAutoItem.Text = localize("AudioMeterModeAuto");
        MeterModeAllItem.Text = localize("AudioMeterModeAll");
        MeterModeMonitoredItem.Text = localize("AudioMeterModeMonitored");
        ToolTipService.SetToolTip(MeterModeButton, localize("AudioMeterModeTooltip"));
        RefreshHeaderAndRouteControls();
        RefreshClipBadges();
    }

    public void SetPreviewTrackStatus(string? status, bool waiting = false)
    {
        var text = status ?? "";
        var meters = waiting ? Visibility.Collapsed : Visibility.Visible;
        ClipIndicatorsGrid.Visibility = MeterRailsHost.Visibility = PeakReadouts.Visibility = meters;
        PhaseReadout.Visibility = waiting || totalChannels == 1 ? Visibility.Collapsed : Visibility.Visible;
        if (waiting)
        {
            if (WaitingStatusText.Text != text) WaitingStatusText.Text = text;
            WaitingStatusOverlay.Visibility = Visibility.Visible;
            PreviewTrackStatus.Visibility = Visibility.Collapsed;
        }
        else
        {
            WaitingStatusOverlay.Visibility = Visibility.Collapsed;
            if (PreviewTrackStatus.Text != text) PreviewTrackStatus.Text = text;
            PreviewTrackStatus.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public void Reset()
    {
        ballistics.Reset();
        activityTracker.Reset();
        totalChannels = 2;
        channelLayout = AudioChannelLayout.Stereo;
        monitorRoute = AudioMonitorRoute.Default;
        displayedPhase = null;
        RebuildMeterColumns([0, 1], force: false);
        for (var i = 0; i < strips.Count; i++)
        {
            strips[i].MeterFill.ScaleY = 0;
            strips[i].PeakHoldBar.Opacity = 0;
        }
        LeftLevel.Text = RightLevel.Text = "—";
        AutomationProperties.SetName(LeftLevel, string.Format(culture, localize("PeakUnavailable"), localize("LeftChannel")));
        AutomationProperties.SetName(RightLevel, string.Format(culture, localize("PeakUnavailable"), localize("RightChannel")));
        PhaseLevel.Text = "—";
        PhaseReadout.Visibility = RightLevel.Visibility = Visibility.Visible;
        RefreshHeaderAndRouteControls();
        RefreshClipBadges();
        UpdatePhaseBar(null);
    }

    public void SetMonitorRoute(AudioMonitorRoute route)
    {
        var clamped = route.ClampForChannels(totalChannels);
        if (monitorRoute == clamped)
            return;
        monitorRoute = clamped;
        RefreshVisibleChannels();
        RefreshHeaderAndRouteControls();
    }

    public void UpdateLevels(StereoLevels audio, CultureInfo currentCulture, AudioMonitorRoute? authoritativeRoute = null)
    {
        culture = currentCulture;
        totalChannels = Math.Clamp(audio.ChannelCount, 1, AudioMonitorRoute.MaxSupportedChannels);
        channelLayout = audio.ChannelLayout;
        monitorRoute = (authoritativeRoute ?? audio.MonitorRoute).ClampForChannels(totalChannels);
        ballistics.UpdateInput(audio);
        activityTracker.Observe(audio);

        RefreshVisibleChannels();
        LeftLevel.Text = audio.LeftPeak > 0 ? (20 * Math.Log10(audio.LeftPeak)).ToString("0.0", culture) : "−∞";
        PhaseReadout.Visibility = RightLevel.Visibility = totalChannels == 1 ? Visibility.Collapsed : Visibility.Visible;
        RightLevel.Text = audio.RightPeak > 0 ? (20 * Math.Log10(audio.RightPeak)).ToString("0.0", culture) : "−∞";
        AutomationProperties.SetName(LeftLevel, string.Format(culture, localize("PeakValue"), localize("LeftChannel"), LeftLevel.Text));
        AutomationProperties.SetName(RightLevel, string.Format(culture, localize("PeakValue"), localize("RightChannel"), RightLevel.Text));
        displayedPhase = audio.PhaseCorrelation;
        PhaseLevel.Text = audio.PhaseCorrelation?.ToString("+0.00;-0.00;0.00", culture) ?? "—";
        RefreshHeaderAndRouteControls();
        RefreshClipBadges();
        UpdatePhaseBar(audio.PhaseCorrelation);
    }

    public void StepBallistics()
    {
        ballistics.StepFrame();
        ApplyMeterTransforms();
    }

    public void RefreshTheme()
    {
        var mutedBrush = StudioBrushes.Resolve(this, "StudioTextMutedBrush");
        foreach (var tick in scaleTickLabels)
            tick.Foreground = mutedBrush;
        RefreshHeaderAndRouteControls();
        RefreshClipBadges();
        UpdatePhaseBar(displayedPhase);
    }

    private void MeterModeAuto_Click(object sender, RoutedEventArgs e) => SetDisplayMode(AudioMeterDisplayMode.Auto);
    private void MeterModeAll_Click(object sender, RoutedEventArgs e) => SetDisplayMode(AudioMeterDisplayMode.All);
    private void MeterModeMonitored_Click(object sender, RoutedEventArgs e) => SetDisplayMode(AudioMeterDisplayMode.MonitoredPair);

    private void SetDisplayMode(AudioMeterDisplayMode mode)
    {
        displayMode = mode;
        MeterModeAutoItem.IsChecked = mode == AudioMeterDisplayMode.Auto;
        MeterModeAllItem.IsChecked = mode == AudioMeterDisplayMode.All;
        MeterModeMonitoredItem.IsChecked = mode == AudioMeterDisplayMode.MonitoredPair;
        RefreshVisibleChannels();
        RefreshHeaderAndRouteControls();
    }

    private void RefreshVisibleChannels()
    {
        Span<bool> clippedPairs = stackalloc bool[AudioMonitorRoute.MaxSupportedPairs];
        ballistics.FillClippedPairs(clippedPairs);
        var next = MultiChannelMeterPolicy.ComputeVisibleChannels(
            totalChannels, displayMode, monitorRoute, activityTracker.ActivePairs, clippedPairs);
        RebuildMeterColumns(next, force: false);
    }

    private void RebuildMeterColumns(int[] nextChannels, bool force)
    {
        var multiChannel = totalChannels > 2;
        if (!force && multiChannel == lastMultiChannelMode && visibleChannels.AsSpan().SequenceEqual(nextChannels))
            return;

        lastMultiChannelMode = multiChannel;
        visibleChannels = nextChannels;
        MeterCard.Width = MultiChannelMeterPolicy.ComputePanelWidth(visibleChannels.Length, multiChannel);
        var spacing = visibleChannels.Length > 8 ? 2 : 3;
        ClipIndicatorsGrid.ColumnSpacing = spacing;
        MeterRailsHost.ColumnSpacing = spacing;
        PairRouteGrid.ColumnSpacing = spacing;

        foreach (var existing in strips)
            existing.ClipButton.Click -= ClipButton_Click;
        ClipIndicatorsGrid.Children.Clear();
        ClipIndicatorsGrid.ColumnDefinitions.Clear();
        MeterRailsHost.Children.Clear();
        MeterRailsHost.ColumnDefinitions.Clear();
        strips.Clear();
        scaleTickLabels.Clear();

        var barWidth = visibleChannels.Length switch
        {
            <= 2 => 12.0,
            <= 4 => 10.0,
            <= 8 => 8.0,
            _ => 6.0
        };
        var clipFontSize = visibleChannels.Length <= 4 ? 10.0 : 8.5;
        var clipStyle = (Style)Resources["ClipLatchButtonStyle"];
        var fillBrush = (Brush)Application.Current.Resources["StudioMeterFillBrush"];

        for (var i = 0; i < visibleChannels.Length; i++)
        {
            var channelIdx = visibleChannels[i];
            ClipIndicatorsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            MeterRailsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                FontSize = clipFontSize,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var clipButton = new Button
            {
                Style = clipStyle,
                Content = label,
                Tag = channelIdx
            };
            clipButton.Click += ClipButton_Click;
            Grid.SetColumn(clipButton, i);
            ClipIndicatorsGrid.Children.Add(clipButton);

            var track = new Border
            {
                CornerRadius = new CornerRadius(1),
                Background = StudioBrushes.Resolve(this, "StudioMeterTrackBrush"),
                BorderBrush = StudioBrushes.Resolve(this, "StudioHairlineBrush"),
                BorderThickness = new Thickness(1)
            };
            var scaleTransform = new ScaleTransform { ScaleY = 0 };
            var fill = new Border
            {
                CornerRadius = new CornerRadius(1),
                Background = fillBrush,
                Margin = new Thickness(1),
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 1),
                RenderTransform = scaleTransform
            };
            var peakOffset = new TranslateTransform { Y = 0 };
            var peakHold = new Border
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(1, 0, 1, 0),
                Opacity = 0,
                Background = StudioBrushes.Resolve(this, "StudioMeterPeakBrush"),
                RenderTransform = peakOffset
            };
            var rail = new Grid
            {
                Width = barWidth,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            rail.Children.Add(track);
            rail.Children.Add(fill);
            rail.Children.Add(peakHold);
            Grid.SetColumn(rail, i);
            MeterRailsHost.Children.Add(rail);

            strips.Add(new ChannelStrip(channelIdx, clipButton, label, track, scaleTransform, peakHold, peakOffset));
        }

        // Right-hand dBFS scale column
        ClipIndicatorsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        MeterRailsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        var scaleColumn = BuildScaleColumn();
        Grid.SetColumn(scaleColumn, visibleChannels.Length);
        MeterRailsHost.Children.Add(scaleColumn);

        RebuildPairRouteRow();
        RefreshClipBadges();
        ApplyMeterTransforms();
    }

    private void RebuildPairRouteRow()
    {
        foreach (var existing in pairButtons)
            existing.Button.Click -= PairRouteButton_Click;
        PairRouteGrid.Children.Clear();
        PairRouteGrid.ColumnDefinitions.Clear();
        pairButtons.Clear();

        if (totalChannels <= 2)
        {
            PairRouteGrid.Visibility = Visibility.Collapsed;
            return;
        }

        // Group visible channels into clickable pair buttons aligned with the meter strips.
        var groups = new List<(int PairIndex, int ColSpan)>();
        var idx = 0;
        while (idx < visibleChannels.Length)
        {
            var pair = visibleChannels[idx] / 2;
            var span = 1;
            if (idx + 1 < visibleChannels.Length && visibleChannels[idx + 1] / 2 == pair)
                span = 2;
            groups.Add((pair, span));
            idx += span;
        }

        for (var i = 0; i < visibleChannels.Length; i++)
            PairRouteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        PairRouteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

        var pillStyle = (Style)Resources["MeterModePillButtonStyle"];
        var monoStyle = (Style)Application.Current.Resources["StudioMonoTextStyle"];
        var col = 0;
        foreach (var (pairIndex, span) in groups)
        {
            var text = new TextBlock
            {
                Text = AudioChannelLayoutHelper.PairCompactLabel(pairIndex, totalChannels),
                Style = monoStyle,
                FontSize = visibleChannels.Length <= 8 ? 9 : 8
            };
            var btn = new Button
            {
                Style = pillStyle,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(2, 0, 2, 0),
                Content = text,
                Tag = pairIndex
            };
            btn.Click += PairRouteButton_Click;
            Grid.SetColumn(btn, col);
            Grid.SetColumnSpan(btn, span);
            PairRouteGrid.Children.Add(btn);
            pairButtons.Add(new PairRouteButton(pairIndex, btn, text));
            col += span;
        }

        PairRouteGrid.Visibility = Visibility.Visible;
        RefreshPairRouteVisuals();
    }

    private Grid BuildScaleColumn()
    {
        var scale = new Grid();
        AutomationProperties.SetAccessibilityView(scale, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        for (var r = 0; r < 5; r++)
            scale.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var monoStyle = (Style)Application.Current.Resources["StudioMonoTextStyle"];
        var mutedBrush = StudioBrushes.Resolve(this, "StudioTextMutedBrush");

        void AddTick(int row, string text, VerticalAlignment align, double topMargin)
        {
            var tb = new TextBlock
            {
                Text = text,
                Style = monoStyle,
                FontSize = 9,
                Foreground = mutedBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = align,
                Margin = new Thickness(0, topMargin, 0, 0)
            };
            Grid.SetRow(tb, row);
            scale.Children.Add(tb);
            scaleTickLabels.Add(tb);
        }

        AddTick(0, "0", VerticalAlignment.Top, 0);
        AddTick(1, "-12", VerticalAlignment.Top, -5);
        AddTick(2, "-24", VerticalAlignment.Top, -5);
        AddTick(3, "-36", VerticalAlignment.Top, -5);
        AddTick(4, "-48", VerticalAlignment.Top, -5);
        AddTick(4, "-60", VerticalAlignment.Bottom, 0);
        return scale;
    }

    private void RefreshHeaderAndRouteControls()
    {
        var multiChannel = totalChannels > 2;
        UnitLabel.Visibility = multiChannel ? Visibility.Collapsed : Visibility.Visible;
        MeterModeButton.Visibility = multiChannel ? Visibility.Visible : Visibility.Collapsed;
        if (multiChannel)
        {
            MeterModeText.Text = displayMode == AudioMeterDisplayMode.All
                ? $"{totalChannels}ch"
                : $"{visibleChannels.Length}/{totalChannels}";
        }

        MonitorRouteTag.Visibility = multiChannel ? Visibility.Visible : Visibility.Collapsed;
        if (multiChannel)
        {
            MonitorRouteTagText.Text = monitorRoute.Mode == AudioMonitorMode.Downmix
                ? localize("AudioMonitorDownmixShort")
                : AudioChannelLayoutHelper.PairCompactLabel(monitorRoute.PairIndex, totalChannels);
        }

        RefreshPairRouteVisuals();
    }

    private void RefreshPairRouteVisuals()
    {
        var activeBg = StudioBrushes.Resolve(this, "StudioAccentSoftBrush");
        var activeBorder = StudioBrushes.Resolve(this, "StudioAccentBrush");
        var activeFg = StudioBrushes.Resolve(this, "StudioAccentBrush");
        var mixBg = StudioBrushes.Resolve(this, "StudioInfoSoftBrush");
        var mixBorder = StudioBrushes.Resolve(this, "StudioInfoBrush");
        var mixFg = StudioBrushes.Resolve(this, "StudioInfoBrush");
        var idleBg = StudioBrushes.Resolve(this, "StudioNeutralPillBrush");
        var idleBorder = StudioBrushes.Resolve(this, "StudioHairlineBrush");
        var idleFg = StudioBrushes.Resolve(this, "StudioTextSecondaryBrush");

        var isDownmix = monitorRoute.Mode == AudioMonitorMode.Downmix;
        foreach (var item in pairButtons)
        {
            var isMonitored = !isDownmix && monitorRoute.PairIndex == item.PairIndex;
            item.Button.Background = isMonitored ? activeBg : isDownmix ? mixBg : idleBg;
            item.Button.BorderBrush = isMonitored ? activeBorder : isDownmix ? mixBorder : idleBorder;
            item.Label.Foreground = isMonitored ? activeFg : isDownmix ? mixFg : idleFg;
            item.Label.FontWeight = isMonitored ? FontWeights.SemiBold : FontWeights.Normal;
            var pairText = AudioChannelLayoutHelper.PairShortLabel(item.PairIndex, totalChannels, channelLayout);
            var tip = string.Format(culture, localize("AudioMonitorPairTooltip"), pairText);
            ToolTipService.SetToolTip(item.Button, tip);
            AutomationProperties.SetName(item.Button, tip);
        }
    }

    private void PairRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int pairIndex })
            MonitorRouteSelected?.Invoke(this, AudioMonitorRoute.Pair(pairIndex));
    }

    private void ClipButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int channelIndex })
            return;
        ballistics.ResetClipLatch(channelIndex);
        RefreshVisibleChannels();
        RefreshClipBadges();
    }

    private void RefreshClipBadges()
    {
        var dangerBrush = StudioBrushes.Resolve(this, "StudioDangerBrush");
        var trackBrush = StudioBrushes.Resolve(this, "StudioMeterTrackBrush");
        var hairlineBrush = StudioBrushes.Resolve(this, "StudioHairlineBrush");
        var secondaryBrush = StudioBrushes.Resolve(this, "StudioTextSecondaryBrush");
        var peakBrush = StudioBrushes.Resolve(this, "StudioMeterPeakBrush");

        foreach (var strip in strips)
        {
            strip.TrackBorder.Background = trackBrush;
            strip.TrackBorder.BorderBrush = hairlineBrush;
            strip.PeakHoldBar.Background = peakBrush;

            var code = AudioChannelLayoutHelper.ChannelCode(strip.ChannelIndex, totalChannels, channelLayout);
            var channelName = totalChannels <= 2
                ? localize(strip.ChannelIndex == 0 ? "LeftChannel" : "RightChannel")
                : string.Format(culture, localize("AudioChannelNumber"), strip.ChannelIndex + 1, code);
            var clips = ballistics.VisibleClips(strip.ChannelIndex);
            if (clips > 0)
            {
                strip.ClipButton.Background = dangerBrush;
                strip.ClipButton.BorderBrush = dangerBrush;
                strip.ClipLabel.Foreground = WhiteBrush;
                strip.ClipLabel.Text = strips.Count <= 4 ? "CLIP" : "!";
                var clipText = string.Format(culture, localize("ClipCount"), channelName, clips);
                var tip = $"{clipText}\n{localize("ClipResetHint")}";
                ToolTipService.SetToolTip(strip.ClipButton, tip);
                AutomationProperties.SetName(strip.ClipButton, tip);
            }
            else
            {
                strip.ClipButton.Background = trackBrush;
                strip.ClipButton.BorderBrush = hairlineBrush;
                strip.ClipLabel.Foreground = secondaryBrush;
                strip.ClipLabel.Text = code;
                ToolTipService.SetToolTip(strip.ClipButton, channelName);
                AutomationProperties.SetName(strip.ClipButton, channelName);
            }
        }
    }

    private void MeterRail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        meterRailHeight = Math.Max(0, e.NewSize.Height - 4);
        ApplyMeterTransforms();
    }

    private void PhaseBarRail_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdatePhaseBar(displayedPhase);

    private void ApplyMeterTransforms()
    {
        foreach (var strip in strips)
        {
            var level = ballistics.NormalizedLevel(strip.ChannelIndex);
            var peak = ballistics.NormalizedPeakHold(strip.ChannelIndex);
            strip.MeterFill.ScaleY = level;
            strip.PeakHoldBar.Opacity = peak > 0.01 ? 0.92 : 0;
            strip.PeakHoldOffset.Y = -peak * meterRailHeight;
        }
    }

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
            ? StudioBrushes.Resolve(this, "StudioSignalLockBrush")
            : clamped >= -0.15
                ? StudioBrushes.Resolve(this, "StudioCautionBrush")
                : StudioBrushes.Resolve(this, "StudioDangerBrush");
    }

    private sealed record ChannelStrip(
        int ChannelIndex,
        Button ClipButton,
        TextBlock ClipLabel,
        Border TrackBorder,
        ScaleTransform MeterFill,
        Border PeakHoldBar,
        TranslateTransform PeakHoldOffset);

    private sealed record PairRouteButton(int PairIndex, Button Button, TextBlock Label);
}
