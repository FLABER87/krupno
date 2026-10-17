using Krupno.Core.Imaging;
using Krupno.Core.Processing;

namespace Krupno.Cli;

internal readonly record struct Job(string Input, string Output);

/// <summary>Which images to upscale and where each result goes, from the inputs on the command line.</summary>
internal static class JobPlanner
{
    private static readonly char[] Wildcards = ['*', '?'];

    /// <param name="inputs">Files, folders and patterns such as "photos\*.jpg" (in the file name only).</param>
    /// <param name="output">-o: a file for a single image, otherwise a folder; null for the defaults.</param>
    /// <param name="suffix">The end of a result's name next to its source: "_x4", "_3840w".</param>
    public static List<Job> Plan(IReadOnlyList<string> inputs, string? output, ImageFileFormat? format, bool recursive, string suffix)
    {
        string extension = ImageCodec.GetExtension(format ?? ImageFileFormat.Png);
        if (inputs.Count == 1 && inputs[0].IndexOfAny(Wildcards) < 0 && File.Exists(inputs[0]))
            return [PlanFile(Path.GetFullPath(inputs[0]), output, format, extension, suffix)];

        if (output is not null && File.Exists(output))
            throw new CliException($"-o must be a folder when there is more than one image: \"{output}\" is a file.");
        string? outputDir = output is null ? null : Path.GetFullPath(output);

        // Each image with the result it would get, before names are made unique. The same image given twice is taken once.
        var planned = new List<Job>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string input in inputs)
        {
            foreach (var (file, target) in Expand(input, outputDir, recursive, extension, suffix))
            {
                if (seen.Add(file))
                    planned.Add(new Job(file, target));
            }
        }

        // An image that is the result of another one is left out: a pattern like "*.png" next to the sources, or "-r" with -o
        // inside the folder, would otherwise pick up the results of an earlier run and upscale them again.
        var results = new HashSet<string>(planned.Select(j => j.Output), StringComparer.OrdinalIgnoreCase);
        planned.RemoveAll(j => results.Contains(j.Input) && !string.Equals(j.Input, j.Output, StringComparison.OrdinalIgnoreCase));

        var outputs = OutputPaths.MakeUnique(planned.Select(j => j.Input), planned.Select(j => j.Output).ToList());
        return planned.Select((j, i) => j with { Output = outputs[i] }).ToList();
    }

    private static IEnumerable<(string File, string Output)> Expand(string input, string? outputDir, bool recursive,
        string extension, string suffix)
    {
        string? root;
        IEnumerable<string> files;
        int wildcard = input.IndexOfAny(Wildcards);
        if (wildcard >= 0)
        {
            string pattern = Path.GetFileName(input);
            string directory = Path.GetDirectoryName(input) is { Length: > 0 } d ? d : ".";
            if (pattern.Length == 0 || directory.IndexOfAny(Wildcards) >= 0)
                throw new CliException($"\"{input}\": wildcards are only supported in the file name, not in folder names.");
            root = Path.GetFullPath(directory);
            if (!Directory.Exists(root))
                throw new CliException($"Folder not found: {root}");
            files = Enumerate(root, pattern, recursive);
            // Patterns keep the results next to their sources, like single files.
            if (outputDir is null)
                return Ordered(files).Select(f => (f, NextToSource(f, extension, suffix)));
        }
        else
        {
            string path = Path.GetFullPath(input);
            if (File.Exists(path))
                return [(path, outputDir is null ? NextToSource(path, extension, suffix) : InFolder(outputDir, path, extension))];
            if (!Directory.Exists(path))
                throw new CliException($"File or folder not found: {path}");
            root = path;
            files = Enumerate(root, "*", recursive);
            outputDir ??= Path.TrimEndingDirectorySeparator(path) + "_upscaled";
        }

        // Subfolders are repeated under the output folder.
        string target = outputDir;
        return Ordered(files).Select(f => (f, InFolder(Path.Combine(target, Path.GetRelativePath(root, Path.GetDirectoryName(f)!)), f, extension)));
    }

    private static Job PlanFile(string input, string? output, ImageFileFormat? format, string extension, string suffix)
    {
        if (output is null)
            return new Job(input, NextToSource(input, extension, suffix));

        if (Directory.Exists(output) || output.EndsWith('\\') || output.EndsWith('/'))
        {
            // The source's folder is a valid choice too: then the name gets " (2)" instead of replacing the source.
            return new Job(input, OutputPaths.MakeUnique([input], [InFolder(Path.GetFullPath(output), input, extension)])[0]);
        }

        string path = Path.GetFullPath(output);
        if (string.Equals(path, input, StringComparison.OrdinalIgnoreCase))
            throw new CliException("The output file is the source image itself; choose another name.");
        // An unknown extension would get PNG data under a misleading name; a name without an extension stays PNG.
        if (format is null && Path.GetExtension(path) is { Length: > 0 } given && ImageCodec.FormatFromExtension(path) is null)
            throw new CliException($"Unknown output format \"{given}\": use .png, .jpg, .tif or .bmp, or set it with -f.");
        return new Job(input, path);
    }

    private static IEnumerable<string> Enumerate(string root, string pattern, bool recursive) =>
        Directory.EnumerateFiles(root, pattern, new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            // "*.jpg" matches "a.JPG" but not "a.jpeg" or "a.jpg_old" (the Win32 rules would match the last one too).
            MatchType = MatchType.Simple,
            MatchCasing = MatchCasing.CaseInsensitive,
        }).Where(ImageCodec.IsSupportedInput);

    /// <summary>A folder's own images first, then each subfolder in turn.</summary>
    private static IEnumerable<string> Ordered(IEnumerable<string> files) =>
        files.Select(Path.GetFullPath)
            .OrderBy(f => Path.GetDirectoryName(f), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

    private static string NextToSource(string file, string extension, string suffix) =>
        Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + suffix + extension);

    private static string InFolder(string folder, string file, string extension) =>
        Path.GetFullPath(Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + extension));
}
