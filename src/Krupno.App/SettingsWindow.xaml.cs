using System.Windows;
using Krupno.App.Infrastructure;

namespace Krupno.App;

/// <summary>
/// "Settings": the device, tiles, TTA, the results' names, language and update check, opened with the gear button in the panel
/// or Ctrl+,.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // A laptop at 200% scaling may have little more than 700 logical pixels of height: the window scrolls instead.
        MaxHeight = SystemParameters.WorkArea.Height - 32;
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
    }
}
