using Krupno.Core.Processing;

namespace Krupno.Cli;

/// <summary>
/// Synchronous progress: in a console app Progress&lt;T&gt; posts to the thread pool and lines get interleaved.
/// </summary>
internal sealed class ConsoleProgress : IProgress<UpscaleProgress>
{
    private const int BarWidth = 30;
    private readonly bool _interactive = !Console.IsOutputRedirected;
    private int _lastPercent = -1;

    public void Report(UpscaleProgress value)
    {
        if (value.Message is { } message)
        {
            ClearLine();
            Console.WriteLine($"  {message}");
            return;
        }
        if (!_interactive)
            return;

        int percent = (int)(value.Fraction * 100);
        if (percent == _lastPercent)
            return;
        _lastPercent = percent;

        int filled = percent * BarWidth / 100;
        string pass = value.PassCount > 1 ? $"  pass {value.Pass}/{value.PassCount}" : "";
        string round = value.RoundCount > 1 ? $"  orientation {value.Round}/{value.RoundCount}" : "";
        Console.Write($"\r  [{new string('#', filled)}{new string('.', BarWidth - filled)}] {percent,3}%  tile {value.Tile}/{value.TileCount}{round}{pass}   ");
    }

    public void Finish() => ClearLine();

    private void ClearLine()
    {
        if (_interactive && _lastPercent >= 0)
            Console.Write("\r" + new string(' ', Math.Max(0, Console.BufferWidth - 1)) + "\r");
        _lastPercent = -1;
    }
}
