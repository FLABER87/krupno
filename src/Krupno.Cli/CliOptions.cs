using System.Globalization;
using Krupno.Core.Imaging;
using Krupno.Core.Inference;
using Krupno.Core.Processing;

namespace Krupno.Cli;

internal sealed class CliException(string message) : Exception(message);

internal sealed class CliOptions
{
    public const string Usage = $"""
        Krupno — AI image upscaling (ONNX Runtime + DirectML).

        Usage:
          krupno-cli <file|folder|pattern>... [-o <file|folder>] [options]
          krupno-cli --list-models | --list-devices | --install-accelerators

        Options:
          -o, --output <path>     output file or folder; a folder when there is more than one image
                                  (default: next to the source / <folder>_upscaled)
          -r, --recursive         also the images in subfolders; their folders are repeated in the output folder
              --skip-existing     leave out images whose result is already there, to resume a batch
              --no-overwrite      keep a result that is already there and number the new one: photo_x4 (2).png
              --name-model        add the model to the result's name, and --denoise, --strength and --tta when
                                  given: photo_x4_4xBHI_dat2_real.png, photo_x4_4xBHI_dat2_real_s70.png
          -m, --model <name|path> model from the catalog or path to an .onnx file
                                  (default: {ModelCatalog.DefaultModelName}; if it is missing, the first one alphabetically)
          -s, --scale <number>    final scale, e.g. 2, 3, 4, 8 or 2.5 (default: the model's own scale)
              --width <px>        a width for every result instead of a scale; the height follows the proportions
              --height <px>       the same for the height
              --long-side <px>    the same for the longer side, whichever it is for each image
          -f, --format <fmt>      png | jpg | tiff | bmp (default: from the output extension, or png); an animated
                                  GIF or WebP stays an animation in its own format
          -q, --quality <1-100>   JPEG quality (default: 95), and that of an animated WebP's frames, where 100 is
                                  lossless (default: lossless for a lossless source, otherwise 95)
          -t, --tile <px>         tile size; 0 = no tiling (default: 384)
              --pad <px>          tile overlap (default: 32; 64 leaves no visible seams even with deep models)
              --tta               test-time augmentation: all 8 rotations and mirrorings, averaged (8 times slower)
              --strength <1-100>  how much of the network's result to keep, in percent; the rest is a plain
                                  Lanczos resize, which tones down invented textures (default: 100)
              --denoise           remove camera noise and grain before upscaling, with the
                                  {ModelCatalog.DenoiseModelName} model; slower, and it smooths fine texture.
                                  With -s 1 (or less) only the noise is removed
          -d, --device <dev>      auto | cpu | gpu0, gpu1, … (DirectML) | acc0, acc1, … (a vendor's accelerator,
                                  see --list-devices) (default: auto)
              --list-models       list the models found
              --list-devices      list the devices
              --install-accelerators  download the GPU and NPU vendors' execution providers Windows ML offers for
                                  this computer (Windows 11 24H2 and later; can take minutes)
          -h, --help              show this help

        Animated GIF and WebP are upscaled frame by frame, keeping the durations and the loop count.

        Several files, folders and patterns can be given at once. Patterns such as photos\*.jpg are expanded by
        krupno-cli itself (cmd and PowerShell pass them on as they are); wildcards work in the file name only.

        Models are looked up in %LOCALAPPDATA%\Krupno\models and in the models folder next to the program.
        """;

    public List<string> Inputs { get; } = [];
    public string? Output { get; private set; }
    public string? Model { get; private set; }
    public double Scale { get; private set; }
    public ScaleTarget? Target { get; private set; }
    public ImageFileFormat? Format { get; private set; }
    public bool Recursive { get; private set; }
    public bool SkipExisting { get; private set; }
    public bool NoOverwrite { get; private set; }
    public bool NameModel { get; private set; }
    public int Quality { get; private set; } = 95;

    /// <summary>-q was given: an animated WebP takes that quality instead of its source's kind of compression.</summary>
    public bool QualityGiven { get; private set; }
    public int Tile { get; private set; } = 384;
    public int Padding { get; private set; } = 32;
    public int Strength { get; private set; } = 100;
    public bool TestTimeAugmentation { get; private set; }
    public bool Denoise { get; private set; }
    public string Device { get; private set; } = "auto";
    public bool ListModels { get; private set; }
    public bool ListDevices { get; private set; }
    public bool InstallAccelerators { get; private set; }
    public bool Help { get; private set; }

    /// <summary>What happens to a result that is already there.</summary>
    public ExistingResult Existing => SkipExisting ? ExistingResult.Skip : NoOverwrite ? ExistingResult.KeepBoth : ExistingResult.Replace;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        // -s and the target sizes all set the size of the result, so only one of them makes sense.
        var sizes = new List<string>();
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
                case "-r" or "--recursive":
                    options.Recursive = true;
                    break;
                case "--tta":
                    options.TestTimeAugmentation = true;
                    break;
                case "--denoise":
                    options.Denoise = true;
                    break;
                case "--skip-existing":
                    options.SkipExisting = true;
                    break;
                case "--no-overwrite":
                    options.NoOverwrite = true;
                    break;
                case "--name-model":
                    options.NameModel = true;
                    break;
                case "-m" or "--model":
                    options.Model = Value();
                    break;
                case "-s" or "--scale":
                    options.Scale = ParseScale(Value());
                    sizes.Add(arg);
                    break;
                case "--width" or "--height" or "--long-side":
                    var side = arg switch { "--width" => TargetSide.Width, "--height" => TargetSide.Height, _ => TargetSide.LongSide };
                    options.Target = new ScaleTarget(side, ParseInt(arg, Value(), 1, 1_000_000));
                    sizes.Add(arg);
                    break;
                case "-f" or "--format":
                    options.Format = ParseFormat(Value());
                    break;
                case "-q" or "--quality":
                    options.Quality = ParseInt(arg, Value(), 1, 100);
                    options.QualityGiven = true;
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
                case "--install-accelerators":
                    options.InstallAccelerators = true;
                    break;
                case "--list-devices":
                    options.ListDevices = true;
                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                        throw new CliException($"Unknown option: {arg}");
                    options.Inputs.Add(arg);
                    break;
            }
        }
        if (sizes.Count > 1)
            throw new CliException($"Use only one of -s, --width, --height and --long-side (got {string.Join(", ", sizes)}).");
        if (options.SkipExisting && options.NoOverwrite)
            throw new CliException("Use either --skip-existing or --no-overwrite: one leaves an image out, the other numbers its result.");
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
