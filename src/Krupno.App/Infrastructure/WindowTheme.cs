using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Krupno.App.Infrastructure;

/// <summary>
/// Dark window title bar. On Windows 10 the WPF Fluent theme paints only the content, while the title bar
/// stays white, so dark mode for the frame is enabled through DWM.
/// </summary>
internal static class WindowTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeOld = 19; // Windows 10 before 20H1

    public static void Apply(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
            return;

        int dark = IsDark() ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeOld, ref dark, sizeof(int));
    }

    private static bool IsDark()
    {
        return Application.Current.ThemeMode.Value switch
        {
            "Dark" => true,
            "Light" => false,
            _ => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is 0,
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
