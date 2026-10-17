using System.Windows;
using System.Windows.Threading;
using Krupno.App.Infrastructure;

namespace Krupno.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Our styles are added to the dictionary where ThemeMode has already put the Fluent theme: replacing
        // Application.Resources entirely (as a <ResourceDictionary> in App.xaml does) would lose the theme.
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Themes/Styles.xaml", UriKind.Relative) });

        // KRUPNO_THEME=light|dark — check the other theme without changing the system one.
        ThemeMode = Environment.GetEnvironmentVariable("KRUPNO_THEME")?.ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode,
        };
        Log.Start();
        DispatcherUnhandledException += OnUnhandledException;
        // Off the UI thread: the process is going down, so only the log can tell what happened.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception, the app is closing", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Error("Unobserved task exception", args.Exception);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Session ended");
        Log.Close();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception", e.Exception);
        string details = Log.FilePath is { } path ? $"\n\nDetails are in the log:\n{path}" : "";
        MessageBox.Show(e.Exception.Message + details, "Krupno — Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
