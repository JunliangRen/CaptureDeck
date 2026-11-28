using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private SubclassProc? settingsWindowProc;
    private readonly List<nint> settingsInputWindows = [];

    private ScrollView? hoveredWheelScroller;

    private void InitializeSettingsInput()
    {
        SettingsPage.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SettingsPointerPressed), true);

        foreach (var scroller in new[] { SettingsPage.Scroller, QcCenterPage.Scroller, InspectorScroller })
        {
            scroller.PointerEntered += (_, _) =>
            {
                hoveredWheelScroller = scroller;
                UpdateWheelFallbackState();
            };
            scroller.PointerExited += (_, _) =>
            {
                if (hoveredWheelScroller != scroller) return;
                hoveredWheelScroller = null;
                UpdateWheelFallbackState();
            };
        }

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

    private void UpdateWheelFallbackState()
    {
        var needsWheel = (hoveredWheelScroller == SettingsPage.Scroller && SettingsPage.Visibility == Visibility.Visible) ||
            (hoveredWheelScroller == QcCenterPage.Scroller && QcCenterPage.Visibility == Visibility.Visible) ||
            (hoveredWheelScroller == InspectorScroller && WorkspacePage.Visibility == Visibility.Visible &&
                InspectorPanel.Visibility == Visibility.Visible);
        if (needsWheel)
            StartSettingsWheelFallback();
        else
            StopSettingsWheelFallback();
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
        UpdateWheelFallbackState();
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
        var point = new WindowPoint { X = unchecked((short)lParam), Y = unchecked((short)(lParam >> 16)) };
        if (modified || delta == 0 || ResolveScrollTargetAt(point) is not { } target)
            return false;

        lastSettingsWheelMessage = System.Diagnostics.Stopwatch.GetTimestamp();
        lastSettingsWheelDelta = delta;
        ScrollTargetByWheel(target, delta);
        return true;
    }

    private bool AnyDropdownOpen() =>
        deckLinkInspector.HasOpenDropdown ||
        monitorOptionsBar.HasOpenDropdown ||
        SettingsPage.HasOpenDropdown ||
        testSignalInspector.HasOpenDropdown ||
        rfInspector.HasOpenDropdown ||
        RecordingOutputPicker?.IsDropDownOpen == true ||
        RfScopeSource?.IsDropDownOpen == true ||
        RfSpectrumSource?.IsDropDownOpen == true ||
        LiveMediaInspector?.HasOpenDropdown == true;

    private ScrollView? ResolveScrollTargetAt(WindowPoint point)
    {
        if (RootShell.XamlRoot is not { } root || AnyDropdownOpen())
            return null;

        if (!ScreenToClient(WinRT.Interop.WindowNative.GetWindowHandle(this), ref point))
            return null;

        var scale = root.RasterizationScale;
        var dipPoint = new Point(point.X / scale, point.Y / scale);

        if (SettingsPage.Visibility == Visibility.Visible && IsPointInside(SettingsPage.Scroller, dipPoint))
            return SettingsPage.Scroller;

        if (QcCenterPage.Visibility == Visibility.Visible && IsPointInside(QcCenterPage.Scroller, dipPoint))
            return QcCenterPage.Scroller;

        if (WorkspacePage.Visibility == Visibility.Visible && InspectorPanel.Visibility == Visibility.Visible &&
            IsPointInside(InspectorScroller, dipPoint))
            return InspectorScroller;

        return null;
    }

    private bool IsPointInside(ScrollView scroller, Point rootPoint)
    {
        if (scroller.ScrollableHeight <= 0 || scroller.ActualWidth <= 0 || scroller.ActualHeight <= 0)
            return false;

        try
        {
            var bounds = scroller.TransformToVisual(RootShell)
                .TransformBounds(new Rect(0, 0, scroller.ActualWidth, scroller.ActualHeight));
            return bounds.Contains(rootPoint);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ScrollTargetByWheel(ScrollView target, int delta) =>
        target.ScrollBy(0, -delta, new ScrollingScrollOptions(
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

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowProc callback, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint window, ref WindowPoint point);
}
