using System.Windows;

namespace Krupno.App.Controls;

/// <summary>
/// Finished tiles of the current pass as fractions of the image sides (0..1): the rest of the preview is dimmed.
/// <see cref="Current"/> is the tile that has just finished; it gets an outline.
/// </summary>
public sealed record TileOverlay(IReadOnlyList<Rect> Done, Rect? Current)
{
    public static TileOverlay Empty { get; } = new([], null);
}
