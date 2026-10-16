using Krupno.App.Infrastructure;
using Krupno.Core.Inference;

namespace Krupno.App.ViewModels;

public enum ModelBadge
{
    None,
    Recommended,
    NonCommercial,
}

/// <summary>A model in the UI: a readable title instead of the file name, a speed hint, badges.</summary>
public sealed class ModelItem : ObservableObject
{
    private string _details;

    private ModelItem(ModelEntry entry, string title, string subtitle, ModelBadge badge, int order)
    {
        Entry = entry;
        Title = title;
        Subtitle = subtitle;
        Badge = badge;
        Order = order;
        _details = entry.Path;
    }

    public ModelEntry Entry { get; }
    public string Name => Entry.Name;
    public string Title { get; }
    public string Subtitle { get; }
    public ModelBadge Badge { get; }
    public int Order { get; }

    public string? BadgeText => Badge switch
    {
        ModelBadge.Recommended => "Recommended",
        ModelBadge.NonCommercial => "Non-commercial",
        _ => null,
    };

    public bool IsRecommended => Badge == ModelBadge.Recommended;
    public bool IsNonCommercial => Badge == ModelBadge.NonCommercial;

    /// <summary>Technical details for the tooltip: architecture, precision, license, path.</summary>
    public string Details
    {
        get => _details;
        set => SetProperty(ref _details, value);
    }

    /// <summary>Name for screen readers and UI Automation.</summary>
    public override string ToString() => $"{Title}, {Subtitle}";

    public static ModelItem Create(ModelEntry entry)
    {
        string key = entry.Name.ToLowerInvariant();
        return Known.TryGetValue(key, out var preset)
            ? new ModelItem(entry, preset.Title, preset.Subtitle, preset.Badge, preset.Order)
            : new ModelItem(entry, entry.Name.Replace('_', ' '), "Custom model", ModelBadge.None, int.MaxValue);
    }

    private sealed record Preset(string Title, string Subtitle, ModelBadge Badge, int Order);

    /// <summary>
    /// Known models: order and descriptions follow the photo comparison (README, "Which model to use").
    /// The first three are shown in the collapsed list.
    /// </summary>
    private static readonly Dictionary<string, Preset> Known = new()
    {
        [ModelCatalog.DefaultModelName.ToLowerInvariant()] = new("Photo, fast", "BHI RealPLKSR · fast", ModelBadge.Recommended, 0),
        ["4xbhi_dat2_real_fp16"] = new("Photo, maximum detail", "BHI DAT2 · slow", ModelBadge.None, 1),
        ["4x-ultrasharpv2"] = new("General purpose", "UltraSharpV2 · slow", ModelBadge.NonCommercial, 2),
        ["4xbhi_dat2_real"] = new("Photo, maximum detail", "BHI DAT2 fp32 · very slow", ModelBadge.None, 3),
        ["realesrgan_x4plus_fp16"] = new("Classic", "Real-ESRGAN x4plus · fast", ModelBadge.None, 4),
        ["realesrgan_x4plus"] = new("Classic", "Real-ESRGAN x4plus fp32", ModelBadge.None, 5),
        ["realesr-general-x4v3"] = new("Draft", "Real-ESRGAN general · very fast", ModelBadge.None, 6),
    };
}
