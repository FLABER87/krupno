using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Krupno.App.Properties;
using Krupno.Core.Imaging;

namespace Krupno.App.Infrastructure;

/// <summary>
/// An image pasted with Ctrl+V (a screenshot, "Copy image" in a browser). The queue and the results work with files, so it is
/// saved as a PNG in Pictures\Krupno and opened from there, and the result lands next to it like for any other file.
/// </summary>
internal sealed class PastedImage
{
    /// <summary>Browsers, Office and the Snipping Tool also put the image on the clipboard as a PNG file.</summary>
    private static readonly string[] PngFormats = ["PNG", "image/png"];

    private readonly byte[]? _png;
    private readonly ImageBuffer? _bitmap;

    private PastedImage(byte[]? png, ImageBuffer? bitmap)
    {
        _png = png;
        _bitmap = bitmap;
    }

    public static string Folder { get; } = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } pictures
        ? Path.Combine(pictures, "Krupno")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Krupno", "Pasted");

    /// <summary>The image on the clipboard, or null if there is none. Must be called on the UI thread, like any clipboard access.</summary>
    public static PastedImage? FromClipboard()
    {
        // The PNG keeps transparency and the exact pixels, so it is saved as is.
        foreach (string format in PngFormats)
        {
            if (Clipboard.ContainsData(format) && ReadBytes(Clipboard.GetData(format)) is { } png && IsPng(png))
                return new PastedImage(png, null);
        }
        return Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap ? new PastedImage(null, ToImage(bitmap)) : null;
    }

    /// <summary>Writes the image to a new file in <see cref="Folder"/> and returns its path.</summary>
    public string Save()
    {
        Directory.CreateDirectory(Folder);
        string name = string.Format(Strings.Pasted_FileName, DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture));
        string path = Path.Combine(Folder, name + ".png");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(Folder, $"{name} ({n}).png");

        if (_png is not null)
            File.WriteAllBytes(path, _png);
        else
            ImageCodec.Save(_bitmap!, path, ImageFileFormat.Png);
        return path;
    }

    private static byte[]? ReadBytes(object? data)
    {
        switch (data)
        {
            case byte[] bytes:
                return bytes;
            case MemoryStream memory:
                return memory.ToArray();
            case Stream stream:
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            default:
                return null;
        }
    }

    private static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

    /// <summary>
    /// A bitmap from the clipboard as BGRA. Most apps leave the fourth byte of a 32-bit clipboard bitmap at 0, which would read
    /// as fully transparent (and premultiplied colors would turn black), so its bytes are taken as stored and an image without
    /// a single visible pixel is made opaque.
    /// </summary>
    private static ImageBuffer ToImage(BitmapSource bitmap)
    {
        var image = new ImageBuffer(bitmap.PixelWidth, bitmap.PixelHeight);
        var format = bitmap.Format;
        bool fourBytes = format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Bgr32;
        (fourBytes ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0))
            .CopyPixels(image.Pixels, image.Stride, 0);

        var px = image.Pixels;
        bool visible = false;
        for (int i = 3; i < px.Length && !visible; i += ImageBuffer.BytesPerPixel)
            visible = px[i] != 0;

        if (format == PixelFormats.Bgr32 || !visible)
        {
            for (int i = 3; i < px.Length; i += ImageBuffer.BytesPerPixel)
                px[i] = 255;
        }
        else if (format == PixelFormats.Pbgra32)
        {
            // Real transparency in premultiplied colors: let WIC convert them to straight ones.
            new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(image.Pixels, image.Stride, 0);
        }
        return image;
    }
}
