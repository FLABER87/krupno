namespace Krupno.Core.Processing;

/// <summary>Output names for a batch: no result overwrites a source image or another result of the same batch.</summary>
public static class OutputPaths
{
    /// <summary>
    /// Returns <paramref name="outputs"/> (made full paths) with clashes resolved; <paramref name="sources"/>[i] is the image of
    /// outputs[i]. A name that is one of the sources, or that several images would get, goes to none of them: each takes its
    /// source's extension instead ("photo.jpg" and "photo.png" → "photo_x4 (jpg).png" and "photo_x4 (png).png"). So which name an
    /// image gets doesn't depend on the order of the batch: an image added next to "photo.jpg" later can't take the name of
    /// photo.jpg's earlier result, which --skip-existing would then take for its own. A name made up for a clash never takes the
    /// name an image of the batch would get by itself (clashing or not), which may be that image's earlier result. Images whose
    /// extension doesn't tell them apart either (two "photo.jpg" from different folders into one) get " (2)", " (3)", … in order.
    /// </summary>
    public static string[] MakeUnique(IReadOnlyList<string> sources, IReadOnlyList<string> outputs)
    {
        if (sources.Count != outputs.Count)
            throw new ArgumentException("Every output needs its source.", nameof(sources));

        var fullOutputs = outputs.Select(Path.GetFullPath).ToArray();
        var outputKeys = fullOutputs.Select(Key).ToArray();
        var extensions = sources.Select(Extension).ToArray();
        var sourceKeys = new HashSet<string>(sources.Select(Key), StringComparer.OrdinalIgnoreCase);
        // No name made up below takes a source or a name an image of the batch would get by itself (clashing or not).
        var reserved = new HashSet<string>(sourceKeys.Concat(outputKeys), StringComparer.OrdinalIgnoreCase);
        var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claims = outputKeys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        // How many of the images that want a name have each extension: the extension tells an image apart only if it is alone.
        var extensionClaims = outputKeys.Zip(extensions, (k, e) => $"{k}|{e}")
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var result = new string[fullOutputs.Length];
        for (int i = 0; i < result.Length; i++)
        {
            string output = fullOutputs[i], key = outputKeys[i];
            if (claims[key] == 1 && !sourceKeys.Contains(key))
            {
                result[i] = output;
                given.Add(key);
                continue;
            }

            bool tagged = extensions[i].Length > 0 && extensionClaims[$"{key}|{extensions[i]}"] == 1;
            if (!tagged && !sourceKeys.Contains(key) && given.Add(key))
            {
                // The first of images the extension can't tell apart keeps the name, if it isn't a source.
                result[i] = output;
                continue;
            }
            // Otherwise the name with the source's extension, or for images it can't tell apart the plain name numbered.
            string baseName = tagged ? Tagged(output, sources[i]) : output;
            int n = 2;
            string candidate = tagged ? baseName : Numbered(baseName, n++);
            while (reserved.Contains(Key(candidate)) || !given.Add(Key(candidate)))
                candidate = Numbered(baseName, n++);
            result[i] = candidate;
        }
        return result;

        static string Numbered(string path, int n) =>
            Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({n}){Path.GetExtension(path)}");
    }

    /// <summary>
    /// The name <paramref name="output"/> takes when it clashes: with <paramref name="source"/>'s extension, "photo_x4 (jpg).png".
    /// A result under this name is the result of that source, which tells it from a source image (see the CLI's job planner).
    /// </summary>
    public static string Tagged(string output, string source) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,
            $"{Path.GetFileNameWithoutExtension(output)} ({Extension(source)}){Path.GetExtension(output)}");

    /// <summary>
    /// What tells two paths to the same file apart from different files, to compare with <see cref="StringComparer.OrdinalIgnoreCase"/>:
    /// the full path without a long-path prefix, so "\\?\C:\a.png" and "C:\a.png" are one file.
    /// </summary>
    public static string Key(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || full.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + full[8..];
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) ? full[4..] : full;
    }

    private static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
}
