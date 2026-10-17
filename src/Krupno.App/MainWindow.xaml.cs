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
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly MainViewModel _viewModel;
    /// <summary>Closing was put off until the run stops; closing once more doesn't wait.</summary>
    private bool _closingAfterRun;
    /// <summary>The window is closing for good (OnClosing got past the run), or has closed.</summary>
    private bool _closing;

    public MainWindow()
    {
        _viewModel = new MainViewModel(_settings);
        InitializeComponent();
        // On a laptop at 200% scaling the height can be as little as ~720 logical pixels, so stay within the screen.
        // The minimum size gives way too: otherwise it wins over the smaller Width/Height and the window sticks out of the screen.
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width - 32);
        Height = Math.Min(Height, workArea.Height - 32);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.RunFinished += (_, _) =>
        {
            // A batch can take an hour: if the user switched to another window meanwhile, the taskbar button asks for attention.
            if (!IsActive)
                TaskbarFlash.Start(this);
        };
        // "Update" started Setup, which replaces this program's files and starts it again.
        _viewModel.ExitRequested += (_, _) => Close();
        Loaded += OnLoaded;
        SourceInitialized += (_, _) =>
        {
            WindowTheme.Apply(this);
            // The size and position from the last session replace the default ones set above.
            if (_settings.Window is { } bounds)
                WindowPlacement.Restore(this, bounds);
        };
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy && !_closingAfterRun)
        {
            // In a batch the previous file's result is written while the next one is upscaled: closing now would end the
            // process in the middle of that save and lose a result that is already computed. Cancel the run (the cancel path
            // finishes the save) and close once it has stopped.
            e.Cancel = true;
            _closingAfterRun = true;
            _ = CloseAfterRunAsync();
            base.OnClosing(e);
            return;
        }

        // From here on the window is going: another Krupno.exe must not hand it files it would drop.
        _closing = true;
        SingleInstance.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (WindowPlacement.Read(this) is { } bounds)
        {
            _settings.Window = bounds;
            _settings.Save();
        }
        _viewModel.Dispose();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        base.OnClosed(e);
    }

    private async Task CloseAfterRunAsync()
    {
        await _viewModel.CancelRunAsync();
        // Closed meanwhile by a second click, which doesn't wait.
        if (!_closing)
            Close();
    }

    /// <summary>The user switched the Windows light/dark theme: repaint the title bar as well.</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(() => WindowTheme.Apply(this));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Files passed on the command line (for example, via "Open with").
        var paths = Environment.GetCommandLineArgs().Skip(1).Where(a => a != SingleInstance.NewWindowSwitch).ToArray();
        if (paths.Length > 0)
            _ = _viewModel.OpenExternalAsync(paths, SingleInstance.StartedAt);
        // The files of the Krupno.exe started after this one: one per file of an Explorer selection. The other Krupno.exe is told
        // they arrived only once the UI thread has taken them. If it doesn't get to them in time, the call is withdrawn, and a
        // dispatcher that is shutting down drops it: then the other Krupno.exe opens a window of its own.
        SingleInstance.Attach((handedOver, arrivedAt, giveUp) =>
        {
            var operation = Dispatcher.InvokeAsync(() => OnHandedOver(handedOver, arrivedAt));
            giveUp.Register(() => operation.Abort());
            return operation.Task.ContinueWith(t => t.Status == TaskStatus.RanToCompletion && t.Result, TaskScheduler.Default);
        });
    }

    /// <summary>
    /// Another Krupno.exe handed its files over (or none: it was started again): come forward and take them. False if the window
    /// is closing, or waits for the run to stop to close: the files would be lost with it.
    /// </summary>
    private bool OnHandedOver(string[] paths, DateTime arrivedAt)
    {
        if (_closing || _closingAfterRun)
            return false;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        if (paths.Length > 0)
            _ = _viewModel.OpenExternalAsync(paths, arrivedAt);
        return true;
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

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>
    /// "Settings" in a dialog of its own, bound to the same view model: a choice applies at once, as it did in the panel. Not while
    /// processing runs, like the rest of the panel.
    /// </summary>
    private void OpenSettings()
    {
        if (!_viewModel.IsIdle || !IsLoaded || _closing || _closingAfterRun)
            return;
        new SettingsWindow { Owner = this, DataContext = _viewModel }.ShowDialog();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        switch (e.Key)
        {
            case Key.OemComma:
                OpenSettings();
                break;
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
            case Key.V when Keyboard.FocusedElement is not TextBox:
                if (_viewModel.PasteCommand.CanExecute(null))
                    _viewModel.PasteCommand.Execute(null);
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
