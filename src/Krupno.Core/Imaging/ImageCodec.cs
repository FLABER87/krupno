using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Krupno.Core.Properties;

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

/// <summary>
/// Reading and writing images via WIC, the built-in Windows codecs. A WebP that Windows has no codec for is read through libwebp;
/// animated GIF and WebP are in <see cref="Animation"/>.
/// </summary>
public static class ImageCodec
{
    /// <summary>sRGB, the color space WPF draws in (the Windows system profile).</summary>
    private static readonly Lazy<ColorContext?> Srgb = new(() => TryGet(() => new ColorContext(PixelFormats.Bgra32)));

    /// <summary>One row of BGRA probe colors for <see cref="DisplayProfile"/>: every combination of five levels per channel.</summary>
    private static readonly byte[] ProbeColors = MakeProbeColors();

    /// <summary>
    /// Extensions we try to open. HEIC/AVIF only work if the matching extensions from the Microsoft Store are installed; WebP
    /// without its extension is read through libwebp.
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
        ReadOnlyCollection<ColorContext>? colorContexts;
        try
        {
            // IgnoreColorProfile: take the pixels as is and carry an RGB profile over to the result (only the preview
            // converts them, see ToBitmapSource). CMYK and grayscale images are converted right away, see DecodePixels.
            decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
            (image, colorContexts) = DecodePixels(frame, TryGet(() => frame.ColorContexts));
        }
        // WIC reports some broken files with other exceptions, for example ArgumentException "The image is missing a frame".
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or COMException
                                       or OverflowException)
        {
            // Without the Microsoft Store's codec (Windows 10 LTSC has none), libwebp reads a WebP: its first frame, as is.
            if (WebPFallback(path) is { } webp)
                return new LoadedImage { Image = webp };
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
            ColorContexts = colorContexts,
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
                string.Format(Strings.Error_FormatSizeLimit, format.ToString().ToUpperInvariant(), max, width, height));
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
        // The pixels written are RGB, so only an RGB profile can go with them (Load converts CMYK and grayscale images to sRGB),
        // and only the first one, which the pixels are shown with.
        var colorContexts = src?.ColorContexts is { Count: > 0 } contexts && IsRgb(contexts[0])
            ? new ReadOnlyCollection<ColorContext>([contexts[0]])
            : null;
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
    public static BitmapSource ToBitmapSource(ImageBuffer image, LoadedImage? source = null, bool convertInPlace = false) =>
        ToDisplayBitmap(image, source?.ColorContexts, convertInPlace);

    /// <summary>
    /// A fast thumbnail that honors EXIF rotation and the color profile the same way as <see cref="Load"/> and
    /// <see cref="ToBitmapSource"/> do for the preview: WIC scales while decoding (for JPEG, right in the DCT), so the full
    /// image never gets into memory.
    /// </summary>
    public static BitmapSource LoadThumbnail(string path, int height)
    {
        try
        {
            return LoadThumbnailWic(path, height);
        }
        catch (Exception) when (WebPFallback(path) is { } webp)
        {
            int width = Math.Max(1, (int)Math.Round((double)webp.Width * height / webp.Height));
            return ToDisplayBitmap(Resampler.Resize(webp, width, Math.Max(1, height)), null, convertInPlace: true);
        }
    }

    /// <summary>A WebP's first frame through libwebp, for when WIC can't read it; null for another format or a broken file.</summary>
    private static ImageBuffer? WebPFallback(string path)
    {
        if (!Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return WebPAnimation.LoadFirstFrame(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or DllNotFoundException
                                       or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static BitmapSource LoadThumbnailWic(string path, int height)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(Path.GetFullPath(path));
        bitmap.DecodePixelHeight = height;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        // The profile is applied below, the same way as for the preview, rather than by WPF. A CMYK or grayscale profile
        // applies only to pixels in their own format.
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
        bitmap.EndInit();

        BitmapSource scaled = bitmap;
        var (orientation, colorContexts, format) = ReadFrameInfo(path);
        // The WIC scaler (JPEG scales while decoding instead) turns CMYK into BGRA, which the CMYK profile no longer applies to,
        // so such a frame is converted to sRGB first. Gray levels convert from any pixel format, see DecodePixels.
        if (bitmap.Format != format && colorContexts is { Count: > 0 } && ColorSpace(colorContexts[0]) is not (null or "RGB " or "GRAY")
            && TryGet(() => ConvertThenScale(path, colorContexts[0], height)) is { } converted)
        {
            scaled = converted;
            colorContexts = null;
        }

        var (image, profile) = DecodePixels(scaled, colorContexts);
        return ToDisplayBitmap(image.ApplyExifOrientation(orientation), profile, convertInPlace: true);
    }

    /// <summary>The image converted from <paramref name="profile"/> to sRGB and then scaled, without decoding it whole into memory.</summary>
    private static BitmapSource ConvertThenScale(string path, ColorContext profile, int height)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None).Frames[0];
        var converted = SrgbConversion(frame, profile) ?? throw new NotSupportedException();
        double scale = (double)height / frame.PixelHeight;
        // CachedBitmap runs the chain right away, while the file is open.
        return new CachedBitmap(new TransformedBitmap(converted, new ScaleTransform(scale, scale)),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }

    /// <summary>A clear reason: Microsoft Store formats lack a codec, anything else is a corrupt file.</summary>
    private static string DecodeError(string path)
    {
        string name = Path.GetFileName(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".webp" or ".heic" or ".heif" or ".avif" or ".jxr" or ".wdp"
            ? string.Format(Strings.Error_NeedsStoreExtension, name, extension.TrimStart('.').ToUpperInvariant())
            : string.Format(Strings.Error_CannotOpen, name);
    }

    /// <summary>The EXIF orientation, the embedded color profiles and the stored pixel format, read without decoding the pixels.</summary>
    private static (int Orientation, ReadOnlyCollection<ColorContext>? ColorContexts, PixelFormat Format) ReadFrameInfo(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            return (ReadOrientation(frame.Metadata as BitmapMetadata), TryGet(() => frame.ColorContexts), frame.Format);
        }
        catch (Exception)
        {
            return (1, null, default);
        }
    }

    /// <summary>
    /// The frame's pixels as BGRA, and the profiles that describe them. An RGB profile goes with the pixels as stored. A CMYK or
    /// grayscale profile can't describe RGB pixels, so the frame is converted with it to sRGB, the way color-managed viewers
    /// show it, and the result is left untagged (untagged means sRGB). A profile WIC can't apply to the pixels (a grayscale
    /// profile on a color image) is dropped.
    /// </summary>
    private static (ImageBuffer Image, ReadOnlyCollection<ColorContext>? ColorContexts) DecodePixels(
        BitmapSource frame, ReadOnlyCollection<ColorContext>? colorContexts)
    {
        var image = new ImageBuffer(frame.PixelWidth, frame.PixelHeight);
        var profile = colorContexts is { Count: > 0 } ? colorContexts[0] : null;
        string? colorSpace = profile is null ? null : ColorSpace(profile);
        if (profile is null || colorSpace == "RGB ")
        {
            CopyAsBgra32(frame, image);
            return (image, colorContexts);
        }

        if (colorSpace == "GRAY")
        {
            // Gray levels convert through a 16-bit grayscale copy: from 8 bits WIC rounds dark levels off by up to 4. A grayscale
            // image with alpha decodes as BGRA, so the alpha is set aside; a color image keeps its colors.
            CopyAsBgra32(frame, image);
            if (image.IsGrayscale()
                && SrgbConversion(new FormatConvertedBitmap(frame, PixelFormats.Gray16, null, 0), profile) is { } converted)
            {
                var alpha = image.HasTransparency() ? image.ExtractAlpha() : null;
                converted.CopyPixels(image.Pixels, image.Stride, 0);
                if (alpha is not null)
                    image.SetAlpha(alpha);
            }
        }
        else if (SrgbConversion(frame, profile) is { } converted)
        {
            // CMYK pixels convert directly.
            converted.CopyPixels(image.Pixels, image.Stride, 0);
        }
        else
        {
            CopyAsBgra32(frame, image);
        }
        return (image, null);
    }

    /// <summary>The pixels converted from <paramref name="profile"/> to sRGB, or null if WIC can't apply the profile to their format.</summary>
    private static BitmapSource? SrgbConversion(BitmapSource source, ColorContext profile) =>
        Srgb.Value is { } srgb
            ? TryGet<BitmapSource>(() => new ColorConvertedBitmap(source, profile, srgb, PixelFormats.Bgra32))
            : null;

    private static void CopyAsBgra32(BitmapSource source, ImageBuffer image) =>
        (source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0))
            .CopyPixels(image.Pixels, image.Stride, 0);

    /// <summary>The color space of an ICC profile from its header ("RGB ", "CMYK", "GRAY" and so on), or null if it can't be read.</summary>
    private static string? ColorSpace(ColorContext profile) => TryGet(() =>
    {
        using var stream = profile.OpenProfileStream();
        var header = new byte[20];
        stream.ReadExactly(header);
        return Encoding.ASCII.GetString(header, 16, 4);
    });

    private static bool IsRgb(ColorContext profile) => ColorSpace(profile) == "RGB ";

    private static BitmapSource ToDisplayBitmap(ImageBuffer image, ReadOnlyCollection<ColorContext>? colorContexts, bool convertInPlace)
    {
        if (DisplayProfile(colorContexts) is { } profile)
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
    /// The embedded profile to convert from for display, or null if there is nothing to convert: no profile, one that isn't RGB
    /// (<see cref="DecodePixels"/> has already applied a CMYK or grayscale profile), a profile that matches sRGB within a couple
    /// of levels (most photos), or one WIC can't apply (broken). Decided by converting a grid of probe colors, so any form of
    /// the profile (matrix, table, ICC v2 or v4) is judged alike.
    /// </summary>
    private static ColorContext? DisplayProfile(ReadOnlyCollection<ColorContext>? colorContexts)
    {
        if (colorContexts is not { Count: > 0 } || Srgb.Value is not { } srgb || !IsRgb(colorContexts[0]))
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
            RemoveProfile(clone);
            // An unfrozen clone belongs to the loading thread: an encoder on another thread (the app loads and saves in
            // separate Task.Run calls) fails on it, and the image would be saved without metadata and without the ICC profile.
            clone.Freeze();
            return clone;
        });
    }

    /// <summary>
    /// Removes the copy of the ICC profile that PNG, TIFF and JPEG metadata holds. The profile goes to the result as a color
    /// context only (see <see cref="Save"/>); the copy would come along even where it doesn't fit, such as a CMYK profile in
    /// an RGB JPEG.
    /// </summary>
    private static void RemoveProfile(BitmapMetadata metadata)
    {
        var queries = new List<string> { "/iCCP", "/ifd/{ushort=34675}" };
        // JPEG keeps the profile in APP2 segments, which WIC exposes as unknown blocks: "/unknown", "/[1]unknown" and so on.
        foreach (string query in metadata)
        {
            if (query.EndsWith("unknown", StringComparison.Ordinal)
                && TryGet(() => metadata.GetQuery(query + "/{}")) is BitmapMetadataBlob blob
                && blob.GetBlobValue().AsSpan() is var bytes
                && bytes[..Math.Min(bytes.Length, 32)].IndexOf("ICC_PROFILE\0"u8) >= 0)
            {
                queries.Add(query);
            }
        }

        // The last one first: removing a block renumbers the ones after it.
        for (int i = queries.Count - 1; i >= 0; i--)
        {
            try
            {
                if (metadata.ContainsQuery(queries[i]))
                    metadata.RemoveQuery(queries[i]);
            }
            catch (Exception)
            {
                // Not a query this container knows.
            }
        }
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
