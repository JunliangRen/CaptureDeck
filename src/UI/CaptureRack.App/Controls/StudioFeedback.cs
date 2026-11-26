using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace CaptureRack.App.Controls;

internal static class StudioFeedback
{
    private static readonly UISettings UiSettings = new();
    private static readonly ConditionalWeakTable<FontIcon, FlashState> ActiveFlashes = new();

    public static bool AnimationsEnabled => UiSettings.AnimationsEnabled;

    public static void RevealSubtle(UIElement element, float offsetY = 6f, int durationMs = 140)
    {
        if (!AnimationsEnabled)
            return;

        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = nameof(UIElement.Opacity);
        fade.InsertKeyFrame(0f, 0.25f);
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);

        var translate = compositor.CreateVector3KeyFrameAnimation();
        translate.Target = nameof(UIElement.Translation);
        translate.InsertKeyFrame(0f, new Vector3(0f, offsetY, 0f));
        translate.InsertKeyFrame(1f, Vector3.Zero, easing);
        translate.Duration = TimeSpan.FromMilliseconds(durationMs);

        element.StartAnimation(fade);
        element.StartAnimation(translate);
    }

    public static async void FlashCopied(FontIcon icon, string originalGlyph, TextBlock? label = null,
        string? copiedLabel = null, Func<string>? getOriginalLabel = null, FrameworkElement? themeRoot = null)
    {
        FlashState state;
        if (ActiveFlashes.TryGetValue(icon, out var existing))
        {
            existing.Cancellation.Cancel();
            existing.Cancellation.Dispose();
            state = existing with { Cancellation = new CancellationTokenSource() };
            ActiveFlashes.AddOrUpdate(icon, state);
        }
        else
        {
            var localForeground = icon.ReadLocalValue(IconElement.ForegroundProperty);
            state = new FlashState(
                new CancellationTokenSource(),
                HasLocalForeground: localForeground != DependencyProperty.UnsetValue,
                LocalForeground: localForeground as Brush);
            ActiveFlashes.AddOrUpdate(icon, state);
        }

        var token = state.Cancellation.Token;
        icon.Glyph = "\uE73E";
        if (themeRoot is not null)
            icon.Foreground = StudioBrushes.Resolve(themeRoot, "StudioSignalLockBrush");
        if (label is not null && copiedLabel is not null)
            label.Text = copiedLabel;

        try
        {
            await Task.Delay(1100, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
            return;

        ActiveFlashes.Remove(icon);
        state.Cancellation.Dispose();

        try
        {
            if (icon.DispatcherQueue?.HasThreadAccess != true)
                return;

            icon.Glyph = originalGlyph;
            if (state.HasLocalForeground)
                icon.Foreground = state.LocalForeground;
            else
                icon.ClearValue(IconElement.ForegroundProperty);

            if (label is not null && getOriginalLabel is not null && label.Text == copiedLabel)
                label.Text = getOriginalLabel();
        }
        catch (COMException)
        {
        }
    }

    private sealed record FlashState(CancellationTokenSource Cancellation, bool HasLocalForeground, Brush? LocalForeground);
}
