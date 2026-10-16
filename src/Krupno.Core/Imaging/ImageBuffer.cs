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

    /// <summary>
    /// Whether the image fits in one array and WIC can still save and show it: WIC wants (height + 1) × stride within
    /// int.MaxValue, a little less than Array.MaxLength. Without the second check an image right at the limit (80000×6710)
    /// was processed for minutes and then failed to save.
    /// </summary>
    public static bool CanAllocate(long width, long height) =>
        width > 0 && height > 0 && width * height * BytesPerPixel <= Array.MaxLength
        && width * BytesPerPixel * (height + 1) <= int.MaxValue;

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
    /// An image in which every fully transparent pixel takes the color of the nearest pixel that is at least partly visible
    /// (a copy; this image itself if it has no fully transparent pixels or nothing visible to take colors from).
    /// The network and the resampler mix neighboring colors, and the color stored under alpha 0 (usually black) would
    /// otherwise show up as a dark fringe along the edges.
    /// </summary>
    public ImageBuffer BleedTransparentColors()
    {
        var source = MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan());
        int count = Width * Height;
        int transparent = 0;
        foreach (uint pixel in source)
        {
            if (pixel >> 24 == 0)
                transparent++;
        }
        if (transparent == 0 || transparent == count)
            return this;

        var result = new ImageBuffer(Width, Height, (byte[])Pixels.Clone());
        var px = MemoryMarshal.Cast<byte, uint>(result.Pixels.AsSpan());

        // Breadth-first search from the visible pixels next to transparency at once: each transparent pixel gets the color of the one
        // that reached it first. Each pixel enters the queue once: the transparent ones plus at most 4 visible neighbors for each.
        var done = new bool[count];
        var queue = new int[(int)Math.Min(count, 5L * transparent)];
        int head = 0, tail = 0;
        for (int i = 0; i < count; i++)
        {
            if (px[i] >> 24 == 0)
                continue;
            done[i] = true;
            int x = i % Width;
            bool bordersTransparent = (x > 0 && px[i - 1] >> 24 == 0) || (x < Width - 1 && px[i + 1] >> 24 == 0)
                || (i >= Width && px[i - Width] >> 24 == 0) || (i + Width < count && px[i + Width] >> 24 == 0);
            if (bordersTransparent)
                queue[tail++] = i;
        }

        while (head < tail)
        {
            int i = queue[head++];
            int x = i % Width;
            uint color = px[i] & 0x00FFFFFF;
            if (x > 0)
                Visit(i - 1, color, px);
            if (x < Width - 1)
                Visit(i + 1, color, px);
            if (i >= Width)
                Visit(i - Width, color, px);
            if (i + Width < count)
                Visit(i + Width, color, px);
        }
        return result;

        void Visit(int n, uint color, Span<uint> pixels)
        {
            if (done[n])
                return;
            done[n] = true;
            pixels[n] = color; // alpha stays 0
            queue[tail++] = n;
        }
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
