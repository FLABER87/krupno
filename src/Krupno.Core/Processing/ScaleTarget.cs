namespace Krupno.Core.Processing;

public enum TargetSide
{
    Width,
    Height,
    /// <summary>The longer of the two sides, whichever it is for each image.</summary>
    LongSide,
}

/// <summary>A length for one side of the result in pixels; the other side follows the image's proportions.</summary>
public sealed record ScaleTarget(TargetSide Side, int Pixels)
{
    public int Pixels { get; } = Pixels > 0 ? Pixels : throw new ArgumentOutOfRangeException(nameof(Pixels), Pixels, "The size must be positive.");

    /// <summary>The scale that makes this side of a width × height image exactly <see cref="Pixels"/> long.</summary>
    public double ScaleFor(int width, int height) => (double)Pixels / Side switch
    {
        TargetSide.Width => width,
        TargetSide.Height => height,
        _ => Math.Max(width, height),
    };

    /// <summary>The end of a result's name next to its source: "_3840w", "_2160h", "_3840px".</summary>
    public string Suffix => Side switch
    {
        TargetSide.Width => $"_{Pixels}w",
        TargetSide.Height => $"_{Pixels}h",
        _ => $"_{Pixels}px",
    };
}
