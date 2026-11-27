using System.Globalization;
using CaptureRack.SignalCapture;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CaptureRack.App.Controls;

public sealed partial class RfPeakMeter : UserControl
{
    private static readonly SolidColorBrush WhiteBrush = new(Colors.White);

    private readonly RfPeakBallistics ballistics = new();
    private Func<string, string> localize = key => key;
    private CultureInfo culture = CultureInfo.InvariantCulture;

    public RfPeakMeter()
    {
        InitializeComponent();
        Reset();
    }

    public void ApplyLanguage(Func<string, string> localizer, CultureInfo currentCulture)
    {
        localize = localizer;
        culture = currentCulture;
        RefreshClipBadges();
    }

    public void RefreshTheme() => RefreshClipBadges();

    public void Reset()
    {
        ballistics.Reset();
        ApplyTransforms();
        RefreshClipBadges();
    }

    public void Show(string channel, RfLevels levels, int scale)
    {
        Channel.Text = channel;
        var (newPos, newNeg) = ballistics.UpdateInput(
            levels.Min, levels.Max, scale, levels.PositiveClips, levels.NegativeClips);
        ApplyTransforms();
        RefreshClipBadges();
        if (newPos)
            StudioFeedback.PulseElement(High);
        if (newNeg)
            StudioFeedback.PulseElement(Low);
    }

    public void StepBallistics()
    {
        ballistics.StepFrame();
        ApplyTransforms();
    }

    private void ApplyTransforms()
    {
        var high = ballistics.PositiveNormalized * 100.0;
        var low = ballistics.NegativeNormalized * 100.0;
        var holdHigh = ballistics.PositivePeakHold * 100.0;
        var holdLow = ballistics.NegativePeakHold * 100.0;

        Positive.Height = high;
        Canvas.SetTop(Positive, 100.0 - high);
        Negative.Height = low;

        HoldHigh.Opacity = holdHigh > 0.5 ? 0.94 : 0;
        Canvas.SetTop(HoldHigh, Math.Max(0, 100.0 - holdHigh));
        HoldLow.Opacity = holdLow > 0.5 ? 0.94 : 0;
        Canvas.SetTop(HoldLow, Math.Min(198.0, 100.0 + holdLow));
    }

    private void RefreshClipBadges()
    {
        var dangerBrush = StudioBrushes.Resolve(this, "StudioDangerBrush");
        var trackBrush = StudioBrushes.Resolve(this, "StudioMeterTrackBrush");
        var hairlineBrush = StudioBrushes.Resolve(this, "StudioHairlineBrush");
        var secondaryBrush = StudioBrushes.Resolve(this, "StudioTextSecondaryBrush");

        var posClips = ballistics.VisiblePositiveClips;
        if (posClips > 0)
        {
            High.Background = dangerBrush;
            High.BorderBrush = dangerBrush;
            HighLabel.Foreground = WhiteBrush;
            var tip = string.Format(culture, localize("RfClipPositiveTooltip"), posClips);
            ToolTipService.SetToolTip(High, tip);
            AutomationProperties.SetName(High, tip);
        }
        else
        {
            High.Background = trackBrush;
            High.BorderBrush = hairlineBrush;
            HighLabel.Foreground = secondaryBrush;
            ToolTipService.SetToolTip(High, "+CLIP");
            AutomationProperties.SetName(High, "Positive CLIP");
        }

        var negClips = ballistics.VisibleNegativeClips;
        if (negClips > 0)
        {
            Low.Background = dangerBrush;
            Low.BorderBrush = dangerBrush;
            LowLabel.Foreground = WhiteBrush;
            var tip = string.Format(culture, localize("RfClipNegativeTooltip"), negClips);
            ToolTipService.SetToolTip(Low, tip);
            AutomationProperties.SetName(Low, tip);
        }
        else
        {
            Low.Background = trackBrush;
            Low.BorderBrush = hairlineBrush;
            LowLabel.Foreground = secondaryBrush;
            ToolTipService.SetToolTip(Low, "−CLIP");
            AutomationProperties.SetName(Low, "Negative CLIP");
        }
    }

    private void ClearHigh(object sender, RoutedEventArgs args)
    {
        ballistics.ResetPositiveClipLatch();
        RefreshClipBadges();
    }

    private void ClearLow(object sender, RoutedEventArgs args)
    {
        ballistics.ResetNegativeClipLatch();
        RefreshClipBadges();
    }
}
