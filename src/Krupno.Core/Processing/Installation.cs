using Microsoft.Win32;

namespace Krupno.Core.Processing;

/// <summary>
/// How this copy of Krupno was installed, by Inno Setup's uninstall entry for the AppId of installer/Krupno.iss: in HKCU for
/// the current user, in HKLM for all users. The window and krupno-cli are in the same folder, so both get the same answer.
/// </summary>
public static class Installation
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{2C15E368-F7C9-4CB9-9800-9F2548D61A83}_is1";

    private static readonly Lazy<bool?> Mode = new(() =>
        ModeOf(AppContext.BaseDirectory, InstallLocation(Registry.CurrentUser), InstallLocation(Registry.LocalMachine)));

    /// <summary>False for the current user, true for all users; null if it wasn't installed (the ZIP, a build). Read once.</summary>
    public static bool? ForAllUsers => Mode.Value;

    /// <summary>Whether <paramref name="appFolder"/> is the installation for the current user (false), for all users (true) or neither.</summary>
    internal static bool? ModeOf(string appFolder, string? currentUser, string? allUsers) =>
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
}
