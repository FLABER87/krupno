using Krupno.App.Infrastructure;
using Krupno.App.Properties;
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
        ModelBadge.Recommended => Strings.Badge_Recommended,
        ModelBadge.NonCommercial => Strings.Badge_NonCommercial,
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
            : new ModelItem(entry, entry.Name.Replace('_', ' '), Strings.Model_Custom, ModelBadge.None, int.MaxValue);
    }

    private sealed record Preset(string Title, string Subtitle, ModelBadge Badge, int Order);

    /// <summary>
    /// Known models: order and descriptions follow the photo comparison (docs/models.md, "Which model to use"); Remacri
    /// follows the text comparison ("Text"). The first three are shown in the collapsed list.
    /// </summary>
    private static readonly Dictionary<string, Preset> Known = new()
    {
        [ModelCatalog.DefaultModelName.ToLowerInvariant()] =
            new(Strings.Model_PhotoFast, $"BHI RealPLKSR · {Strings.Speed_Fast}", ModelBadge.Recommended, 0),
        ["4xbhi_dat2_real_fp16"] = new(Strings.Model_PhotoDetail, $"BHI DAT2 · {Strings.Speed_Slow}", ModelBadge.None, 1),
        ["4x-ultrasharpv2"] = new(Strings.Model_General, $"UltraSharpV2 · {Strings.Speed_Slow}", ModelBadge.NonCommercial, 2),
        ["4x_foolhardy_remacri_fp16"] = new(Strings.Model_Text, $"Remacri · {Strings.Speed_Fast}", ModelBadge.NonCommercial, 3),
        ["4xbhi_dat2_real"] = new(Strings.Model_PhotoDetail, $"BHI DAT2 fp32 · {Strings.Speed_VerySlow}", ModelBadge.None, 4),
        ["realesrgan_x4plus_fp16"] = new(Strings.Model_Classic, $"Real-ESRGAN x4plus · {Strings.Speed_Fast}", ModelBadge.None, 5),
        ["realesrgan_x4plus"] = new(Strings.Model_Classic, "Real-ESRGAN x4plus fp32", ModelBadge.None, 6),
        ["realesr-general-x4v3"] = new(Strings.Model_Draft, $"Real-ESRGAN general · {Strings.Speed_VeryFast}", ModelBadge.None, 7),
    };
}
