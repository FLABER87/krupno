using Krupno.Core.Imaging;
using Krupno.Core.Inference;
using Krupno.Core.Processing;

namespace Krupno.Core.Tests;

/// <summary>
/// The whole pipeline on the CPU with the synthetic models from tests/fixtures/models: each is a nearest-neighbor enlargement
/// that keeps some of the color channels (see tests/make_fixtures.py), so the exact result is known.
/// </summary>
public class UpscalerTests
{
    private static ImageBuffer Run(string model, ImageBuffer input, UpscaleOptions? options = null, IProgress<UpscaleProgress>? progress = null)
    {
        using var loaded = UpscaleModel.Load(TestImages.Model(model), ExecutionDevice.Cpu);
        return new Upscaler(loaded).Upscale(input, options, progress);
    }

    [Theory]
    // Named dynamic axes, krupno.* metadata, red only.
    [InlineData("nearest_red_x4.onnx", 4, false, false, true)]
    // A static 64×64 input: tiles of exactly that size, and mirrored padding for a smaller image.
    [InlineData("fixed64_x2.onnx", 2, false, true, false)]
    // No metadata and unnamed axes: the scale comes from a probe run.
    [InlineData("probe_x3.onnx", 3, true, false, false)]
    // Metadata with the legacy upscalr.* prefix.
    [InlineData("legacy_x2.onnx", 2, true, true, true)]
    // fp16 input and output: each byte level survives the trip through fp16.
    [InlineData("half_x2.onnx", 2, false, true, true)]
    public void FixtureModelsGiveTheExactResult(string model, int scale, bool keepB, bool keepG, bool keepR)
    {
        foreach (var (width, height) in new[] { (150, 100), (40, 30), (1, 1) })
        {
            var input = TestImages.Noise(width, height, seed: width);
            var options = new UpscaleOptions { TileSize = 64, TilePadding = 8 };
            var result = Run(model, input, options);
            Assert.Null(TestImages.Difference(TestImages.Nearest(input, scale, keepB, keepG, keepR), result));
        }
    }

    [Fact]
    public void ModelInfoFromMetadataAndProbe()
    {
        Assert.Equal(4, UpscaleModel.Inspect(TestImages.Model("nearest_red_x4.onnx")).Scale);
        Assert.Equal("Nearest red x4", UpscaleModel.Inspect(TestImages.Model("nearest_red_x4.onnx")).Name);
        Assert.Equal(3, UpscaleModel.Inspect(TestImages.Model("probe_x3.onnx")).Scale);
        Assert.Equal("Legacy x2", UpscaleModel.Inspect(TestImages.Model("legacy_x2.onnx")).Name);

        var fixedModel = UpscaleModel.Inspect(TestImages.Model("fixed64_x2.onnx"));
        Assert.Equal((2, 64, 64), (fixedModel.Scale, fixedModel.FixedWidth, fixedModel.FixedHeight));

        var half = UpscaleModel.Inspect(TestImages.Model("half_x2.onnx"));
        Assert.True(half.IsHalfPrecision);
        Assert.True(half.InputIsHalf && half.OutputIsHalf);
    }

    [Fact]
    public void OutputSizeUsesTheModelScale()
    {
        using var model = UpscaleModel.Load(TestImages.Model("nearest_red_x4.onnx"), ExecutionDevice.Cpu);
        var upscaler = new Upscaler(model);
        Assert.Equal((1200L, 800L), upscaler.OutputSize(300, 200));
        Assert.Equal((750L, 500L), upscaler.OutputSize(300, 200, new UpscaleOptions { Scale = 2.5 }));
        Assert.Equal((540L, 960L), upscaler.OutputSize(90, 160,
            new UpscaleOptions { Target = new ScaleTarget(TargetSide.LongSide, 960) }));
    }

    [Fact]
    public void TargetSizeIsReachedExactly()
    {
        var input = TestImages.Noise(90, 160, seed: 9);
        var result = Run("nearest_red_x4.onnx", input, new UpscaleOptions { Target = new ScaleTarget(TargetSide.LongSide, 960) });
        Assert.Equal((540, 960), (result.Width, result.Height));
    }

    [Fact]
    public void WidthPastTheModelScaleRunsTwoPasses()
    {
        var input = TestImages.Noise(300, 200, seed: 10);
        var progress = new ProgressLog<UpscaleProgress>();
        var result = Run("nearest_red_x4.onnx", input, new UpscaleOptions { Target = new ScaleTarget(TargetSide.Width, 1201) }, progress);
        Assert.Equal((1201, 801), (result.Width, result.Height));
        Assert.All(progress.Reports, p => Assert.Equal(2, p.PassCount));
        Assert.Contains(progress.Reports, p => p.Pass == 2);
    }

    [Fact]
    public void HalfStrengthMatchesTheFormula()
    {
        // Each color is resized + 0.5 × (network − resized), where resized is the plain Lanczos resize clamped to 0..255.
        // The test has the resize only as bytes, already rounded, hence the tolerance of 1.
        var input = TestImages.Noise(120, 80, seed: 11);
        var full = Run("nearest_red_x4.onnx", input);
        var half = Run("nearest_red_x4.onnx", input, new UpscaleOptions { Strength = 0.5 });
        var resized = Resampler.Resize(input, 480, 320);

        for (int i = 0; i < full.Pixels.Length; i++)
        {
            if ((i & 3) == 3)
            {
                Assert.Equal(255, half.Pixels[i]);
                continue;
            }
            double expected = resized.Pixels[i] + 0.5 * (full.Pixels[i] - resized.Pixels[i]);
            Assert.InRange(half.Pixels[i], expected - 1, expected + 1);
        }
    }

    [Fact]
    public void StrengthLeavesAlphaAlone()
    {
        var input = TestImages.Noise(64, 48, seed: 12, alpha: true);
        var full = Run("legacy_x2.onnx", input);
        var weak = Run("legacy_x2.onnx", input, new UpscaleOptions { Strength = 0.3 });
        Assert.Equal(full.ExtractAlpha(), weak.ExtractAlpha());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void StrengthOutOfRangeIsRejected(double strength)
    {
        var input = TestImages.Noise(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => Run("legacy_x2.onnx", input, new UpscaleOptions { Strength = strength }));
    }

    [Fact]
    public void WiderPaddingGivesTheSameResultWithMoreTiles()
    {
        // 1920×1200 with a 384 px tile: 6 × 4 = 24 tiles with 32 px of context, 7 × 5 = 35 with 64 ("Seamless tiles").
        var input = TestImages.Noise(1920, 1200, seed: 13);
        var expected = TestImages.Nearest(input, 2, true, true, true);
        foreach (var (padding, tiles) in new[] { (32, 24), (64, 35) })
        {
            var progress = new ProgressLog<UpscaleProgress>();
            var result = Run("legacy_x2.onnx", input, new UpscaleOptions { TileSize = 384, TilePadding = padding }, progress);
            Assert.Null(TestImages.Difference(expected, result));
            Assert.Equal(tiles, progress.Reports.Last().TileCount);
            Assert.Equal(tiles, progress.Reports.Count(p => p.Region is not null));
        }
    }
}
