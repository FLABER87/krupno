using System.Security.AccessControl;
using System.Security.Principal;

namespace Krupno.Core.Processing;

/// <summary>
/// "Krupno is running" and "Krupno is being installed", as named mutexes of this session. Every Krupno.exe and krupno-cli.exe
/// holds the first one open while it runs; Setup and the uninstaller check it (AppMutex in installer/Krupno.iss) and ask for
/// Krupno to be closed, rather than have the Restart Manager close a window or a command-line run in the middle of processing.
/// Setup holds the second one (SetupMutex) from the moment it has its rights and has found no Krupno running until it ends:
/// no processing starts meanwhile, and the window's "Update" waits for it before closing, as from then on Setup starts Krupno
/// again whatever happens.
/// </summary>
public static class RunningMarker
{
    /// <summary>The AppMutex of installer/Krupno.iss.</summary>
    public const string DefaultName = "Krupno-Running";

    /// <summary>The SetupMutex of installer/Krupno.iss.</summary>
    public const string DefaultSetupName = "Krupno-Setup";

    private static readonly Lock Gate = new();
    private static Mutex? _held;

    /// <summary>The unit tests set names of their own, so a Krupno or a Setup that is running doesn't count.</summary>
    internal static string Name { get; set; } = DefaultName;

    internal static string SetupName { get; set; } = DefaultSetupName;

    /// <summary>Holds the mutex until <see cref="Release"/> or the end of the process.</summary>
    public static void Hold()
    {
        lock (Gate)
            _held ??= Open();
    }

    public static void Release()
    {
        lock (Gate)
        {
            _held?.Dispose();
            _held = null;
        }
    }

    /// <summary>Whether another Krupno.exe or krupno-cli.exe of this session is running, that is, holds the mutex besides this one.</summary>
    public static bool OthersRunning()
    {
        lock (Gate)
        {
            bool held = _held is not null;
            _held?.Dispose();
            _held = null;
            try
            {
                return Exists(Name);
            }
            finally
            {
                if (held)
                    _held = Open();
            }
        }
    }

    /// <summary>Whether Setup of Krupno is installing in this session (it creates its mutex with a null DACL, open to anyone).</summary>
    public static bool SetupRunning() => Exists(SetupName);

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
    /// default DACL of an elevated creator would. Null if it can't be had at all.
    /// </summary>
    private static Mutex? Open()
    {
        try
        {
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                MutexRights.Synchronize, AccessControlType.Allow));
            if (WindowsIdentity.GetCurrent().User is { } user)
                security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
            return MutexAcl.Create(initiallyOwned: false, Name, out _, security);
        }
        catch (UnauthorizedAccessException)
        {
            // Created by another user in this session: holding it open needs no more than waiting on it.
            try
            {
                return MutexAcl.TryOpenExisting(Name, MutexRights.Synchronize, out var existing) ? existing : null;
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
}
