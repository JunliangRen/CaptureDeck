using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CaptureRack.App.Controls;

public sealed partial class StudioWrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing), typeof(double), typeof(StudioWrapPanel),
        new PropertyMetadata(8d, OnSpacingChanged));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing), typeof(double), typeof(StudioWrapPanel),
        new PropertyMetadata(8d, OnSpacingChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StudioWrapPanel panel)
            panel.InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var childWidth = WrapLayoutCalculator.NormalizeChildAvailableWidth(availableSize.Width);
        var visibleSizes = new List<(double Width, double Height)>(Children.Count);
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            child.Measure(new Size(childWidth, double.PositiveInfinity));
            visibleSizes.Add((child.DesiredSize.Width, child.DesiredSize.Height));
        }

        var (w, h) = WrapLayoutCalculator.Measure(visibleSizes, availableSize.Width, HorizontalSpacing, VerticalSpacing);
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visibleChildren = new List<UIElement>(Children.Count);
        var visibleSizes = new List<(double Width, double Height)>(Children.Count);
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            visibleChildren.Add(child);
            visibleSizes.Add((child.DesiredSize.Width, child.DesiredSize.Height));
        }

        var rects = WrapLayoutCalculator.Arrange(visibleSizes, finalSize.Width, HorizontalSpacing, VerticalSpacing);
        for (var i = 0; i < visibleChildren.Count; i++)
        {
            var r = rects[i];
            visibleChildren[i].Arrange(new Rect(r.X, r.Y, r.Width, r.Height));
        }

        return finalSize;
    }
}
