using System.IO;
using System.Windows.Media.Imaging;
using Krupno.App.Infrastructure;

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
        FileState.Done => $"{Name}\nDone: {System.IO.Path.GetFileName(OutputPath)}",
        FileState.Error => $"{Name}\n{Error}",
        FileState.Processing => $"{Name}\nProcessing…",
        _ => Name,
    };

    public bool OutputExists => OutputPath is not null && File.Exists(OutputPath);

    public override string ToString() => Name;
}
