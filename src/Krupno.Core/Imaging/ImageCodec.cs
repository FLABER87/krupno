using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Krupno.Core.Imaging;

public enum ImageFileFormat
{
    Png,
    Jpeg,
    Tiff,
    Bmp,
}

/// <summary>A decoded image and everything worth carrying over to the result.</summary>
public sealed class LoadedImage
{
    public required ImageBuffer Image { get; init; }
    public ImageFileFormat? SourceFormat { get; init; }
    public double DpiX { get; init; } = 96;
    public double DpiY { get; init; } = 96;
    internal BitmapMetadata? Metadata { get; init; }
    internal ReadOnlyCollection<ColorContext>? ColorContexts { get; init; }
}

public sealed record SaveOptions
{
    /// <summary>JPEG quality, 1–100.</summary>
    public int JpegQuality { get; init; } = 95;

    /// <summary>The source to take DPI, the ICC profile and metadata from (if the format matches).</summary>
    public LoadedImage? Source { get; init; }
}

/// <summary>Reading and writing via WIC, the built-in Windows codecs, with no third-party libraries.</summary>
public static class ImageCodec
{
    /// <summary>
    /// Extensions we try to open. WebP/HEIC/AVIF only work if the matching extensions
    /// from the Microsoft Store are installed.
    /// </summary>
    public static IReadOnlySet<string> InputExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".tif", ".tiff", ".gif",
        ".webp", ".jxr", ".wdp", ".heic", ".heif", ".avif",
    };

    public static bool IsSupportedInput(string path) => InputExtensions.Contains(Path.GetExtension(path));

    public static string GetExtension(ImageFileFormat format) => format switch
    {
        ImageFileFormat.Png => ".png",
        ImageFileFormat.Jpeg => ".jpg",
        ImageFileFormat.Tiff => ".tif",
        ImageFileFormat.Bmp => ".bmp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static ImageFileFormat? FormatFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ImageFileFormat.Png,
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageFileFormat.Jpeg,
        ".tif" or ".tiff" => ImageFileFormat.Tiff,
        ".bmp" => ImageFileFormat.Bmp,
        _ => null,
    };

    public static LoadedImage Load(string path)
    {
        using var stream = File.OpenRead(path);
        BitmapDecoder decoder;
        BitmapFrame frame;
        ImageBuffer image;
        try
        {
            // IgnoreColorProfile: take the pixels as is and carry the ICC profile over to the result.
            decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
            BitmapSource source = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

            image = new ImageBuffer(source.PixelWidth, source.PixelHeight);
            source.CopyPixels(image.Pixels, image.Stride, 0);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException)
        {
            throw new NotSupportedException(DecodeError(path), ex);
        }

        var metadata = frame.Metadata as BitmapMetadata;
        image = image.ApplyExifOrientation(ReadOrientation(metadata));

        return new LoadedImage
        {
            Image = image,
            SourceFormat = decoder switch
            {
                PngBitmapDecoder => ImageFileFormat.Png,
                JpegBitmapDecoder => ImageFileFormat.Jpeg,
                TiffBitmapDecoder => ImageFileFormat.Tiff,
                BmpBitmapDecoder => ImageFileFormat.Bmp,
                _ => null,
            },
            DpiX = frame.DpiX > 0 ? frame.DpiX : 96,
            DpiY = frame.DpiY > 0 ? frame.DpiY : 96,
            Metadata = CloneMetadata(metadata),
            ColorContexts = TryGet(() => frame.ColorContexts),
        };
    }

    public static void Save(ImageBuffer image, string path, ImageFileFormat format, SaveOptions? options = null)
    {
        options ??= new SaveOptions();
        var src = options.Source;

        BitmapSource bitmap = BitmapSource.Create(image.Width, image.Height,
            src?.DpiX ?? 96, src?.DpiY ?? 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        if (format is ImageFileFormat.Jpeg or ImageFileFormat.Bmp || !image.HasTransparency())
            bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);

        // Metadata is carried over only into the same container: WIC does not convert it between formats.
        var metadata = src?.SourceFormat == format ? src.Metadata : null;
        var colorContexts = format == ImageFileFormat.Bmp ? null : src?.ColorContexts;

        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string tempPath = path + ".tmp";
        try
        {
            try
            {
                Encode(bitmap, tempPath, format, options.JpegQuality, metadata, colorContexts);
            }
            catch (Exception) when (metadata is not null || colorContexts is not null)
            {
                // Carrying metadata over is best-effort; if the codec rejects it, save without it.
                Encode(bitmap, tempPath, format, options.JpegQuality, null, null);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>A frozen BitmapSource for display in WPF (can be created on a background thread).</summary>
    public static BitmapSource ToBitmapSource(ImageBuffer image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96,
            PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// A fast thumbnail that honors EXIF rotation: WIC scales while decoding (for JPEG, right in the DCT),
    /// so the full image never gets into memory.
    /// </summary>
    public static BitmapSource LoadThumbnail(string path, int height)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(Path.GetFullPath(path));
        bitmap.DecodePixelHeight = height;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.EndInit();

        BitmapSource result = bitmap;
        int orientation = ReadOrientation(path);
        if (orientation is >= 2 and <= 8)
        {
            // Rotate clockwise first, then flip horizontally: this yields all eight EXIF variants.
            (double angle, bool flip) = orientation switch
            {
                2 => (0, true),
                3 => (180, false),
                4 => (180, true),
                5 => (90, true),
                6 => (90, false),
                7 => (270, true),
                _ => (270, false), // 8
            };
            var transform = new TransformGroup();
            transform.Children.Add(new RotateTransform(angle));
            if (flip)
                transform.Children.Add(new ScaleTransform(-1, 1));
            result = TryGet<BitmapSource>(() => new TransformedBitmap(bitmap, transform)) ?? bitmap;
        }

        result.Freeze();
        return result;
    }

    /// <summary>A clear reason: Microsoft Store formats lack a codec, anything else is a corrupt file.</summary>
    private static string DecodeError(string path)
    {
        string name = Path.GetFileName(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".webp" or ".heic" or ".heif" or ".avif" or ".jxr" or ".wdp"
            ? $"Windows cannot open \"{name}\": the {extension.TrimStart('.').ToUpperInvariant()} format requires an extension from the Microsoft Store."
            : $"Cannot open \"{name}\": the file is corrupt or is not an image.";
    }

    private static int ReadOrientation(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return ReadOrientation(frame.Metadata as BitmapMetadata);
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static void Encode(BitmapSource bitmap, string path, ImageFileFormat format, int jpegQuality,
        BitmapMetadata? metadata, ReadOnlyCollection<ColorContext>? colorContexts)
    {
        BitmapEncoder encoder = format switch
        {
            ImageFileFormat.Png => new PngBitmapEncoder(),
            ImageFileFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
            ImageFileFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            ImageFileFormat.Bmp => new BmpBitmapEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, colorContexts));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static int ReadOrientation(BitmapMetadata? metadata) =>
        TryGet(() => metadata?.GetQuery("System.Photo.Orientation")) is ushort value ? value : 1;

    private static BitmapMetadata? CloneMetadata(BitmapMetadata? metadata)
    {
        return TryGet(() =>
        {
            if (metadata is null)
                return null;

            var clone = metadata.Clone();
            // The rotation is already applied to the pixels; reset the tag so viewers don't rotate the image again.
            if (clone.ContainsQuery("System.Photo.Orientation"))
                clone.SetQuery("System.Photo.Orientation", (ushort)1);
            return clone;
        });
    }

    private static T? TryGet<T>(Func<T?> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception)
        {
            return default;
        }
    }
}
