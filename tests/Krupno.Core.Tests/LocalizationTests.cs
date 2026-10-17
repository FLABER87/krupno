using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Krupno.App.Infrastructure;
using Krupno.Core.Localization;

namespace Krupno.Core.Tests;

/// <summary>
/// The .resx text of the window (src/Krupno.App/Properties) and of the library (src/Krupno.Core/Properties), read from the
/// source tree: a translation with a missing key falls back to English silently, and one with a wrong placeholder throws only
/// when that text is shown.
/// </summary>
public partial class LocalizationTests
{
    public static TheoryData<string> Projects => ["Krupno.App", "Krupno.Core"];

    private static readonly string Root = FindRoot();

    /// <summary>The repository this file was compiled from: the folder above it that holds Krupno.slnx.</summary>
    private static string FindRoot([CallerFilePath] string source = "")
    {
        for (var dir = new FileInfo(source).Directory; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Krupno.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"The repository root (Krupno.slnx) was not found above {source}.");
    }

    private static string Folder(string project) => Path.Combine(Root, "src", project, "Properties");

    /// <summary>Key → (value, comment) of a .resx file.</summary>
    private static Dictionary<string, (string Value, string? Comment)> Read(string path) =>
        XDocument.Load(path).Root!.Elements("data").ToDictionary(
            d => (string)d.Attribute("name")!,
            d => ((string?)d.Element("value") ?? "", (string?)d.Element("comment")));

