using Krupno.Core.Imaging;
using Krupno.Core.Inference;
using Krupno.Core.Processing;

namespace Krupno.Core.Tests;

/// <summary>
/// Test-time augmentation: the fixture models are nearest-neighbor enlargements, which give the same result in any orientation,
/// so the average of the 8 orientations turned back must be exactly the plain result. A turn that isn't undone, or undone the
/// wrong way, would show as a difference.
/// </summary>
public class TestTimeAugmentationTests
{
    private static ImageBuffer Run(string model, ImageBuffer input, UpscaleOptions options, IProgress<UpscaleProgress>? progress = null)
    {
        using var loaded = UpscaleModel.Load(TestImages.Model(model), ExecutionDevice.Cpu);
        return new Upscaler(loaded).Upscale(input, options, progress);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 6)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(6, 2)]
    [InlineData(7, 7)]
    [InlineData(8, 8)]
    public void InverseTurnsBack(int orientation, int seed)
    {
        var image = TestImages.Noise(13, 7, seed);
        var back = image.ApplyExifOrientation(orientation).ApplyExifOrientation(Upscaler.InverseOrientation(orientation));
        Assert.Null(TestImages.Difference(image, back));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void TileRegionsMapBackToTheImage(int orientation)
    {
        // A rectangle marked on the turned image, turned back, must land where Unturn says.
        const int width = 20, height = 12;
        var turned = orientation >= 5 ? TestImages.Fill(height, width, 0, 0, 0) : TestImages.Fill(width, height, 0, 0, 0);
        int left = 3, top = 2, w = 5, h = 4;
        for (int y = top; y < top + h; y++)
        {
            for (int x = left; x < left + w; x++)
                turned.Pixels[(y * turned.Width + x) * ImageBuffer.BytesPerPixel] = 255;
        }
        var region = new TileRegion((double)left / turned.Width, (double)top / turned.Height,
            (double)w / turned.Width, (double)h / turned.Height);

        var back = turned.ApplyExifOrientation(Upscaler.InverseOrientation(orientation));
        Assert.Equal((width, height), (back.Width, back.Height));
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int p = 0; p < width * height; p++)
        {
            if (back.Pixels[p * ImageBuffer.BytesPerPixel] == 0)
                continue;
            minX = Math.Min(minX, p % width);
            maxX = Math.Max(maxX, p % width);
            minY = Math.Min(minY, p / width);
            maxY = Math.Max(maxY, p / width);
        }

        var mapped = Upscaler.Unturn(region, orientation);
        Assert.Equal((double)minX / width, mapped.Left, 9);
        Assert.Equal((double)minY / height, mapped.Top, 9);
        Assert.Equal((double)(maxX + 1 - minX) / width, mapped.Width, 9);
        Assert.Equal((double)(maxY + 1 - minY) / height, mapped.Height, 9);
    }

    [Fact]
    public void AverageRoundsToTheNearestLevel()
    {
        // Eight results of two pixels: blue 10 ×5 and 11 ×3 is 10.375 → 10, ×4 each is 10.5 → 11; 0 and 255 stay; opaque.
        byte[][] results =
        [
            [10, 0, 255, 255, 10, 0, 255, 7], [10, 0, 255, 255, 10, 0, 255, 7], [10, 0, 255, 255, 10, 0, 255, 7],
            [10, 0, 255, 255, 10, 0, 255, 7], [10, 0, 255, 255, 11, 0, 255, 7], [11, 0, 255, 255, 11, 0, 255, 7],
            [11, 0, 255, 255, 11, 0, 255, 7], [11, 0, 255, 255, 11, 0, 255, 7],
        ];
        var sum = new ushort[2 * 3];
        foreach (var result in results)
            Upscaler.AddColors(new ImageBuffer(2, 1, result), 1, sum);
        var average = new byte[2 * ImageBuffer.BytesPerPixel];
        Upscaler.Average(sum, results.Length, average);
        Assert.Equal([10, 0, 255, 255, 11, 0, 255, 255], average);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void AddingTurnedColorsIsAddingTheTurnedBackCopy(int orientation)
    {
        // TTA reads a round's result through the orientation's pixel mapping instead of turning it back as a copy first.
        var turned = TestImages.Noise(13, 7, seed: orientation);
        var copy = turned.ApplyExifOrientation(orientation);
        var expected = new ushort[copy.Width * copy.Height * 3];
        Upscaler.AddColors(copy, 1, expected);
        var actual = new ushort[expected.Length];
        Upscaler.AddColors(turned, orientation, actual);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("nearest_red_x4.onnx", 150, 100)]
    [InlineData("fixed64_x2.onnx", 150, 100)]
    [InlineData("half_x2.onnx", 97, 61)]
    [InlineData("legacy_x2.onnx", 40, 30)]
    public void SameAsWithoutForAModelThatIgnoresOrientation(string model, int width, int height)
    {
        var input = TestImages.Noise(width, height, seed: width + height);
        var plain = Run(model, input, new UpscaleOptions { TileSize = 64, TilePadding = 8 });
        var augmented = Run(model, input, new UpscaleOptions { TileSize = 64, TilePadding = 8, TestTimeAugmentation = true });
        Assert.Null(TestImages.Difference(plain, augmented));
    }

    [Fact]
    public void EveryPassRunsInEightOrientations()
    {
        // ×8 with a ×4 model is two passes, each in 8 rounds; with transparency the alpha goes its own way as without TTA.
        var input = TestImages.Noise(100, 60, seed: 21, alpha: true);
        var progress = new ProgressLog<UpscaleProgress>();
        var options = new UpscaleOptions { Scale = 8, TileSize = 64, TilePadding = 8 };
        var augmented = Run("nearest_red_x4.onnx", input, options with { TestTimeAugmentation = true }, progress);
        Assert.Null(TestImages.Difference(Run("nearest_red_x4.onnx", input, options), augmented));

        var tiles = progress.Reports.Where(p => p.Message is null).ToList();
        foreach (int pass in new[] { 1, 2 })
        {
            Assert.Equal(Enumerable.Range(1, 8), tiles.Where(p => p.Pass == pass).Select(p => p.Round).Distinct());
            foreach (int round in Enumerable.Range(1, 8))
            {
                // The tiles of a round, turned back, cover the image once.
                var regions = tiles.Where(p => p.Pass == pass && p.Round == round && p.Region is not null).Select(p => p.Region!.Value).ToList();
                Assert.All(regions, r => Assert.True(r.Left >= -1e-9 && r.Top >= -1e-9 && r.Left + r.Width <= 1 + 1e-9 && r.Top + r.Height <= 1 + 1e-9));
                Assert.Equal(1, regions.Sum(r => r.Width * r.Height), 9);
            }
        }

        // The rotated rounds run on the turned image: their first tile isn't where the first round's is.
        var firstTiles = tiles.Where(p => p.Pass == 1 && p.Region is not null).GroupBy(p => p.Round).Select(g => g.First().Region).ToList();
        Assert.Equal(8, firstTiles.Count);
        Assert.True(firstTiles.Distinct().Count() > 1);

        // Progress only moves forward and reaches the end.
        var fractions = tiles.Select(p => p.Fraction).ToList();
        Assert.Equal(fractions.Order(), fractions);
        Assert.Equal(1, fractions[^1], 9);
    }
}
