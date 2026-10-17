using System.Globalization;

namespace Krupno.Core.Localization;

/// <summary>
/// Plural forms in the .resx text: a text that depends on a count holds one form per plural category of its language,
/// separated by "|" ("{0} file|{0} files"; Ukrainian has three, Chinese one), and {0} is the count.
/// </summary>
public static class Plurals
{
    /// <summary>The form of <paramref name="forms"/> for <paramref name="count"/>, with {0} = the count and {1}, … = <paramref name="args"/>.</summary>
    /// <param name="culture">The language whose rules apply, and whose number format: the current UI culture if null.</param>
    public static string Format(string forms, long count, CultureInfo? culture = null, params object?[] args)
    {
        var language = culture ?? CultureInfo.CurrentUICulture;
        string[] split = forms.Split('|');
        string form = split[Math.Min(Form(language, count), split.Length - 1)];
        return string.Format(culture ?? CultureInfo.CurrentCulture, form, [count, .. args]);
    }

    /// <summary>How many plural forms a language has: how many "|"-separated texts its plural resources hold.</summary>
    public static int FormCount(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "uk" or "ru" or "pl" => 3,
        "ja" or "ko" or "zh" or "vi" or "id" => 1,
        _ => 2,
    };

    /// <summary>
    /// Which plural form a count takes (the CLDR rules for whole numbers). English, German, Italian, Spanish, Turkish: "1 file",
    /// "2 files"; French and Portuguese take the singular for 0 too. Ukrainian and Russian have one (1, 21, 101), few (2–4, 22–24)
    /// and many (0, 5–20, 25–30); Polish the same, except that only 1 itself takes the first form. Japanese, Korean, Chinese,
    /// Vietnamese and Indonesian have a single form.
    /// </summary>
    public static int Form(CultureInfo culture, long count)
    {
        long n = Math.Abs(count);
        bool few = n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14;
        return culture.TwoLetterISOLanguageName switch
        {
            "uk" or "ru" => n % 10 == 1 && n % 100 != 11 ? 0 : few ? 1 : 2,
            "pl" => n == 1 ? 0 : few ? 1 : 2,
            "fr" or "pt" => n <= 1 ? 0 : 1,
            "ja" or "ko" or "zh" or "vi" or "id" => 0,
            _ => n == 1 ? 0 : 1,
        };
    }
}
