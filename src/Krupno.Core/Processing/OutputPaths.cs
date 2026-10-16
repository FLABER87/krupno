namespace Krupno.Core.Processing;

/// <summary>Output names for a batch: no result overwrites a source image or another result of the same batch.</summary>
public static class OutputPaths
{
    /// <summary>
    /// Returns <paramref name="outputs"/> (full paths) with clashes resolved: an output that is one of the <paramref name="inputs"/>
    /// or was already given to an earlier job gets " (2)", " (3)", … before the extension.
    /// "photo.jpg" and "photo.png" from one folder would otherwise both become "photo_x4.png", and saving "photo.png" next to
    /// the original with "-o" pointing at its folder would replace the original.
    /// </summary>
    public static string[] MakeUnique(IEnumerable<string> inputs, IReadOnlyList<string> outputs)
    {
        var taken = new HashSet<string>(inputs.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var result = new string[outputs.Count];
        for (int i = 0; i < outputs.Count; i++)
        {
            string output = Path.GetFullPath(outputs[i]);
            string directory = Path.GetDirectoryName(output)!;
            string name = Path.GetFileNameWithoutExtension(output);
            string extension = Path.GetExtension(output);
            for (int n = 2; !taken.Add(output); n++)
                output = Path.Combine(directory, $"{name} ({n}){extension}");
            result[i] = output;
        }
        return result;
    }
}
