using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Win32;
using Krupno.App.Controls;
using Krupno.App.Infrastructure;
using Krupno.Core.Imaging;
using Krupno.Core.Inference;
using Krupno.Core.Processing;

namespace Krupno.App.ViewModels;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>What the bottom part of the settings panel shows.</summary>
public enum PanelState
{
    Empty,
    Ready,
    Busy,
    Done,
    Error,
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int CompactModelCount = 3;
    private const int ThumbnailHeight = 128;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly List<ModelItem> _allModels = [];
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _refreshingModels;
    private bool _rebuildingModels;
    private bool _suppressPreview;
    private int _previewVersion;
    private CancellationTokenSource? _previewCts;
    /// <summary>One full-size preview decode at a time: clicking quickly through the filmstrip must not start a decode per click.</summary>
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private int _thumbnailGeneration;
    /// <summary>Bumped by clearing the list: selections still being read are dropped.</summary>
    private int _selectionGeneration;
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private UpscaleModel? _model;
    private (string Path, ExecutionDevice Device) _modelKey;
    private CancellationTokenSource? _cts;

    private FileItem? _currentFile;
    private BitmapSource? _before;
    private BitmapSource? _after;
    private TileOverlay? _tiles;
    private string? _previewError;
    private (int Width, int Height)? _sourceSize;
    private ModelItem? _selectedModel;
    private bool _showAllModels;
    private Option<double> _selectedScale;
    private Option<ImageFileFormat> _selectedFormat;
    private int _jpegQuality = 92;
    private string? _outputFolder;
    private Option<ExecutionDevice> _selectedDevice;
    private Option<int> _selectedTile;
    private PanelState _state = PanelState.Empty;
    private bool _isBusy;
    private bool _isDragOver;
    private string? _notice;
    private string? _toast;
    private string _progressTitle = "";
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private string _progressDetail = "";
    private string? _progressNote;
    private string _etaText = "";
    private string _doneTitle = "";
    private string _doneSubtitle = "";
    private string _errorDetail = "";
    private string? _errorTechnical;
    private TaskbarItemProgressState _taskbarState;
    private FileItem? _lastDone;

    public MainViewModel()
    {
        var autoDevice = ExecutionDevice.Auto.Resolve();
        Devices =
        [
            new(ExecutionDevice.Auto, $"Auto ({Text.Device(autoDevice)})"),
            .. GpuAdapters.Enumerate().Select(a => new Option<ExecutionDevice>(ExecutionDevice.Gpu(a), a.Name.Replace("(R)", "").Replace("(TM)", ""))),
            new(ExecutionDevice.Cpu, "CPU (slow)"),
        ];
        var savedDevice = FindSavedDevice();
        _selectedDevice = Devices.FirstOrDefault(d => d.Value == savedDevice) ?? Devices[0];

        Scales = [new(2, "×2"), new(3, "×3"), new(4, "×4"), new(8, "×8")];
        _selectedScale = Scales[2];

        Formats = [new(ImageFileFormat.Png, "PNG"), new(ImageFileFormat.Jpeg, "JPEG"), new(ImageFileFormat.Tiff, "TIFF")];
        _selectedFormat = Formats[0];

        TileSizes = [new(384, "Auto"), new(256, "256 px"), new(512, "512 px"), new(768, "768 px"), new(1024, "1024 px"), new(0, "No tiling")];
        _selectedTile = TileSizes[0];

        PrimaryCommand = new RelayCommand(() =>
        {
            if (State == PanelState.Empty)
                OpenFiles(append: false);
            else
                _ = UpscaleAsync();
        }, () => !IsBusy && (State == PanelState.Empty || SelectedModel is not null));
        OpenCommand = new RelayCommand(() => OpenFiles(append: false), () => !IsBusy);
        AddFilesCommand = new RelayCommand(() => OpenFiles(append: true), () => !IsBusy);
        ClearFilesCommand = new RelayCommand(ClearFiles, () => !IsBusy && HasFiles);
        UpscaleCommand = new RelayCommand(() => _ = UpscaleAsync(), () => !IsBusy && HasFiles && SelectedModel is not null);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        OpenResultCommand = new RelayCommand(OpenResult, () => ResultFile is not null);
        ShowInFolderCommand = new RelayCommand(ShowInFolder, () => ResultFile is not null);
        CopyResultCommand = new RelayCommand(CopyResult, () => After is not null);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        ResetOutputCommand = new RelayCommand(() => OutputFolder = null, () => !IsBusy && OutputFolder is not null);
        RefreshModelsCommand = new RelayCommand(RefreshModels, () => !IsBusy);
        OpenModelsFolderCommand = new RelayCommand(OpenModelsFolder);
        ToggleAllModelsCommand = new RelayCommand(() => ShowAllModels = !ShowAllModels);

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = null;
        };

