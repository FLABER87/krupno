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
            NormalPosition = new NativeRect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom },
        };
        // Twice: the window is created on the primary monitor, and moving it to one with another scale rescales it (WM_DPICHANGED).
        // The second call sets the saved size again, now at the scale of the monitor it is on.
        SetWindowPlacement(handle, ref placement);
        SetWindowPlacement(handle, ref placement);
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
}
