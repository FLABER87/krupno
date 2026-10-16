using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Krupno.App.Infrastructure;
using Krupno.App.ViewModels;

namespace Krupno.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        // On a laptop at 200% scaling the height can be as little as ~720 logical pixels, so stay within the screen.
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width - 32);
        Height = Math.Min(Height, workArea.Height - 32);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _viewModel.Dispose();
        base.OnClosing(e);
    }

    /// <summary>The user switched the Windows light/dark theme: repaint the title bar as well.</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(() => WindowTheme.Apply(this));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Files passed on the command line (for example, via "Open with").
        var paths = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (paths.Length > 0)
            await _viewModel.SetFilesAsync(paths);
    }

    /// <summary>Progress on the taskbar button: TaskbarItemInfo is outside the visual tree, so it's easier to update from here.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.TaskbarState):
                Taskbar.ProgressState = _viewModel.TaskbarState;
                if (_viewModel.TaskbarState == System.Windows.Shell.TaskbarItemProgressState.Error)
                    Taskbar.ProgressValue = 1;
                break;
            case nameof(MainViewModel.ProgressValue):
                Taskbar.ProgressValue = _viewModel.ProgressValue;
                break;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        switch (e.Key)
        {
            case Key.D0 or Key.NumPad0:
                Compare.FitToWindow();
                break;
            case Key.D1 or Key.NumPad1:
                Compare.ActualSize();
                break;
            case Key.OemPlus or Key.Add:
                Compare.ZoomIn();
                break;
            case Key.OemMinus or Key.Subtract:
                Compare.ZoomOut();
                break;
            case Key.C when Keyboard.FocusedElement is not TextBox:
                if (_viewModel.CopyResultCommand.CanExecute(null))
                    _viewModel.CopyResultCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        bool accept = e.Data.GetDataPresent(DataFormats.FileDrop) && _viewModel.IsIdle;
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        _viewModel.IsDragOver = accept;
        e.Handled = true;
    }

    private void OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements, so clear the highlight only if the cursor has left the window.
        var point = e.GetPosition(this);
        if (point.X <= 0 || point.Y <= 0 || point.X >= ActualWidth || point.Y >= ActualHeight)
            _viewModel.IsDragOver = false;
    }

    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _viewModel.IsDragOver = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            // With Ctrl held, the files are added to the queue; without it, they replace it.
            bool append = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            await _viewModel.SetFilesAsync(paths, append);
        }
    }
}
