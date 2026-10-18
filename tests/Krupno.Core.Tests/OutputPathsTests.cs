using Krupno.Core.Processing;

namespace Krupno.Core.Tests;

public class OutputPathsTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "krupno-paths");

    private static string In(string name) => Path.Combine(Folder, name);

    [Theory]
    [InlineData("4xBHI_dat2_real_fp16", false, 100, false, "_4xBHI_dat2_real")]
    [InlineData("RealESRGAN_x4plus-FP32", false, 100, false, "_RealESRGAN_x4plus")]
    [InlineData("4x_fp16_model", false, 100, false, "_4x_fp16_model")]
    [InlineData("4xBHI_realplksr_dysample_real", true, 70, true, "_4xBHI_realplksr_dysample_real_denoise_s70_tta")]
    [InlineData(null, true, 100, false, "_denoise")]
    [InlineData(null, false, 100, false, "")]
    public void SettingsTag(string? model, bool denoise, int strength, bool tta, string expected)
    {
        Assert.Equal(expected, OutputPaths.SettingsTag(model, denoise, strength, tta));
    }

    [Theory]
    [InlineData("photo_x4 (2).png", "photo_x4.png")]
    [InlineData("photo_x4 (jpg) (13).png", "photo_x4 (jpg).png")]
    [InlineData("photo_x4 (1).png", "photo_x4 (1).png")]
    [InlineData("photo_x4 (jpg).png", "photo_x4 (jpg).png")]
    [InlineData("photo_x4.png", "photo_x4.png")]
    public void Unnumbered(string name, string expected)
    {
        Assert.Equal(In(expected), OutputPaths.Unnumbered(In(name)));
    }

    [Fact]
    public void KeepExistingNumbersOnlyTheResultsThatAreThere()
    {
        var there = new HashSet<string>([In("a_x4.png"), In("a_x4 (2).png"), In("c_x4.png")], StringComparer.OrdinalIgnoreCase);
        // a's result and its first number are there; b's isn't; c's number 2 is another image's source, 3 another's result.
        Assert.Equal(
            [In("a_x4 (3).png"), In("b_x4.png"), In("c_x4 (4).png"), In("c_x4 (3).png")],
            OutputPaths.KeepExisting([In("a.jpg"), In("b.jpg"), In("c_x4 (2).png"), In("d.jpg")],
                [In("a_x4.png"), In("b_x4.png"), In("c_x4.png"), In("c_x4 (3).png")], there.Contains));
        // A number never takes the name another image of the batch gets, numbered or not.
        Assert.Equal(
            [In("e (3).png"), In("e (2) (2).png")],
            OutputPaths.KeepExisting([In("e.jpg"), In("f.jpg")], [In("e.png"), In("e (2).png")],
                new HashSet<string>([In("e.png"), In("e (2).png")], StringComparer.OrdinalIgnoreCase).Contains));
    }

    [Fact]
    public void DistinctOutputsStayAsTheyAre()
    {
        string[] outputs = [In("a_x4.png"), In("b_x4.png")];
        Assert.Equal(outputs, OutputPaths.MakeUnique([In("a.jpg"), In("b.jpg")], outputs));
    }

    [Fact]
    public void SameOutputTakesTheSourceExtension()
    {
        // "photo.jpg" and "photo.png" from one folder both become "photo_x4.png": neither gets it.
        Assert.Equal(
            [In("photo_x4 (jpg).png"), In("photo_x4 (png).png"), In("photo_x4 (bmp).png")],
            OutputPaths.MakeUnique([In("photo.jpg"), In("photo.png"), In("photo.bmp")],
                [In("photo_x4.png"), In("photo_x4.png"), In("photo_x4.png")]));
    }

    [Fact]
    public void NameDoesNotDependOnTheOtherImages()
    {
        // photo.jpg alone gets the plain name. With photo.bmp added, which sorts first, photo.bmp must not get photo.jpg's earlier
        // result's name: --skip-existing would skip it as done.
        Assert.Equal([In("photo_x4.png")], OutputPaths.MakeUnique([In("photo.jpg")], [In("photo_x4.png")]));
        Assert.DoesNotContain(In("photo_x4.png"),
            OutputPaths.MakeUnique([In("photo.bmp"), In("photo.jpg")], [In("photo_x4.png"), In("photo_x4.png")]));
    }

    [Fact]
    public void SameExtensionGetsNumbered()
    {
        // Two "a.jpg" from different folders into one: the extension doesn't tell them apart.
        Assert.Equal(
            [In("a.png"), In("a (2).png")],
            OutputPaths.MakeUnique([Path.Combine(Folder, "one", "a.jpg"), Path.Combine(Folder, "two", "a.jpg")], [In("a.png"), In("a.png")]));
    }

    [Fact]
    public void OutputNeverReplacesASource()
    {
        // A result named like its own source, or like another queued source.
        Assert.Equal([In("photo (png).png")], OutputPaths.MakeUnique([In("photo.png")], [In("photo.png")]));
        Assert.Equal([In("photo_x4 (png).png"), In("photo_x4_x4.png")],
            OutputPaths.MakeUnique([In("photo.png"), In("photo_x4.png")], [In("photo_x4.png"), In("photo_x4_x4.png")]));
    }

    [Fact]
    public void NumberedNameSkipsTakenOnes()
    {
        // The name with the source's extension is taken by another source: then a number.
        Assert.Equal(
            [In("a (png) (2).png"), In("x.png")],
            OutputPaths.MakeUnique([In("a.png"), In("a (png).png")], [In("a.png"), In("x.png")]));
    }

    [Fact]
    public void NamesNobodyElseWantsComeFirst()
    {
        // "a (jpg).bmp"'s own result is "a (jpg).png", which is also what a.jpg would take after clashing with a.png: the name
        // nobody else wants stays its image's, whatever the order.
        Assert.Equal([In("a (jpg) (2).png"), In("a (png).png"), In("a (jpg).png")],
            OutputPaths.MakeUnique([In("a.jpg"), In("a.png"), In("a (jpg).bmp")], [In("a.png"), In("a.png"), In("a (jpg).png")]));
    }

    [Fact]
    public void MadeUpNamesNeverTakeAnImagesOwnName()
    {
        // "a (jpg).bmp" and "a (jpg).jpg" clash over "a (jpg).png", which a.jpg would take after clashing with a.png: nobody
        // gets it, since it may be the earlier result of either of the two.
        var result = OutputPaths.MakeUnique([In("a.jpg"), In("a.png"), In("a (jpg).bmp"), In("a (jpg).jpg")],
            [In("a.png"), In("a.png"), In("a (jpg).png"), In("a (jpg).png")]);
        Assert.Equal([In("a (jpg) (2).png"), In("a (png).png"), In("a (jpg) (bmp).png"), In("a (jpg) (jpg).png")], result);
    }

    [Fact]
    public void LongPathPrefixIsTheSameFile()
    {
        // The result of "\\?\…\a.png" is the other source, given without the prefix: it must not replace it.
        var result = OutputPaths.MakeUnique([@"\\?\" + In("a.png"), In("a_x4.png")], [@"\\?\" + In("a_x4.png"), In("a_x4_x4.png")]);
        Assert.Equal(@"\\?\" + In("a_x4 (png).png"), result[0]);
        Assert.Equal(In("a.png"), OutputPaths.Key(@"\\?\" + In("a.png")));
        Assert.Equal(@"\\server\share\a.png", OutputPaths.Key(@"\\?\UNC\server\share\a.png"));
        Assert.Equal(@"\\server\share\a.png", OutputPaths.Key(@"\\.\UNC\server\share\a.png"));
    }

    [Fact]
    public void ComparisonIgnoresCase()
    {
        // Windows file names don't depend on case.
        Assert.Equal([In("Photo (png).PNG")], OutputPaths.MakeUnique([In("photo.png")], [In("Photo.PNG")]));
    }

    [Fact]
    public void RelativePathsBecomeFull()
    {
        var result = OutputPaths.MakeUnique(["in.jpg"], ["out.png"]);
        Assert.Equal(Path.GetFullPath("out.png"), result[0]);
    }
}
