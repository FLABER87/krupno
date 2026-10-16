using System.Diagnostics;
using System.Globalization;
using System.Text;
using Krupno.Cli;
using Krupno.Core.Imaging;
using Krupno.Core.Inference;
using Krupno.Core.Processing;

Console.OutputEncoding = Encoding.UTF8;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (CliException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Try 'krupno-cli --help' for more information.");
    return 2;
}

if (options.Help || (options.Input is null && !options.ListModels && !options.ListDevices))
{
    Console.WriteLine(CliOptions.Usage);
    return options.Help ? 0 : 2;
}

if (options.ListDevices)
{
    foreach (var device in ExecutionDevice.List())
        Console.WriteLine($"  {DeviceArgument(device),-6} {device}");
}

if (options.ListModels)
{
    var models = ModelCatalog.Discover();
    if (models.Count == 0)
        Console.WriteLine($"  No models found. Search folders: {string.Join("; ", ModelCatalog.SearchDirectories)}");
    var defaultModel = ModelCatalog.PickDefault(models);
    foreach (var model in models)
        Console.WriteLine($"{(model == defaultModel ? "* " : "  ")}{model.Name,-36} {model.Path}");
    if (defaultModel is not null)
        Console.WriteLine("  (* = default model)");
}

if (options.Input is null)
    return 0;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // The first Ctrl+C cancels gracefully, the second one exits immediately.
    if (cts.IsCancellationRequested)
        return;
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return Run(options, cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Canceled.");
    return 130;
}
catch (Exception ex) when (ex is CliException or IOException or NotSupportedException or InvalidOperationException
                               or UnauthorizedAccessException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static int Run(CliOptions options, CancellationToken cancellationToken)
{
    string modelPath = ResolveModel(options.Model);
    var stopwatch = Stopwatch.StartNew();
    using var model = UpscaleModel.Load(modelPath, ParseDevice(options.Device));

    var jobs = PlanJobs(options, options.Scale > 0 ? options.Scale : model.Info.Scale);
    if (jobs.Count == 0)
        throw new CliException($"No supported images found in \"{options.Input}\".");
    var upscaler = new Upscaler(model);
    var upscaleOptions = new UpscaleOptions { Scale = options.Scale, TileSize = options.Tile, TilePadding = options.Padding };

    Console.WriteLine($"Model:  {model.Info}{(model.Info.IsHalfPrecision ? ", fp16" : "")}");
    Console.WriteLine($"Device: {model.Device}");

    int failed = 0;
    for (int i = 0; i < jobs.Count; i++)
    {
        var (input, output) = jobs[i];
        string prefix = jobs.Count > 1 ? $"[{i + 1}/{jobs.Count}] " : "";
        Console.WriteLine($"{prefix}{input}");

        try
        {
            var itemTimer = Stopwatch.StartNew();
            var loaded = ImageCodec.Load(input);
            var progress = new ConsoleProgress();
            var result = upscaler.Upscale(loaded.Image, upscaleOptions, progress, cancellationToken);
            progress.Finish();

            var format = options.Format ?? ImageCodec.FormatFromExtension(output) ?? ImageFileFormat.Png;
            ImageCodec.Save(result, output, format, new SaveOptions { JpegQuality = options.Quality, Source = loaded });
            Console.WriteLine($"  {loaded.Image.Width}×{loaded.Image.Height} → {result.Width}×{result.Height}  " +
                              $"{itemTimer.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s  → {output}");
        }
        catch (Exception ex) when (jobs.Count > 1 && ex is not OperationCanceledException)
        {
            // In batch mode one broken image must not stop the rest.
            failed++;
            Console.Error.WriteLine($"  Skipped: {ex.Message}");
        }

        if (model.FallbackReason is { } reason && i == 0)
            Console.WriteLine($"  DirectML is unavailable, running on CPU: {reason}");
    }

    if (jobs.Count > 1)
        Console.WriteLine($"Done: {jobs.Count - failed} of {jobs.Count} in {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
    return failed == 0 ? 0 : 1;
}

static List<(string Input, string Output)> PlanJobs(CliOptions options, double scale)
{
    string input = Path.GetFullPath(options.Input!);
    string suffix = "_x" + scale.ToString("0.##", CultureInfo.InvariantCulture);

    if (Directory.Exists(input))
    {
        string outputDir = options.Output is null
            ? input.TrimEnd(Path.DirectorySeparatorChar) + "_upscaled"
            : Path.GetFullPath(options.Output);
        var extension = ImageCodec.GetExtension(options.Format ?? ImageFileFormat.Png);
        return Directory.EnumerateFiles(input)
            .Where(ImageCodec.IsSupportedInput)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(file => (file, Path.Combine(outputDir, Path.GetFileNameWithoutExtension(file) + extension)))
            .ToList();
    }

    if (!File.Exists(input))
        throw new CliException($"File or folder not found: {input}");

    string output;
    if (options.Output is null)
    {
        var extension = ImageCodec.GetExtension(options.Format ?? ImageFileFormat.Png);
        output = Path.Combine(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input) + suffix + extension);
    }
    else if (Directory.Exists(options.Output) || options.Output.EndsWith('\\') || options.Output.EndsWith('/'))
    {
        var extension = ImageCodec.GetExtension(options.Format ?? ImageFileFormat.Png);
        output = Path.Combine(Path.GetFullPath(options.Output), Path.GetFileNameWithoutExtension(input) + extension);
    }
    else
    {
        output = Path.GetFullPath(options.Output);
    }
    return [(input, output)];
}

static string ResolveModel(string? model)
{
    if (model is not null)
    {
        return ModelCatalog.Resolve(model)
            ?? throw new CliException($"Model \"{model}\" not found. To list models: krupno-cli --list-models");
    }

    var entry = ModelCatalog.PickDefault(ModelCatalog.Discover())
        ?? throw new CliException(
            $"No models found. Put an .onnx file into {ModelCatalog.UserDirectory} or specify a path with -m.");
    return entry.Path;
}

static ExecutionDevice ParseDevice(string value)
{
    switch (value.ToLowerInvariant())
    {
        case "auto":
            return ExecutionDevice.Auto;
        case "cpu":
            return ExecutionDevice.Cpu;
    }

    string number = value.StartsWith("gpu", StringComparison.OrdinalIgnoreCase) ? value[3..] : value;
    if (number.Length == 0)
        number = "0";
    if (int.TryParse(number, out int index))
    {
        var adapter = GpuAdapters.Enumerate().FirstOrDefault(a => a.Index == index)
            ?? throw new CliException($"GPU {index} not found. To list devices: krupno-cli --list-devices");
        return ExecutionDevice.Gpu(adapter);
    }
    throw new CliException($"Unknown device \"{value}\": expected auto, cpu or gpuN.");
}

static string DeviceArgument(ExecutionDevice device) => device.Kind switch
{
    DeviceKind.Auto => "auto",
    DeviceKind.Cpu => "cpu",
    _ => $"gpu{device.AdapterIndex}",
};
