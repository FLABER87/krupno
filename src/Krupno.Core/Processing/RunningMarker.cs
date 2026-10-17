using System.Security.AccessControl;
using System.Security.Principal;

namespace Krupno.Core.Processing;

/// <summary>
/// "Krupno is running" and "Krupno is being installed", as named mutexes. Every Krupno.exe and krupno-cli.exe holds the first
/// one open while it runs; Setup and the uninstaller check it (AppMutex in installer/Krupno.iss) and ask for Krupno to be
/// closed, rather than have the Restart Manager close a window or a command-line run in the middle of processing. Setup holds
/// the second one (SetupMutex) from the moment it has its rights and has found no Krupno running until it ends: no processing
/// starts meanwhile, and the window's "Update" waits for it before closing, as from then on Setup starts Krupno again whatever
/// happens.
/// The names are those Setup uses in the install mode of this copy (<see cref="Installation"/>): this session's for the current
/// user (and for a copy that wasn't installed), global ones for all users, as a Krupno installed for all users may also be
/// running in another user's session (Fast User Switching, Remote Desktop), which this session's names don't reach. Global
/// names in both modes would have Setup for one user's own copy wait for another user's.
/// </summary>
public static class RunningMarker
{
    /// <summary>The AppMutex of installer/Krupno.iss for the current user.</summary>
    public const string SessionName = "Krupno-Running";

    /// <summary>The SetupMutex of installer/Krupno.iss for the current user.</summary>
    public const string SessionSetupName = "Krupno-Setup";

    /// <summary>The AppMutex of installer/Krupno.iss for all users, together with <see cref="SessionName"/>.</summary>
    public const string GlobalName = @"Global\Krupno-Running-AllUsers";

    /// <summary>The SetupMutex of installer/Krupno.iss for all users, together with <see cref="SessionSetupName"/>.</summary>
    public const string GlobalSetupName = @"Global\Krupno-Setup-AllUsers";

    private static readonly Lock Gate = new();
    private static Mutex? _held;

    /// <summary>
    /// The mutex this process holds, the one Setup holds, and the other one Setup checks: for all users, this session's name,
    /// which Krupno 0.2.2 and earlier hold however they were installed, and so does a copy installed for the current user.
    /// The unit tests set names of their own, so a Krupno or a Setup that is running doesn't count.
    /// </summary>
    internal static (string Running, string Setup, string? AlsoChecked) Names { get; set; } = NamesFor(Installation.ForAllUsers == true);

    internal static (string Running, string Setup, string? AlsoChecked) NamesFor(bool allUsers) =>
        allUsers ? (GlobalName, GlobalSetupName, SessionName) : (SessionName, SessionSetupName, null);

    /// <summary>Holds the mutex until <see cref="Release"/> or the end of the process.</summary>
    public static void Hold()
    {
        lock (Gate)
            _held ??= Open(Names.Running);
    }

    public static void Release()
    {
        lock (Gate)
        {
            _held?.Dispose();
            _held = null;
        }
    }

    /// <summary>
    /// Whether Setup would find another Krupno.exe or krupno-cli.exe running: one that holds the mutex besides this one, in this
    /// session or, for all users, in any; for all users also one that holds this session's name.
    /// </summary>
    public static bool OthersRunning()
    {
        lock (Gate)
        {
            var names = Names;
            if (names.AlsoChecked is { } also && Exists(also))
                return true;
            bool held = _held is not null;
            _held?.Dispose();
            _held = null;
            try
            {
                return Exists(names.Running);
            }
            finally
            {
                if (held)
                    _held = Open(names.Running);
            }
        }
    }

    /// <summary>
    /// Whether Setup of Krupno is installing, in this session or, for all users, in any (it creates its mutex with a null DACL,
    /// open to anyone).
    /// </summary>
    public static bool SetupRunning() => Exists(Names.Setup);

    private static bool Exists(string name)
    {
        try
        {
            if (!MutexAcl.TryOpenExisting(name, MutexRights.Synchronize, out var other))
                return false;
            other.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // There, but not ours to open: it exists all the same.
            return true;
        }
    }

    /// <summary>
    /// The mutex, created or opened. Anyone may wait on it, which is all Setup does: Setup may run as another user (an
    /// administrator's credentials) or elevated, and a Krupno started elevated must not shut the others out of it, which the
    /// default DACL of an elevated creator would. No privilege is needed for a global name: a standard user may create a mutex
    /// there. Null if it can't be had at all.
    /// </summary>
    private static Mutex? Open(string name)
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            MutexRights.Synchronize, AccessControlType.Allow));
        if (WindowsIdentity.GetCurrent().User is { } user)
            security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
        // The other user's Krupno may end between creating and opening: the mutex is gone then, and this one makes it.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return MutexAcl.Create(initiallyOwned: false, name, out _, security);
            }
            catch (UnauthorizedAccessException)
            {
                // Created by another user, in this session or, for a global name, in any: holding it open needs no more than
                // waiting on it.
                try
                {
                    if (MutexAcl.TryOpenExisting(name, MutexRights.Synchronize, out var existing))
                        return existing;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    return null;
                }
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or IOException)
            {
                return null;
            }
        }
        return null;
    }
}
