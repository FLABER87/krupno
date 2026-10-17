using Krupno.App.Infrastructure;
using Krupno.Core.Processing;

namespace Krupno.App;

/// <summary>The entry point: a file for a window that is already open is handed over before WPF starts (see SingleInstance).</summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!args.Contains(SingleInstance.NewWindowSwitch) && SingleInstance.TryHandOver(args))
            return;
        // For the installer: it asks for this window to be closed instead of closing it in the middle of processing.
        RunningMarker.Hold();
        UiLanguage.Apply(AppSettings.Load().Language);
        App.Main();
    }
}
