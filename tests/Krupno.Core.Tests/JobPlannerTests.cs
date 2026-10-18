using Krupno.Cli;
using Krupno.Core.Imaging;

namespace Krupno.Core.Tests;

/// <summary>Which images krupno-cli takes and where their results go: files, folders, -r and patterns, in a temporary folder.</summary>
public sealed class JobPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"krupno-jobs-{Guid.NewGuid():N}");

    public JobPlannerTests()
    {
        // photos/a.jpg, b.png, notes.txt, sub/c.jpg, sub/deeper/d.png
        foreach (string file in new[] { "photos/a.jpg", "photos/b.png", "photos/notes.txt", "photos/sub/c.jpg", "photos/sub/deeper/d.png" })
        {
            string path = P(file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string P(string relative) => Path.GetFullPath(Path.Combine(_root, relative));

    private List<Job> Plan(string[] inputs, string? output = null, bool recursive = false, ImageFileFormat? format = null) =>
        JobPlanner.Plan(inputs.Select(P).ToList(), output is null ? null : P(output), format, recursive, "_x4");

    private static void AssertJobs(IEnumerable<(string Input, string Output)> expected, List<Job> actual) =>
        Assert.Equal(expected.Select(e => $"{e.Input} → {e.Output}"), actual.Select(j => $"{j.Input} → {j.Output}"));

    [Fact]
    public void SingleFileNextToTheSource()
    {
        AssertJobs([(P("photos/a.jpg"), P("photos/a_x4.png"))], Plan(["photos/a.jpg"]));
        AssertJobs([(P("photos/a.jpg"), P("photos/a_x4.jpg"))], Plan(["photos/a.jpg"], format: ImageFileFormat.Jpeg));
    }

    [Fact]
    public void SingleFileToAFileOrAFolder()
    {
        AssertJobs([(P("photos/a.jpg"), P("big.png"))], Plan(["photos/a.jpg"], "big.png"));
        Directory.CreateDirectory(P("out"));
        AssertJobs([(P("photos/a.jpg"), P("out/a.png"))], Plan(["photos/a.jpg"], "out"));
        Assert.Throws<CliException>(() => Plan(["photos/a.jpg"], "photos/a.jpg"));
        Assert.Throws<CliException>(() => Plan(["photos/a.jpg"], "big.webp"));
    }

    [Fact]
    public void FolderTakesItsOwnImages()
    {
        AssertJobs(
            [(P("photos/a.jpg"), P("photos_upscaled/a.png")), (P("photos/b.png"), P("photos_upscaled/b.png"))],
            Plan(["photos"]));
    }

    [Fact]
    public void RecursiveRepeatsTheSubfolders()
    {
        AssertJobs(
            [
                (P("photos/a.jpg"), P("out/a.png")), (P("photos/b.png"), P("out/b.png")),
                (P("photos/sub/c.jpg"), P("out/sub/c.png")), (P("photos/sub/deeper/d.png"), P("out/sub/deeper/d.png")),
            ],
            Plan(["photos"], "out", recursive: true));
        Assert.Equal(P("photos_upscaled/sub/deeper/d.png"), Plan(["photos"], recursive: true)[^1].Output);
    }

    [Fact]
    public void RecursiveIntoAFolderInsideLeavesEarlierResultsOut()
    {
        // The results of an earlier run in photos/out are not sources of this one.
        Directory.CreateDirectory(P("photos/out"));
        File.WriteAllBytes(P("photos/out/a.png"), []);
        File.WriteAllBytes(P("photos/out/b.png"), []);
        var jobs = Plan(["photos"], "photos/out", recursive: true);
        Assert.DoesNotContain(jobs, j => j.Input.StartsWith(P("photos/out"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, jobs.Count);
    }

    [Fact]
    public void PatternsMatchFileNames()
    {
        File.WriteAllBytes(P("photos/e.JPG"), []);
        File.WriteAllBytes(P("photos/f.jpeg"), []);
        AssertJobs(
            [(P("photos/a.jpg"), P("photos/a_x4.png")), (P("photos/e.JPG"), P("photos/e_x4.png"))],
            Plan(["photos/*.jpg"]));
        AssertJobs(
            [
                (P("photos/a.jpg"), P("photos/a_x4.png")), (P("photos/e.JPG"), P("photos/e_x4.png")),
                (P("photos/sub/c.jpg"), P("photos/sub/c_x4.png")),
            ],
            Plan(["photos/?.jpg"], recursive: true));
        // Into a folder, a pattern repeats the subfolders too.
        Assert.Equal(P("out/sub/c.png"), Plan(["photos/?.jpg"], "out", recursive: true)[2].Output);
    }

    [Fact]
    public void PatternLeavesEarlierResultsOut()
    {
        // "*.png" next to the sources would otherwise upscale b_x4.png from an earlier run into b_x4_x4.png.
        File.WriteAllBytes(P("photos/b_x4.png"), []);
        AssertJobs([(P("photos/b.png"), P("photos/b_x4.png"))], Plan(["photos/*.png"]));
    }

    [Fact]
    public void SeveralInputsGoIntoOneFolder()
    {
        // b.png given twice is taken once; a.jpg and the other a.jpg get different names.
        Directory.CreateDirectory(P("more"));
        File.WriteAllBytes(P("more/a.jpg"), []);
        AssertJobs(
            [
                (P("photos/b.png"), P("out/b.png")), (P("photos/a.jpg"), P("out/a.png")),
                (P("more/a.jpg"), P("out/a (2).png")),
            ],
            Plan(["photos/b.png", "photos/*.*", "more"], "out"));
    }

    [Fact]
    public void SeveralInputsWithoutOutputKeepTheirDefaults()
    {
        AssertJobs(
            [
                (P("photos/sub/c.jpg"), P("photos/sub/c_x4.png")),
                (P("photos/sub/deeper/d.png"), P("photos/sub/deeper_upscaled/d.png")),
            ],
            Plan(["photos/sub/c.jpg", "photos/sub/deeper"]));
    }

    [Fact]
    public void NamedImagesAreNeverLeftOut()
    {
        // b_x4.png is named like b.png's result, but it was asked for by name: it is upscaled, and b.png's result goes elsewhere
        // instead of replacing it.
        File.WriteAllBytes(P("photos/b_x4.png"), []);
        AssertJobs(
            [(P("photos/b.png"), P("photos/b_x4 (png).png")), (P("photos/b_x4.png"), P("photos/b_x4_x4.png"))],
            Plan(["photos/b.png", "photos/b_x4.png"]));
        // Found by a pattern, the same file is an earlier result and is left out.
        var jobs = JobPlanner.Plan([P("photos/*.png")], null, null, false, "_x4", "", out int leftOut);
        Assert.Equal(1, leftOut);
        AssertJobs([(P("photos/b.png"), P("photos/b_x4.png"))], jobs);
    }

    [Fact]
    public void SourceFolderAsOutputKeepsTheSuffix()
    {
        // -o pointing at the sources' own folder: the results are named as next to the sources, so a rerun tells them apart.
        AssertJobs(
            [(P("photos/a.jpg"), P("photos/a_x4.png")), (P("photos/b.png"), P("photos/b_x4.png"))],
            Plan(["photos"], "photos"));
        File.WriteAllBytes(P("photos/a_x4.png"), []);
        File.WriteAllBytes(P("photos/b_x4.png"), []);
        AssertJobs(
            [(P("photos/a.jpg"), P("photos/a_x4.png")), (P("photos/b.png"), P("photos/b_x4.png"))],
            Plan(["photos"], "photos"));
        AssertJobs([(P("photos/a.jpg"), P("photos/a_x4.png"))], Plan(["photos/a.jpg"], "photos"));
    }

    [Fact]
    public void AddedImageDoesNotTakeAnEarlierResult()
    {
        // photos_upscaled/a.png is a.jpg's. With a.bmp added (it sorts first) neither takes that name, so --skip-existing
        // can't skip a.bmp as done.
        Assert.Equal(P("photos_upscaled/a.png"), Plan(["photos"])[0].Output);
        File.WriteAllBytes(P("photos/a.bmp"), []);
        AssertJobs(
            [
                (P("photos/a.bmp"), P("photos_upscaled/a (bmp).png")), (P("photos/a.jpg"), P("photos_upscaled/a (jpg).png")),
                (P("photos/b.png"), P("photos_upscaled/b.png")),
            ],
            Plan(["photos"]));
    }

    [Fact]
    public void ResultsWithTheSourceExtensionAreLeftOut()
    {
        // b.jpg and b.png both want b_x4.png; the earlier run's "b_x4 (jpg).png" and "b_x4 (png).png" are results, not sources.
        File.WriteAllBytes(P("photos/b.jpg"), []);
        File.WriteAllBytes(P("photos/b_x4 (jpg).png"), []);
        File.WriteAllBytes(P("photos/b_x4 (png).png"), []);
        AssertJobs(
            [
                (P("photos/a.jpg"), P("photos/a_x4.png")), (P("photos/b.jpg"), P("photos/b_x4 (jpg).png")),
                (P("photos/b.png"), P("photos/b_x4 (png).png")),
            ],
            Plan(["photos/*.*"]));
    }

    [Fact]
    public void ModelTagFollowsTheSuffix()
    {
        // --name-model: next to the source after the scale, in an output folder after the source's name.
        Assert.Equal(P("photos/a_x4_4xBHI_dat2_real.png"),
            JobPlanner.Plan([P("photos/a.jpg")], null, null, false, "_x4", "_4xBHI_dat2_real").Single().Output);
        Assert.Equal(P("out/a_4xBHI_dat2_real.png"),
            JobPlanner.Plan([P("photos/a.jpg")], P("out") + Path.DirectorySeparatorChar, null, false, "_x4", "_4xBHI_dat2_real").Single().Output);
        // A result named with the model is known as one: a pattern leaves it out, but not one of another model.
        File.WriteAllBytes(P("photos/b_x4_4xBHI_dat2_real.png"), []);
        File.WriteAllBytes(P("photos/b_x4_RealESRGAN_x4plus.png"), []);
        var jobs = JobPlanner.Plan([P("photos/*.png")], null, null, false, "_x4", "_4xBHI_dat2_real", out int leftOut);
        Assert.Equal(1, leftOut);
        Assert.Equal([P("photos/b.png"), P("photos/b_x4_RealESRGAN_x4plus.png")], jobs.Select(j => j.Input));
    }

    [Fact]
    public void LongPathPrefixIsNotAPattern()
    {
        string file = @"\\?\" + P("photos/a.jpg");
        Assert.Equal(@"\\?\" + P("photos/a_x4.png"), JobPlanner.Plan([file], null, null, false, "_x4").Single().Output);
        Assert.Equal(2, JobPlanner.Plan([file, P("photos/b.png")], null, null, false, "_x4").Count);
    }

    [Fact]
    public void Errors()
    {
        Assert.Contains("not found", Assert.Throws<CliException>(() => Plan(["missing.jpg"])).Message);
        Assert.Contains("not found", Assert.Throws<CliException>(() => Plan(["photos/a.jpg", "missing"])).Message);
        Assert.Contains("not found", Assert.Throws<CliException>(() => Plan(["nowhere/*.jpg"])).Message);
        Assert.Contains("file name", Assert.Throws<CliException>(() => Plan(["pho*/a.jpg"])).Message);
        File.WriteAllBytes(P("taken.png"), []);
        Assert.Contains("must be a folder", Assert.Throws<CliException>(() => Plan(["photos"], "taken.png")).Message);
        Assert.Empty(Plan(["photos/*.gif"]));
    }
}
