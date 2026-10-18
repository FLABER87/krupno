using Krupno.Core.Processing;

namespace Krupno.Cli;

/// <summary>
/// Synchronous progress: in a console app Progress&lt;T&gt; posts to the thread pool and lines get interleaved.
/// For an animation the frame comes first, and the messages of the frames after the first one are left out (they repeat),
/// except warnings.
/// </summary>
internal sealed class ConsoleProgress : IProgress<UpscaleProgress>, IProgress<FrameProgress>
{
    private const int BarWidth = 30;
    private readonly bool _interactive = !Console.IsOutputRedirected;
    private int _lastPercent = -1;

    public void Report(UpscaleProgress value) => Report(value, value.Fraction, "");

    public void Report(FrameProgress value)
    {
        if (value.Upscale.Message is not null && value.Frame > 1 && !value.Upscale.IsWarning)
            return;
        Report(value.Upscale, value.Fraction, $"  frame {value.Frame}/{value.FrameCount}");
    }

    private void Report(UpscaleProgress value, double fraction, string frame)
    {
        if (value.Message is { } message)
        {
            ClearLine();
            Console.WriteLine($"  {message}");
            return;
        }
        if (!_interactive)
            return;

        int percent = (int)(fraction * 100);
        if (percent == _lastPercent)
            return;
        _lastPercent = percent;

        int filled = percent * BarWidth / 100;
        string pass = value.Denoising ? "  removing noise" : value.PassCount > 1 ? $"  pass {value.Pass}/{value.PassCount}" : "";
        string round = value.RoundCount > 1 ? $"  orientation {value.Round}/{value.RoundCount}" : "";
        Console.Write($"\r  [{new string('#', filled)}{new string('.', BarWidth - filled)}] {percent,3}%{frame}  tile {value.Tile}/{value.TileCount}{round}{pass}   ");
    }

    public void Finish() => ClearLine();

    private void ClearLine()
    {
        if (_interactive && _lastPercent >= 0)
            Console.Write("\r" + new string(' ', Math.Max(0, Console.BufferWidth - 1)) + "\r");
        _lastPercent = -1;
    }
}
