namespace Krupno.Core.Inference;

/// <summary>What is known about a model: from the ONNX metadata (written by tools/convert_to_onnx.py) or from a probe run.</summary>
public sealed record ModelInfo
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required int Scale { get; init; }
    public string? Architecture { get; init; }
    public string? License { get; init; }
    public required bool IsHalfPrecision { get; init; }

    /// <summary>Fixed input size if the model was exported with a static shape.</summary>
    public int? FixedWidth { get; init; }
    public int? FixedHeight { get; init; }

    /// <summary>Input sides must be multiples of this number.</summary>
    public int SizeMultiple { get; init; } = 1;
    public int MinimumSize { get; init; } = 1;

    /// <summary>The largest window side, already a multiple of <see cref="SizeMultiple"/>: on a larger window the model computes
    /// wrong results (DySample on DirectML does so silently, without an exception).</summary>
    public int? MaximumSize { get; init; }
    public bool RequiresSquare { get; init; }

    internal string InputName { get; init; } = "";
    internal string OutputName { get; init; } = "";
    internal bool InputIsHalf { get; init; }
    internal bool OutputIsHalf { get; init; }

    /// <summary>Names of the dynamic input axes, used to set the shape for DirectML.</summary>
    internal string? BatchDimName { get; init; }
    internal string? HeightDimName { get; init; }
    internal string? WidthDimName { get; init; }

    internal bool HasNamedSpatialDims =>
        HeightDimName is not null && WidthDimName is not null && HeightDimName != WidthDimName;

    public override string ToString() =>
        Architecture is null ? $"{Name} (×{Scale})" : $"{Name} (×{Scale}, {Architecture})";
}
