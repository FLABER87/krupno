using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Krupno.Core.Imaging;
using Krupno.Core.Inference;

namespace Krupno.App.Infrastructure;

/// <summary>What the number under "Other" in "Scale" sets.</summary>
public enum CustomScaleMode
{
    Scale,
    Width,
    Height,
    LongSide,
}

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

    /// <summary>For an accelerator: its execution provider ("NvTensorRtRtxExecutionProvider"), with the device in <see cref="DeviceName"/>.</summary>
    public string? DeviceProvider { get; set; }

    /// <summary>How much of the network's result goes into the output, in percent.</summary>
    public int? Strength { get; set; }

    /// <summary>The final scale (×2, ×3, ×4, ×8); 0 for "Other", see <see cref="CustomScaleMode"/>.</summary>
    public double? Scale { get; set; }

    /// <summary>What the number under "Other" sets: a scale, or the width, height or long side of the result.</summary>
    public CustomScaleMode? CustomScaleMode { get; set; }

    /// <summary>The number under "Other", as typed.</summary>
    public string? CustomScaleValue { get; set; }

    public ImageFileFormat? Format { get; set; }

    public int? JpegQuality { get; set; }

    /// <summary>Where results go; null means next to the originals.</summary>
    public string? OutputFolder { get; set; }

    /// <summary>The tile size from "Settings"; 0 means no tiling.</summary>
    public int? TileSize { get; set; }

    /// <summary>"Seamless tiles" in "Settings": a 64 px tile overlap instead of 32.</summary>
    public bool? SeamlessTiles { get; set; }

    /// <summary>"Test-time augmentation" in "Settings": every pass in all 8 orientations, averaged.</summary>
    public bool? TestTimeAugmentation { get; set; }

    /// <summary>The window's language (a culture name from <see cref="UiLanguage.All"/>); null follows Windows.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// "Check for updates" in "Settings", once the user has changed it there; until then null, and the installer's choice applies
    /// (see <see cref="UpdateCheck.Decide"/>).
    /// </summary>
    public bool? CheckForUpdates { get; set; }

    /// <summary>The installation (its time) that was the last one when <see cref="CheckForUpdates"/> was chosen or reset.</summary>
    public string? CheckForUpdatesSetAt { get; set; }

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
