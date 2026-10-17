namespace Krupno.Core.Processing;

/// <summary>
/// "Krupno is running": a named mutex that every Krupno.exe and krupno-cli.exe of this session holds open while it runs. Setup
/// and the uninstaller check it (AppMutex in installer/Krupno.iss) and ask for Krupno to be closed, rather than have the Restart
/// Manager close a window or a command-line run in the middle of processing. The window's "Update" lets go of its own just
/// before it starts Setup, and checks first that no other Krupno holds it.
/// </summary>
public static class RunningMarker
{
    /// <summary>The AppMutex of installer/Krupno.iss.</summary>
    public const string DefaultName = "Krupno-Running";

    private static readonly Lock Gate = new();
    private static Mutex? _held;

    /// <summary>The unit tests set a name of their own, so a Krupno that is open doesn't count.</summary>
    internal static string Name { get; set; } = DefaultName;

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
                if (!Mutex.TryOpenExisting(Name, out var other))
                    return false;
                other.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Created by an elevated Krupno, which this one may not open: it is there all the same.
                return true;
            }
            finally
            {
                if (held)
                    _held = Open();
            }
        }
    }

    /// <summary>The mutex, created or opened; null if an elevated Krupno created it and this one may not open it (it exists anyway).</summary>
    private static Mutex? Open()
    {
        try
        {
            return new Mutex(initiallyOwned: false, Name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            return null;
        }
    }
}
