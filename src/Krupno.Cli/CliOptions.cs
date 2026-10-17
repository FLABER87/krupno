using System.Globalization;
using Krupno.Core.Imaging;
using Krupno.Core.Inference;

namespace Krupno.Cli;

internal sealed class CliException(string message) : Exception(message);

internal sealed class CliOptions
{
    public const string Usage = $"""
        Krupno — AI image upscaling (ONNX Runtime + DirectML).

        Usage:
          krupno-cli <file|folder> [-o <file|folder>] [options]
          krupno-cli --list-models | --list-devices

        Options:
          -o, --output <path>     output file or folder
                                  (default: next to the source / <folder>_upscaled)
          -m, --model <name|path> model from the catalog or path to an .onnx file
                                  (default: {ModelCatalog.DefaultModelName}; if it is missing, the first one alphabetically)
          -s, --scale <number>    final scale, e.g. 2, 3, 4, 8 or 2.5 (default: the model's own scale)
          -f, --format <fmt>      png | jpg | tiff | bmp (default: from the output extension, or png)
          -q, --quality <1-100>   JPEG quality (default: 95)
          -t, --tile <px>         tile size; 0 = no tiling (default: 384)
              --pad <px>          tile overlap (default: 32; 64 leaves no visible seams even with deep models)
              --strength <1-100>  how much of the network's result to keep, in percent; the rest is a plain
                                  Lanczos resize, which tones down invented textures (default: 100)
          -d, --device <dev>      auto | cpu | gpu0, gpu1, … (default: auto)
              --list-models       list the models found
              --list-devices      list the devices
          -h, --help              show this help

        Models are looked up in %LOCALAPPDATA%\Krupno\models and in the models folder next to the program.
        """;

    public string? Input { get; private set; }
    public string? Output { get; private set; }
    public string? Model { get; private set; }
    public double Scale { get; private set; }
    public ImageFileFormat? Format { get; private set; }
    public int Quality { get; private set; } = 95;
    public int Tile { get; private set; } = 384;
    public int Padding { get; private set; } = 32;
    public int Strength { get; private set; } = 100;
    public string Device { get; private set; } = "auto";
    public bool ListModels { get; private set; }
    public bool ListDevices { get; private set; }
    public bool Help { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Value() => i + 1 < args.Length ? args[++i] : throw new CliException($"Missing value for {arg}.");

            switch (arg)
            {
                case "-h" or "--help" or "/?":
                    options.Help = true;
                    break;
                case "-o" or "--output":
                    options.Output = Value();
                    break;
                case "-m" or "--model":
                    options.Model = Value();
                    break;
                case "-s" or "--scale":
                    options.Scale = ParseScale(Value());
                    break;
                case "-f" or "--format":
                    options.Format = ParseFormat(Value());
                    break;
                case "-q" or "--quality":
                    options.Quality = ParseInt(arg, Value(), 1, 100);
                    break;
                case "-t" or "--tile":
                    options.Tile = ParseInt(arg, Value(), 0, 8192);
                    break;
                case "--pad":
                    options.Padding = ParseInt(arg, Value(), 0, 256);
                    break;
                case "--strength":
                    options.Strength = ParseInt(arg, Value().TrimEnd('%'), 1, 100);
                    break;
                case "-d" or "--device":
                    options.Device = Value();
                    break;
                case "--list-models":
                    options.ListModels = true;
                    break;
                case "--list-devices":
                    options.ListDevices = true;
                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                        throw new CliException($"Unknown option: {arg}");
                    if (options.Input is not null)
                        throw new CliException($"Unexpected argument: {arg}");
                    options.Input = arg;
                    break;
            }
        }
        return options;
    }

    private static double ParseScale(string text)
    {
        string trimmed = text.Trim().Trim('x', 'X', '×');
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) && scale is > 0 and <= 64)
            return scale;
        throw new CliException($"Invalid scale: {text}");
    }

    private static ImageFileFormat ParseFormat(string text) => text.ToLowerInvariant().TrimStart('.') switch
    {
        "png" => ImageFileFormat.Png,
        "jpg" or "jpeg" => ImageFileFormat.Jpeg,
        "tif" or "tiff" => ImageFileFormat.Tiff,
        "bmp" => ImageFileFormat.Bmp,
        _ => throw new CliException($"Unknown format: {text}"),
    };

    private static int ParseInt(string name, string text, int min, int max)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= min && value <= max)
            return value;
        throw new CliException($"{name}: expected an integer from {min} to {max}, got \"{text}\".");
    }
}
