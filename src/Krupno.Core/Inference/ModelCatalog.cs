namespace Krupno.Core.Inference;

public sealed record ModelEntry(string Name, string Path)
{
    public override string ToString() => Name;
}

/// <summary>Finds .onnx models: first in the user folder, then next to the program.</summary>
public static class ModelCatalog
{
    /// <summary>
    /// The default model: nearly DAT2 quality at Real-ESRGAN speed, CC-BY-4.0 license
    /// (docs/models.md, "Which model to use").
    /// </summary>
    public const string DefaultModelName = "4xBHI_realplksr_dysample_real";

    /// <summary>
    /// The ×1 model of "Remove noise" (--denoise): SCUNet trained on real camera noise, Apache-2.0 (docs/models.md, "Noise").
    /// It is not an upscaler, so it is not offered as one and never becomes the default. The fp16 file is the one released;
    /// a conversion of the same weights in fp32 is taken too, if it is the only one there.
    /// </summary>
    public const string DenoiseModelName = "scunet_color_real_psnr_fp16";

    private static readonly string[] DenoiseModelNames = [DenoiseModelName, "scunet_color_real_psnr"];

    public static string AppDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "models");

    public static string UserDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Krupno", "models");

    public static IReadOnlyList<string> SearchDirectories { get; } = [UserDirectory, AppDirectory];

    public static IReadOnlyList<ModelEntry> Discover()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new List<ModelEntry>();
        foreach (var directory in SearchDirectories)
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.onnx"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (seen.Add(name))
                    models.Add(new ModelEntry(name, file));
            }
        }
        models.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return models;
    }

    /// <summary>A file path or the name of a catalog model (with or without the .onnx extension).</summary>
    public static string? Resolve(string nameOrPath)
    {
        if (File.Exists(nameOrPath))
            return Path.GetFullPath(nameOrPath);

        string name = nameOrPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ? nameOrPath[..^5] : nameOrPath;
        return Find(Discover(), name)?.Path;
    }

    /// <summary>The model named <paramref name="name"/> from the list (case-insensitive).</summary>
    public static ModelEntry? Find(IEnumerable<ModelEntry> models, string? name) =>
        name is null ? null : models.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary><see cref="DefaultModelName"/> if it is installed, otherwise the first upscaler alphabetically.</summary>
    public static ModelEntry? PickDefault(IReadOnlyList<ModelEntry> models) =>
        Find(models, DefaultModelName) ?? models.FirstOrDefault(m => !IsDenoiser(m));

    /// <summary>Whether the model is the one of "Remove noise" (<see cref="DenoiseModelName"/>) rather than an upscaler.</summary>
    public static bool IsDenoiser(ModelEntry model) =>
        DenoiseModelNames.Contains(model.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The model of "Remove noise" from the list, the released fp16 one first; null if it isn't installed.</summary>
    public static ModelEntry? FindDenoiser(IReadOnlyList<ModelEntry> models) =>
        DenoiseModelNames.Select(name => Find(models, name)).FirstOrDefault(m => m is not null);
}
