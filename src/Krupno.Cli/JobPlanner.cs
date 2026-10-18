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
    /// <param name="tag">What follows it, next to the source and in an output folder: "" or "_4xBHI_dat2_real" (see
    /// <see cref="OutputPaths.SettingsTag"/>).</param>
    public static List<Job> Plan(IReadOnlyList<string> inputs, string? output, ImageFileFormat? format, bool recursive, string suffix,
        string tag = "") =>
        Plan(inputs, output, format, recursive, suffix, tag, out _);

    /// <param name="leftOut">How many images found in folders and patterns were left out as results of other images.</param>
    public static List<Job> Plan(IReadOnlyList<string> inputs, string? output, ImageFileFormat? format, bool recursive, string suffix,
        string tag, out int leftOut)
    {
        leftOut = 0;
        string ending = tag + ImageCodec.GetExtension(format ?? ImageFileFormat.Png);
        if (inputs.Count == 1 && !HasWildcard(inputs[0]) && File.Exists(inputs[0]))
            return [PlanFile(Path.GetFullPath(inputs[0]), output, format, ending, suffix)];

        if (output is not null && File.Exists(output))
            throw new CliException($"-o must be a folder when there is more than one image: \"{output}\" is a file.");
        string? outputDir = output is null ? null : Path.GetFullPath(output);

        // Each image with the result it would get, before names are made unique. The same image given twice is taken once,
        // and named on the command line it counts as named, wherever else it is found too.
        var planned = new List<(Job Job, bool Named)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string input in inputs)
        {
            foreach (var (file, target, named) in Expand(input, outputDir, recursive, ending, suffix))
            {
                if (index.TryGetValue(OutputPaths.Key(file), out int at))
                    planned[at] = planned[at] with { Named = planned[at].Named || named };
                else
                {
                    index[OutputPaths.Key(file)] = planned.Count;
                    planned.Add((new Job(file, target), named));
                }
            }
        }

        // An image found in a folder or by a pattern that is the result of another one is left out: "*.png" next to the
        // sources, or "-r" with -o inside the folder, would otherwise pick up the results of an earlier run and upscale them
        // again. A result is known by the name it gets, plain or with its source's extension after a clash (see OutputPaths).
        // An image named on the command line is always taken, and then it is a source no result may replace.
        var results = new HashSet<string>(planned.SelectMany(p => new[] { p.Job.Output, OutputPaths.Tagged(p.Job.Output, p.Job.Input) }).Select(OutputPaths.Key),
            StringComparer.OrdinalIgnoreCase);
        leftOut = planned.RemoveAll(p => !p.Named && results.Contains(OutputPaths.Key(p.Job.Input))
                                         && !string.Equals(OutputPaths.Key(p.Job.Input), OutputPaths.Key(p.Job.Output), StringComparison.OrdinalIgnoreCase));

        var outputs = OutputPaths.MakeUnique(planned.Select(p => p.Job.Input).ToList(), planned.Select(p => p.Job.Output).ToList());
        return planned.Select((p, i) => p.Job with { Output = outputs[i] }).ToList();
    }

    private static IEnumerable<(string File, string Output, bool Named)> Expand(string input, string? outputDir, bool recursive,
        string ending, string suffix)
    {
        string? root;
        IEnumerable<string> files;
        if (HasWildcard(input))
        {
            string pattern = Path.GetFileName(input);
            string directory = Path.GetDirectoryName(input) is { Length: > 0 } d ? d : ".";
            if (pattern.Length == 0 || HasWildcard(directory))
                throw new CliException($"\"{input}\": wildcards are only supported in the file name, not in folder names.");
            root = Path.GetFullPath(directory);
            if (!Directory.Exists(root))
                throw new CliException($"Folder not found: {root}");
            files = Enumerate(root, pattern, recursive);
            // Patterns keep the results next to their sources, like single files.
            if (outputDir is null)
                return Ordered(files).Select(f => (f, NextToSource(f, ending, suffix), false));
        }
        else
        {
            string path = Path.GetFullPath(input);
            if (File.Exists(path))
                return [(path, outputDir is null ? NextToSource(path, ending, suffix) : Target(outputDir, path, ending, suffix), true)];
            if (!Directory.Exists(path))
                throw new CliException($"File or folder not found: {path}");
            root = path;
            files = Enumerate(root, "*", recursive);
            outputDir ??= Path.TrimEndingDirectorySeparator(path) + "_upscaled";
        }

        // Subfolders are repeated under the output folder.
        string target = outputDir;
        return Ordered(files).Select(f => (f, Target(Path.Combine(target, Path.GetRelativePath(root, Path.GetDirectoryName(f)!)), f, ending, suffix), false));
    }

    private static Job PlanFile(string input, string? output, ImageFileFormat? format, string ending, string suffix)
    {
        if (output is null)
            return new Job(input, NextToSource(input, ending, suffix));

        if (Directory.Exists(output) || output.EndsWith('\\') || output.EndsWith('/'))
            return new Job(input, OutputPaths.MakeUnique([input], [Target(Path.GetFullPath(output), input, ending, suffix)])[0]);

        string path = Path.GetFullPath(output);
        if (string.Equals(OutputPaths.Key(path), OutputPaths.Key(input), StringComparison.OrdinalIgnoreCase))
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

    private static string NextToSource(string file, string ending, string suffix) =>
        Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + suffix + ending);

    /// <summary>
    /// The result in an output folder keeps the source's name ("a.jpg" → "a.png"); in the source's own folder it gets the suffix,
    /// as next to the source: "a.png" as its result would replace the source or another image there, and a rerun couldn't tell
    /// the earlier results from the sources.
    /// </summary>
    private static string Target(string folder, string file, string ending, string suffix)
    {
        string path = Path.GetFullPath(Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + ending));
        return string.Equals(OutputPaths.Key(Path.GetDirectoryName(path)!), OutputPaths.Key(Path.GetDirectoryName(file)!), StringComparison.OrdinalIgnoreCase)
            ? NextToSource(file, ending, suffix)
            : path;
    }

    /// <summary>A "*" or "?" outside the "\\?\" prefix of a long path.</summary>
    private static bool HasWildcard(string path) =>
        (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ? path[4..] : path)
        .IndexOfAny(Wildcards) >= 0;
}
