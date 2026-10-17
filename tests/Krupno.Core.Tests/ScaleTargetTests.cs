using Krupno.Core.Processing;

namespace Krupno.Core.Tests;

public class ScaleTargetTests
{
    [Theory]
    [InlineData(300, 200, TargetSide.Width, 750, 750, 500)]
    [InlineData(300, 200, TargetSide.Height, 500, 750, 500)]
    [InlineData(90, 160, TargetSide.LongSide, 960, 540, 960)]
    [InlineData(160, 90, TargetSide.LongSide, 960, 960, 540)]
    [InlineData(300, 200, TargetSide.Width, 1000, 1000, 667)]
    [InlineData(300, 200, TargetSide.Width, 1, 1, 1)]
    public void OutputSizeForTarget(int width, int height, TargetSide side, int pixels, long expectedWidth, long expectedHeight)
    {
        var options = new UpscaleOptions { Target = new ScaleTarget(side, pixels) };
        double scale = options.ScaleFor(width, height, modelScale: 4);
        Assert.Equal((expectedWidth, expectedHeight), Upscaler.OutputSize(width, height, scale));
    }

    [Fact]
    public void TargetWinsOverScale()
    {
        var options = new UpscaleOptions { Scale = 3, Target = new ScaleTarget(TargetSide.Width, 750) };
        Assert.Equal(2.5, options.ScaleFor(300, 200, 4));
    }

    [Fact]
    public void ScaleOrTheModelsOwn()
    {
        Assert.Equal(3, new UpscaleOptions { Scale = 3 }.ScaleFor(300, 200, 4));
        Assert.Equal(4, UpscaleOptions.Default.ScaleFor(300, 200, 4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void SizeMustBePositive(int pixels)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScaleTarget(TargetSide.Width, pixels));
    }

    [Fact]
    public void Suffixes()
    {
        Assert.Equal("_3840w", new ScaleTarget(TargetSide.Width, 3840).Suffix);
        Assert.Equal("_2160h", new ScaleTarget(TargetSide.Height, 2160).Suffix);
        Assert.Equal("_3840px", new ScaleTarget(TargetSide.LongSide, 3840).Suffix);
    }

    [Theory]
    [InlineData(4.0, 4, 1)]
    [InlineData(2.5, 4, 1)]
    [InlineData(0.5, 4, 1)]
    [InlineData(1201.0 / 300, 4, 2)] // a pixel past the model's ×4 takes a second pass
    [InlineData(16.0, 4, 2)]
    [InlineData(16.5, 4, 3)]
    [InlineData(8.0, 2, 3)]
    [InlineData(3.0, 1, 1)] // ×1 models run once
    public void PassCount(double scale, int modelScale, int expected)
    {
        Assert.Equal(expected, Upscaler.PassCount(scale, modelScale));
    }

    [Fact]
    public void WidthJustPastTheModelScaleTakesTwoPasses()
    {
        var options = new UpscaleOptions { Target = new ScaleTarget(TargetSide.Width, 1201) };
        Assert.Equal(2, Upscaler.PassCount(options.ScaleFor(300, 200, 4), 4));
        Assert.Equal(1, Upscaler.PassCount(new UpscaleOptions { Target = new ScaleTarget(TargetSide.Width, 1200) }.ScaleFor(300, 200, 4), 4));
    }
}
