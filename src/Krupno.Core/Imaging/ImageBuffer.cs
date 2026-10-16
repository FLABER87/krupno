using System.Runtime.InteropServices;

namespace Krupno.Core.Imaging;

/// <summary>
/// In-memory image: 8 bits per channel, BGRA byte order (native to WIC/WPF), rows without padding.
/// </summary>
public sealed class ImageBuffer
{
    public const int BytesPerPixel = 4;

    public ImageBuffer(int width, int height)
        : this(width, height, AllocatePixels(width, height))
    {
    }

    public ImageBuffer(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != (long)width * height * BytesPerPixel)
            throw new ArgumentException("The buffer size does not match the image dimensions.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * BytesPerPixel;
    public byte[] Pixels { get; }

    public static bool CanAllocate(long width, long height) =>
        width > 0 && height > 0 && width * height * BytesPerPixel <= Array.MaxLength;

    /// <summary>Whether at least one pixel is not fully opaque.</summary>
    public bool HasTransparency()
    {
        var px = Pixels;
        for (int i = 3; i < px.Length; i += BytesPerPixel)
        {
            if (px[i] != 255)
                return true;
        }
        return false;
    }

    public byte[] ExtractAlpha()
    {
        var alpha = GC.AllocateUninitializedArray<byte>(Width * Height);
        var px = Pixels;
        for (int i = 0; i < alpha.Length; i++)
            alpha[i] = px[i * BytesPerPixel + 3];
        return alpha;
    }

    public void SetAlpha(ReadOnlySpan<byte> alpha)
    {
        if (alpha.Length != Width * Height)
            throw new ArgumentException("The alpha channel size does not match the image.", nameof(alpha));

        var px = Pixels;
        for (int i = 0; i < alpha.Length; i++)
            px[i * BytesPerPixel + 3] = alpha[i];
    }

    /// <summary>
    /// Applies the EXIF orientation (1–8): returns the image the way it should be displayed.
    /// </summary>
    public ImageBuffer ApplyExifOrientation(int orientation)
    {
        if (orientation is < 2 or > 8)
            return this;

        int w = Width, h = Height;
        bool swap = orientation >= 5;
        var dst = new ImageBuffer(swap ? h : w, swap ? w : h);
        var s = MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan());
        var d = MemoryMarshal.Cast<byte, uint>(dst.Pixels.AsSpan());

        for (int y = 0; y < dst.Height; y++)
        {
            for (int x = 0; x < dst.Width; x++)
            {
                // For each output pixel, look up the source pixel (inverse transform).
                (int sx, int sy) = orientation switch
                {
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),
                    6 => (y, h - 1 - x),
                    7 => (w - 1 - y, h - 1 - x),
                    _ => (w - 1 - y, x), // 8
                };
                d[y * dst.Width + x] = s[sy * w + sx];
            }
        }
        return dst;
    }

    private static byte[] AllocatePixels(int width, int height)
    {
        if (!CanAllocate(width, height))
            throw new InvalidOperationException($"The image {width}×{height} is too large to process in memory.");
        return GC.AllocateUninitializedArray<byte>(width * height * BytesPerPixel);
    }
}
