using System.Globalization;
using Krupno.App.Properties;
using Krupno.Core.Inference;
using Krupno.Core.Localization;
using Krupno.Core.Processing;

namespace Krupno.App.Infrastructure;

/// <summary>
/// UI text formatting: plurals, sizes, times, device names. The texts come from Properties/Strings.resx in the window's
/// language; pass <see cref="CultureInfo.InvariantCulture"/> as <c>culture</c> for the English text the log keeps.
/// </summary>
public static class Text
{
    /// <summary>"1 file", "2 files".</summary>
    public static string Files(int count, CultureInfo? culture = null) => Plurals.Format(Get(nameof(Strings.Files), culture), count, culture);

    public static string Size(int width, int height) => $"{width} × {height}";

    public static string Bytes(long bytes, CultureInfo? culture = null) => bytes switch
    {
        < 1024 * 1024 => Format(culture, nameof(Strings.Unit_KB), Math.Max(1, bytes / 1024)),
        < 1024L * 1024 * 1024 => Format(culture, nameof(Strings.Unit_MB), (bytes / 1024.0 / 1024).ToString("0.0", Numbers(culture))),
        _ => Format(culture, nameof(Strings.Unit_GB), (bytes / 1024.0 / 1024 / 1024).ToString("0.00", Numbers(culture))),
    };

    public static string Seconds(TimeSpan time, CultureInfo? culture = null) => time.TotalSeconds < 60
        ? Format(culture, nameof(Strings.Time_Seconds), time.TotalSeconds.ToString(time.TotalSeconds < 10 ? "0.0" : "0", Numbers(culture)))
        : Format(culture, nameof(Strings.Time_MinutesSeconds), (int)time.TotalMinutes, time.Seconds);

    /// <summary>"Time left" estimate: rounded coarsely so the number doesn't jitter.</summary>
    public static string Remaining(TimeSpan time) => time.TotalSeconds switch
    {
        < 5 => Strings.Remaining_AlmostDone,
        < 60 => string.Format(Strings.Remaining_Seconds, Math.Ceiling(time.TotalSeconds / 5) * 5),
        _ => string.Format(Strings.Remaining_Minutes, Math.Ceiling(time.TotalMinutes)),
    };

    public static string Scale(double scale, CultureInfo? culture = null) => "×" + scale.ToString("0.##", Numbers(culture));

    /// <summary>"×4", or for a target size "to 3840 px wide", "to 2160 px high", "to 3840 px long side".</summary>
    public static string ScaleChoice(double scale, ScaleTarget? target, CultureInfo? culture = null) => target?.Side switch
    {
        null => Scale(scale, culture),
        TargetSide.Width => Format(culture, nameof(Strings.Target_Width), target.Pixels),
        TargetSide.Height => Format(culture, nameof(Strings.Target_Height), target.Pixels),
        _ => Format(culture, nameof(Strings.Target_LongSide), target.Pixels),
    };

    /// <summary>"NVIDIA GeForce RTX 3080" → "RTX 3080", "Intel(R) UHD Graphics 770" → "UHD Graphics 770".</summary>
    public static string ShortAdapterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "GPU";

        string result = name.Replace("(R)", "").Replace("(TM)", "");
        foreach (var prefix in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD ", "Intel " })
        {
            if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                result = result[prefix.Length..];
                break;
            }
        }
        return result.Trim();
    }

    public static string Device(ExecutionDevice device) => device.Kind switch
    {
        DeviceKind.Cpu => "CPU",
        DeviceKind.Gpu => ShortAdapterName(device.AdapterName),
        DeviceKind.Accelerator => $"{ShortAdapterName(device.AdapterName)} · {Accelerators.ProviderName(device.Provider!)}",
        _ => Strings.Auto,
    };

    private static string Get(string key, CultureInfo? culture) => Strings.ResourceManager.GetString(key, culture) ?? key;

    private static string Format(CultureInfo? culture, string key, params object?[] args) =>
        string.Format(Numbers(culture), Get(key, culture), args);

    /// <summary>The number format: the user's own, or the invariant one for the log.</summary>
    private static CultureInfo Numbers(CultureInfo? culture) => culture ?? CultureInfo.CurrentCulture;
}
