using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? settingsWheelFallbackTimer;
    private bool settingsRawMouseRegistered;
    private long lastSettingsWheelMessage;
    private int lastSettingsWheelDelta;
    private long rawWheelBatchStarted;
    private int rawWheelDelta;
    private double rawWheelInitialOffset;
    private WindowPoint rawWheelPoint;
    private ScrollView? rawWheelTarget;

    private void StartSettingsWheelFallback()
    {
        if (settingsRawMouseRegistered)
            return;

        // Foreground only. Leave normal mouse, pointer, and touch input enabled.
        var mouse = new RawInputDevice
        {
            UsagePage = 1, Usage = 2,
            Target = WinRT.Interop.WindowNative.GetWindowHandle(this)
        };
        settingsRawMouseRegistered = RegisterRawInputDevices(ref mouse, 1, RawInputDevice.Size);
    }

    private void StopSettingsWheelFallback()
    {
        settingsWheelFallbackTimer?.Stop();
        rawWheelTarget = null;
        if (!settingsRawMouseRegistered)
            return;

        const uint remove = 1;
        var mouse = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = remove };
        RegisterRawInputDevices(ref mouse, 1, RawInputDevice.Size);
        settingsRawMouseRegistered = false;
        lastSettingsWheelMessage = 0;
    }

    private static bool IsWheelModified() => GetKeyState(0x10) < 0 || GetKeyState(0x11) < 0; // Shift, Control

    private void ReceiveSettingsRawWheel(nint handle)
    {
        if (!settingsRawMouseRegistered)
            return;

        const uint input = 0x10000003;
        const ushort verticalWheel = 0x0400;
        var size = RawMouseInput.Size;
        var copied = GetRawInputData(handle, input, out var mouse, ref size, RawMouseInput.HeaderSize);
        if (copied == RawMouseInput.Size && mouse.Type == 0 && (mouse.ButtonFlags & verticalWheel) != 0 &&
            GetCursorPos(out var point))
            QueueSettingsRawWheel(mouse.ButtonData, point);
    }

    private void QueueSettingsRawWheel(int delta, WindowPoint point)
    {
        if (delta == 0 || IsWheelModified() || ResolveScrollTargetAt(point) is not { } target)
            return;

        var now = Stopwatch.GetTimestamp();
        if (lastSettingsWheelMessage != 0 && lastSettingsWheelDelta == delta &&
            Stopwatch.GetElapsedTime(lastSettingsWheelMessage, now) < TimeSpan.FromMilliseconds(20))
            return;

        if (settingsWheelFallbackTimer is null)
        {
            settingsWheelFallbackTimer = DispatcherQueue.CreateTimer();
            settingsWheelFallbackTimer.IsRepeating = false;
            settingsWheelFallbackTimer.Interval = TimeSpan.FromMilliseconds(65);
            settingsWheelFallbackTimer.Tick += FlushSettingsRawWheel;
        }

        if (settingsWheelFallbackTimer.IsRunning && ReferenceEquals(rawWheelTarget, target))
        {
            rawWheelDelta += delta;
            return;
        }

        rawWheelBatchStarted = now;
        rawWheelTarget = target;
        rawWheelInitialOffset = target.VerticalOffset;
        rawWheelPoint = point;
        rawWheelDelta = delta;
        settingsWheelFallbackTimer.Start();
    }

    private void FlushSettingsRawWheel(DispatcherQueueTimer sender, object args)
    {
        // The same detent can also arrive through Win32 or the native ScrollView.
        if (rawWheelDelta != 0 && rawWheelTarget is { } target &&
            lastSettingsWheelMessage < rawWheelBatchStarted &&
            Math.Abs(target.VerticalOffset - rawWheelInitialOffset) < 0.5 &&
            ReferenceEquals(ResolveScrollTargetAt(rawWheelPoint), target))
            ScrollTargetByWheel(target, rawWheelDelta);
        rawWheelTarget = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public static readonly uint Size = (uint)Marshal.SizeOf<RawInputDevice>();
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouseInput
    {
        public static readonly uint HeaderSize = (uint)(8 + 2 * IntPtr.Size);
        public static readonly uint Size = (uint)Marshal.SizeOf<RawMouseInput>();
        public uint Type;
        public uint PacketSize;
        public nint Device;
        public nuint Parameter;
        public ushort MotionFlags;
        public ushort AlignmentPadding;
        public ushort ButtonFlags;
        public short ButtonData;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterRawInputDevices(ref RawInputDevice device, uint count, uint size);

    [LibraryImport("user32.dll")]
    private static partial uint GetRawInputData(nint handle, uint command, out RawMouseInput data,
        ref uint size, uint headerSize);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out WindowPoint point);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);
}
