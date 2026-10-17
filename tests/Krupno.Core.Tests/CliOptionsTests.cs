using Krupno.Cli;
using Krupno.Core.Processing;

namespace Krupno.Core.Tests;

public class CliOptionsTests
{
    [Fact]
    public void Defaults()
    {
        var options = CliOptions.Parse(["photo.jpg"]);
        Assert.Equal(["photo.jpg"], options.Inputs);
        Assert.False(options.Recursive);
        Assert.False(options.SkipExisting);
        Assert.False(options.TestTimeAugmentation);
        Assert.True(CliOptions.Parse(["photo.jpg", "--tta"]).TestTimeAugmentation);
        Assert.False(options.InstallAccelerators);
        Assert.True(CliOptions.Parse(["--install-accelerators"]).InstallAccelerators);
        Assert.Equal(0, options.Scale);
        Assert.Null(options.Target);
        Assert.Equal((384, 32, 100, 95), (options.Tile, options.Padding, options.Strength, options.Quality));
        Assert.Equal("auto", options.Device);
    }

    [Theory]
    [InlineData("--width", TargetSide.Width)]
    [InlineData("--height", TargetSide.Height)]
    [InlineData("--long-side", TargetSide.LongSide)]
    public void TargetSizes(string option, TargetSide side)
    {
        Assert.Equal(new ScaleTarget(side, 3840), CliOptions.Parse(["photo.jpg", option, "3840"]).Target);
    }

    [Theory]
    [InlineData("-s", "2", "--width", "100")]
    [InlineData("--width", "100", "--scale", "2")]
    [InlineData("--width", "100", "--height", "100")]
    [InlineData("--long-side", "100", "--long-side", "200")]
    public void OnlyOneSize(string a, string aValue, string b, string bValue)
    {
        var ex = Assert.Throws<CliException>(() => CliOptions.Parse(["photo.jpg", a, aValue, b, bValue]));
        Assert.Contains("only one of", ex.Message);
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("x4", 4)]
    [InlineData("2.5", 2.5)]
    [InlineData("×3", 3)]
    public void Scales(string text, double expected)
    {
        Assert.Equal(expected, CliOptions.Parse(["photo.jpg", "-s", text]).Scale);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("2,5")]
    [InlineData("big")]
    public void BadScales(string text)
    {
        Assert.Throws<CliException>(() => CliOptions.Parse(["photo.jpg", "-s", text]));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("50", 50)]
    [InlineData("50%", 50)]
    [InlineData("100", 100)]
    public void Strength(string text, int expected)
    {
        Assert.Equal(expected, CliOptions.Parse(["photo.jpg", "--strength", text]).Strength);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-5")]
    [InlineData("0.5")]
    public void StrengthOutOfRange(string text)
    {
        var ex = Assert.Throws<CliException>(() => CliOptions.Parse(["photo.jpg", "--strength", text]));
        Assert.Contains("from 1 to 100", ex.Message);
    }

    [Theory]
    [InlineData("--width", "0")]
    [InlineData("--pad", "300")]
    [InlineData("-q", "0")]
    [InlineData("-t", "-1")]
    public void NumbersOutOfRange(string option, string value)
    {
        Assert.Throws<CliException>(() => CliOptions.Parse(["photo.jpg", option, value]));
    }

    [Fact]
    public void SeveralInputsAndBatchSwitches()
    {
        var options = CliOptions.Parse(["a.jpg", "-r", "photos", "--skip-existing", "*.png"]);
        Assert.Equal(["a.jpg", "photos", "*.png"], options.Inputs);
        Assert.True(options.Recursive);
        Assert.True(options.SkipExisting);
        Assert.True(CliOptions.Parse(["photos", "--recursive"]).Recursive);
    }

    [Fact]
    public void MissingValueAndUnknownOption()
    {
        Assert.Contains("Missing value", Assert.Throws<CliException>(() => CliOptions.Parse(["photo.jpg", "--width"])).Message);
        Assert.Contains("Unknown option", Assert.Throws<CliException>(() => CliOptions.Parse(["photo.jpg", "--wdith", "5"])).Message);
    }
}
