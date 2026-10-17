using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Krupno.App.Infrastructure;

/// <summary>The window's normal (not maximized) bounds in screen pixels, and whether it was maximized.</summary>
public sealed record WindowBounds(int Left, int Top, int Right, int Bottom, bool Maximized);

/// <summary>
/// Saves and restores the window's size and position through GetWindowPlacement/SetWindowPlacement: they keep the normal
/// bounds of a maximized window, and Windows moves a window that would land off screen (on a monitor that is no longer
/// connected) onto a visible one.
/// </summary>
internal static class WindowPlacement
{
    private const int SwShowNormal = 1;
    private const int SwShowMinimized = 2;
    private const int SwShowMaximized = 3;
    private const int WpfRestoreToMaximized = 2;
    private const int MonitorDefaultToPrimary = 1;
    private const int MonitorDefaultToNearest = 2;

    public static WindowBounds? Read(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (handle == 0 || !GetWindowPlacement(handle, ref placement))
            return null;

        var rect = placement.NormalPosition;
        // Closed while minimized: the window comes back the way it was before it was minimized.
        bool maximized = placement.ShowCmd == SwShowMaximized
            || (placement.ShowCmd == SwShowMinimized && (placement.Flags & WpfRestoreToMaximized) != 0);
        return new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom, maximized);
    }

    /// <summary>Called on SourceInitialized: WPF has already sized and centered the window, which isn't shown yet.</summary>
    public static void Restore(Window window, WindowBounds bounds)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
            return;

        var placement = new Placement
        {
            Length = Marshal.SizeOf<Placement>(),
            ShowCmd = bounds.Maximized ? SwShowMaximized : SwShowNormal,
            NormalPosition = FitToWorkArea(new NativeRect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom }),
        };
        // Twice: the window is created on the primary monitor, and moving it to one with another scale rescales it (WM_DPICHANGED).
        // The second call sets the saved size again, now at the scale of the monitor it is on.
        SetWindowPlacement(handle, ref placement);
        SetWindowPlacement(handle, ref placement);
    }

    /// <summary>
    /// Windows only moves a window that would be entirely off screen. One saved on a larger monitor or at a higher resolution
    /// can still stick out of the screen it lands on (the Upscale button below the taskbar), so it is shrunk to that monitor's
    /// work area and moved into it. The placement is in workspace coordinates: relative to the primary monitor's work area.
    /// </summary>
    private static NativeRect FitToWorkArea(NativeRect rect)
    {
        var primary = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(default, MonitorDefaultToPrimary), ref primary))
            return rect;
        int dx = primary.Work.Left - primary.Monitor.Left, dy = primary.Work.Top - primary.Monitor.Top;

        var screen = new NativeRect { Left = rect.Left + dx, Top = rect.Top + dy, Right = rect.Right + dx, Bottom = rect.Bottom + dy };
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromRect(ref screen, MonitorDefaultToNearest), ref info))
            return rect;

        var work = info.Work;
        int width = Math.Min(screen.Right - screen.Left, work.Right - work.Left);
        int height = Math.Min(screen.Bottom - screen.Top, work.Bottom - work.Top);
        int left = Math.Clamp(screen.Left, work.Left, work.Right - width);
        int top = Math.Clamp(screen.Top, work.Top, work.Bottom - height);
        return new NativeRect { Left = left - dx, Top = top - dy, Right = left + width - dx, Bottom = top + height - dy };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>MONITORINFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    /// <summary>WINDOWPLACEMENT.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(nint hwnd, ref Placement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(nint hwnd, ref Placement placement);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, int flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rect, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
