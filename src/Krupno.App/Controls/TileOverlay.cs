using System.Windows;

namespace Krupno.App.Controls;

/// <summary>Finished tiles of the current pass as fractions of the image sides (0..1): the rest of the preview is dimmed.</summary>
public sealed record TileOverlay(IReadOnlyList<Rect> Done)
{
    public static TileOverlay Empty { get; } = new([]);
}
