using System.Globalization;
using CaptureRack.DeckLink;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CaptureRack.App.Controls;

public sealed partial class BroadcastMeterControl : UserControl
{
    private readonly BroadcastMeterBallistics ballistics = new();
    private double meterRailHeight;
    private double? displayedPhase;
    private Func<string, string> localize = key => key;
    private CultureInfo culture = CultureInfo.InvariantCulture;

    public BroadcastMeterControl() => InitializeComponent();

    public void ApplyLanguage(Func<string, string> localizer, CultureInfo currentCulture)
    {
        localize = localizer;
        culture = currentCulture;
        StereoHeading.Text = localize("AudioShort");
        AutomationProperties.SetName(StereoMeterGrid, localize("StereoMeter"));
        AutomationProperties.SetName(LeftChannel, localize("LeftChannel"));
        AutomationProperties.SetName(RightChannel, localize("RightChannel"));
        ToolTipService.SetToolTip(LeftLevel, localize("LeftPeak"));
        ToolTipService.SetToolTip(RightLevel, localize("RightPeak"));
        ToolTipService.SetToolTip(PhaseLevel, localize("PhaseHelp"));
        RefreshClipBadges();
    }

    public void Reset()
    {
        ballistics.Reset();
        displayedPhase = null;
        LeftMeterFill.ScaleY = RightMeterFill.ScaleY = 0;
        LeftPeakHoldBar.Opacity = RightPeakHoldBar.Opacity = 0;
        LeftLevel.Text = RightLevel.Text = "—";
        AutomationProperties.SetName(LeftLevel, string.Format(culture, localize("PeakUnavailable"), localize("LeftChannel")));
        AutomationProperties.SetName(RightLevel, string.Format(culture, localize("PeakUnavailable"), localize("RightChannel")));
        PhaseLevel.Text = "—";
        RefreshClipBadges();
        UpdatePhaseBar(null);
    }

    public void UpdateLevels(StereoLevels audio, CultureInfo currentCulture)
    {
        culture = currentCulture;
        ballistics.UpdateInput(audio.LeftPeak, audio.RightPeak, audio.LeftClips, audio.RightClips);
        LeftLevel.Text = audio.LeftPeak > 0 ? (20 * Math.Log10(audio.LeftPeak)).ToString("0.0", culture) : "−∞";
        RightLevel.Text = audio.RightPeak > 0 ? (20 * Math.Log10(audio.RightPeak)).ToString("0.0", culture) : "−∞";
        AutomationProperties.SetName(LeftLevel, string.Format(culture, localize("PeakValue"), localize("LeftChannel"), LeftLevel.Text));
        AutomationProperties.SetName(RightLevel, string.Format(culture, localize("PeakValue"), localize("RightChannel"), RightLevel.Text));
        displayedPhase = audio.PhaseCorrelation;
        PhaseLevel.Text = audio.PhaseCorrelation?.ToString("+0.00;-0.00;0.00", culture) ?? "—";
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
        RefreshClipBadges();
        UpdatePhaseBar(displayedPhase);
    }

    private void LeftClipButton_Click(object sender, RoutedEventArgs e)
    {
        ballistics.ResetLeftClipLatch();
        RefreshClipBadges();
    }

    private void RightClipButton_Click(object sender, RoutedEventArgs e)
    {
        ballistics.ResetRightClipLatch();
        RefreshClipBadges();
    }

    private void RefreshClipBadges()
    {
        UpdateClipBadge(LeftClipButton, LeftChannel, "L", ballistics.VisibleLeftClips);
        UpdateClipBadge(RightClipButton, RightChannel, "R", ballistics.VisibleRightClips);
    }

    private void UpdateClipBadge(Button button, TextBlock label, string channelCode, long clips)
    {
        var channelName = localize(channelCode == "L" ? "LeftChannel" : "RightChannel");
        if (clips > 0)
        {
            button.Background = StudioBrushes.Resolve(this, "StudioDangerBrush");
            button.BorderBrush = StudioBrushes.Resolve(this, "StudioDangerBrush");
            label.Foreground = new SolidColorBrush(Colors.White);
            label.Text = "CLIP";
            var clipText = string.Format(culture, localize("ClipCount"), channelName, clips);
            var tip = $"{clipText}\n{localize("ClipResetHint")}";
            ToolTipService.SetToolTip(button, tip);
            AutomationProperties.SetName(button, tip);
        }
        else
        {
            button.Background = StudioBrushes.Resolve(this, "StudioMeterTrackBrush");
            button.BorderBrush = StudioBrushes.Resolve(this, "StudioHairlineBrush");
            label.Foreground = StudioBrushes.Resolve(this, "StudioTextSecondaryBrush");
            label.Text = channelCode;
            ToolTipService.SetToolTip(button, channelName);
            AutomationProperties.SetName(button, channelName);
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
        LeftMeterFill.ScaleY = ballistics.LeftNormalized;
        RightMeterFill.ScaleY = ballistics.RightNormalized;
        LeftPeakHoldBar.Opacity = ballistics.LeftPeakNormalized > 0.01 ? 0.92 : 0;
        RightPeakHoldBar.Opacity = ballistics.RightPeakNormalized > 0.01 ? 0.92 : 0;
        LeftPeakHoldOffset.Y = -ballistics.LeftPeakNormalized * meterRailHeight;
        RightPeakHoldOffset.Y = -ballistics.RightPeakNormalized * meterRailHeight;
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
}
