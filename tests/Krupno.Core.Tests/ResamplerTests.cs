using Krupno.Core.Imaging;

namespace Krupno.Core.Tests;

public class ResamplerTests
{
    [Fact]
    public void SameSizeLeavesPixelsAlone()
    {
        var source = TestImages.Noise(37, 23);
        var result = Resampler.Resize(source, 37, 23);
        Assert.Null(TestImages.Difference(source, result));
    }

    [Theory]
    [InlineData(10, 10, 25, 7)]
    [InlineData(64, 48, 256, 192)]
    [InlineData(300, 200, 97, 61)]
    public void FlatColorStaysFlat(int width, int height, int newWidth, int newHeight)
    {
        var source = TestImages.Fill(width, height, 12, 130, 250, 200);
        var result = Resampler.Resize(source, newWidth, newHeight);
        Assert.Null(TestImages.Difference(TestImages.Fill(newWidth, newHeight, 12, 130, 250, 200), result));
    }

    [Fact]
    public void ResizePlaneMatchesTheAlphaOfResize()
    {
        var source = TestImages.Noise(50, 40, alpha: true);
        var resized = Resampler.Resize(source, 130, 70);
        var plane = Resampler.ResizePlane(source.ExtractAlpha(), 50, 40, 130, 70);
        Assert.Equal(resized.ExtractAlpha(), plane);
    }

    [Fact]
    public void PremultipliedResizeIgnoresColorsUnderTransparency()
    {
        // Left half opaque blue, right half fully transparent red: no red may show up anywhere.
        var source = TestImages.Fill(40, 20, 255, 0, 0);
        for (int y = 0; y < 20; y++)
        {
            for (int x = 20; x < 40; x++)
            {
                int i = (y * 40 + x) * ImageBuffer.BytesPerPixel;
                source.Pixels[i] = 0;
                source.Pixels[i + 2] = 255;
                source.Pixels[i + 3] = 0;
            }
        }

        var result = Resampler.ResizePremultiplied(source, 100, 50);
        for (int i = 0; i < result.Pixels.Length; i += ImageBuffer.BytesPerPixel)
        {
            if (result.Pixels[i + 3] == 0)
                continue;
            Assert.Equal(0, result.Pixels[i + 2]);
            Assert.Equal(0, result.Pixels[i + 1]);
        }

        // The plain resize does pull the red in, which is what the premultiplied one is for.
        var plain = Resampler.Resize(source, 100, 50);
        Assert.Contains(Enumerable.Range(0, plain.Width * plain.Height),
            p => plain.Pixels[p * 4 + 3] > 0 && plain.Pixels[p * 4 + 2] > 0);
    }

    [Fact]
    public void PremultipliedResizeOfAnOpaqueImageIsThePlainOne()
    {
        var source = TestImages.Noise(60, 45, seed: 3);
        var plain = Resampler.Resize(source, 150, 100);
        var premultiplied = Resampler.ResizePremultiplied(source, 150, 100);
        for (int i = 0; i < plain.Pixels.Length; i++)
            Assert.InRange(premultiplied.Pixels[i] - plain.Pixels[i], -1, 1);
    }

    [Fact]
    public void BlendKeepsAlpha()
    {
        var source = TestImages.Noise(30, 20, seed: 4, alpha: true);
        var destination = TestImages.Noise(120, 80, seed: 5, alpha: true);
        var alpha = destination.ExtractAlpha();
        Resampler.BlendResized(source, destination, 0.3f);
        Assert.Equal(alpha, destination.ExtractAlpha());
    }

    [Fact]
    public void BlendAtTheEnds()
    {
        var source = TestImages.Noise(30, 20, seed: 6);
        var destination = TestImages.Noise(120, 80, seed: 7);

        // Keeping all of the destination changes nothing.
        var kept = new ImageBuffer(120, 80, (byte[])destination.Pixels.Clone());
        Resampler.BlendResized(source, kept, 1f);
        Assert.Null(TestImages.Difference(destination, kept));

        // Keeping none of it gives the plain resize.
        var replaced = new ImageBuffer(120, 80, (byte[])destination.Pixels.Clone());
        Resampler.BlendResized(source, replaced, 0f);
        Assert.Null(TestImages.Difference(Resampler.Resize(source, 120, 80), replaced));
    }

    [Fact]
    public void BlendClampsTheResizeFirst()
    {
        // A black and white checkerboard: Lanczos overshoots 0..255 at every edge. Blended half and half into black, each
        // color must be half of the saved resize (0..255), not half of the overshoot: a sum of 300 would give 150, not 128.
        var source = new ImageBuffer(16, 16);
        for (int p = 0; p < 16 * 16; p++)
        {
            byte v = ((p % 16) / 2 + p / 16 / 2) % 2 == 0 ? (byte)255 : (byte)0;
            source.Pixels.AsSpan(p * 4, 4).Fill(v);
        }
        var resized = Resampler.Resize(source, 61, 61);
        var black = TestImages.Fill(61, 61, 0, 0, 0);
        Resampler.BlendResized(source, black, 0.5f);

        bool clipped = false;
        for (int i = 0; i < black.Pixels.Length; i++)
        {
            if ((i & 3) == 3)
                continue;
            Assert.InRange(black.Pixels[i], resized.Pixels[i] / 2 - 1, resized.Pixels[i] / 2 + 1);
            clipped |= resized.Pixels[i] == 255;
        }
        Assert.True(clipped, "The checkerboard was meant to clip at 255.");
    }

    [Fact]
    public void BlendRejectsKeepOutside01()
    {
        var source = TestImages.Noise(4, 4);
        var destination = TestImages.Noise(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => Resampler.BlendResized(source, destination, -0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Resampler.BlendResized(source, destination, 1.1f));
    }
}
