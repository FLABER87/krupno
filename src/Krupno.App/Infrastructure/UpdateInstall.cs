using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Krupno.App.Infrastructure;

/// <summary>
/// Installing a newer release once the user clicks "Update" on the notice of <see cref="UpdateCheck"/>: nothing is downloaded
/// before that. The release's installer and its SHA256SUMS.txt come from github.com by their direct links (GitHub sends them
/// on to its file storage), not through the API. The files aren't signed, so Setup runs only if the installer's SHA-256 is the
/// one SHA256SUMS.txt gives for it. It runs without questions (/SILENT shows its progress) in the mode of the installation this
/// copy belongs to, while the window closes; it keeps the components and tasks chosen before, skips the models it already has
/// and, told /UPDATE=1, starts Krupno again when it is done, or after it failed and rolled back (installer/Krupno.iss). Only
/// for an installed copy: the portable ZIP and a build link to the release page instead.
/// </summary>
internal static partial class UpdateInstall
{
    private const string DownloadUrl = "https://github.com/FLABER87/krupno/releases/download";

    /// <summary>Inno Setup's uninstall entry for the AppId of installer/Krupno.iss: in HKCU for the current user, HKLM for all users.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{2C15E368-F7C9-4CB9-9800-9F2548D61A83}_is1";

    /// <summary>A download that sends nothing for this long has failed.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Far more than an installer (63 MB in 0.2.1): a larger answer isn't one.</summary>
    private const long MaxInstallerBytes = 1L << 30;

    /// <summary>Where the installer is downloaded. It stays there while Setup runs, so the next start deletes it (<see cref="CleanUp"/>).</summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Krupno", "updates");

    /// <summary>An installer whose SHA-256 matched SHA256SUMS.txt.</summary>
    public sealed record Verified(string Path, string Sha256);

    public static string InstallerName(Version version) => $"Krupno-{version.ToString(3)}-setup-x64.exe";

    /// <summary>Setup's log of the update to <paramref name="version"/>, next to the window's logs.</summary>
    public static string SetupLogPath(Version version) => Path.Combine(Log.Folder, $"setup-{version.ToString(3)}.log");

    /// <summary>How this copy was installed: false for the current user, true for all users; null if it wasn't (the ZIP, a build).</summary>
    public static bool? InstalledForAllUsers() =>
        InstallMode(AppContext.BaseDirectory, InstallLocation(Registry.CurrentUser), InstallLocation(Registry.LocalMachine));

    /// <summary>Whether <paramref name="appFolder"/> is the installation for the current user (false), for all users (true) or neither.</summary>
    internal static bool? InstallMode(string appFolder, string? currentUser, string? allUsers) =>
        SameFolder(appFolder, currentUser) ? false : SameFolder(appFolder, allUsers) ? true : null;

