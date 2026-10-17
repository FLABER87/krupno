using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Krupno.App.Infrastructure;

/// <summary>
/// Whether a newer Krupno has been released. One HEAD request to the repository's "latest release" page on github.com, which
/// answers with a redirect to that release's tag (".../releases/tag/v0.3.0"): not the GitHub API, which allows an address
/// 60 requests an hour without a token. Nothing else is sent, the redirect isn't followed, and nothing is downloaded. Only a
/// tag of the form vX.Y.Z is a release of Krupno: the models are released too (models-v2), and are published without being
/// marked as the latest. Off with "Check for updates" under "Advanced", or with the installer's task (see <see cref="Decide"/>).
/// </summary>
internal static partial class UpdateCheck
{
    public const string LatestUrl = "https://github.com/FLABER87/krupno/releases/latest";

    /// <summary>The installer's choice: HKCU (installed for the current user) or HKLM (for all users), Software\Krupno.</summary>
    private const string RegistryKey = @"Software\Krupno";
    private const string RegistryValue = "CheckForUpdates";
    /// <summary>When that installation made it: tells a newer installation's choice from one the user has already overridden.</summary>
    private const string RegistrySetAt = "CheckForUpdatesSetAt";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The release page and version, if github.com names a release newer than <paramref name="current"/>; null if it names this
    /// one or an older one, or something that isn't a version of Krupno. Throws if github.com can't be reached.
    /// </summary>
    /// <param name="handler">For the tests; it must not follow redirects.</param>
    public static async Task<(Version Version, string Page)?> FindNewerAsync(Version current, HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: handler is null)
        {
            Timeout = Timeout,
        };
        using var request = new HttpRequestMessage(HttpMethod.Head, LatestUrl);
        request.Headers.UserAgent.ParseAdd($"Krupno/{current.ToString(3)}");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.MovedPermanently or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) || response.Headers.Location is not { } location)
        {
            return null;
        }
        var page = location.IsAbsoluteUri ? location : new Uri(new Uri(LatestUrl), location);
        return ParseTag(page) is { } version && version > current ? (version, page.AbsoluteUri) : null;
    }

    /// <summary>The version in a release page's address (".../releases/tag/v1.2.3" → 1.2.3); null for any other page or tag.</summary>
    internal static Version? ParseTag(Uri page)
    {
        var match = ReleaseTag().Match(page.AbsolutePath);
        return match.Success
            && int.TryParse(match.Groups[1].ValueSpan, out int major)
            && int.TryParse(match.Groups[2].ValueSpan, out int minor)
            && int.TryParse(match.Groups[3].ValueSpan, out int patch)
            ? new Version(major, minor, patch)
            : null;
    }

    /// <summary>This program's version in three parts, as the tags have it; KRUPNO_UPDATE_FROM=0.1.0 pretends to be another one, to see the notice.</summary>
    public static Version Current
    {
        get
        {
            if (Environment.GetEnvironmentVariable("KRUPNO_UPDATE_FROM") is { } pretend && Version.TryParse(pretend, out var pretended))
                return new Version(pretended.Major, pretended.Minor, Math.Max(0, pretended.Build));
            var version = typeof(UpdateCheck).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    /// <summary>
    /// Whether the installer's "Check for new versions" task was ticked, and when that installation ran. Null without an
    /// installer (the portable ZIP, a build).
    /// </summary>
    public static (bool Enabled, string SetAt)? InstallerChoice()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(RegistryKey);
                if (key?.GetValue(RegistryValue) is int value)
                    return (value != 0, key.GetValue(RegistrySetAt) as string ?? "");
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// Whether to check: the user's own choice in the window (<paramref name="choice"/>, made while installation
    /// <paramref name="choiceSetAt"/> was the last one), else the installer's, else on. An installation since then that turned
    /// the check off wins, and the user's choice is dropped: <paramref name="choice"/> and <paramref name="choiceSetAt"/> come
    /// back as they are to be saved. So clearing the installer's checkbox always turns the check off, and the user can still
    /// turn it on in the window afterwards.
    /// </summary>
    internal static bool Decide(ref bool? choice, ref string? choiceSetAt, (bool Enabled, string SetAt)? installer)
    {
        if (installer is { Enabled: false } off && off.SetAt != choiceSetAt)
        {
            choice = null;
            choiceSetAt = off.SetAt;
            return false;
        }
        return choice ?? installer?.Enabled ?? true;
    }

    [GeneratedRegex(@"/releases/tag/v(\d{1,9})\.(\d{1,9})\.(\d{1,9})/?$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTag();
}
