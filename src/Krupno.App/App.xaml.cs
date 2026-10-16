using System.Windows;
using System.Windows.Threading;

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
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Krupno — Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