        RefreshModels();
    }

    // ── Files and preview ───────────────────────────────────────────

    public ObservableCollection<FileItem> Files { get; } = [];
    public bool HasFiles => Files.Count > 0;
    public bool HasMultipleFiles => Files.Count > 1;

    public string WindowTitle => Files.Count switch
    {
        0 => "Krupno",
        1 => $"{Files[0].Name} — Krupno",
        _ => $"{Text.Files(Files.Count)} — Krupno",
    };

    public FileItem? CurrentFile
    {
        get => _currentFile;
        set
        {
            if (SetProperty(ref _currentFile, value) && !_suppressPreview)
                _ = ShowPreviewAsync(value);
        }
    }

    public BitmapSource? Before
    {
        get => _before;
        private set => SetProperty(ref _before, value);
    }

    public BitmapSource? After
    {
        get => _after;
        private set
        {
            if (SetProperty(ref _after, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public TileOverlay? Tiles
    {
        get => _tiles;
        private set => SetProperty(ref _tiles, value);
    }

    /// <summary>Why the current file isn't shown: the canvas shows an explanation instead of a blank space.</summary>
    public string? PreviewError
    {
        get => _previewError;
        private set => SetProperty(ref _previewError, value);
    }

    public bool IsDragOver
    {
        get => _isDragOver;
        set => SetProperty(ref _isDragOver, value);
    }

    /// <summary>A short notification over the preview ("Copied") that disappears on its own.</summary>
    public string? Toast
    {
        get => _toast;
        private set => SetProperty(ref _toast, value);
    }

    // ── Models ──────────────────────────────────────────────────────

    public ObservableCollection<ModelItem> VisibleModels { get; } = [];
    public bool HasNoModels => _allModels.Count == 0;
    public bool HasMoreModels => _allModels.Count > CompactModelCount;
    public string AllModelsToggleText => ShowAllModels ? "Show fewer" : $"All models ({_allModels.Count})";

    public ModelItem? SelectedModel
    {
        get => _selectedModel;
        set
        {
            // When the list is rebuilt, the ListBox resets the selection to null; that isn't the user's choice.
            if (value is null && (_rebuildingModels || _refreshingModels))
                return;
            if (SetProperty(ref _selectedModel, value))
                OnModelSettingsChanged();
        }
    }

    public bool ShowAllModels
    {
        get => _showAllModels;
        set
        {
            if (SetProperty(ref _showAllModels, value))
            {
                OnPropertyChanged(nameof(AllModelsToggleText));
                RebuildVisibleModels();
            }
        }
    }

    // ── Scale and saving ────────────────────────────────────────────

    public IReadOnlyList<Option<double>> Scales { get; }

    public Option<double> SelectedScale
    {
        get => _selectedScale;
        set
        {
            if (value is not null && SetProperty(ref _selectedScale, value))
                OnOutputSettingsChanged();
        }
    }

    public string SizePreview => _sourceSize is { } size
        ? $"{Text.Size(size.Width, size.Height)}  →  " +
          Text.Size((int)Math.Round(size.Width * SelectedScale.Value), (int)Math.Round(size.Height * SelectedScale.Value))
        : "Select a file to see the output size";

    public IReadOnlyList<Option<ImageFileFormat>> Formats { get; }

    public Option<ImageFileFormat> SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            if (value is not null && SetProperty(ref _selectedFormat, value))
            {
                OnPropertyChanged(nameof(IsJpeg));
                OnOutputSettingsChanged();
            }
        }
    }

    public bool IsJpeg => SelectedFormat.Value == ImageFileFormat.Jpeg;

    public int JpegQuality
    {
        get => _jpegQuality;
        set
        {
            if (SetProperty(ref _jpegQuality, value))
                OnOutputSettingsChanged();
        }
    }

    public string? OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (SetProperty(ref _outputFolder, value))
            {
                OnPropertyChanged(nameof(OutputFolderDisplay));
                OnPropertyChanged(nameof(HasCustomOutputFolder));
                OnOutputSettingsChanged();
            }
        }
    }

    public string OutputFolderDisplay => OutputFolder ?? "Next to the original";
    public bool HasCustomOutputFolder => OutputFolder is not null;

    // ── Advanced ────────────────────────────────────────────────────

    public IReadOnlyList<Option<ExecutionDevice>> Devices { get; }

    public Option<ExecutionDevice> SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value is not null && SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(AdvancedSummary));
                OnModelSettingsChanged();
            }
        }
    }

    public IReadOnlyList<Option<int>> TileSizes { get; }

    public Option<int> SelectedTile
    {
        get => _selectedTile;
        set
        {
            if (value is not null && SetProperty(ref _selectedTile, value))
            {
                OnPropertyChanged(nameof(AdvancedSummary));
                OnOutputSettingsChanged();
            }
        }
    }

    public string AdvancedSummary
    {
        get
        {
            var device = SelectedDevice.Value.Resolve();
            string tile = SelectedTile.Value switch
            {
                0 => "no tiling",
                384 when SelectedTile == TileSizes[0] => "auto tiles",
                var size => $"{size} px tiles",
            };
            return $"{Text.Device(device)} · {tile}";
        }
    }

    // ── Panel state ─────────────────────────────────────────────────

    public PanelState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
                return;
            OnPropertyChanged(nameof(IsEmptyState));
            OnPropertyChanged(nameof(IsReadyState));
            OnPropertyChanged(nameof(IsBusyState));
            OnPropertyChanged(nameof(IsDoneState));
            OnPropertyChanged(nameof(IsErrorState));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsEmptyState => State == PanelState.Empty;
    public bool IsReadyState => State == PanelState.Ready;
    public bool IsBusyState => State == PanelState.Busy;
    public bool IsDoneState => State == PanelState.Done;
    public bool IsErrorState => State == PanelState.Error;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    public string UpscaleButtonText => Files.Count > 1
        ? $"Upscale {Text.Files(Files.Count)} {Text.Scale(SelectedScale.Value)}"
        : $"Upscale {Text.Scale(SelectedScale.Value)}";

    /// <summary>A note above the button: cancellation, an unreadable file, etc.</summary>
    public string? Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public string ProgressTitle
    {
        get => _progressTitle;
        private set => SetProperty(ref _progressTitle, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public string ProgressDetail
    {
        get => _progressDetail;
        private set => SetProperty(ref _progressDetail, value);
    }

    /// <summary>A message from the core during processing (for example, the tile was reduced due to low memory).</summary>
    public string? ProgressNote
    {
        get => _progressNote;
        private set => SetProperty(ref _progressNote, value);
    }

    public string EtaText
    {
        get => _etaText;
        private set => SetProperty(ref _etaText, value);
    }

    public string DoneTitle
    {
        get => _doneTitle;
        private set => SetProperty(ref _doneTitle, value);
    }

    public string DoneSubtitle
    {
        get => _doneSubtitle;
        private set => SetProperty(ref _doneSubtitle, value);
    }

    public string ErrorDetail
    {
        get => _errorDetail;
        private set => SetProperty(ref _errorDetail, value);
    }

    /// <summary>The original exception text, shown in the tooltip for whoever investigates.</summary>
    public string? ErrorTechnical
    {
        get => _errorTechnical;
        private set => SetProperty(ref _errorTechnical, value);
    }

    public TaskbarItemProgressState TaskbarState
    {
        get => _taskbarState;
        private set => SetProperty(ref _taskbarState, value);
    }

    // ── Commands ────────────────────────────────────────────────────

    public ICommand PrimaryCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand AddFilesCommand { get; }
    public ICommand ClearFilesCommand { get; }
    public ICommand UpscaleCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenResultCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand CopyResultCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand ResetOutputCommand { get; }
    public ICommand RefreshModelsCommand { get; }
    public ICommand OpenModelsFolderCommand { get; }
    public ICommand ToggleAllModelsCommand { get; }

    private FileItem? ResultFile => CurrentFile is { IsDone: true } current ? current : _lastDone;

    /// <summary>Accepts files and folders from the dialog, drag and drop, or the command line.</summary>
    public async Task SetFilesAsync(IEnumerable<string> paths, bool append = false)
    {
        if (IsBusy)
            return;

        // Folders are read off the UI thread: a large folder or a slow network share would freeze the window. Selections are
        // applied one at a time in the order they were made, so a slow folder dropped first can't replace (or, with Ctrl,
        // lose) one dropped after it; clearing the list drops the selections still being read.
        int selection = _selectionGeneration;
        var added = new List<FileItem>();
        await _selectionGate.WaitAsync();
        try
        {
            if (selection != _selectionGeneration)
                return;

            List<string> found;
            try
            {
                found = await Task.Run(() => paths
                    .SelectMany(p => Directory.Exists(p)
                        ? Directory.EnumerateFiles(p).Order(StringComparer.OrdinalIgnoreCase)
                        : (IEnumerable<string>)[p])
                    .Where(ImageCodec.IsSupportedInput)
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                if (selection == _selectionGeneration)
                    ShowToast($"Can't read the selection: {ex.Message}");
                return;
            }
            // Processing may have started, or the list may have been cleared, while the folder was being read.
            if (IsBusy || selection != _selectionGeneration)
                return;
            if (found.Count == 0)
            {
                ShowToast("The selection contains no supported images");
                return;
            }

            if (!append)
            {
                Files.Clear();
                ++_thumbnailGeneration;
            }
            var known = new HashSet<string>(Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var path in found)
            {
                if (!known.Add(path))
                    continue;
                var item = new FileItem(path);
                Files.Add(item);
                added.Add(item);
            }

            _lastDone = null;
            Notice = null;
            OnFilesChanged();
            CurrentFile = append && CurrentFile is not null ? CurrentFile : Files.FirstOrDefault();
        }
        finally
        {
            _selectionGate.Release();
        }
        // Thumbnails load outside the gate: the next selection doesn't wait for them.
        await LoadThumbnailsAsync(added, _thumbnailGeneration);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _previewCts?.Cancel();
        // The session must not be touched while processing is running; the process is exiting anyway.
        if (!IsBusy)
            _model?.Dispose();
    }

    private void OnFilesChanged()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasMultipleFiles));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(UpscaleButtonText));
        State = HasFiles ? PanelState.Ready : PanelState.Empty;
        CommandManager.InvalidateRequerySuggested();
    }

    private void ClearFiles()
    {
        Files.Clear();
        ++_thumbnailGeneration;
        ++_selectionGeneration;
        _lastDone = null;
        Notice = null;
        CurrentFile = null;
        OnFilesChanged();
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<FileItem> items, int generation)
    {
        foreach (var item in items)
        {
            // The list was replaced or cleared: stop instead of decoding thumbnails nobody will see.
            if (generation != _thumbnailGeneration)
                return;
            try
            {
                item.Thumbnail = await Task.Run(() => ImageCodec.LoadThumbnail(item.Path, ThumbnailHeight));
            }
            catch (Exception)
            {
                // Without a thumbnail the filmstrip shows a placeholder; the preview or processing reports the file error.
            }
        }
    }

    private async Task ShowPreviewAsync(FileItem? file)
    {
        int version = ++_previewVersion;
        // A newer request makes the older one pointless: skip its decodes that haven't started yet.
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var cancellationToken = _previewCts.Token;
        Tiles = null;
        PreviewError = null;
        if (file is null)
        {
            Before = null;
            After = null;
            SetSourceSize(null);
            return;
        }

        try
        {
            await _previewGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var before = await Task.Run(() => ImageCodec.ToBitmapSource(ImageCodec.Load(file.Path).Image), cancellationToken);
            BitmapSource? after = null;
            string? afterError = null;
            if (file.IsDone && file.OutputExists)
            {
                try
                {
                    after = await Task.Run(() => ImageCodec.ToBitmapSource(ImageCodec.Load(file.OutputPath!).Image), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The result can't be shown (deleted or replaced by another app), but the source is fine: it isn't an error of the file.
                    afterError = Friendly(ex);
                }
            }
            if (version != _previewVersion)
                return;

            Before = before;
            After = after;
            SetSourceSize((before.PixelWidth, before.PixelHeight));
            if (afterError is not null)
                ShowToast($"Can't show the result: {afterError}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version != _previewVersion)
                return;
            Before = null;
            After = null;
            SetSourceSize(null);
            file.State = FileState.Error;
            file.Error = Friendly(ex);
            PreviewError = file.Error;
        }
        finally
        {
            _previewGate.Release();
        }
    }

    private void SetSourceSize((int Width, int Height)? size)
    {
        _sourceSize = size;
        OnPropertyChanged(nameof(SizePreview));
    }

    // ── Processing ──────────────────────────────────────────────────

    private async Task UpscaleAsync()
    {
        if (IsBusy || !HasFiles || SelectedModel is null)
            return;

        var files = Files.ToList();
        var settings = (Scale: SelectedScale.Value, Tile: SelectedTile.Value, Format: SelectedFormat.Value,
            Quality: JpegQuality, Folder: OutputFolder);
        // All names up front: a result must not overwrite a queued source ("photo.png" → "photo_x4.png" while
        // "photo_x4.png" waits in the queue) or another result ("photo.jpg" and "photo.png" → "photo_x4.png").
        var outputs = OutputPaths.MakeUnique(files.Select(f => f.Path),
            files.Select(f => BuildOutputPath(f.Path, settings.Folder, settings.Scale, settings.Format)).ToList());
        foreach (var file in files)
        {
            file.State = FileState.Pending;
            file.Error = null;
        }

        IsBusy = true;
        State = PanelState.Busy;
        Notice = null;
        ProgressNote = null;
        ProgressTitle = "Preparing the model…";
        ProgressDetail = SelectedModel.Title;
        ProgressValue = 0;
        EtaText = "";
        IsProgressIndeterminate = true;
        TaskbarState = TaskbarItemProgressState.Indeterminate;
        _lastDone = null;
        _cts = new CancellationTokenSource();
        var cancellationToken = _cts.Token;
        var total = Stopwatch.StartNew();
        Stopwatch? work = null;
        int succeeded = 0, failed = 0;
        string? firstError = null;
        long lastBytes = 0;

        try
        {
            var model = await GetModelAsync();
            var upscaler = new Upscaler(model);
            var options = new UpscaleOptions { Scale = settings.Scale, TileSize = settings.Tile };

            for (int i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[i];
                int index = i;
                file.State = FileState.Processing;
                _suppressPreview = true;
                CurrentFile = file;
                _suppressPreview = false;
                ++_previewVersion; // stale preview loads must not overwrite the current frame

                try
                {
                    var loaded = await Task.Run(() => ImageCodec.Load(file.Path), cancellationToken);
                    // The user may be looking at another file of the batch meanwhile: the canvas shows only the selected one,
                    // so every step checks the selection again after an await.
                    if (CurrentFile == file)
                    {
                        var before = await Task.Run(() => ImageCodec.ToBitmapSource(loaded.Image), cancellationToken);
                        if (CurrentFile == file)
                        {
                            Before = before;
                            After = null;
                            PreviewError = null;
                            SetSourceSize((loaded.Image.Width, loaded.Image.Height));
                            Tiles = TileOverlay.Empty;
                        }
                    }

                    // The format's size limit, before minutes of work rather than after.
                    var (outputWidth, outputHeight) = upscaler.OutputSize(loaded.Image.Width, loaded.Image.Height, options);
                    ImageCodec.EnsureCanSave(settings.Format, outputWidth, outputHeight);

                    var tiles = new TileProgress();
                    work ??= Stopwatch.StartNew();
                    var progress = new Progress<UpscaleProgress>(p =>
                        OnProgress(p, file, index, files.Count, model, tiles, work));
                    var result = await upscaler.UpscaleAsync(loaded.Image, options, progress, cancellationToken);

                    string output = outputs[index];
                    await Task.Run(() => ImageCodec.Save(result, output, settings.Format,
                        new SaveOptions { Source = loaded, JpegQuality = settings.Quality }), cancellationToken);

                    // The file is written: record it before anything that can still be canceled.
                    file.OutputPath = output;
                    file.State = FileState.Done;
                    _lastDone = file;
                    lastBytes = new FileInfo(output).Length;
                    succeeded++;

                    if (CurrentFile == file)
                    {
                        Tiles = null;
                        BitmapSource? after = null;
                        try
                        {
                            after = await Task.Run(() => ImageCodec.ToBitmapSource(result));
                        }
                        catch (OutOfMemoryException)
                        {
                            // The result is saved; without a preview the canvas just shows the original.
                        }
                        if (CurrentFile == file)
                            After = after;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    file.State = FileState.Error;
                    file.Error = Friendly(ex);
                    firstError ??= file.Error;
                    if (CurrentFile == file)
                    {
                        Tiles = null;
                        if (ex is NotSupportedException)
                        {
                            // The file couldn't be read, so don't leave the previous image on the canvas.
                            Before = null;
                            After = null;
                            PreviewError = file.Error;
                        }
                    }
                    failed++;
                    if (files.Count == 1)
                        throw;
                }
            }

            string elapsed = Text.Seconds(total.Elapsed);
            if (succeeded == 0)
            {
                // Every file of the batch failed: that is not a success, whatever the count.
                ErrorDetail = $"None of the {files.Count} files could be upscaled. {firstError}";
                ErrorTechnical = firstError;
                State = PanelState.Error;
                TaskbarState = TaskbarItemProgressState.Error;
            }
            else
            {
                if (files.Count == 1)
                {
                    DoneTitle = $"Done in {elapsed} · {Text.Bytes(lastBytes)}";
                    DoneSubtitle = After is { } after
                        ? $"{Path.GetFileName(_lastDone!.OutputPath)} · {Text.Size(after.PixelWidth, after.PixelHeight)}"
                        : Path.GetFileName(_lastDone!.OutputPath)!;
                }
                else
                {
                    DoneTitle = failed == 0
                        ? $"Done: {Text.Files(succeeded)} in {elapsed}"
                        : $"Done: {succeeded} of {files.Count}, {failed} failed";
                    DoneSubtitle = settings.Folder ?? "Saved next to the originals";
                }
                State = PanelState.Done;
                TaskbarState = failed > 0 ? TaskbarItemProgressState.Error : TaskbarItemProgressState.None;
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var file in files.Where(f => f.IsProcessing))
                file.State = FileState.Pending;
            Tiles = null;
            State = PanelState.Ready;
            Notice = "Processing canceled";
            TaskbarState = TaskbarItemProgressState.None;
        }
        catch (Exception ex)
        {
            Tiles = null;
            ErrorDetail = Friendly(ex);
            ErrorTechnical = ex.Message;
            State = PanelState.Error;
            TaskbarState = TaskbarItemProgressState.Error;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            ProgressNote = null;
        }
    }

    private void OnProgress(UpscaleProgress p, FileItem file, int index, int count, UpscaleModel model, TileProgress tiles,
        Stopwatch work)
    {
        // The session is created lazily, and DirectML may have just fallen back to the CPU: say so, the run is much slower then.
        string device = Text.Device(model.Device);
        if (model.FallbackReason is not null)
            ProgressNote ??= "DirectML is unavailable, running on the CPU";

        if (p.Message is { } message)
        {
            // A warning stays visible for the rest of the run; a stage ("Resizing to …") only replaces the detail line.
            if (p.IsWarning)
                ProgressNote = message;
            else
                ProgressDetail = count > 1 ? $"File {index + 1} of {count} · {message}" : message;
            return;
        }

        if (p.Region is { } region)
        {
            var overlay = tiles.Add(new Rect(region.Left, region.Top, region.Width, region.Height));
            if (CurrentFile == file)
                Tiles = overlay;
        }
        else if (p.Tile == 0)
        {
            tiles.Clear();
            if (CurrentFile == file)
                Tiles = TileOverlay.Empty;
        }

        double fraction = Math.Clamp((index + p.Fraction) / count, 0, 1);
        IsProgressIndeterminate = false;
        TaskbarState = TaskbarItemProgressState.Normal;
        ProgressValue = fraction;
        ProgressTitle = $"Processing… {fraction * 100:0}%";

        var elapsed = work.Elapsed;
        EtaText = fraction > 0.02 && elapsed.TotalSeconds > 1.5
            ? Text.Remaining(elapsed * ((1 - fraction) / fraction))
            : "";

        string tileText = p.TileCount > 0 ? $"tile {p.Tile} of {p.TileCount}" : "";
        string pass = p.PassCount > 1 ? $" · pass {p.Pass} of {p.PassCount}" : "";
        ProgressDetail = count > 1
            ? $"File {index + 1} of {count} · {tileText}{pass}"
            : $"{(tileText.Length > 0 ? char.ToUpper(tileText[0]) + tileText[1..] : "")}{pass} · {device}";
    }

    /// <summary>
    /// Finished tiles of the current pass for the overlay. Tiles finish row by row, so complete rows are merged into one
    /// rectangle: the overlay holds at most a row of tiles plus one, instead of growing with every tile (quadratic work on large images).
    /// </summary>
    private sealed class TileProgress
    {
        private readonly List<Rect> _row = [];
        private Rect? _rows;

        public void Clear()
        {
            _row.Clear();
            _rows = null;
        }

        public TileOverlay Add(Rect tile)
        {
            // A tile at the left edge starts a new row, so the previous row is complete.
            if (tile.X == 0 && _row.Count > 0)
            {
                var rows = _rows ?? _row[0];
                foreach (var done in _row)
                    rows.Union(done);
                _rows = rows;
                _row.Clear();
            }
            _row.Add(tile);
            return new TileOverlay(_rows is { } merged ? [merged, .. _row] : [.. _row], tile);
        }
    }

    private async Task<UpscaleModel> GetModelAsync()
    {
        var entry = SelectedModel?.Entry ?? throw new InvalidOperationException("No model is selected.");
        var device = SelectedDevice.Value;
        if (_model is not null && _modelKey != (entry.Path, device))
        {
            _model.Dispose();
            _model = null;
        }

        if (_model is null)
        {
            _model = await Task.Run(() => UpscaleModel.Load(entry.Path, device));
            _modelKey = (entry.Path, device);
        }
        return _model;
    }

    private static string Friendly(Exception ex) => ex switch
    {
        OnnxRuntimeException => "The GPU couldn't handle this model. Choose a different device or tile size under \"Advanced\", or a lighter model.",
        FileNotFoundException => "File not found. It may have been moved or deleted.",
        UnauthorizedAccessException => "Access to the file or folder was denied. Choose a different output folder.",
        NotSupportedException => ex.Message,
        IOException => $"Couldn't read or write the file. {ex.Message}",
        _ => ex.Message,
    };

    // ── Models ──────────────────────────────────────────────────────

    private void RefreshModels()
    {
        _refreshingModels = true;
        try
        {
            string? selectedPath = SelectedModel?.Entry.Path;
            _allModels.Clear();
            _allModels.AddRange(ModelCatalog.Discover()
                .Select(ModelItem.Create)
                .OrderBy(m => m.Order)
                .ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase));

            var pick = _allModels.FirstOrDefault(m => m.Entry.Path == selectedPath)
                ?? _allModels.FirstOrDefault(m => string.Equals(m.Name, _settings.Model, StringComparison.OrdinalIgnoreCase))
                ?? _allModels.FirstOrDefault(m => string.Equals(m.Name, ModelCatalog.DefaultModelName, StringComparison.OrdinalIgnoreCase))
                ?? _allModels.FirstOrDefault();
            SelectedModel = pick;
            RebuildVisibleModels();
        }
        finally
        {
            _refreshingModels = false;
        }

        OnPropertyChanged(nameof(HasNoModels));
        OnPropertyChanged(nameof(HasMoreModels));
        OnPropertyChanged(nameof(AllModelsToggleText));
        // The main button depends on whether a model is selected.
        CommandManager.InvalidateRequerySuggested();
        if (SelectedModel is { } selected)
            _ = ShowModelDetailsAsync(selected);
    }

    private void RebuildVisibleModels()
    {
        var visible = ShowAllModels ? _allModels.ToList() : _allModels.Take(CompactModelCount).ToList();
        if (SelectedModel is { } selected && !visible.Contains(selected))
            visible.Add(selected);

        _rebuildingModels = true;
        try
        {
            VisibleModels.Clear();
            foreach (var model in visible)
                VisibleModels.Add(model);
        }
        finally
        {
            _rebuildingModels = false;
        }
        // The ListBox dropped the selection when it was cleared, so restore it.
        OnPropertyChanged(nameof(SelectedModel));
    }

    private void OnModelSettingsChanged()
    {
        SaveSelection();
        InvalidateResult();
        if (IsBusy)
            return;

        _model?.Dispose();
        _model = null;
        if (SelectedModel is { } selected && !_refreshingModels)
            _ = ShowModelDetailsAsync(selected);
    }

    private void OnOutputSettingsChanged()
    {
        OnPropertyChanged(nameof(SizePreview));
        OnPropertyChanged(nameof(UpscaleButtonText));
        InvalidateResult();
    }

    /// <summary>The settings changed, so the displayed result no longer matches them.</summary>
    private void InvalidateResult()
    {
        if (State is PanelState.Done or PanelState.Error)
            State = HasFiles ? PanelState.Ready : PanelState.Empty;
    }

    private static async Task ShowModelDetailsAsync(ModelItem item)
    {
        try
        {
            var info = await Task.Run(() => UpscaleModel.Inspect(item.Entry.Path));
            var lines = new List<string>
            {
                Path.GetFileName(item.Entry.Path),
                string.Join(" · ", new[] { info.Architecture, $"×{info.Scale}", info.IsHalfPrecision ? "fp16" : "fp32" }.OfType<string>()),
            };
            if (info.FixedWidth is int fixedTile)
                lines.Add($"Tile {fixedTile} px (set by the model)");
            else if (info.MaximumSize is int maximum)
                lines.Add($"Window up to {maximum} px");
            lines.Add(info.License is { } license ? $"License: {license}" : "License not specified");
            item.Details = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            item.Details = $"{Path.GetFileName(item.Entry.Path)}\nCan't read the model: {ex.Message}";
        }
    }

    private void SaveSelection()
    {
        if (_refreshingModels)
            return;

        if (SelectedModel is not null)
            _settings.Model = SelectedModel.Name;
        var device = SelectedDevice.Value;
        _settings.DeviceKind = device.Kind;
        _settings.DeviceName = device.AdapterName;
        _settings.DeviceIndex = device.Kind == DeviceKind.Gpu ? device.AdapterIndex : null;
        _settings.Save();
    }

    private ExecutionDevice? FindSavedDevice()
    {
        var gpus = GpuAdapters.Enumerate();
        return _settings.DeviceKind switch
        {
            DeviceKind.Cpu => ExecutionDevice.Cpu,
            DeviceKind.Gpu => (gpus.FirstOrDefault(a => a.Name == _settings.DeviceName && a.Index == _settings.DeviceIndex)
                               ?? gpus.FirstOrDefault(a => a.Name == _settings.DeviceName)) is { } adapter
                ? ExecutionDevice.Gpu(adapter)
                : null,
            _ => null,
        };
    }

    // ── Actions ─────────────────────────────────────────────────────

    private void OpenFiles(bool append)
    {
        var extensions = string.Join(";", ImageCodec.InputExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = append ? "Add images" : "Select images",
            Multiselect = true,
            Filter = $"Images|{extensions}|All files|*.*",
        };
        if (dialog.ShowDialog() == true)
            _ = SetFilesAsync(dialog.FileNames, append);
    }

    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog { Title = "Output folder" };
        if (dialog.ShowDialog() == true)
            OutputFolder = dialog.FolderName;
    }

    private static void OpenModelsFolder()
    {
        Directory.CreateDirectory(ModelCatalog.UserDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ModelCatalog.UserDirectory}\"") { UseShellExecute = true });
    }

    private void OpenResult()
    {
        if (Files.Count > 1 && CurrentFile is not { IsDone: true })
        {
            ShowInFolder();
            return;
        }
        if (ResultFile?.OutputPath is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void ShowInFolder()
    {
        if (ResultFile?.OutputPath is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void CopyResult()
    {
        if (After is null)
            return;
        try
        {
            Clipboard.SetImage(After);
            ShowToast("Result copied to the clipboard");
        }
        catch (Exception)
        {
            ShowToast("The clipboard is in use by another app — try again");
        }
    }

    private void ShowToast(string message)
    {
        Toast = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private static string BuildOutputPath(string input, string? folder, double scale, ImageFileFormat format)
    {
        string directory = folder ?? Path.GetDirectoryName(input)!;
        string suffix = "_x" + scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(input) + suffix + ImageCodec.GetExtension(format));
    }
}
