using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private SubclassProc? settingsWindowProc;
    private readonly List<nint> settingsInputWindows = [];

    private void InitializeSettingsInput()
    {
        SettingsPage.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SettingsPointerPressed), true);

        // Mouse drivers can send the browser-back app command instead of a XAML pointer event.
        var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SubclassProc procedure = SettingsWindowMessage;
        settingsWindowProc = procedure;
        RegisterSettingsInputWindow(window);

        // WinUI hosts the XAML input site in a child HWND, which can receive the wheel before the top-level window.
        ((FrameworkElement)Content).Loaded += RegisterSettingsInputChildren;
        Closed += (_, _) =>
        {
            StopSettingsWheelFallback();
            foreach (var inputWindow in settingsInputWindows)
                RemoveWindowSubclass(inputWindow, procedure, 1);
        };
    }

    private void RegisterSettingsInputChildren(object sender, RoutedEventArgs args)
    {
        ((FrameworkElement)sender).Loaded -= RegisterSettingsInputChildren;
        var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        EnumChildWindows(window, (child, _) =>
        {
            RegisterSettingsInputWindow(child);
            return true;
        }, 0);
    }

    private void RegisterSettingsInputWindow(nint window)
    {
        if (settingsWindowProc is { } procedure && SetWindowSubclass(window, procedure, 1, 0))
            settingsInputWindows.Add(window);
    }

    private void SettingsPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (args.GetCurrentPoint(SettingsPage).Properties.PointerUpdateKind == PointerUpdateKind.XButton1Pressed &&
            TryBackFromSettings())
            args.Handled = true;
    }

    private nint SettingsWindowMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        const uint appCommand = 0x0319;
        const int browserBackward = 1;
        if (message == appCommand && ((lParam.ToInt64() >> 16) & 0x0fff) == browserBackward &&
            TryBackFromSettings())
            return 1;

        const uint mouseWheel = 0x020A;
        const uint pointerWheel = 0x024E;
        const uint rawInput = 0x00FF;
        if ((message is mouseWheel or pointerWheel) &&
            ScrollSettingsFromWindowMessage(wParam, lParam, message == pointerWheel))
            return 0;
        if (message == rawInput)
            ReceiveSettingsRawWheel(lParam);

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private bool ScrollSettingsFromWindowMessage(nuint wParam, nint lParam, bool isPointerMessage)
    {
        const uint modifierKeys = 0x000c; // MK_SHIFT | MK_CONTROL
        var modified = isPointerMessage ? IsWheelModified() : (wParam & modifierKeys) != 0;
        var delta = unchecked((short)(wParam >> 16));
        // Both wheel messages carry a signed delta and signed screen coordinates.
        var point = new WindowPoint { X = unchecked((short)lParam), Y = unchecked((short)(lParam >> 16)) };
        if (modified || delta == 0 || !CanScrollSettingsAt(point))
            return false;

        lastSettingsWheelMessage = System.Diagnostics.Stopwatch.GetTimestamp();
        lastSettingsWheelDelta = delta;
        ScrollSettingsByWheel(delta);
        return true;
    }

    private bool CanScrollSettingsAt(WindowPoint point)
    {
        if (SettingsPage.Visibility != Visibility.Visible || SettingsScroller.ScrollableHeight <= 0 ||
            SettingsPage.XamlRoot is not { } root ||
            DevicePicker.IsDropDownOpen || VideoInputPicker.IsDropDownOpen || AudioInputPicker.IsDropDownOpen ||
            ModePicker.IsDropDownOpen || FormatPicker.IsDropDownOpen || AudioFormatPicker.IsDropDownOpen ||
            LanguagePicker.IsDropDownOpen)
            return false;

        if (!ScreenToClient(WinRT.Interop.WindowNative.GetWindowHandle(this), ref point))
            return false;

        var scale = root.RasterizationScale;
        var x = point.X / scale;
        var y = point.Y / scale;
        return x >= 0 && y >= 0 && x < SettingsPage.ActualWidth && y < SettingsPage.ActualHeight;
    }

    private void ScrollSettingsByWheel(int delta) =>
        SettingsScroller.ScrollBy(0, -delta, new ScrollingScrollOptions(
            ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));

    private struct WindowPoint { public int X; public int Y; }

    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowProc(nint window, nint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc procedure, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc procedure, nuint id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowProc callback, nint data);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref WindowPoint point);
}
