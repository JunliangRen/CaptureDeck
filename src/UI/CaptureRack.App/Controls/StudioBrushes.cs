using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CaptureRack.App;

internal static class StudioBrushes
{
    // Each XAML thread owns its brushes. The light and dark dictionaries are fixed at startup.
    [ThreadStatic] private static Dictionary<(bool Dark, string Key), Brush>? resolvedBrushes;

    public static bool IsDark(FrameworkElement? scope)
    {
        if (scope is { IsLoaded: true })
            return scope.ActualTheme == ElementTheme.Dark;

        DependencyObject? current = scope;
        while (current is not null)
        {
            if (current is FrameworkElement fe)
            {
                if (fe.RequestedTheme == ElementTheme.Light)
                    return false;
                if (fe.RequestedTheme == ElementTheme.Dark)
                    return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }

        if (scope is not null)
        {
            if (scope.ActualTheme == ElementTheme.Light)
                return false;
            if (scope.ActualTheme == ElementTheme.Dark)
                return true;
        }
        return Application.Current.RequestedTheme == ApplicationTheme.Dark;
    }

    public static Brush Resolve(FrameworkElement? scope, string key)
    {
        bool dark = IsDark(scope);
        var cache = resolvedBrushes ??= [];
        if (cache.TryGetValue((dark, key), out var cached)) return cached;
        var themeKey = dark ? "Dark" : "Light";
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(themeKey, out var obj) &&
                obj is ResourceDictionary dict && dict.TryGetValue(key, out var themed) && themed is Brush themedBrush)
                return cache[(dark, key)] = themedBrush;
        }
        return Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush : new SolidColorBrush(Colors.Transparent);
    }
}
