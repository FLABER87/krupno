using Krupno.Core.Imaging;

namespace Krupno.Core.Tests;

internal static class TestImages
{
    /// <summary>The synthetic models from tests/fixtures/models, copied next to the tests.</summary>
    public static string Model(string fileName) => Path.Combine(AppContext.BaseDirectory, "models", fileName);

    /// <summary>Random BGRA pixels, opaque unless <paramref name="alpha"/> is set.</summary>
    public static ImageBuffer Noise(int width, int height, int seed = 1, bool alpha = false)
    {
        var image = new ImageBuffer(width, height);
        new Random(seed).NextBytes(image.Pixels);
        if (!alpha)
        {
            for (int i = 3; i < image.Pixels.Length; i += ImageBuffer.BytesPerPixel)
                image.Pixels[i] = 255;
        }
        return image;
    }

    public static ImageBuffer Fill(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var image = new ImageBuffer(width, height);
        for (int i = 0; i < image.Pixels.Length; i += ImageBuffer.BytesPerPixel)
        {
            image.Pixels[i] = b;
            image.Pixels[i + 1] = g;
            image.Pixels[i + 2] = r;
            image.Pixels[i + 3] = a;
        }
        return image;
    }

    /// <summary>
    /// What the fixture models compute: a nearest-neighbor enlargement by <paramref name="scale"/> that keeps only the
    /// channels set in <paramref name="keepB"/>, <paramref name="keepG"/>, <paramref name="keepR"/>, opaque.
    /// </summary>
    public static ImageBuffer Nearest(ImageBuffer source, int scale, bool keepB, bool keepG, bool keepR)
    {
        var result = new ImageBuffer(source.Width * scale, source.Height * scale);
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                int s = ((y / scale) * source.Width + x / scale) * ImageBuffer.BytesPerPixel;
                int d = (y * result.Width + x) * ImageBuffer.BytesPerPixel;
                result.Pixels[d] = keepB ? source.Pixels[s] : (byte)0;
                result.Pixels[d + 1] = keepG ? source.Pixels[s + 1] : (byte)0;
                result.Pixels[d + 2] = keepR ? source.Pixels[s + 2] : (byte)0;
                result.Pixels[d + 3] = 255;
            }
        }
        return result;
    }

    /// <summary>The first differing pixel, for a readable failure; null if the images are the same.</summary>
    public static string? Difference(ImageBuffer expected, ImageBuffer actual)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
            return $"size {actual.Width}×{actual.Height}, expected {expected.Width}×{expected.Height}";
        for (int i = 0; i < expected.Pixels.Length; i++)
        {
            if (expected.Pixels[i] != actual.Pixels[i])
            {
                int pixel = i / ImageBuffer.BytesPerPixel;
                return $"pixel ({pixel % expected.Width}, {pixel / expected.Width}) channel {i % ImageBuffer.BytesPerPixel}: " +
                       $"{actual.Pixels[i]}, expected {expected.Pixels[i]}";
            }
        }
        return null;
    }
}

/// <summary>Reports on the calling thread, unlike <see cref="Progress{T}"/>, so everything is in by the time the run returns.</summary>
internal sealed class ProgressLog<T> : IProgress<T>
{
    public List<T> Reports { get; } = [];

    public void Report(T value)
    {
        lock (Reports)
            Reports.Add(value);
    }
}
