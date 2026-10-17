using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Krupno.Core.Imaging;
using Krupno.Core.Inference;

namespace Krupno.App.Infrastructure;

/// <summary>User choices kept between launches: %LOCALAPPDATA%\Krupno\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Krupno", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Model name: the .onnx file name without the extension.</summary>
    public string? Model { get; set; }

    public DeviceKind? DeviceKind { get; set; }

    /// <summary>GPU name: the DXGI index can change, for example when a monitor is connected to another GPU.</summary>
    public string? DeviceName { get; set; }

    public int? DeviceIndex { get; set; }

    /// <summary>How much of the network's result goes into the output, in percent.</summary>
    public int? Strength { get; set; }

    /// <summary>The final scale (×2, ×3, ×4, ×8).</summary>
    public double? Scale { get; set; }

    public ImageFileFormat? Format { get; set; }

    public int? JpegQuality { get; set; }

    /// <summary>Where results go; null means next to the originals.</summary>
    public string? OutputFolder { get; set; }

    /// <summary>The tile size from "Advanced"; 0 means no tiling.</summary>
    public int? TileSize { get; set; }

    /// <summary>"Seamless tiles" in "Advanced": a 64 px tile overlap instead of 32.</summary>
    public bool? SeamlessTiles { get; set; }

    public WindowBounds? Window { get; set; }

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Settings aren't critical: if saving fails, just carry on.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
