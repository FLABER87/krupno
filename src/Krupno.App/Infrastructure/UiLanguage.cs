using System.Globalization;

namespace Krupno.App.Infrastructure;

/// <summary>The languages the window is translated into (Properties/Strings.*.resx), and the choice among them.</summary>
public static class UiLanguage
{
    /// <summary>Culture names with the language's own name for it, as the list in "Advanced" shows them.</summary>
    public static IReadOnlyList<(string Culture, string Name)> All { get; } =
    [
        ("en", "English"),
        ("uk", "Українська"),
        ("ru", "Русский"),
        ("pl", "Polski"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("it", "Italiano"),
        ("es", "Español"),
        ("pt-BR", "Português (Brasil)"),
        ("tr", "Türkçe"),
        ("id", "Bahasa Indonesia"),
        ("vi", "Tiếng Việt"),
        ("ja", "日本語"),
        ("ko", "한국어"),
        ("zh-Hans", "简体中文"),
        ("zh-Hant", "繁體中文"),
    ];

    /// <summary>
    /// Shows the window in <paramref name="culture"/> instead of the Windows display language; null or an unknown name keeps
    /// that. Only the text changes: numbers and dates keep the user's regional format. Called before the window is created.
    /// </summary>
    public static void Apply(string? culture)
    {
        if (culture is null || !All.Any(l => l.Culture == culture))
            return;
        var info = CultureInfo.GetCultureInfo(culture);
        CultureInfo.DefaultThreadCurrentUICulture = info;
        CultureInfo.CurrentUICulture = info;
    }
}
