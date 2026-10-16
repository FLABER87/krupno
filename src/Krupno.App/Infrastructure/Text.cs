using System.Globalization;
using Krupno.Core.Inference;

namespace Krupno.App.Infrastructure;

/// <summary>UI text formatting: plurals, sizes, times, device names.</summary>
public static class Text
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>1 file, 2 files.</summary>
    public static string Plural(int count, string one, string many) => $"{count} {(Math.Abs(count) == 1 ? one : many)}";

    public static string Files(int count) => Plural(count, "file", "files");

    public static string Size(int width, int height) => $"{width} × {height}";

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => (bytes / 1024.0 / 1024).ToString("0.0", Culture) + " MB",
        _ => (bytes / 1024.0 / 1024 / 1024).ToString("0.00", Culture) + " GB",
    };

    public static string Seconds(TimeSpan time) => time.TotalSeconds < 60
        ? time.TotalSeconds.ToString(time.TotalSeconds < 10 ? "0.0" : "0", Culture) + " s"
        : $"{(int)time.TotalMinutes} min {time.Seconds} s";

    /// <summary>"Time left" estimate: rounded coarsely so the number doesn't jitter.</summary>
    public static string Remaining(TimeSpan time) => time.TotalSeconds switch
    {
        < 5 => "almost done",
        < 60 => $"≈ {Math.Ceiling(time.TotalSeconds / 5) * 5:0} s",
        _ => $"≈ {Math.Ceiling(time.TotalMinutes)} min",
    };

    public static string Scale(double scale) => "×" + scale.ToString("0.##", Culture);

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
        _ => "Auto",
    };
}
