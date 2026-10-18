using System.IO;
using System.Windows.Media.Imaging;
using Krupno.App.Infrastructure;
using Krupno.App.Properties;

namespace Krupno.App.ViewModels;

public enum FileState
{
    Pending,
    Processing,
    Done,
    Error,
}

/// <summary>A file in the queue: filmstrip thumbnail, state, result.</summary>
public sealed class FileItem(string path) : ObservableObject
{
    private BitmapSource? _thumbnail;
    private FileState _state;
    private string? _outputPath;
    private string? _error;
    private bool _skipped;

    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public FileState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsProcessing));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    public bool IsProcessing => State == FileState.Processing;
    public bool IsDone => State == FileState.Done;
    public bool IsError => State == FileState.Error;

    public string? OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
    }

    /// <summary>Done without a run: its result was already there, and "Skip the image" is chosen for that.</summary>
    public bool Skipped
    {
        get => _skipped;
        set
        {
            if (SetProperty(ref _skipped, value))
                OnPropertyChanged(nameof(ToolTip));
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(ToolTip));
        }
    }

    public string ToolTip => State switch
    {
        FileState.Done => $"{Name}\n{string.Format(Skipped ? Strings.File_AlreadyThere : Strings.File_Done, System.IO.Path.GetFileName(OutputPath))}",
        FileState.Error => $"{Name}\n{Error}",
        FileState.Processing => $"{Name}\n{Strings.File_Processing}",
        _ => Name,
    };

    public bool OutputExists => OutputPath is not null && File.Exists(OutputPath);

    public override string ToString() => Name;
}
