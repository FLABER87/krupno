using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Krupno.App.Infrastructure;

/// <summary>
/// Flashes the window's taskbar button until the window comes to the foreground, the way Windows asks for attention.
/// After a few flashes (the system's setting) the button stays highlighted; activating the window clears it.
/// </summary>
internal static class TaskbarFlash
{
    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    public static void Start(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
            return;

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = FlashTray | FlashUntilForeground,
            Count = uint.MaxValue,
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
