using System.Diagnostics;
using System.Globalization;
using System.Text;
using Krupno.Cli;
using Krupno.Core.Imaging;
using Krupno.Core.Inference;
using Krupno.Core.Processing;

Console.OutputEncoding = Encoding.UTF8;
// The command-line tool speaks English whatever the Windows language: scripts read its output, and the messages it passes on
// from Krupno.Core (progress, errors) would otherwise follow the language the window is translated into.
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
// For the installer: it asks for Krupno to be closed instead of stopping a run halfway through to replace these files.
RunningMarker.Hold();

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

if (options.Help || (options.Inputs.Count == 0 && !options.ListModels && !options.ListDevices && !options.InstallAccelerators))
{
    Console.WriteLine(CliOptions.Usage);
    return options.Help ? 0 : 2;
}

if (options.InstallAccelerators && InstallAccelerators() != 0)
    return 1;

if (options.ListDevices)
{
    var devices = ExecutionDevice.List();
    var accelerators = devices.Where(d => d.Kind == DeviceKind.Accelerator).ToList();
    foreach (var device in devices)
        Console.WriteLine($"  {DeviceArgument(device, accelerators),-6} {device}");
    var missing = Accelerators.Packages().Where(p => !p.IsInstalled).Select(p => p.Name).ToList();
    if (missing.Count > 0)
        Console.WriteLine($"  Windows ML also offers {string.Join(", ", missing)} for this computer: krupno-cli --install-accelerators");
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

if (options.Inputs.Count == 0)
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
                               or UnauthorizedAccessException or OutOfMemoryException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static int Run(CliOptions options, CancellationToken cancellationToken)
{
    // The jobs are planned before the model is loaded, which takes seconds: the model file is only inspected for its scale,
    // and that inspection is cached for the load. A wrong path or a batch with nothing left to do fails or ends right away.
    string modelPath = ResolveModel(options.Model);
    // Next to the source, the name says what was asked for: "_x4", "_x2.5", "_3840w".
    string suffix = options.Target?.Suffix
        ?? "_x" + (options.Scale > 0 ? options.Scale : UpscaleModel.Inspect(modelPath).Scale).ToString("0.##", CultureInfo.InvariantCulture);
    var jobs = JobPlanner.Plan(options.Inputs, options.Output, options.Format, options.Recursive, suffix, out int leftOut);
    if (leftOut > 0)
        Console.WriteLine($"Leaving out {leftOut} of {jobs.Count + leftOut} images: they are results of other images in this command.");
    if (jobs.Count == 0)
        throw new CliException($"No supported images found in {string.Join(", ", options.Inputs.Select(i => $"\"{i}\""))}.");
    if (options.SkipExisting)
    {
        int planned = jobs.Count;
        jobs.RemoveAll(job => File.Exists(job.Output));
        if (jobs.Count < planned)
            Console.WriteLine($"Skipping {planned - jobs.Count} of {planned} images: their results are already there.");
        if (jobs.Count == 0)
            return 0;
    }

    using var keepAwake = new KeepAwake();
    var stopwatch = Stopwatch.StartNew();
    using var model = UpscaleModel.Load(modelPath, ParseDevice(options.Device));
    var upscaler = new Upscaler(model);
    var upscaleOptions = new UpscaleOptions
    {
        Scale = options.Scale, Target = options.Target, TileSize = options.Tile, TilePadding = options.Padding,
        Strength = options.Strength / 100.0, TestTimeAugmentation = options.TestTimeAugmentation,
    };

    Console.WriteLine($"Model:  {model.Info}{(model.Info.IsHalfPrecision ? ", fp16" : "")}");
    Console.WriteLine($"Device: {model.Device}");

    int failed = 0;
    // The previous file's result while it is written in the background (see BatchSaving), and its "[i/n] " prefix.
    Task? pendingSave = null;
    string pendingPrefix = "";
    try
    {
        for (int i = 0; i < jobs.Count; i++)
        {
            var (input, output) = jobs[i];
            string prefix = jobs.Count > 1 ? $"[{i + 1}/{jobs.Count}] " : "";
            Console.WriteLine($"{prefix}{input}");

            try
            {
                var itemTimer = Stopwatch.StartNew();
                var loaded = ImageCodec.Load(input);
                var format = options.Format ?? ImageCodec.FormatFromExtension(output) ?? ImageFileFormat.Png;
                var (outputWidth, outputHeight) = upscaler.OutputSize(loaded.Image.Width, loaded.Image.Height, upscaleOptions);
                ImageCodec.EnsureCanSave(format, outputWidth, outputHeight);

                var progress = new ConsoleProgress();
                var result = upscaler.Upscale(loaded.Image, upscaleOptions, progress, cancellationToken);
                progress.Finish();
                cancellationToken.ThrowIfCancellationRequested();

                // One save at a time: the previous file is finished first.
                FinishPendingSave();
                var saveOptions = new SaveOptions { JpegQuality = options.Quality, Source = loaded };
                if (i < jobs.Count - 1 && BatchSaving.CanSaveInBackground(result))
                {
                    // The line below comes before the file is written; a failed save is reported once the next file is done.
                    pendingSave = Task.Run(() => ImageCodec.Save(result, output, format, saveOptions));
                    pendingPrefix = prefix;
                }
                else
                {
                    ImageCodec.Save(result, output, format, saveOptions);
                }
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
            if (model.CompileFailure is { } compileFailure && i == 0)
                Console.WriteLine($"  The accelerator can't compile the model for the cache, so it builds the engine on every start: {compileFailure}");
        }
    }
    finally
    {
        // Also on Ctrl+C or an error: a result already being written is finished rather than cut off by the exit.
        FinishPendingSave();
    }

    if (jobs.Count > 1)
        Console.WriteLine($"Done: {jobs.Count - failed} of {jobs.Count} in {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
    return failed == 0 ? 0 : 1;

    void FinishPendingSave()
    {
        if (pendingSave is null)
            return;
        try
        {
            pendingSave.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            failed++;
            Console.Error.WriteLine($"  {pendingPrefix}Not saved: {ex.Message}");
        }
        pendingSave = null;
    }
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

    if (value.StartsWith("acc", StringComparison.OrdinalIgnoreCase))
    {
        var accelerators = Accelerators.Devices();
        return int.TryParse(value[3..], out int accelerator) && accelerator >= 0 && accelerator < accelerators.Count
            ? accelerators[accelerator]
            : throw new CliException($"Accelerator \"{value}\" not found. To list devices: krupno-cli --list-devices");
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
    throw new CliException($"Unknown device \"{value}\": expected auto, cpu, gpuN or accN.");
}

static string DeviceArgument(ExecutionDevice device, List<ExecutionDevice> accelerators) => device.Kind switch
{
    DeviceKind.Auto => "auto",
    DeviceKind.Cpu => "cpu",
    DeviceKind.Accelerator => $"acc{accelerators.IndexOf(device)}",
    _ => $"gpu{device.AdapterIndex}",
};

// Downloads every provider Windows ML offers for this computer that it doesn't have yet. Asking for it is the user's consent:
// Windows downloads them from Microsoft, and some are hundreds of megabytes.
static int InstallAccelerators()
{
    var missing = Accelerators.Packages().Where(p => !p.IsInstalled).ToList();
    if (missing.Count == 0)
    {
        Console.WriteLine(Accelerators.Packages().Count == 0
            ? "Windows ML offers no accelerators for this computer (they need Windows 11 24H2 or later and a supported GPU or NPU)."
            : "All the accelerators Windows ML offers for this computer are installed.");
        return 0;
    }
    int failed = 0;
    foreach (var package in missing)
    {
        Console.WriteLine($"Downloading {package.Name}…");
        var progress = new Progress<double>(fraction =>
        {
            if (!Console.IsOutputRedirected)
                Console.Write($"\r  {fraction * 100,3:0}%");
        });
        try
        {
            Accelerators.InstallAsync(package.Provider, progress).GetAwaiter().GetResult();
            Console.WriteLine($"\r  {package.Name} installed");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failed++;
            Console.Error.WriteLine($"\r  {package.Name} was not installed: {ex.Message}");
        }
    }
    return failed == 0 ? 0 : 1;
}
