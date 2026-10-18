using System.Windows.Media.Imaging;
using Krupno.Core.Imaging;
using Krupno.Core.Inference;
using Krupno.Core.Processing;

namespace Krupno.Core.Tests;

/// <summary>Animated GIF and WebP: writing and reading them back, and upscaling one frame by frame.</summary>
public sealed class AnimationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"krupno-animation-{Guid.NewGuid():N}");

    public AnimationTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string P(string name) => Path.Combine(_root, name);

    /// <summary>A few colors in blocks, with a moving square and, if asked, a transparent corner.</summary>
    private static ImageBuffer Frame(int width, int height, int step, bool transparentCorner = false)
    {
        var image = new ImageBuffer(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                bool square = x >= step * 3 && x < step * 3 + 6 && y >= 4 && y < 10;
                image.Pixels[o] = (byte)(square ? 20 : x / 8 * 40);
                image.Pixels[o + 1] = (byte)(square ? 200 : y / 8 * 50);
                image.Pixels[o + 2] = (byte)(square ? 240 : 90);
                image.Pixels[o + 3] = (byte)(transparentCorner && x < 8 && y < 8 ? 0 : 255);
            }
        }
        return image;
    }

    private static List<AnimationFrame> ReadAll(string path, out AnimationReader info)
    {
        info = Animation.Open(path);
        return info.ReadFrames().ToList();
    }

    private static void AssertSame(ImageBuffer expected, ImageBuffer actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int i = 0; i < expected.Pixels.Length; i += 4)
        {
            // A transparent pixel's color doesn't matter.
            if (expected.Pixels[i + 3] == 0)
            {
                Assert.Equal(0, actual.Pixels[i + 3]);
                continue;
            }
            Assert.True(expected.Pixels.AsSpan(i, 4).SequenceEqual(actual.Pixels.AsSpan(i, 4)),
                $"pixel {i / 4}: expected {string.Join(",", expected.Pixels[i..(i + 4)])}, got {string.Join(",", actual.Pixels[i..(i + 4)])}");
        }
    }

    [Fact]
    public void GifKeepsFramesDurationsAndLoopCount()
    {
        ImageBuffer a = Frame(40, 30, 0), b = Frame(40, 30, 2), c = Frame(40, 30, 4, transparentCorner: true), d = Frame(40, 30, 5);
        using (var writer = Animation.Create(P("a.gif"), AnimationFormat.Gif, 40, 30, new AnimationSaveOptions { LoopCount = 3 }))
        {
            writer.Add(a, 100);
            writer.Add(b, 50);
            writer.Add(b, 70);   // the same frame again: b lasts 120 ms
            writer.Add(c, 200);
            writer.Add(d, 30);
            writer.Finish();
        }

        Assert.Equal(AnimationFormat.Gif, Animation.Detect(P("a.gif")));
        var frames = ReadAll(P("a.gif"), out var reader);
        using (reader)
        {
            Assert.Equal((40, 30, 4, 3), (reader.Width, reader.Height, reader.FrameCount, reader.LoopCount));
            Assert.Equal([100, 120, 200, 30], frames.Select(f => f.Duration));
            AssertSame(a, frames[0].Image);
            AssertSame(b, frames[1].Image);
            AssertSame(c, frames[2].Image);
            AssertSame(d, frames[3].Image);
        }

        // WIC reads the file as well: the same frames, the delays in hundredths of a second.
        using var stream = File.OpenRead(P("a.gif"));
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(4, decoder.Frames.Count);
        Assert.Equal((ushort)12, ((BitmapMetadata)decoder.Frames[1].Metadata).GetQuery("/grctlext/Delay"));
    }

    [Fact]
    public void GifWithManyColorsIsDitheredCloseToTheOriginal()
    {
        var image = new ImageBuffer(64, 64);
        for (int i = 0; i < 64 * 64; i++)
        {
            int x = i % 64, y = i / 64;
            image.Pixels[i * 4] = (byte)(x * 4);
            image.Pixels[i * 4 + 1] = (byte)(y * 4);
            image.Pixels[i * 4 + 2] = (byte)((x + y) * 2);
            image.Pixels[i * 4 + 3] = 255;
        }
        using (var writer = Animation.Create(P("many.gif"), AnimationFormat.Gif, 64, 64, new AnimationSaveOptions()))
        {
            writer.Add(image, 100);
            writer.Add(Frame(64, 64, 1), 100);
            writer.Finish();
        }
        var frames = ReadAll(P("many.gif"), out var reader);
        reader.Dispose();
        double error = 0;
        for (int i = 0; i < image.Pixels.Length; i++)
            error += Math.Abs(image.Pixels[i] - frames[0].Image.Pixels[i]);
        Assert.InRange(error / image.Pixels.Length, 0, 4);
    }

    [Fact]
    public void LosslessWebPKeepsFramesDurationsAndLoopCount()
    {
        ImageBuffer a = Frame(40, 30, 0), b = Frame(40, 30, 3, transparentCorner: true), c = Frame(40, 30, 5);
        using (var writer = Animation.Create(P("a.webp"), AnimationFormat.WebP, 40, 30,
                   new AnimationSaveOptions { Quality = 100, LoopCount = 2 }))
        {
            writer.Add(a, 100);
            writer.Add(b, 40);
            writer.Add(c, 250);
            writer.Finish();
        }

        Assert.Equal(AnimationFormat.WebP, Animation.Detect(P("a.webp")));
        var frames = ReadAll(P("a.webp"), out var reader);
        using (reader)
        {
            Assert.Equal((40, 30, 3, 2), (reader.Width, reader.Height, reader.FrameCount, reader.LoopCount));
            Assert.Equal([100, 40, 250], frames.Select(f => f.Duration));
            AssertSame(a, frames[0].Image);
            AssertSame(b, frames[1].Image);
            AssertSame(c, frames[2].Image);
        }
    }

    [Fact]
    public void LossyWebPIsCloseAndKeepsTheProfile()
    {
        // The muxer stores the profile as it is: any bytes will do to see it carried over.
        byte[] icc = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        using (var writer = Animation.Create(P("lossy.webp"), AnimationFormat.WebP, 40, 30,
                   new AnimationSaveOptions { Quality = 90, IccProfile = icc }))
        {
            writer.Add(Frame(40, 30, 0), 80);
            writer.Add(Frame(40, 30, 4), 80);
            writer.Finish();
        }
        var frames = ReadAll(P("lossy.webp"), out var reader);
        using (reader)
        {
            Assert.Equal(icc, reader.IccProfile);
            Assert.Equal(0, reader.LoopCount);
        }
        var expected = Frame(40, 30, 4);
        double error = 0;
        for (int i = 0; i < expected.Pixels.Length; i++)
            error += Math.Abs(expected.Pixels[i] - frames[1].Image.Pixels[i]);
        Assert.InRange(error / expected.Pixels.Length, 0, 8);
    }

    [Fact]
    public void StillImagesAreNotAnimations()
    {
        using (var writer = Animation.Create(P("one.gif"), AnimationFormat.Gif, 10, 10, new AnimationSaveOptions()))
        {
            writer.Add(Frame(10, 10, 0), 100);
            writer.Finish();
        }
        Assert.Null(Animation.Detect(P("one.gif")));
        using (var writer = Animation.Create(P("one.webp"), AnimationFormat.WebP, 10, 10, new AnimationSaveOptions()))
        {
            writer.Add(Frame(10, 10, 0), 100);
            writer.Finish();
        }
        Assert.Null(Animation.Detect(P("one.webp")));
        File.WriteAllBytes(P("broken.gif"), [1, 2, 3]);
        Assert.Null(Animation.Detect(P("broken.gif")));
        Assert.Null(Animation.Detect(P("missing.webp")));
    }

    [Fact]
    public void SizeLimits()
    {
        Animation.EnsureCanSave(AnimationFormat.WebP, 16383, 100);
        Assert.Contains("16383", Assert.Throws<InvalidOperationException>(() => Animation.EnsureCanSave(AnimationFormat.WebP, 16384, 100)).Message);
        Animation.EnsureCanSave(AnimationFormat.Gif, 65535, 100);
        Assert.Throws<InvalidOperationException>(() => Animation.EnsureCanSave(AnimationFormat.Gif, 65536, 100));
    }

    [Theory]
    [InlineData(AnimationFormat.Gif)]
    [InlineData(AnimationFormat.WebP)]
    public void UpscalesEveryFrameAndReusesRepeatedOnes(AnimationFormat format)
    {
        string source = P("source" + Animation.GetExtension(format)), output = P("result" + Animation.GetExtension(format));
        var frames = new[] { Frame(24, 16, 0), Frame(24, 16, 1), Frame(24, 16, 1), Frame(24, 16, 3, transparentCorner: true) };
        using (var writer = Animation.Create(source, format, 24, 16, new AnimationSaveOptions { Quality = 100, LoopCount = 0 }))
        {
            writer.Add(frames[0], 100);
            writer.Add(frames[1], 100);
            // The same frame again: the file may keep it or make the one before last longer.
            writer.Add(frames[2], 100);
            writer.Add(frames[3], 100);
            writer.Finish();
        }

        using var model = UpscaleModel.Load(TestImages.Model("nearest_red_x4.onnx"), ExecutionDevice.Cpu);
        var upscaler = new Upscaler(model);
        var progress = new List<FrameProgress>();
        var result = AnimationUpscaler.Upscale(upscaler, source, output, UpscaleOptions.Default,
            new AnimationSaveOptions { Quality = 100 }, new SyncProgress<FrameProgress>(progress.Add));

        var read = ReadAll(source, out var sourceReader);
        sourceReader.Dispose();
        var upscaled = ReadAll(output, out var reader);
        using (reader)
        {
            Assert.Equal(read.Count, upscaled.Count);
            Assert.Equal(read.Select(f => f.Duration), upscaled.Select(f => f.Duration));
            Assert.Equal(0, reader.LoopCount);
            for (int i = 0; i < read.Count; i++)
            {
                var expected = upscaler.Upscale(read[i].Image);
                Assert.Equal((96, 64), (upscaled[i].Image.Width, upscaled[i].Image.Height));
                // The fixture model keeps the colors (it changes red the same way everywhere), so a GIF's palette is exact,
                // and the alpha is scaled separately: compare the opaque pixels.
                for (int p = 0; p < expected.Pixels.Length; p += 4)
                {
                    if (expected.Pixels[p + 3] < 255)
                        continue;
                    Assert.True(expected.Pixels.AsSpan(p, 3).SequenceEqual(upscaled[i].Image.Pixels.AsSpan(p, 3)), $"frame {i}, pixel {p / 4}");
                }
            }
        }
        Assert.Equal(read.Count, result.Frames);
        // The repeated frame goes through the network once, whether the file kept it (WebP may) or not.
        Assert.Equal(3, result.Upscaled);
        Assert.Contains(progress, p => p.Frame == read.Count && p.FrameCount == read.Count);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