    private static string? InstallLocation(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") as string ?? key?.GetValue("Inno Setup: App Path") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static bool SameFolder(string folder, string? other)
    {
        if (string.IsNullOrWhiteSpace(other))
            return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The SHA-256 that SHA256SUMS.txt gives for <paramref name="fileName"/>, in lower case: lines of "hash *name", as sha256sum
    /// writes them for binary files, or "hash  name". Null if it has no line for it.
    /// </summary>
    internal static string? FindHash(string sums, string fileName)
    {
        foreach (string line in sums.Split('\n'))
        {
            var match = SumsLine().Match(line.TrimEnd('\r'));
            if (match.Success && match.Groups[2].Value == fileName)
                return match.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Downloads the installer of <paramref name="version"/> into <paramref name="folder"/> (<see cref="Folder"/>) and returns it
    /// once its SHA-256 matches the release's SHA256SUMS.txt. Throws <see cref="InvalidDataException"/> if it doesn't, or
    /// the list has no line for it; <see cref="HttpRequestException"/> if github.com can't be reached or doesn't have the file;
    /// <see cref="TimeoutException"/> if the download stalls; OperationCanceledException once <paramref name="cancellationToken"/>
    /// is canceled. A partial or mismatching file is deleted.
    /// </summary>
    /// <param name="handler">For the tests; it must follow redirects.</param>
    public static async Task<Verified> DownloadAsync(Version version, IProgress<double> progress, CancellationToken cancellationToken,
        HttpMessageHandler? handler = null, string? folder = null)
    {
        folder ??= Folder;
        string name = InstallerName(version);
        string release = $"{DownloadUrl}/v{version.ToString(3)}/";
        using var client = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is null)
        {
            // The whole download may take minutes; a stall is caught by StallTimeout instead.
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 1 << 20,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Krupno/{UpdateCheck.Own.ToString(3)}");
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);

        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        string partial = path + ".part";
        try
        {
            string expected = FindHash(await client.GetStringAsync(release + "SHA256SUMS.txt", stall.Token), name)
                              ?? throw new InvalidDataException($"SHA256SUMS.txt of v{version.ToString(3)} has no line for {name}");
            stall.CancelAfter(StallTimeout);
            // "Try again" after Setup couldn't be started: the installer is already here.
            if (File.Exists(path) && await HashOfFileAsync(path, cancellationToken) == expected)
                return new Verified(path, expected);

            using var response = await client.GetAsync(release + name, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            response.EnsureSuccessStatusCode();
            long? length = response.Content.Headers.ContentLength;
            if (length > MaxInstallerBytes)
                throw new InvalidDataException($"{name} is {length} bytes");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    total += read;
                    if (total > MaxInstallerBytes)
                        throw new InvalidDataException($"{name} is over {MaxInstallerBytes} bytes");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                    if (length > 0)
                        progress.Report(Math.Min(1, (double)total / length.Value));
                }
            }
            string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != expected)
                throw new InvalidDataException($"{name} has SHA-256 {actual}, while SHA256SUMS.txt gives {expected}");
            File.Move(partial, path, overwrite: true);
            return new Verified(path, expected);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DeleteQuietly(partial);
            throw new TimeoutException($"github.com sent nothing for {StallTimeout.TotalSeconds:0} s");
        }
        catch
        {
            DeleteQuietly(partial);
            throw;
        }
    }

    /// <summary>
    /// Setup's command line: no wizard and no reboot, the install mode of this copy, the window's language, a log next to the
    /// window's, and /UPDATE=1 for installer/Krupno.iss. Its message boxes are left on: one only comes up when something is
    /// wrong (a Krupno started meanwhile, a model that didn't download), and then it asks the user rather than giving up.
    /// Whatever else holds the files is closed by the Restart Manager and not started again: Setup starts Krupno itself.
    /// </summary>
    internal static IReadOnlyList<string> Arguments(bool allUsers, string culture, string logPath) =>
    [
        "/SILENT", "/NORESTART", "/CLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS",
        allUsers ? "/ALLUSERS" : "/CURRENTUSER",
        // The [Languages] names of Krupno.iss are the culture names without the hyphen.
        "/LANG=" + culture.Replace("-", "").ToLowerInvariant(),
        "/LOG=" + logPath,
        "/UPDATE=1",
    ];

    /// <summary>
    /// Starts Setup for the downloaded installer; the window closes right after, and Setup starts Krupno again. The file is checked
    /// once more and held against writing until Setup has started from it, so nothing can swap it in between: Setup may go on
    /// to ask for administrator rights. Throws <see cref="InvalidDataException"/> if it changed since the download.
    /// </summary>
    public static void Start(Verified installer, bool allUsers, string culture, Version version)
    {
        using var hold = new FileStream(installer.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexStringLower(SHA256.HashData(hold)) != installer.Sha256)
            throw new InvalidDataException($"{installer.Path} changed after it was downloaded");
        var arguments = Arguments(allUsers, culture, SetupLogPath(version));
        var start = new ProcessStartInfo(installer.Path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer.Path)! };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        // For this window only: the Krupno that Setup starts again must compare as its own version.
        start.Environment.Remove(UpdateCheck.PretendVariable);
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            // ERROR_ELEVATION_REQUIRED: an installer that asks for administrator rights up front; only the shell can start it.
            var elevated = new ProcessStartInfo(installer.Path) { UseShellExecute = true, WorkingDirectory = start.WorkingDirectory };
            foreach (string argument in arguments)
                elevated.ArgumentList.Add(argument);
            Process.Start(elevated)?.Dispose();
        }
    }

    /// <summary>Deletes what earlier updates left in <see cref="Folder"/>; false if a file is still in use (Setup is finishing).</summary>
    public static bool CleanUp()
    {
        bool done = true;
        try
        {
            if (!Directory.Exists(Folder))
                return true;
            foreach (var file in new DirectoryInfo(Folder).GetFiles())
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    done = false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return done;
    }

    private static async Task<string> HashOfFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^([0-9a-fA-F]{64}) [ *](.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SumsLine();
}
