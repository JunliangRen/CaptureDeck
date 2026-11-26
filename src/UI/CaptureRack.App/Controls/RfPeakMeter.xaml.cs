using CaptureRack.SignalCapture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App.Controls;

public sealed partial class RfPeakMeter : UserControl
{
    private long positiveCount, negativeCount, highUntil, lowUntil;
    private double highPeak, lowPeak;

    public RfPeakMeter() { InitializeComponent(); Reset(); }
    public void Reset()
    {
        positiveCount = negativeCount = highUntil = lowUntil = 0;
        highPeak = lowPeak = Positive.Height = Negative.Height = 0;
        Canvas.SetTop(HoldHigh, 100);
        Canvas.SetTop(HoldLow, 100);
        ClearHigh(this, new());
        ClearLow(this, new());
    }

    public void Show(string channel, RfLevels levels, int scale)
    {
        Channel.Text = channel;
        double high = 100d * Math.Max(0, levels.Max) / (scale - 1);
        double low = 100d * Math.Max(0, -levels.Min) / scale;
        Positive.Height = high;
        Canvas.SetTop(Positive, 100 - high);
        Negative.Height = low;
        long now = Environment.TickCount64;
        if (high >= highPeak || now >= highUntil) { highPeak = high; highUntil = now + 1000; }
        if (low >= lowPeak || now >= lowUntil) { lowPeak = low; lowUntil = now + 1000; }
        Canvas.SetTop(HoldHigh, 100 - highPeak);
        Canvas.SetTop(HoldLow, Math.Min(198.5, 100 + lowPeak));
        if (levels.PositiveClips > positiveCount) High.Background = StudioBrushes.Resolve(this, "StudioDangerBrush");
        if (levels.NegativeClips > negativeCount) Low.Background = StudioBrushes.Resolve(this, "StudioDangerBrush");
        positiveCount = levels.PositiveClips;
        negativeCount = levels.NegativeClips;
    }

    private void ClearHigh(object sender, RoutedEventArgs args) => High.ClearValue(BackgroundProperty);
    private void ClearLow(object sender, RoutedEventArgs args) => Low.ClearValue(BackgroundProperty);
}
