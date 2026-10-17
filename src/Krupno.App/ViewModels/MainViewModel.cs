using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Win32;
using Krupno.App.Controls;
using Krupno.App.Infrastructure;
using Krupno.App.Properties;
using Krupno.Core.Imaging;
using Krupno.Core.Inference;
using Krupno.Core.Localization;
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
    /// <summary>The ranges of the quality and strength sliders.</summary>
    private const int MinJpegQuality = 60, MaxJpegQuality = 100;
    private const int MinStrength = 10, MaxStrength = 100;
    /// <summary>The tile overlap of "Seamless tiles": 64 px leaves no visible seams even with deep models (docs/how-it-works.md).</summary>
    private const int SeamlessPadding = 64;
    /// <summary>The limits of "Other", the same as the command-line tool's.</summary>
    private const double MaxCustomScale = 64;
    private const int MaxTargetPixels = 1_000_000;
    /// <summary>Files from outside that arrive closer together than this are one Explorer selection.</summary>
    private static readonly TimeSpan ExternalSelectionGap = TimeSpan.FromSeconds(2);

    private readonly AppSettings _settings;
    private readonly List<ModelItem> _allModels = [];
    private readonly DispatcherTimer _toastTimer = new();
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
    private int _strength = MaxStrength;
    private Option<double> _selectedScale;
    private readonly Option<double> _customScale = new(0, Strings.Scale_Other);
    private Option<CustomScaleMode> _selectedCustomMode;
    private string _customValueText;
    private Option<ImageFileFormat> _selectedFormat;
    private int _jpegQuality = 92;
    private string? _outputFolder;
    private Option<ExecutionDevice> _selectedDevice;
    private Option<int> _selectedTile;
    private bool _seamlessTiles;
    private bool _testTimeAugmentation;
    private bool _checkForUpdates;
    private readonly (bool Enabled, string SetAt)? _installerUpdateChoice;
    /// <summary>
    /// A newer release that github.com named at startup (see UpdateCheck), or the one "Update" didn't install; null if there is
    /// none, or it was hidden.
    /// </summary>
    private (Version Version, string Page)? _update;
    private UpdateStage _updateStage;
    private double _updateProgress;
    private string? _updateError;
    /// <summary>The download "Update" started; null when there is none.</summary>
    private CancellationTokenSource? _updateCts;
    /// <summary>
    /// From "Update" until Setup has taken over or the update has failed: no run starts and no second update, whatever the line
    /// shows (it can be hidden meanwhile by turning the update check off).
    /// </summary>
    private bool _updating;
    /// <summary>How this copy was installed (see UpdateInstall): only an installed one updates itself; null for the ZIP and a build.</summary>
    private readonly bool? _installedForAllUsers = UpdateInstall.InstalledForAllUsers();
    private IReadOnlyList<Option<ExecutionDevice>> _devices;
    private IReadOnlyList<AcceleratorPackage> _acceleratorPackages = [];
    /// <summary>Whether the accelerators are known yet: until then a saved accelerator must not be overwritten by "Auto".</summary>
    private bool _acceleratorsLoaded;
    private bool _installingAccelerators;
    /// <summary>The run going on, or the last one: closing the window waits for it (see <see cref="CancelRunAsync"/>).</summary>
    private Task _run = Task.CompletedTask;
    private string? _acceleratorStatus;
    private Option<string?> _selectedLanguage;
    /// <summary>The language the window was started in: a different choice takes effect after a restart.</summary>
    private readonly string? _startupLanguage;
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
    private string? _errorReport;
    /// <summary>The fallback to the CPU and an accelerator that can't compile the model are logged once per run.</summary>
    private bool _fallbackReported;
    private bool _compileFailureReported;
    /// <summary>When files last came from outside the window, see <see cref="OpenExternalAsync"/>.</summary>
    private DateTime _lastExternalOpen = DateTime.MinValue;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        // The vendors' accelerators are added once Windows ML has registered them (LoadAcceleratorsAsync, in the background).
        _devices = BuildDevices([]);
        var savedDevice = FindSavedDevice([]);
        _selectedDevice = _devices.FirstOrDefault(d => d.Value == savedDevice) ?? _devices[0];

        // The choices from the last session; a value the window doesn't offer (an edited settings file) falls back to the default.
        if (_settings.Strength is int strength)
            _strength = Math.Clamp(strength, MinStrength, MaxStrength);
        // "Other" is saved as scale 0, with its mode and number alongside.
        Scales = [new(2, "×2"), new(3, "×3"), new(4, "×4"), new(8, "×8"), _customScale];
        _selectedScale = Scales.FirstOrDefault(s => s.Value == _settings.Scale) ?? Scales[2];
        CustomModes =
        [
            new(CustomScaleMode.Scale, Strings.CustomMode_Scale), new(CustomScaleMode.Width, Strings.CustomMode_Width),
            new(CustomScaleMode.Height, Strings.CustomMode_Height), new(CustomScaleMode.LongSide, Strings.CustomMode_LongSide),
        ];
        _selectedCustomMode = CustomModes.FirstOrDefault(m => m.Value == _settings.CustomScaleMode) ?? CustomModes[3];
        _customValueText = _settings.CustomScaleValue ?? DefaultCustomValue(_selectedCustomMode.Value);

        Formats = [new(ImageFileFormat.Png, "PNG"), new(ImageFileFormat.Jpeg, "JPEG"), new(ImageFileFormat.Tiff, "TIFF")];
        _selectedFormat = Formats.FirstOrDefault(f => f.Value == _settings.Format) ?? Formats[0];
        if (_settings.JpegQuality is int quality)
            _jpegQuality = Math.Clamp(quality, MinJpegQuality, MaxJpegQuality);
        _outputFolder = _settings.OutputFolder;

        TileSizes =
        [
            new(384, Strings.Auto), .. new[] { 256, 512, 768, 1024 }.Select(size => new Option<int>(size, string.Format(Strings.Pixels, size))),
            new(0, Strings.Tile_None),
        ];
        _selectedTile = TileSizes.FirstOrDefault(t => t.Value == _settings.TileSize) ?? TileSizes[0];
        _seamlessTiles = _settings.SeamlessTiles == true;
        _testTimeAugmentation = _settings.TestTimeAugmentation == true;
        // The user's own choice in the window, else the installer's, else on (the portable ZIP); a newer installation that
        // turned the check off wins over the user's choice.
        _installerUpdateChoice = UpdateCheck.InstallerChoice();
        bool? updateChoice = _settings.CheckForUpdates;
        string? updateChoiceSetAt = _settings.CheckForUpdatesSetAt;
        _checkForUpdates = UpdateCheck.Decide(ref updateChoice, ref updateChoiceSetAt, _installerUpdateChoice);
        if (updateChoice != _settings.CheckForUpdates || updateChoiceSetAt != _settings.CheckForUpdatesSetAt)
        {
            _settings.CheckForUpdates = updateChoice;
            _settings.CheckForUpdatesSetAt = updateChoiceSetAt;
            _settings.Save();
        }
        Languages = [new(null, Strings.Language_Windows), .. UiLanguage.All.Select(l => new Option<string?>(l.Culture, l.Name))];
        _selectedLanguage = Languages.FirstOrDefault(l => l.Value == _settings.Language) ?? Languages[0];
        _startupLanguage = _selectedLanguage.Value;

        PrimaryCommand = new RelayCommand(() =>
        {
            if (State == PanelState.Empty)
                OpenFiles(append: false);
            else
                StartRun();
        }, () => !IsBusy && (State == PanelState.Empty || (CanStartRun && SelectedModel is not null && ScaleChoice is not null)));
        OpenCommand = new RelayCommand(() => OpenFiles(append: false), () => !IsBusy);
        AddFilesCommand = new RelayCommand(() => OpenFiles(append: true), () => !IsBusy);
        ClearFilesCommand = new RelayCommand(ClearFiles, () => !IsBusy && HasFiles);
        UpscaleCommand = new RelayCommand(StartRun,
            () => !IsBusy && CanStartRun && HasFiles && SelectedModel is not null && ScaleChoice is not null);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        OpenResultCommand = new RelayCommand(OpenResult, () => ResultFile is not null);
        ShowInFolderCommand = new RelayCommand(ShowInFolder, () => ResultFile is not null);
        CopyResultCommand = new RelayCommand(CopyResult, () => After is not null);
        PasteCommand = new RelayCommand(() => _ = PasteAsync(), () => !IsBusy);
        CopyErrorDetailsCommand = new RelayCommand(CopyErrorDetails, () => ErrorReport is not null);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        ResetOutputCommand = new RelayCommand(() => OutputFolder = null, () => !IsBusy && OutputFolder is not null);
        RefreshModelsCommand = new RelayCommand(RefreshModels, () => !IsBusy);
        OpenModelsFolderCommand = new RelayCommand(OpenModelsFolder);
        ToggleAllModelsCommand = new RelayCommand(() => ShowAllModels = !ShowAllModels);
        InstallAcceleratorsCommand = new RelayCommand(() => _ = InstallAcceleratorsAsync(),
            () => !IsBusy && !_installingAccelerators && MissingAccelerators.Count > 0);
        InstallUpdateCommand = new RelayCommand(() => _ = InstallUpdateAsync(),
            () => !IsBusy && !_updating && _update is not null && CanInstallUpdates && _updateStage is UpdateStage.Available or UpdateStage.Failed);
        OpenUpdateCommand = new RelayCommand(OpenUpdate, () => _update is not null);
        HideUpdateCommand = new RelayCommand(HideUpdate, () => _updateStage != UpdateStage.Installing);
        OpenSetupLogCommand = new RelayCommand(OpenSetupLog, () => UpdateSetupLog is not null);

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = null;
        };

        _ = LoadAcceleratorsAsync();
        FinishUpdate();
        _ = CleanUpUpdatesAsync();
        if (_checkForUpdates)
            _ = CheckForUpdatesAsync();

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
    public string AllModelsToggleText => ShowAllModels ? Strings.Models_ShowFewer : string.Format(Strings.Models_All, _allModels.Count);

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

    /// <summary>How much of the network's result goes into the output, in percent; the rest is a plain resize of the original.</summary>
    public int Strength
    {
        get => _strength;
        set
        {
            if (SetProperty(ref _strength, value))
            {
                OnPropertyChanged(nameof(StrengthText));
                OnOutputSettingsChanged();
            }
        }
    }

    public string StrengthText => string.Format(Strings.Percent, Strength);

    // ── Scale and saving ────────────────────────────────────────────

    public IReadOnlyList<Option<double>> Scales { get; }

    public Option<double> SelectedScale
    {
        get => _selectedScale;
        set
        {
            if (value is not null && SetProperty(ref _selectedScale, value))
            {
                OnPropertyChanged(nameof(IsCustomScale));
                OnOutputSettingsChanged();
            }
        }
    }

    /// <summary>"Other": a scale of the user's own, or a length for one side of the result.</summary>
    public bool IsCustomScale => SelectedScale == _customScale;

    public IReadOnlyList<Option<CustomScaleMode>> CustomModes { get; }

    public Option<CustomScaleMode> SelectedCustomMode
    {
        get => _selectedCustomMode;
        set
        {
            if (value is null || !SetProperty(ref _selectedCustomMode, value))
                return;
            // "3840" means nothing as a scale, and "2.5" nothing as a size: start the new mode from its own example.
            if (ScaleChoice is null)
                CustomValueText = DefaultCustomValue(value.Value);
            OnOutputSettingsChanged();
        }
    }

    public string CustomValueText
    {
        get => _customValueText;
        set
        {
            if (SetProperty(ref _customValueText, value ?? ""))
            {
                OnOutputSettingsChanged();
                // The buttons are disabled while the number is not valid.
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>The scale and target size chosen under "Scale"; null while "Other" holds something that isn't a valid number.</summary>
    private (double Scale, ScaleTarget? Target)? ScaleChoice
    {
        get
        {
            if (!IsCustomScale)
                return (SelectedScale.Value, null);

            string text = CustomValueText.Trim();
            var mode = SelectedCustomMode.Value;
            if (mode == CustomScaleMode.Scale)
            {
                // "2,5" as well as "2.5": a decimal comma is what many keyboards type.
                return double.TryParse(text.TrimStart('×', 'x', 'X').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
                           out double scale) && scale is > 0 and <= MaxCustomScale
                    ? (scale, null)
                    : null;
            }

            var side = mode switch
            {
                CustomScaleMode.Width => TargetSide.Width,
                CustomScaleMode.Height => TargetSide.Height,
                _ => TargetSide.LongSide,
            };
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pixels) && pixels is > 0 and <= MaxTargetPixels
                ? (0, new ScaleTarget(side, pixels))
                : null;
        }
    }

    public string SizePreview
    {
        get
        {
            if (ScaleChoice is not { } choice)
            {
                return SelectedCustomMode.Value == CustomScaleMode.Scale
                    ? string.Format(Strings.SizePreview_EnterScale, MaxCustomScale)
                    : Strings.SizePreview_EnterSize;
            }
            if (_sourceSize is not { } size)
                return Strings.SizePreview_SelectFile;

            double scale = choice.Target?.ScaleFor(size.Width, size.Height) ?? choice.Scale;
            var (width, height) = Upscaler.OutputSize(size.Width, size.Height, scale);
            string preview = $"{Text.Size(size.Width, size.Height)}  →  {Text.Size((int)width, (int)height)}";
            // With a target size the scale differs from image to image, and above the model's own it takes more passes.
            return choice.Target is null ? preview : $"{preview}  ({Text.Scale(scale)})";
        }
    }

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

    public string OutputFolderDisplay => OutputFolder ?? Strings.OutputFolder_NextToOriginal;
    public bool HasCustomOutputFolder => OutputFolder is not null;

    // ── Settings ────────────────────────────────────────────────────

    public IReadOnlyList<Option<ExecutionDevice>> Devices
    {
        get => _devices;
        private set => SetProperty(ref _devices, value);
    }

    /// <summary>The vendors' accelerators Windows ML offers for this computer and doesn't have yet.</summary>
    private List<AcceleratorPackage> MissingAccelerators => [.. _acceleratorPackages.Where(p => !p.IsInstalled)];

    /// <summary>Under the device list: which accelerators Windows can add, while there are any or one is being downloaded.</summary>
    public bool HasAcceleratorOffer => MissingAccelerators.Count > 0 || _installingAccelerators;

    public string? AcceleratorOffer => MissingAccelerators is { Count: > 0 } missing
        ? string.Format(Strings.Accelerators_Available, string.Join(", ", missing.Select(p => p.Name)))
        : null;

    /// <summary>The download in progress: "Downloading TensorRT-RTX… 45%".</summary>
    public string? AcceleratorStatus
    {
        get => _acceleratorStatus;
        private set => SetProperty(ref _acceleratorStatus, value);
    }

    public Option<ExecutionDevice> SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value is not null && SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(SettingsSummary));
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
                OnPropertyChanged(nameof(SettingsSummary));
                OnOutputSettingsChanged();
            }
        }
    }

    /// <summary>Twice the overlap between tiles: no visible seams, but more tiles to run.</summary>
    public bool SeamlessTiles
    {
        get => _seamlessTiles;
        set
        {
            if (SetProperty(ref _seamlessTiles, value))
            {
                OnPropertyChanged(nameof(SettingsSummary));
                OnOutputSettingsChanged();
            }
        }
    }

    /// <summary>Every pass in all 8 orientations, averaged: a little cleaner, eight times slower.</summary>
    public bool TestTimeAugmentation
    {
        get => _testTimeAugmentation;
        set
        {
            if (SetProperty(ref _testTimeAugmentation, value))
            {
                OnPropertyChanged(nameof(SettingsSummary));
                OnOutputSettingsChanged();
            }
        }
    }

    /// <summary>
    /// "Check for updates" in "Settings" (see UpdateCheck). Turned on, it checks right away; turned off, the notice goes, and so
    /// does a download "Update" started.
    /// </summary>
    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set
        {
            if (!SetProperty(ref _checkForUpdates, value))
                return;
            OnPropertyChanged(nameof(SettingsSummary));
            // Kept as the user's own choice from now on: it overrides the installer's, until an installation turns it off.
            _settings.CheckForUpdates = value;
            _settings.CheckForUpdatesSetAt = _installerUpdateChoice?.SetAt;
            SaveSettings();
            if (value)
            {
                _ = CheckForUpdatesAsync();
            }
            else
            {
                // A download stops; Setup that has started goes on, and its line stays until the window closes or it fails.
                _updateCts?.Cancel();
                if (_updateStage != UpdateStage.Installing)
                    SetUpdate(null, UpdateStage.None);
            }
        }
    }

    /// <summary>"Krupno 0.2.2" next to "Check for updates" in "Settings".</summary>
    public string VersionText => $"Krupno {UpdateCheck.Own.ToString(3)}";

    public IReadOnlyList<Option<string?>> Languages { get; }

    public Option<string?> SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is not null && SetProperty(ref _selectedLanguage, value))
            {
                OnPropertyChanged(nameof(LanguageNeedsRestart));
                OnPropertyChanged(nameof(SettingsSummary));
                SaveSettings();
            }
        }
    }

    /// <summary>The window's text is set when it is created, so another language shows only after a restart.</summary>
    public bool LanguageNeedsRestart => SelectedLanguage.Value != _startupLanguage;

    /// <summary>
    /// In the tooltip of the settings button, under its name: what is set there, "RTX 3080 · auto tiles · English", and "no
    /// update check" when that is off.
    /// </summary>
    public string SettingsSummary
    {
        get
        {
            var device = SelectedDevice.Value.Resolve();
            string tile = SelectedTile.Value switch
            {
                0 => Strings.Summary_NoTiling,
                384 when SelectedTile == TileSizes[0] => Strings.Summary_AutoTiles,
                var size => string.Format(Strings.Summary_Tiles, size),
            };
            return $"{Text.Device(device)} · {tile}{(SeamlessTiles ? ", " + Strings.Summary_Seamless : "")}{(TestTimeAugmentation ? ", TTA" : "")}" +
                   $" · {LanguageName}{(CheckForUpdates ? "" : " · " + Strings.Summary_NoUpdateCheck)}";
        }
    }

    /// <summary>The chosen language's own name; with "Windows language", the one the window speaks.</summary>
    private string LanguageName => UiLanguage.All.First(l => l.Culture == LanguageCulture).Name;

    /// <summary>The chosen language; with "Windows language", the one the window speaks.</summary>
    private string LanguageCulture
    {
        get
        {
            if (SelectedLanguage.Value is { } chosen)
                return chosen;
            // The translation .NET picks: the UI culture or the nearest parent with one ("ru-RU" → "ru", "zh-CN" → "zh-Hans").
            for (var culture = CultureInfo.CurrentUICulture; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
            {
                if (UiLanguage.All.Any(l => l.Culture == culture.Name))
                    return culture.Name;
            }
            return UiLanguage.All[0].Culture;
        }
    }

    // ── Update line ─────────────────────────────────────────────────

    /// <summary>What the update line above the settings shows.</summary>
    private enum UpdateStage
    {
        None,
        /// <summary>"Krupno 0.3.0 is available", with "Update" (or "Download" without an installation).</summary>
        Available,
        Downloading,
        /// <summary>Setup has started: the window closes once it has taken over (UpdateInstall.WaitForSetupAsync).</summary>
        Installing,
        /// <summary>Not downloaded, not started or not installed: why, with "Try again".</summary>
        Failed,
    }

    /// <summary>The line above the settings: a newer release, its download, or why it isn't installed; null while there is nothing to say.</summary>
    public string? UpdateText => (_updateStage, _update) switch
    {
        (UpdateStage.Available, { } update) => string.Format(Strings.Update_Available, update.Version.ToString(3)),
        (UpdateStage.Downloading, { } update) => string.Format(Strings.Update_Downloading, update.Version.ToString(3), (int)(_updateProgress * 100)),
        (UpdateStage.Installing, { } update) => string.Format(Strings.Update_Installing, update.Version.ToString(3)),
        (UpdateStage.Failed, not null) => _updateError,
        _ => null,
    };

    public double UpdateProgress => _updateProgress;
    public bool IsDownloadingUpdate => _updateStage == UpdateStage.Downloading;
    public bool IsInstallingUpdate => _updateStage == UpdateStage.Installing;
    /// <summary>The download's progress, then an indeterminate bar while Setup starts (a UAC prompt may be waiting).</summary>
    public bool ShowUpdateProgress => _updateStage is UpdateStage.Downloading or UpdateStage.Installing;
    public bool IsUpdateFailed => _updateStage == UpdateStage.Failed;
    /// <summary>An installed copy updates itself; the ZIP and a build only link to the release page.</summary>
    private bool CanInstallUpdates => _installedForAllUsers is not null;
    public bool ShowInstallUpdate => _updateStage == UpdateStage.Available && CanInstallUpdates;
    public bool ShowDownloadUpdate => _updateStage == UpdateStage.Available && !CanInstallUpdates;
    public bool ShowRetryUpdate => _updateStage == UpdateStage.Failed && CanInstallUpdates;
    /// <summary>The release page, with what has changed; "Download" leads there already.</summary>
    public bool ShowWhatsNew => _updateStage == UpdateStage.Failed || ShowInstallUpdate;
    /// <summary>The ✕ on the line: hides it, or cancels the download.</summary>
    public string UpdateCloseText => IsDownloadingUpdate ? Strings.Update_CancelDownload : Strings.Update_Hide;

    /// <summary>Setup's log of an update that didn't install, if it wrote one.</summary>
    public string? UpdateSetupLog => _updateStage == UpdateStage.Failed && _update is { } update
                                     && UpdateInstall.SetupLogPath(update.Version) is var path && File.Exists(path)
        ? path
        : null;

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

    public string UpscaleButtonText
    {
        get
        {
            string? choice = ScaleChoice is { } c ? Text.ScaleChoice(c.Scale, c.Target) : null;
            return (Files.Count > 1, choice) switch
            {
                (true, null) => string.Format(Strings.Upscale_Files, Text.Files(Files.Count)),
                (true, _) => string.Format(Strings.Upscale_FilesScale, Text.Files(Files.Count), choice),
                (false, null) => Strings.Upscale,
                _ => string.Format(Strings.Upscale_Scale, choice),
            };
        }
    }

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

    /// <summary>The last run's errors for a bug report (see <see cref="BuildErrorReport"/>); null if it had none.</summary>
    private string? ErrorReport
    {
        get => _errorReport;
        set
        {
            if (SetProperty(ref _errorReport, value))
            {
                OnPropertyChanged(nameof(HasErrorReport));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasErrorReport => _errorReport is not null;

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
    public ICommand PasteCommand { get; }
    public ICommand CopyErrorDetailsCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand ResetOutputCommand { get; }
    public ICommand RefreshModelsCommand { get; }
    public ICommand OpenModelsFolderCommand { get; }
    public ICommand ToggleAllModelsCommand { get; }
    public ICommand InstallAcceleratorsCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand OpenUpdateCommand { get; }
    public ICommand HideUpdateCommand { get; }
    public ICommand OpenSetupLogCommand { get; }

    /// <summary>"Update" has started Setup: the window closes, and Setup starts Krupno again when it is done.</summary>
    public event EventHandler? ExitRequested;

    private FileItem? ResultFile => CurrentFile is { IsDone: true } current ? current : _lastDone;

    /// <summary>
    /// Files from outside the window: its command line, or another Krupno.exe that handed its files over (see SingleInstance).
    /// Explorer starts a Krupno.exe for every selected file, a moment apart, so files that arrive within a couple of seconds of
    /// each other are one selection and share one queue; a later selection replaces the queue, like opening files.
    /// </summary>
    public async Task OpenExternalAsync(string[] paths, DateTime arrivedAt)
    {
        bool append = arrivedAt - _lastExternalOpen < ExternalSelectionGap;
        _lastExternalOpen = arrivedAt;
        if (IsBusy)
        {
            Log.Warning($"Processing is running, files not opened: {string.Join(", ", paths)}");
            ShowToast(Strings.Toast_BusyNotOpened);
            return;
        }
        await SetFilesAsync(paths, append);
    }

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
                    ShowToast(string.Format(Strings.Toast_CantReadSelection, ex.Message));
                return;
            }
            // Processing may have started, or the list may have been cleared, while the folder was being read.
            if (IsBusy || selection != _selectionGeneration)
                return;
            if (found.Count == 0)
            {
                ShowToast(Strings.Toast_NoImagesInSelection);
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
        _updateCts?.Cancel();
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
            var before = await Task.Run(() => LoadPreview(file.Path), cancellationToken);
            BitmapSource? after = null;
            string? afterError = null;
            if (file.IsDone && file.OutputExists)
            {
                try
                {
                    after = await Task.Run(() => LoadPreview(file.OutputPath!), cancellationToken);
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
            {
                Log.Warning($"Can't show the result {file.OutputPath}: {afterError}");
                ShowToast(string.Format(Strings.Toast_CantShowResult, afterError));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"Can't show {file.Path}", ex);
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

    /// <summary>A file decoded for display only: the decoded pixels aren't needed afterwards, so the color conversion reuses them.</summary>
    private static BitmapSource LoadPreview(string path)
    {
        var loaded = ImageCodec.Load(path);
        return ImageCodec.ToBitmapSource(loaded.Image, loaded, convertInPlace: true);
    }

    private void SetSourceSize((int Width, int Height)? size)
    {
        _sourceSize = size;
        OnPropertyChanged(nameof(SizePreview));
    }

    // ── Processing ──────────────────────────────────────────────────

    /// <summary>A run came to an end by itself, done or failed (not canceled): the user may want to know if they looked away.</summary>
    public event EventHandler? RunFinished;

    /// <summary>Cancels the run, if one is going, and completes once it has stopped, with a result being written finished.</summary>
    public Task CancelRunAsync()
    {
        _cts?.Cancel();
        return _run;
    }

    /// <summary>
    /// While an accelerator is being downloaded and registered with ONNX Runtime, no run creates sessions; while "Update" downloads
    /// the installer and starts Setup, none starts, as the window closes when it is done.
    /// </summary>
    private bool CanStartRun => !_installingAccelerators && !_updating;

    private void StartRun()
    {
        // Setup is replacing this program's files (an update another Krupno started, or one run by hand), and would close the
        // window in the middle of the run.
        if (RunningMarker.SetupRunning())
        {
            Log.Info("Not started: Setup of Krupno is running");
            ShowToast(Strings.SetupRunning, TimeSpan.FromSeconds(5));
            return;
        }
        _run = UpscaleAsync();
    }

    private async Task UpscaleAsync()
    {
        if (IsBusy || !CanStartRun || !HasFiles || SelectedModel is not { } modelItem || ScaleChoice is not { } choice)
            return;

        var files = Files.ToList();
        var deviceOption = SelectedDevice;
        var settings = (Scale: choice.Scale, Target: choice.Target, Tile: SelectedTile.Value,
            Padding: SeamlessTiles ? SeamlessPadding : UpscaleOptions.Default.TilePadding, Strength: Strength,
            Tta: TestTimeAugmentation,
            Format: SelectedFormat.Value, Quality: JpegQuality, Folder: OutputFolder);
        // All names up front: a result must not overwrite a queued source ("photo.png" → "photo_x4.png" while
        // "photo_x4.png" waits in the queue) or another result ("photo.jpg" and "photo.png" → "photo_x4.png").
        var outputs = OutputPaths.MakeUnique(files.Select(f => f.Path).ToList(),
            files.Select(f => BuildOutputPath(f.Path, settings.Folder, settings.Scale, settings.Target, settings.Format)).ToList());
        foreach (var file in files)
        {
            file.State = FileState.Pending;
            file.Error = null;
        }

        IsBusy = true;
        State = PanelState.Busy;
        Notice = null;
        ProgressNote = null;
        ProgressTitle = Strings.Progress_PreparingModel;
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
        // The previous file's result while it is written in the background, see BatchSaving.
        Task? pendingSave = null;
        // The run may take many minutes without any input. The request is released on this (UI) thread, which made it:
        // the awaits below come back to it.
        using var keepAwake = new KeepAwake();
        // What went wrong, for the log and "Copy error details": the file (null for the run as a whole) and the error.
        var errors = new List<(string? File, Exception Error)>();
        UpscaleModel? runModel = null;
        bool finished = false;
        ErrorReport = null;
        _fallbackReported = false;
        _compileFailureReported = false;

        try
        {
            if (settings.Folder is { } folder)
            {
                // The folder may come from an earlier session, on a drive that is gone now: fail before the work, not after it.
                try
                {
                    await Task.Run(() => Directory.CreateDirectory(folder));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        string.Format(Strings.Error_OutputFolder, ex.Message), ex);
                }
            }

            var model = await GetModelAsync();
            runModel = model;
            Log.Info("Run started\n" + DescribeRun());
            var upscaler = new Upscaler(model);
            var options = new UpscaleOptions
            {
                Scale = settings.Scale, Target = settings.Target, TileSize = settings.Tile, TilePadding = settings.Padding,
                Strength = settings.Strength / 100.0, TestTimeAugmentation = settings.Tta,
            };

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
                    Log.Info($"File {index + 1} of {files.Count}: {file.Path} ({Text.Size(loaded.Image.Width, loaded.Image.Height)})");
                    // The user may be looking at another file of the batch meanwhile: the canvas shows only the selected one,
                    // so every step checks the selection again after an await.
                    if (CurrentFile == file)
                    {
                        // Not in place: these pixels are upscaled next, as stored.
                        var before = await Task.Run(() => ImageCodec.ToBitmapSource(loaded.Image, loaded), cancellationToken);
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

                    // One save at a time (see BatchSaving): the previous file is finished first.
                    if (pendingSave is not null)
                        await pendingSave;
                    pendingSave = null;

                    // Not canceled with the run: the result is computed, so it is written whatever happens next.
                    string output = outputs[index];
                    var save = Task.Run(() => ImageCodec.Save(result, output, settings.Format,
                        new SaveOptions { Source = loaded, JpegQuality = settings.Quality }));
                    if (index < files.Count - 1 && BatchSaving.CanSaveInBackground(result))
                    {
                        pendingSave = SaveInBackgroundAsync(file, save, output, result, loaded);
                        continue;
                    }
                    await save;
                    await OnSavedAsync(file, output, result, loaded);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    OnFailed(file, ex);
                    if (files.Count == 1)
                        throw;
                }
            }
            if (pendingSave is not null)
                await pendingSave;

            string elapsed = Text.Seconds(total.Elapsed);
            Log.Info($"Run finished in {Text.Seconds(total.Elapsed, Log.Culture)}: {succeeded} of {Text.Files(files.Count, Log.Culture)} done");
            if (succeeded == 0)
            {
                // Every file of the batch failed: that is not a success, whatever the count.
                ErrorDetail = Plurals.Format(Strings.Error_NoneUpscaled, files.Count, null, firstError);
                ErrorTechnical = firstError;
                State = PanelState.Error;
                TaskbarState = TaskbarItemProgressState.Error;
            }
            else
            {
                if (files.Count == 1)
                {
                    DoneTitle = string.Format(Strings.Done_InTime, elapsed, Text.Bytes(lastBytes));
                    DoneSubtitle = After is { } after
                        ? $"{Path.GetFileName(_lastDone!.OutputPath)} · {Text.Size(after.PixelWidth, after.PixelHeight)}"
                        : Path.GetFileName(_lastDone!.OutputPath)!;
                }
                else
                {
                    DoneTitle = failed == 0
                        ? string.Format(Strings.Done_Files, Text.Files(succeeded), elapsed)
                        : string.Format(Strings.Done_Partial, succeeded, files.Count, failed);
                    DoneSubtitle = settings.Folder ?? Strings.Done_SavedNextToOriginals;
                }
                State = PanelState.Done;
                TaskbarState = failed > 0 ? TaskbarItemProgressState.Error : TaskbarItemProgressState.None;
            }
            finished = true;
        }
        catch (OperationCanceledException)
        {
            // A result already being written is finished: it is computed, and its file counts as done.
            if (pendingSave is not null)
                await pendingSave;
            Log.Info("Run canceled");
            foreach (var file in files.Where(f => f.IsProcessing))
                file.State = FileState.Pending;
            Tiles = null;
            State = PanelState.Ready;
            Notice = Strings.Notice_Canceled;
            TaskbarState = TaskbarItemProgressState.None;
        }
        catch (Exception ex)
        {
            // A single file's error is rethrown here after it is recorded.
            if (!errors.Any(e => e.Error == ex))
            {
                errors.Add((null, ex));
                Log.Error("Run failed", ex);
            }
            Tiles = null;
            ErrorDetail = Friendly(ex);
            ErrorTechnical = ex.Message;
            State = PanelState.Error;
            TaskbarState = TaskbarItemProgressState.Error;
            finished = true;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            ProgressNote = null;
            if (errors.Count > 0)
                ErrorReport = BuildErrorReport(DescribeRun(), errors);
        }
        if (finished)
            RunFinished?.Invoke(this, EventArgs.Empty);

        // The model, the device and the settings, for the log and the error report.
        string DescribeRun()
        {
            var info = runModel?.Info;
            string modelLine = info is null
                ? $"Model: {modelItem.Name} · {modelItem.Entry.Path}"
                : $"Model: {info}{(info.IsHalfPrecision ? ", fp16" : "")} · {info.Path}";
            string device = runModel?.Device.ToString() ?? deviceOption.Label;
            string fallback = runModel?.FallbackReason is { } reason ? $"; DirectML is unavailable: {reason}" : "";
            string tile = $"{(settings.Tile == 0 ? "no tiling" : $"tile {settings.Tile} px")}, overlap {settings.Padding} px" +
                          (settings.Tta ? ", TTA" : "");
            string format = settings.Format == ImageFileFormat.Jpeg ? $"JPEG {settings.Quality}" : settings.Format.ToString().ToUpperInvariant();
            return $"{modelLine}\nDevice: {device} (selected: {deviceOption.Label}{fallback}) · {tile} · strength {settings.Strength}% · " +
                   $"{Text.ScaleChoice(settings.Scale, settings.Target, Log.Culture)} · {format} · {Text.Files(files.Count, Log.Culture)} → " +
                   (settings.Folder ?? "next to the originals");
        }

        // The file is written: record it, and show the result if its file is the one on the canvas.
        async Task OnSavedAsync(FileItem file, string output, ImageBuffer result, LoadedImage loaded)
        {
            file.OutputPath = output;
            file.State = FileState.Done;
            _lastDone = file;
            lastBytes = new FileInfo(output).Length;
            succeeded++;
            Log.Info($"Saved {output} ({Text.Bytes(lastBytes, Log.Culture)})");

            if (CurrentFile == file)
            {
                Tiles = null;
                BitmapSource? after = null;
                try
                {
                    // The result is saved, so its pixels can be converted for display in place.
                    after = await Task.Run(() => ImageCodec.ToBitmapSource(result, loaded, convertInPlace: true));
                }
                catch (OutOfMemoryException)
                {
                    // The result is saved; without a preview the canvas just shows the original.
                }
                if (CurrentFile == file)
                    After = after;
            }
        }

        void OnFailed(FileItem file, Exception ex)
        {
            errors.Add((file.Path, ex));
            Log.Error($"Failed: {file.Path}", ex);
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
        }

        // Awaited before the next save and at the end, so it never throws: a failure is recorded on its own file.
        async Task SaveInBackgroundAsync(FileItem file, Task save, string output, ImageBuffer result, LoadedImage loaded)
        {
            try
            {
                await save;
                await OnSavedAsync(file, output, result, loaded);
            }
            catch (Exception ex)
            {
                OnFailed(file, ex);
            }
        }
    }

    private void OnProgress(UpscaleProgress p, FileItem file, int index, int count, UpscaleModel model, TileProgress tiles,
        Stopwatch work)
    {
        // The session is created lazily, and DirectML may have just fallen back to the CPU: say so, the run is much slower then.
        string device = Text.Device(model.Device);
        if (model.FallbackReason is { } reason)
        {
            ProgressNote ??= Strings.Note_DirectMLUnavailable;
            if (!_fallbackReported)
                Log.Warning($"DirectML is unavailable, running on the CPU: {reason}");
            _fallbackReported = true;
        }
        if (model.CompileFailure is { } compileFailure && !_compileFailureReported)
        {
            _compileFailureReported = true;
            Log.Warning($"{model.Device} can't compile the model for the cache, so it builds the engine on every start: {compileFailure}");
        }

        if (p.Message is { } message)
        {
            // A warning stays visible for the rest of the run; a stage ("Resizing to …") only replaces the detail line.
            if (p.IsWarning)
            {
                ProgressNote = message;
                Log.Warning(message);
            }
            else
                ProgressDetail = count > 1 ? $"{string.Format(Strings.Progress_File, index + 1, count)} · {message}" : message;
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
        ProgressTitle = string.Format(Strings.Progress_Percent, Math.Round(fraction * 100));

        var elapsed = work.Elapsed;
        EtaText = fraction > 0.02 && elapsed.TotalSeconds > 1.5
            ? Text.Remaining(elapsed * ((1 - fraction) / fraction))
            : "";

        string tileText = p.TileCount > 0 ? string.Format(Strings.Progress_Tile, p.Tile, p.TileCount) : "";
        if (p.RoundCount > 1)
            tileText += " · " + string.Format(Strings.Progress_Orientation, p.Round, p.RoundCount);
        string pass = p.PassCount > 1 ? " · " + string.Format(Strings.Progress_Pass, p.Pass, p.PassCount) : "";
        ProgressDetail = count > 1
            ? $"{string.Format(Strings.Progress_File, index + 1, count)} · {tileText}{pass}"
            : $"{(tileText.Length > 0 ? char.ToUpper(tileText[0], CultureInfo.CurrentCulture) + tileText[1..] : "")}{pass} · {device}";
    }

    /// <summary>
    /// Finished tiles of the current pass for the overlay. Tiles finish row by row, so complete rows are merged into one
    /// rectangle: the overlay holds at most a row of tiles plus one, instead of growing with every tile (quadratic work on large images).
    /// With test-time augmentation the image is turned, so a row of its tiles may be a column, and in any direction; the finished
    /// rows or columns still make one rectangle along an edge.
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
            // A tile out of line with the row starts a new one, so the previous row is complete. The first two tiles of a row
            // tell whether it runs across (the same top) or down (the same left).
            bool inRow = _row.Count switch
            {
                0 => true,
                1 => tile.Y == _row[0].Y || tile.X == _row[0].X,
                _ => _row[0].Y == _row[1].Y ? tile.Y == _row[0].Y : tile.X == _row[0].X,
            };
            if (!inRow)
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
        var entry = SelectedModel?.Entry ?? throw new InvalidOperationException(Strings.Error_NoModelSelected);
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
        OnnxRuntimeException => Strings.Friendly_Gpu,
        FileNotFoundException => Strings.Friendly_FileNotFound,
        UnauthorizedAccessException => Strings.Friendly_AccessDenied,
        NotSupportedException => ex.Message,
        IOException => string.Format(Strings.Friendly_IO, ex.Message),
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
        SaveSettings();
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
        SaveSettings();
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
                lines.Add(string.Format(Strings.ModelDetails_FixedTile, fixedTile));
            else if (info.MaximumSize is int maximum)
                lines.Add(string.Format(Strings.ModelDetails_MaxWindow, maximum));
            lines.Add(info.License is { } license ? string.Format(Strings.ModelDetails_License, license) : Strings.ModelDetails_NoLicense);
            item.Details = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            Log.Error($"Can't read the model {item.Entry.Path}", ex);
            item.Details = $"{Path.GetFileName(item.Entry.Path)}\n{string.Format(Strings.ModelDetails_CantRead, ex.Message)}";
        }
    }

    private void SaveSettings()
    {
        if (_refreshingModels)
            return;

        if (SelectedModel is not null)
            _settings.Model = SelectedModel.Name;
        var device = SelectedDevice.Value;
        if (_acceleratorsLoaded || _settings.DeviceKind != DeviceKind.Accelerator)
        {
            _settings.DeviceKind = device.Kind;
            _settings.DeviceName = device.AdapterName;
            _settings.DeviceIndex = device.Kind is DeviceKind.Gpu or DeviceKind.Accelerator && device.AdapterIndex >= 0 ? device.AdapterIndex : null;
            _settings.DeviceProvider = device.Provider;
        }
        _settings.Strength = Strength;
        _settings.Scale = SelectedScale.Value;
        _settings.CustomScaleMode = SelectedCustomMode.Value;
        _settings.CustomScaleValue = CustomValueText;
        _settings.Format = SelectedFormat.Value;
        _settings.JpegQuality = JpegQuality;
        _settings.OutputFolder = OutputFolder;
        _settings.TileSize = SelectedTile.Value;
        _settings.SeamlessTiles = SeamlessTiles;
        _settings.TestTimeAugmentation = TestTimeAugmentation;
        _settings.Language = SelectedLanguage.Value;
        _settings.Save();
    }

    private ExecutionDevice? FindSavedDevice(IReadOnlyList<ExecutionDevice> accelerators)
    {
        var gpus = GpuAdapters.Enumerate();
        return _settings.DeviceKind switch
        {
            DeviceKind.Cpu => ExecutionDevice.Cpu,
            // Of two identical cards, the one with the saved index; if the indices changed, the first of that name.
            DeviceKind.Accelerator => accelerators.FirstOrDefault(a => a.Provider == _settings.DeviceProvider && a.AdapterName == _settings.DeviceName
                                                                       && a.AdapterIndex == (_settings.DeviceIndex ?? -1))
                                      ?? accelerators.FirstOrDefault(a => a.Provider == _settings.DeviceProvider && a.AdapterName == _settings.DeviceName),
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
            Title = append ? Strings.Dialog_AddImages : Strings.Dialog_SelectImages,
            Multiselect = true,
            Filter = string.Format(Strings.Dialog_Filter, extensions),
        };
        if (dialog.ShowDialog() == true)
            _ = SetFilesAsync(dialog.FileNames, append);
    }

    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog { Title = Strings.Dialog_OutputFolder };
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
            ShowToast(Strings.Toast_ResultCopied);
        }
        catch (Exception)
        {
            ShowToast(Strings.Toast_ClipboardBusy);
        }
    }

    /// <summary>
    /// Ctrl+V: files copied in Explorer open as if dropped; an image (a screenshot, "Copy image" in a browser) is saved as a PNG
    /// first, see <see cref="PastedImage"/>. Either one replaces the queue, like opening files.
    /// </summary>
    private async Task PasteAsync()
    {
        if (IsBusy)
            return;

        string[]? files = null;
        PastedImage? image = null;
        try
        {
            if (Clipboard.ContainsFileDropList())
                files = [.. Clipboard.GetFileDropList().Cast<string>()];
            else
                image = PastedImage.FromClipboard();
        }
        catch (Exception ex)
        {
            // Nobody awaits this task, so every failure ends up here rather than vanishing.
            Log.Error("Can't paste", ex);
            ShowToast(ex is ExternalException ? Strings.Toast_ClipboardBusy : string.Format(Strings.Toast_CantPaste, ex.Message));
            return;
        }

        if (files is not null)
        {
            await SetFilesAsync(files);
            return;
        }
        if (image is null)
        {
            ShowToast(Strings.Toast_ClipboardEmpty);
            return;
        }

        string path;
        try
        {
            path = await Task.Run(image.Save);
        }
        catch (Exception ex)
        {
            Log.Error("Can't save the pasted image", ex);
            ShowToast(string.Format(Strings.Toast_CantSavePasted, ex.Message));
            return;
        }
        Log.Info($"Pasted image saved to {path}");
        await SetFilesAsync([path]);
        ShowToast(string.Format(Strings.Toast_PastedSaved, path));
    }

    private void CopyErrorDetails()
    {
        if (ErrorReport is not { } report)
            return;
        try
        {
            Clipboard.SetText(report);
            ShowToast(Strings.Toast_ErrorDetailsCopied);
        }
        catch (Exception)
        {
            ShowToast(Strings.Toast_ClipboardBusy);
        }
    }

    /// <summary>
    /// What "Copy error details" puts on the clipboard: the computer, the run and its errors with their stack traces (the first
    /// few; the log has every one), and where the log is.
    /// </summary>
    private static string BuildErrorReport(string run, IReadOnlyList<(string? File, Exception Error)> errors)
    {
        const int MaxErrors = 5;
        var report = new StringBuilder();
        report.AppendLine(SystemInfo.Describe()).AppendLine(run.ReplaceLineEndings());
        foreach (var (file, error) in errors.Take(MaxErrors))
            report.AppendLine().AppendLine(file is null ? "Error:" : $"Failed: {file}").AppendLine(error.ToString());
        if (errors.Count > MaxErrors)
            report.AppendLine().AppendLine($"… and {errors.Count - MaxErrors} more, see the log");
        if (Log.FilePath is { } log)
            report.AppendLine().AppendLine($"Log: {log}");
        return report.ToString();
    }

    // ── Accelerators (Windows ML) ───────────────────────────────────

    /// <summary>"Auto", the GPUs through DirectML, the vendors' accelerators and the CPU.</summary>
    private static IReadOnlyList<Option<ExecutionDevice>> BuildDevices(IReadOnlyList<ExecutionDevice> accelerators)
    {
        var autoDevice = ExecutionDevice.Auto.Resolve();
        return
        [
            new(ExecutionDevice.Auto, string.Format(Strings.Device_AutoWith, Text.Device(autoDevice))),
            .. GpuAdapters.Enumerate().Select(a => new Option<ExecutionDevice>(ExecutionDevice.Gpu(a), a.Name.Replace("(R)", "").Replace("(TM)", ""))),
            .. accelerators.Select(a => new Option<ExecutionDevice>(a, AcceleratorLabel(a, accelerators))),
            new(ExecutionDevice.Cpu, Strings.Device_CpuSlow),
        ];
    }

    /// <summary>"RTX 3080 · TensorRT-RTX"; two identical cards get their DXGI index as well, to tell them apart.</summary>
    private static string AcceleratorLabel(ExecutionDevice device, IReadOnlyList<ExecutionDevice> accelerators) =>
        device.AdapterIndex >= 0 && accelerators.Count(a => a.ToString() == device.ToString()) > 1
            ? $"{device} #{device.AdapterIndex}"
            : device.ToString();

    /// <summary>
    /// Registers the accelerators Windows already has (loading a provider's library can take a while, so not on the UI thread),
    /// adds their devices to the list and finds out which others Windows ML could download.
    /// </summary>
    private async Task LoadAcceleratorsAsync()
    {
        var (devices, packages, error) = await Task.Run(() => (Accelerators.Devices(), Accelerators.Packages(), Accelerators.RegistrationError));
        Log.Info(error is not null && packages.Count == 0
            ? $"Windows ML accelerators: none ({error})"
            : $"Windows ML accelerators: {(devices.Count == 0 ? "none registered" : string.Join("; ", devices))}; offered: " +
              (packages.Count == 0 ? "none" : string.Join(", ", packages.Select(p => p.IsInstalled ? p.Name : p.Name + " (not installed)"))));

        var selected = SelectedDevice.Value;
        if (!_acceleratorsLoaded && _settings.DeviceKind == DeviceKind.Accelerator && FindSavedDevice(devices) is { } saved)
            selected = saved;
        if (!Accelerators.LateRegistration.IsCompleted)
        {
            // Windows ML didn't answer in time and is still registering: the saved accelerator may only be missing from the list
            // for now, so it isn't replaced with "Auto" yet, and the list is built again once the registration ends.
            _ = ReloadAcceleratorsAsync();
        }
        else
        {
            _acceleratorsLoaded = true;
        }
        Devices = BuildDevices(devices);
        SelectedDevice = Devices.FirstOrDefault(d => d.Value == selected) ?? Devices[0];
        // The same choice in a new list: the combo box must find it there again.
        OnPropertyChanged(nameof(SelectedDevice));
        _acceleratorPackages = packages;
        OnAcceleratorsChanged();
    }

    private async Task ReloadAcceleratorsAsync()
    {
        await Accelerators.LateRegistration;
        Log.Info("Windows ML finished registering the accelerators late");
        await LoadAcceleratorsAsync();
    }

    /// <summary>Downloads every accelerator Windows ML offers and doesn't have yet, one after another, then lists their devices.</summary>
    private async Task InstallAcceleratorsAsync()
    {
        _installingAccelerators = true;
        OnAcceleratorsChanged();
        var installed = new List<string>();
        foreach (var package in MissingAccelerators)
        {
            AcceleratorStatus = string.Format(Strings.Accelerators_Downloading, package.Name, 0);
            var progress = new Progress<double>(fraction =>
                AcceleratorStatus = string.Format(Strings.Accelerators_Downloading, package.Name, Math.Round(fraction * 100)));
            try
            {
                Log.Info($"Downloading the accelerator {package.Provider}");
                await Accelerators.InstallAsync(package.Provider, progress);
                installed.Add(package.Name);
                Log.Info($"Accelerator {package.Provider} installed");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error($"Accelerator {package.Provider} was not installed", ex);
                ShowToast(string.Format(Strings.Accelerators_Failed, package.Name, ex.Message));
            }
        }
        _installingAccelerators = false;
        AcceleratorStatus = null;
        await LoadAcceleratorsAsync();
        if (installed.Count > 0)
            ShowToast(string.Format(Strings.Accelerators_Installed, string.Join(", ", installed)));
    }

    private void OnAcceleratorsChanged()
    {
        OnPropertyChanged(nameof(HasAcceleratorOffer));
        OnPropertyChanged(nameof(AcceleratorOffer));
        CommandManager.InvalidateRequerySuggested();
    }

    // ── Updates ─────────────────────────────────────────────────────

    /// <summary>Asks github.com once which release is the latest (see UpdateCheck); a failure only goes to the log.</summary>
    private async Task CheckForUpdatesAsync()
    {
        var current = UpdateCheck.Current;
        try
        {
            var newer = await UpdateCheck.FindNewerAsync(current);
            Log.Info(newer is { } found
                ? $"Update check: Krupno {found.Version.ToString(3)} is available, {found.Page}"
                : $"Update check: no release newer than {current.ToString(3)}");
            // Turned off while the request was on its way: then nothing is shown. An update that didn't install keeps its line.
            if (newer is not null && CheckForUpdates && _updateStage == UpdateStage.None)
                SetUpdate(newer, UpdateStage.Available);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UriFormatException)
        {
            Log.Warning($"Update check failed: {ex.Message}");
        }
    }

    private void SetUpdate((Version Version, string Page)? update, UpdateStage stage, string? error = null)
    {
        _update = update;
        _updateStage = update is null ? UpdateStage.None : stage;
        _updateError = error;
        OnPropertyChanged(nameof(UpdateText));
        OnPropertyChanged(nameof(UpdateProgress));
        OnPropertyChanged(nameof(IsDownloadingUpdate));
        OnPropertyChanged(nameof(IsInstallingUpdate));
        OnPropertyChanged(nameof(ShowUpdateProgress));
        OnPropertyChanged(nameof(IsUpdateFailed));
        OnPropertyChanged(nameof(ShowInstallUpdate));
        OnPropertyChanged(nameof(ShowDownloadUpdate));
        OnPropertyChanged(nameof(ShowRetryUpdate));
        OnPropertyChanged(nameof(ShowWhatsNew));
        OnPropertyChanged(nameof(UpdateCloseText));
        OnPropertyChanged(nameof(UpdateSetupLog));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// "Update" (see UpdateInstall): downloads the release's installer and checks it, then starts Setup and closes the window,
    /// which Setup opens again when it is done. Not while processing runs, and no run starts during the download, so the window
    /// never closes in the middle of one. Another Krupno (a second window, the command-line tool) would hold the files Setup
    /// replaces, so it has to be closed first.
    /// </summary>
    private async Task InstallUpdateAsync()
    {
        if (_update is not { } update || _installedForAllUsers is not { } allUsers || IsBusy || _updating)
            return;
        _updating = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await InstallUpdateAsync(update, allUsers);
        }
        finally
        {
            _updating = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task InstallUpdateAsync((Version Version, string Page) update, bool allUsers)
    {
        string version = update.Version.ToString(3);
        if (CantInstallNow(update))
            return;

        using var cts = new CancellationTokenSource();
        _updateCts = cts;
        _updateProgress = 0;
        SetUpdate(update, UpdateStage.Downloading);
        var progress = new Progress<double>(fraction =>
        {
            // A report per 64 KB: the line changes only with the percentage.
            if (_updateStage != UpdateStage.Downloading || (int)(fraction * 100) == (int)(_updateProgress * 100))
                return;
            _updateProgress = fraction;
            OnPropertyChanged(nameof(UpdateProgress));
            OnPropertyChanged(nameof(UpdateText));
        });
        UpdateInstall.Verified installer;
        try
        {
            Log.Info($"Downloading the installer of Krupno {version}");
            installer = await UpdateInstall.DownloadAsync(update.Version, progress, cts.Token);
            // Canceled after the last read, while the result was on its way to the UI thread.
            cts.Token.ThrowIfCancellationRequested();
            Log.Info($"Downloaded {installer.Path}, its SHA-256 matches SHA256SUMS.txt");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Log.Info($"The download of Krupno {version} was canceled");
            // Hidden meanwhile (the update check turned off): then it stays hidden.
            if (_update == update)
                SetUpdate(update, UpdateStage.Available);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TimeoutException
                                       or InvalidDataException)
        {
            Log.Error($"Krupno {version} was not downloaded", ex);
            SetUpdate(update, UpdateStage.Failed,
                string.Format(Strings.Update_Failed, version, ex is InvalidDataException ? Strings.Update_Mismatch : ex.Message));
            return;
        }
        finally
        {
            _updateCts = null;
        }

        // Started during the download.
        if (CantInstallNow(update))
            return;
        _settings.UpdatingTo = version;
        _settings.Save();
        // Setup waits for every Krupno to let go of it (AppMutex), this one included.
        RunningMarker.Release();
        Process? setup;
        try
        {
            setup = UpdateInstall.Start(installer, allUsers, LanguageCulture, update.Version);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException
                                       or InvalidDataException)
        {
            RunningMarker.Hold();
            _settings.UpdatingTo = null;
            _settings.Save();
            Log.Error($"Setup of Krupno {version} did not start", ex);
            SetUpdate(update, UpdateStage.Failed,
                string.Format(Strings.Update_CantStart, version, ex is InvalidDataException ? Strings.Update_Mismatch : ex.Message));
            return;
        }
        Log.Info($"Setup of Krupno {version} started ({(allUsers ? "for all users" : "for the current user")})");
        SetUpdate(update, UpdateStage.Installing);
        bool tookOver;
        using (setup)
        {
            // Without the process (the shell started it) there is nothing to wait on.
            tookOver = setup is null || await UpdateInstall.WaitForSetupAsync(RunningMarker.SetupRunning, () => setup.HasExited,
                TimeSpan.FromMilliseconds(200));
            if (!tookOver)
            {
                RunningMarker.Hold();
                _settings.UpdatingTo = null;
                _settings.Save();
                Log.Warning($"Setup of Krupno {version} ended before installing (exit code {setup!.ExitCode}): the UAC prompt was " +
                            "declined, or it was canceled");
                SetUpdate(update, UpdateStage.Failed, string.Format(Strings.Update_NotInstalled, version));
                return;
            }
        }
        Log.Info("Setup has taken over; the window closes");
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Another Krupno would hold the files Setup replaces; another Setup of Krupno would be taken for this one when the window
    /// waits for it to take over (it holds the same mutex), and would keep this one from starting anyway.
    /// </summary>
    private bool CantInstallNow((Version Version, string Page) update)
    {
        string? reason = RunningMarker.OthersRunning() ? Strings.Update_CloseOthers
            : RunningMarker.SetupRunning() ? Strings.SetupRunning
            : null;
        if (reason is null)
            return false;
        Log.Info($"Update to {update.Version.ToString(3)} not started: {(reason == Strings.SetupRunning ? "Setup is running" : "another Krupno is running")}");
        SetUpdate(update, UpdateStage.Failed, reason);
        return true;
    }

    /// <summary>The ✕ on the update line: cancels the download, or hides the line until the next start.</summary>
    private void HideUpdate()
    {
        if (_updateCts is { } download)
            download.Cancel();
        else
            SetUpdate(null, UpdateStage.None);
    }

    /// <summary>
    /// The first start after "Update": Setup starts Krupno again, the new version or, if it failed or was canceled, the old one.
    /// Says which.
    /// </summary>
    private void FinishUpdate()
    {
        if (_settings.UpdatingTo is not { } target)
            return;
        _settings.UpdatingTo = null;
        _settings.Save();
        if (!Version.TryParse(target, out var version))
            return;
        if (UpdateCheck.Own >= version)
        {
            Log.Info($"Updated to Krupno {target}");
            ShowToast(string.Format(Strings.Update_Installed, target), TimeSpan.FromSeconds(6));
        }
        else
        {
            Log.Warning($"Krupno {target} was not installed, this is still {UpdateCheck.Own.ToString(3)}; Setup's log: {UpdateInstall.SetupLogPath(version)}");
            SetUpdate((version, UpdateCheck.ReleasePage(version)), UpdateStage.Failed, string.Format(Strings.Update_NotInstalled, target));
        }
    }

    /// <summary>The installer an earlier "Update" downloaded; Setup may still be finishing when it starts Krupno again.</summary>
    private async Task CleanUpUpdatesAsync()
    {
        if (await Task.Run(UpdateInstall.CleanUp))
            return;
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (!_updating)
            await Task.Run(UpdateInstall.CleanUp);
    }

    /// <summary>The release page in the browser: the installer and the ZIP are there, with what has changed.</summary>
    private void OpenUpdate()
    {
        if (_update is not { } update)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(update.Page) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warning($"Can't open {update.Page}: {ex.Message}");
            ShowToast(ex.Message);
        }
    }

    private void OpenSetupLog()
    {
        if (UpdateSetupLog is not { } path)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warning($"Can't open {path}: {ex.Message}");
            ShowToast(ex.Message);
        }
    }

    private void ShowToast(string message, TimeSpan? duration = null)
    {
        Toast = message;
        _toastTimer.Stop();
        _toastTimer.Interval = duration ?? TimeSpan.FromSeconds(2.5);
        _toastTimer.Start();
    }

    /// <summary>The name says what was asked for: "photo_x4.png", "photo_x2.5.png", "photo_3840w.png" (see ScaleTarget.Suffix).</summary>
    private static string BuildOutputPath(string input, string? folder, double scale, ScaleTarget? target, ImageFileFormat format)
    {
        string directory = folder ?? Path.GetDirectoryName(input)!;
        string suffix = target?.Suffix ?? "_x" + scale.ToString("0.##", CultureInfo.InvariantCulture);
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(input) + suffix + ImageCodec.GetExtension(format));
    }

    private static string DefaultCustomValue(CustomScaleMode mode) => mode switch
    {
        CustomScaleMode.Scale => "2.5",
        CustomScaleMode.Height => "2160",
        _ => "3840",
    };
}
