using Krupno.Core.Processing;

namespace Krupno.Core.Tests;

public class OutputPathsTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "krupno-paths");

    private static string In(string name) => Path.Combine(Folder, name);

    [Fact]
    public void DistinctOutputsStayAsTheyAre()
    {
        string[] outputs = [In("a_x4.png"), In("b_x4.png")];
        Assert.Equal(outputs, OutputPaths.MakeUnique([In("a.jpg"), In("b.jpg")], outputs));
    }

    [Fact]
    public void SameOutputGetsNumbered()
    {
        // "photo.jpg" and "photo.png" from one folder both become "photo_x4.png".
        Assert.Equal(
            [In("photo_x4.png"), In("photo_x4 (2).png"), In("photo_x4 (3).png")],
            OutputPaths.MakeUnique([In("photo.jpg"), In("photo.png"), In("photo.bmp")],
                [In("photo_x4.png"), In("photo_x4.png"), In("photo_x4.png")]));
    }

    [Fact]
    public void OutputNeverReplacesASource()
    {
        // -o pointing at the source folder with the same name and format.
        Assert.Equal([In("photo (2).png")], OutputPaths.MakeUnique([In("photo.png")], [In("photo.png")]));
    }

    [Fact]
    public void NumberedNameSkipsTakenOnes()
    {
        Assert.Equal(
            [In("a (3).png"), In("a (2) (2).png")],
            OutputPaths.MakeUnique([In("a.png"), In("a (2).png")], [In("a.png"), In("a (2).png")]));
    }

    [Fact]
    public void ComparisonIgnoresCase()
    {
        // Windows file names don't depend on case.
        Assert.Equal([In("Photo (2).PNG")], OutputPaths.MakeUnique([In("photo.png")], [In("Photo.PNG")]));
    }

    [Fact]
    public void RelativePathsBecomeFull()
    {
        var result = OutputPaths.MakeUnique([], ["out.png"]);
        Assert.Equal(Path.GetFullPath("out.png"), result[0]);
    }
}
