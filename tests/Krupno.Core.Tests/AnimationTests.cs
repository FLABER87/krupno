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
    public void RepeatedGifFramesAreShownForAsLongAsBefore()
    {
        // Browsers show a delay of 0 or 10 ms as 100 ms, so a repeated frame that short stays a frame of its own: the two last
        // 200 ms, one of their sum would last 20 ms. So does one whose sum doesn't fit in a delay (655.35 s); others are merged.
        ImageBuffer a = Frame(40, 30, 0), b = Frame(40, 30, 2);
        using (var writer = Animation.Create(P("repeat.gif"), AnimationFormat.Gif, 40, 30, new AnimationSaveOptions()))
        {
            writer.Add(a, 10);
            writer.Add(a, 10);
            writer.Add(b, 0);
            writer.Add(b, 0);
            writer.Add(a, 50);
            writer.Add(a, 10);
            writer.Add(b, 600_000);
            writer.Add(b, 600_000);
            writer.Add(a, 20);
            writer.Add(a, 30);
            // 25 ms is stored as 20, so the two last 40 ms; one of 50 ms would be stored as 50.
            writer.Add(b, 25);
            writer.Add(b, 25);
            writer.Finish();
        }

        var frames = ReadAll(P("repeat.gif"), out var reader);
        using (reader)
        {
            Assert.Equal([10, 10, 0, 0, 50, 10, 600_000, 600_000, 50, 20, 20], frames.Select(f => f.Duration));
            for (int i = 0; i < frames.Count; i++)
                AssertSame(i is 2 or 3 or 6 or 7 or 9 or 10 ? b : a, frames[i].Image);
        }

        // A repeated frame of its own leaves the canvas as the one before it: with more than 256 colors that one is dithered,
        // and a pixel of the source's own colors would stand out.
        var noise = TestImages.Noise(40, 30, seed: 5);
        using (var writer = Animation.Create(P("repeat_noise.gif"), AnimationFormat.Gif, 40, 30, new AnimationSaveOptions()))
        {
            writer.Add(noise, 10);
            writer.Add(noise, 10);
            writer.Finish();
        }
        AssertStrictGif(P("repeat_noise.gif"));
        var noisy = ReadAll(P("repeat_noise.gif"), out var noiseReader);
        noiseReader.Dispose();
        Assert.Equal(2, noisy.Count);
        Assert.False(noisy[0].Image.Pixels.AsSpan(0, 4).SequenceEqual(noise.Pixels.AsSpan(0, 4)), "the first pixel kept its color");
        Assert.True(noisy[0].Image.Pixels.AsSpan().SequenceEqual(noisy[1].Image.Pixels), "the repeated frame differs");

        // The same before a transparent frame, after a frame stored as the area it changed: the repeat isn't quantized again
        // over the whole canvas, which could give it other colors, but waits as well and then clears the canvas.
        var changed = TestImages.Noise(40, 30, seed: 5);
        changed.Pixels[(10 * 40 + 10) * 4] ^= 0xFF;
        using (var writer = Animation.Create(P("repeat_before_transparent.gif"), AnimationFormat.Gif, 40, 30, new AnimationSaveOptions()))
        {
            writer.Add(changed, 100);
            writer.Add(noise, 10);
            writer.Add(noise, 10);
            writer.Add(Frame(40, 30, 0, transparentCorner: true), 100);
            writer.Finish();
        }
        AssertStrictGif(P("repeat_before_transparent.gif"));
        var beforeTransparent = ReadAll(P("repeat_before_transparent.gif"), out var transparentReader);
        transparentReader.Dispose();
        Assert.Equal([100, 10, 10, 100], beforeTransparent.Select(f => f.Duration));
        Assert.True(beforeTransparent[1].Image.Pixels.AsSpan().SequenceEqual(beforeTransparent[2].Image.Pixels), "the repeated frame differs");
        AssertSame(Frame(40, 30, 0, transparentCorner: true), beforeTransparent[3].Image);
    }

    [Fact]
    public void GifLoopCountAfterAnotherApplicationExtension()
    {
        // NETSCAPE2.0 needn't be the first application extension: an XMP packet (as Photoshop writes one) may come before it.
        using (var writer = Animation.Create(P("loop.gif"), AnimationFormat.Gif, 40, 30, new AnimationSaveOptions { LoopCount = 0 }))
        {
            writer.Add(Frame(40, 30, 0), 100);
            writer.Add(Frame(40, 30, 2), 100);
            writer.Finish();
        }
        var bytes = File.ReadAllBytes(P("loop.gif"));
        // After the header and the logical screen (no global color table) comes the writer's NETSCAPE2.0 block.
        Assert.Equal("NETSCAPE2.0", System.Text.Encoding.ASCII.GetString(bytes, 16, 11));
        byte[] xmp = [0x21, 0xFF, 0x0B, .. "XMP DataXMP"u8, 3, (byte)'<', (byte)'x', (byte)'>', 0];
        File.WriteAllBytes(P("loop.gif"), [.. bytes[..13], .. xmp, .. bytes[13..]]);

        using var reader = Animation.Open(P("loop.gif"));
        Assert.Equal((2, 0), (reader.FrameCount, reader.LoopCount));
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

    [Fact]
    public void GifCodesFollowTheDecodersCodeSizeToTheEnd()
    {
        // Frames of every length from 1 to 300 pixels with 1, 2, 16 and 256 colors end the LZW stream at every point of the
        // code size; a strict decoder (codes within the table, the end code reached) must read each one.
        var random = new Random(7);
        foreach (int colors in new[] { 1, 2, 16, 256 })
        {
            for (int length = 1; length <= 300; length += colors == 256 ? 1 : 3)
            {
                var frame = new ImageBuffer(length, 1);
                for (int x = 0; x < length; x++)
                {
                    int c = random.Next(colors);
                    frame.Pixels[x * 4] = (byte)c;
                    frame.Pixels[x * 4 + 1] = (byte)(c * 7);
                    frame.Pixels[x * 4 + 3] = 255;
                }
                string path = P($"lzw_{colors}_{length}.gif");
                using (var writer = Animation.Create(path, AnimationFormat.Gif, length, 1, new AnimationSaveOptions()))
                {
                    writer.Add(frame, 10);
                    writer.Finish();
                }
                AssertStrictGif(path);
            }
        }
        // A long run of one color: the codes grow to 12 bits and the table is cleared on the way.
        var plain = TestImages.Fill(300, 300, 10, 20, 30);
        using (var writer = Animation.Create(P("plain.gif"), AnimationFormat.Gif, 300, 300, new AnimationSaveOptions()))
        {
            writer.Add(plain, 10);
            writer.Add(TestImages.Noise(300, 300), 10);
            writer.Finish();
        }
        AssertStrictGif(P("plain.gif"));
    }

    /// <summary>Walks the GIF's blocks and decodes each image's LZW codes the way a strict decoder does.</summary>
    private static void AssertStrictGif(string path)
    {
        var b = File.ReadAllBytes(path);
        Assert.Equal(0, b[10] & 0x80); // no global color table
        int pos = 13;
        while (true)
        {
            byte block = b[pos++];
            if (block == 0x3B)
                return;
            if (block == 0x21)
            {
                pos++;
                while (b[pos] != 0)
                    pos += b[pos] + 1;
                pos++;
                continue;
            }
            Assert.Equal(0x2C, block);
            int packed = b[pos + 8];
            pos += 9;
            if ((packed & 0x80) != 0)
                pos += 3 << ((packed & 7) + 1);
            int minCodeSize = b[pos++];
            var data = new List<byte>();
            while (b[pos] != 0)
            {
                data.AddRange(b.AsSpan(pos + 1, b[pos]).ToArray());
                pos += b[pos] + 1;
            }
            pos++;
            DecodeStrictly([.. data], minCodeSize, Path.GetFileName(path));
        }
    }

    private static void DecodeStrictly(byte[] data, int minCodeSize, string name)
    {
        int clear = 1 << minCodeSize, end = clear + 1, size = minCodeSize + 1, next = clear + 2, bit = 0;
        bool first = true;
        while (true)
        {
            Assert.True(bit + size <= data.Length * 8, $"{name}: the data ends before the end code");
            int code = 0;
            for (int i = 0; i < size; i++, bit++)
                code |= (data[bit >> 3] >> (bit & 7) & 1) << i;
            if (code == clear)
            {
                size = minCodeSize + 1;
                next = clear + 2;
                first = true;
                continue;
            }
            if (code == end)
                return;
            Assert.True(first ? code < clear : code <= next, $"{name}: code {code} with the next free one {next}");
            if (first)
            {
                first = false;
                continue;
            }
            if (next < 4096 && ++next == 1 << size && size < 12)
                size++;
        }
    }

    [Fact]
    public void WebPKeepsItsSourcesKindOfCompression()
    {
        using (var writer = Animation.Create(P("lossless.webp"), AnimationFormat.WebP, 24, 16, new AnimationSaveOptions { Quality = 100 }))
        {
            writer.Add(Frame(24, 16, 0), 100);
            writer.Add(Frame(24, 16, 2), 100);
            writer.Finish();
        }
        using (var writer = Animation.Create(P("lossy.webp"), AnimationFormat.WebP, 24, 16, new AnimationSaveOptions { Quality = 80 }))
        {
            writer.Add(Frame(24, 16, 0), 100);
            writer.Add(Frame(24, 16, 2), 100);
            writer.Finish();
        }
        Assert.True(WebPAnimation.AllFramesLossless(File.ReadAllBytes(P("lossless.webp"))));
        Assert.False(WebPAnimation.AllFramesLossless(File.ReadAllBytes(P("lossy.webp"))));

        using var model = UpscaleModel.Load(TestImages.Model("nearest_red_x4.onnx"), ExecutionDevice.Cpu);
        var upscaler = new Upscaler(model);
        AnimationUpscaler.Upscale(upscaler, P("lossless.webp"), P("lossless_x4.webp"), UpscaleOptions.Default);
        AnimationUpscaler.Upscale(upscaler, P("lossy.webp"), P("lossy_x4.webp"), UpscaleOptions.Default);
        AnimationUpscaler.Upscale(upscaler, P("lossless.webp"), P("forced_x4.webp"), UpscaleOptions.Default,
            new AnimationSaveOptions { Quality = 70 });
        Assert.True(WebPAnimation.AllFramesLossless(File.ReadAllBytes(P("lossless_x4.webp"))));
        Assert.False(WebPAnimation.AllFramesLossless(File.ReadAllBytes(P("lossy_x4.webp"))));
        Assert.False(WebPAnimation.AllFramesLossless(File.ReadAllBytes(P("forced_x4.webp"))));
    }

    [Fact]
    public void GifWithAHugeCanvasIsRefused()
    {
        using (var writer = Animation.Create(P("huge.gif"), AnimationFormat.Gif, 1, 1, new AnimationSaveOptions()))
        {
            writer.Add(Frame(1, 1, 0), 10);
            writer.Add(Frame(1, 1, 0, transparentCorner: true), 10);
            writer.Finish();
        }
        // The logical screen in the header claims 60000×60000.
        var bytes = File.ReadAllBytes(P("huge.gif"));
        bytes[6] = bytes[8] = 0x60;
        bytes[7] = bytes[9] = 0xEA;
        File.WriteAllBytes(P("huge.gif"), bytes);
        Assert.Contains("too large", Assert.Throws<NotSupportedException>(() => Animation.Open(P("huge.gif"))).Message);
    }

    [Fact]
    public void FirstGifFrameIsLoadedAsShown()
    {
        // The second frame of an opaque animation is stored as the changed area only; ImageCodec.Load shows frame one whole,
        // and a GIF whose first frame is a smaller rectangle gets the canvas around it.
        using (var writer = Animation.Create(P("two.gif"), AnimationFormat.Gif, 40, 30, new AnimationSaveOptions()))
        {
            writer.Add(Frame(40, 30, 0), 10);
            writer.Add(Frame(40, 30, 3), 10);
            writer.Finish();
        }
        AssertSame(Frame(40, 30, 0), ImageCodec.Load(P("two.gif")).Image);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