    /// <summary>The translations of a project: culture → its .resx file.</summary>
    private static Dictionary<string, string> Translations(string project) =>
        Directory.GetFiles(Folder(project), "Strings.*.resx")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f)["Strings.".Length..]);

    private static bool IsPlural(string? comment) => comment?.StartsWith("Plural forms", StringComparison.Ordinal) == true;

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static SortedSet<int> Placeholders(string text) =>
        [.. Placeholder().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];

    [Theory]
    [MemberData(nameof(Projects))]
    public void EveryLanguageOfTheWindowHasItsTranslation(string project)
    {
        Assert.Equal(
            UiLanguage.All.Select(l => l.Culture).Where(c => c != "en").Order(StringComparer.Ordinal),
            Translations(project).Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void TranslationsMatchTheEnglishText(string project)
    {
        var english = Read(Path.Combine(Folder(project), "Strings.resx"));
        var problems = new List<string>();
        foreach (var (culture, path) in Translations(project))
        {
            var translated = Read(path);
            foreach (string missing in english.Keys.Except(translated.Keys))
                problems.Add($"{culture}: {missing} is missing");
            foreach (string extra in translated.Keys.Except(english.Keys))
                problems.Add($"{culture}: {extra} is not in Strings.resx");

            int forms = Plurals.FormCount(CultureInfo.GetCultureInfo(culture));
            foreach (var (key, (value, _)) in translated.Where(t => english.ContainsKey(t.Key)))
            {
                var (source, comment) = english[key];
                if (!Placeholders(value).SetEquals(Placeholders(source)))
                    problems.Add($"{culture}: {key} has {{{string.Join("}, {", Placeholders(value))}}} instead of {{{string.Join("}, {", Placeholders(source))}}}");
                if (IsPlural(comment) && value.Split('|').Length != forms)
                    problems.Add($"{culture}: {key} has {value.Split('|').Length} plural forms instead of {forms}");
                problems.AddRange(FormatProblems(culture, key, value, Placeholders(source)));
            }
        }
        foreach (var (key, (value, _)) in english)
            problems.AddRange(FormatProblems("en", key, value, Placeholders(value)));
        Assert.Empty(problems);
    }

    /// <summary>A stray brace makes string.Format throw, but only when the text is shown.</summary>
    private static IEnumerable<string> FormatProblems(string culture, string key, string value, SortedSet<int> placeholders)
    {
        var args = new object[placeholders.Count == 0 ? 0 : placeholders.Max + 1];
        Array.Fill(args, 1);
        foreach (string form in value.Split('|'))
        {
            string? error = null;
            try
            {
                string.Format(CultureInfo.InvariantCulture, form, args);
            }
            catch (FormatException ex)
            {
                error = $"{culture}: {key} is not a valid format: {ex.Message}";
            }
            if (error is not null)
                yield return error;
        }
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void GeneratedClassMatchesTheResx(string project)
    {
        // Strings.Designer.cs is regenerated by Visual Studio or Rider when Strings.resx is saved; a key added or removed by hand
        // in the XML needs the class regenerated too.
        var keys = Read(Path.Combine(Folder(project), "Strings.resx")).Keys.Order(StringComparer.Ordinal);
        string designer = File.ReadAllText(Path.Combine(Folder(project), "Strings.Designer.cs"));
        var properties = Regex.Matches(designer, @"(?:public|internal) static string (\w+) \{")
            .Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal);
        Assert.Equal(keys, properties);
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void EveryTextIsUsed(string project)
    {
        string code = string.Join("\n", Directory.EnumerateFiles(Path.Combine(Root, "src", project), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) && !f.EndsWith(".Designer.cs", StringComparison.Ordinal)
                        || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        var unused = Read(Path.Combine(Folder(project), "Strings.resx")).Keys
            .Where(key => !Regex.IsMatch(code, $@"Strings\.{key}\b"));
        Assert.Empty(unused);
    }

    [Theory]
    [InlineData("en", new long[] { 1 }, new long[] { 0, 2, 5, 11, 21, 101 }, new long[0])]
    [InlineData("de", new long[] { 1 }, new long[] { 0, 2, 21 }, new long[0])]
    [InlineData("tr", new long[] { 1 }, new long[] { 0, 2, 21 }, new long[0])]
    [InlineData("fr", new long[] { 0, 1 }, new long[] { 2, 5, 21, 101 }, new long[0])]
    [InlineData("pt-BR", new long[] { 0, 1 }, new long[] { 2, 5, 21 }, new long[0])]
    [InlineData("uk", new long[] { 1, 21, 101, 1001 }, new long[] { 2, 3, 4, 22, 24, 102 }, new long[] { 0, 5, 11, 12, 14, 19, 20, 25, 111, 112 })]
    [InlineData("ru", new long[] { 1, 21, 31 }, new long[] { 2, 4, 23, 34 }, new long[] { 0, 5, 11, 13, 100, 111 })]
    [InlineData("pl", new long[] { 1 }, new long[] { 2, 4, 22, 24, 102 }, new long[] { 0, 5, 11, 12, 14, 21, 25, 112 })]
    public void PluralForms(string culture, long[] first, long[] second, long[] third)
    {
        var info = CultureInfo.GetCultureInfo(culture);
        Assert.All(first, n => Assert.Equal(0, Plurals.Form(info, n)));
        Assert.All(second, n => Assert.Equal(1, Plurals.Form(info, n)));
        Assert.All(third, n => Assert.Equal(2, Plurals.Form(info, n)));
        Assert.Equal(third.Length > 0 ? 3 : 2, Plurals.FormCount(info));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("vi")]
    [InlineData("id")]
    public void SingleFormLanguages(string culture)
    {
        var info = CultureInfo.GetCultureInfo(culture);
        Assert.Equal(1, Plurals.FormCount(info));
        Assert.All(new long[] { 0, 1, 2, 5, 21 }, n => Assert.Equal(0, Plurals.Form(info, n)));
    }

    [Fact]
    public void PluralFormatPicksTheFormAndFillsTheCount()
    {
        var uk = CultureInfo.GetCultureInfo("uk");
        Assert.Equal("21 файл", Plurals.Format("{0} файл|{0} файли|{0} файлів", 21, uk));
        Assert.Equal("3 файли", Plurals.Format("{0} файл|{0} файли|{0} файлів", 3, uk));
        Assert.Equal("12 файлів", Plurals.Format("{0} файл|{0} файли|{0} файлів", 12, uk));
        Assert.Equal("1 file of 3", Plurals.Format("{0} file of {1}|{0} files of {1}", 1, CultureInfo.InvariantCulture, 3));
        // Fewer forms than the language has (a translation not done yet): the last one is used rather than nothing.
        Assert.Equal("5 x", Plurals.Format("{0} x", 5, uk));
    }
}
