namespace Krupno.Core.Processing;

/// <summary>
/// A span along one axis: the window <see cref="WindowStart"/>..+window size is fed to the network,
/// and only the core <see cref="CoreStart"/>..+<see cref="CoreLength"/> goes into the result.
/// </summary>
internal readonly record struct TileSpan(int CoreStart, int CoreLength, int WindowStart);

internal static class TilePlanner
{
    /// <summary>
    /// Splits an axis into fixed-size windows. Tile cores don't overlap and cover the whole axis,
    /// and each core keeps at least <paramref name="padding"/> pixels of real context around it
    /// (except at the image edges), so there are no seams where tiles meet and no blending is needed.
    /// </summary>
    public static TileSpan[] PlanAxis(int length, int window, int padding)
    {
        if (length <= window)
            return [new TileSpan(0, length, 0)];
        if (window <= 2 * padding)
            throw new ArgumentException("The tile window must be larger than twice the padding.", nameof(padding));

        var spans = new List<TileSpan>();
        int coreStart = 0;
        while (coreStart < length)
        {
            int windowStart = Math.Max(0, coreStart - padding);
            if (windowStart + window >= length)
            {
                // Last tile: push the window against the edge so the context on the left is real.
                spans.Add(new TileSpan(coreStart, length - coreStart, length - window));
                break;
            }

            int coreEnd = windowStart + window - padding;
            spans.Add(new TileSpan(coreStart, coreEnd - coreStart, windowStart));
            coreStart = coreEnd;
        }
        return [.. spans];
    }

    /// <summary>Mirrors an index outside [0, length), to pad small images up to the window size.</summary>
    public static int Mirror(int index, int length)
    {
        if ((uint)index < (uint)length)
            return index;
        if (length == 1)
            return 0;

        int period = 2 * (length - 1);
        index %= period;
        if (index < 0)
            index += period;
        return index < length ? index : period - index;
    }

    public static int AlignUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;

    public static int AlignDown(int value, int multiple) => value / multiple * multiple;
}
