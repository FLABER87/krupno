using Krupno.App.Infrastructure;

namespace Krupno.App;

/// <summary>The entry point: a file for a window that is already open is handed over before WPF starts (see SingleInstance).</summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!args.Contains(SingleInstance.NewWindowSwitch) && SingleInstance.TryHandOver(args))
            return;
        App.Main();
    }
}
