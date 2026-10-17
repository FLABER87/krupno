using System.Globalization;
using System.IO;
using System.Text;

namespace Krupno.App.Infrastructure;

/// <summary>
/// A plain text log in %LOCALAPPDATA%\Krupno\logs, one file per session: the computer (<see cref="SystemInfo"/>), what each run
/// used and did, and every error with its stack trace, for bug reports (DirectML failures depend on the GPU and its driver).
/// The log stays on the computer. Logging never gets in the way of the work: if the file can't be written, entries are dropped.
/// </summary>
public static class Log
{
    /// <summary>The newest session logs that are kept; older ones are deleted when a session starts.</summary>
    private const int KeptFiles = 20;

    /// <summary>The newest logs of Setup run by "Update" (setup-0.3.0.log, see UpdateInstall) that are kept.</summary>
    private const int KeptSetupFiles = 5;

    private static readonly Lock Gate = new();
    private static StreamWriter? _writer;

    /// <summary>The log is in English whatever the window's language: it is read by whoever fixes the bug.</summary>
    public static CultureInfo Culture => CultureInfo.InvariantCulture;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Krupno", "logs");

    /// <summary>This session's log file; null before <see cref="Start"/> or if it couldn't be created.</summary>
    public static string? FilePath { get; private set; }

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var now = DateTime.Now;
            string path = Path.Combine(Folder,
                $"krupno-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Environment.ProcessId}.log");
            // Readable while the window is open; flushed on every entry, so a crash leaves everything up to it. Not deletable:
            // another window's DeleteOld must leave the log of a window that is still open alone.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            lock (Gate)
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            FilePath = path;
            var logs = new DirectoryInfo(Folder);
            // Newest first: a session's log by its name (the time it started), Setup's by when it was written.
            DeleteOld(logs.GetFiles("krupno-*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal), KeptFiles);
            DeleteOld(logs.GetFiles("setup-*.log").OrderByDescending(f => f.LastWriteTimeUtc), KeptSetupFiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        Info("Session started\n" + SystemInfo.Describe());
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warning(string message) => Write("WARN ", message);

    /// <summary>The message, then the exception with its type, inner exceptions and stack traces.</summary>
    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}\n{exception}");

    public static void Close()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (_writer is null)
                return;
            try
            {
                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                // Continuation lines are indented, so every entry starts with its time.
                _writer.WriteLine($"{time}  {level}  {message.ReplaceLineEndings("\n").Replace("\n", "\n" + new string(' ', 32))}");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // A full disk or a removed folder: the work goes on without the log.
            }
        }
    }

    private static void DeleteOld(IEnumerable<FileInfo> newestFirst, int kept)
    {
        foreach (var file in newestFirst.Skip(kept))
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open in another window or an editor: it goes next time.
            }
        }
    }
}
