using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
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
    /// <summary>sRGB, the color space WPF draws in (the Windows system profile).</summary>
    private static readonly Lazy<ColorContext?> Srgb = new(() => TryGet(() => new ColorContext(PixelFormats.Bgra32)));

    /// <summary>One row of BGRA probe colors for <see cref="DisplayProfile"/>: every combination of five levels per channel.</summary>
    private static readonly byte[] ProbeColors = MakeProbeColors();

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
            // IgnoreColorProfile: take the pixels as is and carry the ICC profile over to the result
            // (only the preview converts them, see ToBitmapSource).
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
        // WIC reports some broken files with other exceptions, for example ArgumentException "The image is missing a frame".
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or COMException
                                       or OverflowException)
        {
            throw new NotSupportedException(DecodeError(path), ex);
        }

        var metadata = frame.Metadata as BitmapMetadata;
        int orientation = ReadOrientation(metadata);
        image = image.ApplyExifOrientation(orientation);
        // A quarter turn swaps the axes, and with them the horizontal and vertical resolution.
        bool swapAxes = orientation is >= 5 and <= 8;
        double dpiX = frame.DpiX > 0 ? frame.DpiX : 96, dpiY = frame.DpiY > 0 ? frame.DpiY : 96;

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
            DpiX = swapAxes ? dpiY : dpiX,
            DpiY = swapAxes ? dpiX : dpiY,
            Metadata = CloneMetadata(metadata),
            ColorContexts = TryGet(() => frame.ColorContexts),
        };
    }

    /// <summary>
    /// Throws a clear error if the format can't hold an image of this size: the WIC PNG encoder stops at 65535 px on a side,
    /// JPEG at 65500. Called before processing too, so a panorama doesn't fail only after minutes of work.
    /// </summary>
    public static void EnsureCanSave(ImageFileFormat format, long width, long height)
    {
        int max = format switch
        {
            ImageFileFormat.Png => 65535,
            ImageFileFormat.Jpeg => 65500,
            _ => int.MaxValue,
        };
        if (width > max || height > max)
        {
            throw new InvalidOperationException(
                $"{format.ToString().ToUpperInvariant()} can't hold an image larger than {max} px on a side, and the result is " +
                $"{width}×{height}. Save as TIFF or choose a smaller scale.");
        }
    }

    public static void Save(ImageBuffer image, string path, ImageFileFormat format, SaveOptions? options = null)
    {
        EnsureCanSave(format, image.Width, image.Height);
        options ??= new SaveOptions();
        var src = options.Source;

        BitmapSource bitmap = BitmapSource.Create(image.Width, image.Height,
            src?.DpiX ?? 96, src?.DpiY ?? 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        if (format is ImageFileFormat.Jpeg or ImageFileFormat.Bmp || !image.HasTransparency())
            bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);

        // Metadata is carried over only into the same container: WIC does not convert it between formats.
        var metadata = src?.SourceFormat == format ? src.Metadata : null;
        var colorContexts = src?.ColorContexts is { Count: > 0 } contexts ? contexts : null;
        if (format == ImageFileFormat.Bmp && colorContexts is not null)
        {
            // BMP can't carry the profile, so convert the pixels to sRGB instead of silently changing their meaning.
            bitmap = TryGet<BitmapSource>(() => new ColorConvertedBitmap(bitmap, colorContexts[0],
                new ColorContext(bitmap.Format), bitmap.Format)) ?? bitmap;
            colorContexts = null;
        }

        // Carrying metadata and the profile over is best-effort: if the codec rejects them, drop the metadata first, then the profile.
        var attempts = new List<(BitmapMetadata? Metadata, ReadOnlyCollection<ColorContext>? ColorContexts)> { (metadata, colorContexts) };
        if (metadata is not null && colorContexts is not null)
            attempts.Add((null, colorContexts));
        if (metadata is not null || colorContexts is not null)
            attempts.Add((null, null));

        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        // A short name of our own: an existing "<name>.tmp" may belong to the user, another Krupno may be saving the same file,
        // and a suffix on a long name could exceed the 255-character limit of a file name.
        string tempPath = Path.Combine(directory, $"krupno-{Guid.NewGuid():N}.tmp");
        try
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    Encode(bitmap, tempPath, format, options.JpegQuality, attempts[i].Metadata, attempts[i].ColorContexts);
                    break;
                }
                catch (Exception) when (i < attempts.Count - 1)
                {
                }
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// A frozen BitmapSource for display in WPF (can be created on a background thread). WPF draws pixels as sRGB, so pixels
    /// in another embedded profile (Adobe RGB, Display P3) are converted to sRGB here, for display only: what is processed
    /// and saved keeps the original pixels and profile.
    /// </summary>
    /// <param name="source">The loaded image whose ICC profile describes the pixels: the original also for its upscaled result.</param>
    /// <param name="convertInPlace">
    /// The caller no longer needs <paramref name="image"/>: the conversion overwrites it instead of taking another full-size copy
    /// (a result can take a gigabyte or two).
    /// </param>
    public static BitmapSource ToBitmapSource(ImageBuffer image, LoadedImage? source = null, bool convertInPlace = false)
    {
        if (DisplayProfile(source?.ColorContexts) is { } profile)
        {
            var converted = convertInPlace ? image : new ImageBuffer(image.Width, image.Height);
            ConvertToSrgb(image, converted, profile);
            image = converted;
        }

        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96,
            PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// A fast thumbnail that honors EXIF rotation and the color profile, like <see cref="ToBitmapSource"/>: WIC scales while
    /// decoding (for JPEG, right in the DCT), so the full image never gets into memory.
    /// </summary>
    public static BitmapSource LoadThumbnail(string path, int height)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(Path.GetFullPath(path));
        bitmap.DecodePixelHeight = height;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        // The profile is applied below, the same way as for the preview, rather than by WPF.
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.EndInit();

        BitmapSource result = bitmap;
        var (orientation, colorContexts) = ReadFrameInfo(path);
        if (DisplayProfile(colorContexts) is { } profile && Srgb.Value is { } srgb)
        {
            // Converted right away (CachedBitmap) rather than lazily on the UI thread whenever WPF reads the pixels.
            result = TryGet<BitmapSource>(() => new CachedBitmap(
                new ColorConvertedBitmap(new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0), profile, srgb, PixelFormats.Bgra32),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad)) ?? bitmap;
        }

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
            var unrotated = result;
            result = TryGet<BitmapSource>(() => new TransformedBitmap(unrotated, transform)) ?? unrotated;
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

    /// <summary>The EXIF orientation and the embedded color profiles, read without decoding the pixels.</summary>
    private static (int Orientation, ReadOnlyCollection<ColorContext>? ColorContexts) ReadFrameInfo(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (ReadOrientation(frame.Metadata as BitmapMetadata), TryGet(() => frame.ColorContexts));
        }
        catch (Exception)
        {
            return (1, null);
        }
    }

    /// <summary>
    /// The embedded profile to convert from for display, or null if there is nothing to convert: no profile, a profile that
    /// matches sRGB within a couple of levels (most photos), or one WIC can't apply to RGB pixels (broken, grayscale, CMYK).
    /// Decided by converting a grid of probe colors, so any form of the profile (matrix, table, ICC v2 or v4) is judged alike.
    /// </summary>
    private static ColorContext? DisplayProfile(ReadOnlyCollection<ColorContext>? colorContexts)
    {
        if (colorContexts is not { Count: > 0 } || Srgb.Value is not { } srgb)
            return null;

        var profile = colorContexts[0];
        return TryGet(() =>
        {
            int width = ProbeColors.Length / ImageBuffer.BytesPerPixel;
            var probe = BitmapSource.Create(width, 1, 96, 96, PixelFormats.Bgra32, null, ProbeColors, ProbeColors.Length);
            var converted = new byte[ProbeColors.Length];
            new ColorConvertedBitmap(probe, profile, srgb, PixelFormats.Bgra32).CopyPixels(converted, ProbeColors.Length, 0);
            for (int i = 0; i < converted.Length; i++)
            {
                if (Math.Abs(converted[i] - ProbeColors[i]) > 2)
                    return profile;
            }
            return null;
        });
    }

    /// <summary>
    /// Converts the pixels from <paramref name="profile"/> to sRGB, a strip of rows at a time, so the conversion needs only
    /// a few megabytes on top of the images. <paramref name="destination"/> may be <paramref name="source"/> itself.
    /// </summary>
    private static void ConvertToSrgb(ImageBuffer source, ImageBuffer destination, ColorContext profile)
    {
        var srgb = Srgb.Value!;
        int stride = source.Stride;
        int rows = Math.Clamp((4 << 20) / stride, 1, source.Height);
        var strip = GC.AllocateUninitializedArray<byte>(rows * stride);
        for (int y = 0; y < source.Height; y += rows)
        {
            int count = Math.Min(rows, source.Height - y);
            Buffer.BlockCopy(source.Pixels, y * stride, strip, 0, count * stride);
            var bitmap = BitmapSource.Create(source.Width, count, 96, 96, PixelFormats.Bgra32, null, strip, stride);
            new ColorConvertedBitmap(bitmap, profile, srgb, PixelFormats.Bgra32).CopyPixels(destination.Pixels, stride, y * stride);
        }
    }

    private static byte[] MakeProbeColors()
    {
        byte[] levels = [0, 64, 128, 192, 255];
        var colors = new List<byte>();
        foreach (byte b in levels)
        {
            foreach (byte g in levels)
            {
                foreach (byte r in levels)
                    colors.AddRange([b, g, r, 255]);
            }
        }
        return [.. colors];
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
            // An unfrozen clone belongs to the loading thread: an encoder on another thread (the app loads and saves in
            // separate Task.Run calls) fails on it, and the image would be saved without metadata and without the ICC profile.
            clone.Freeze();
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
