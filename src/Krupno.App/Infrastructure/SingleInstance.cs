using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Krupno.App.Infrastructure;

/// <summary>
/// One window per session. Explorer starts a Krupno.exe for every file of a selection ("Upscale with Krupno", "Open with"), and
/// every one after the first hands its paths over a named pipe to the window already open and exits, so the whole selection
/// lands in one queue. Starting Krupno.exe again with no files brings that window forward. <see cref="NewWindowSwitch"/> opens
/// a window of its own anyway.
/// </summary>
internal static class SingleInstance
{
    /// <summary>A window of its own, with no hand-over: two windows on two GPUs, or the UI test scripts.</summary>
    public const string NewWindowSwitch = "--new-window";

    /// <summary>The window's answer once it has the files.</summary>
    private const byte Received = 1;

    /// <summary>The first window may still be starting when the next process tries to reach it.</summary>
    private const int ConnectTimeoutMs = 5000;

    /// <summary>Per user and session: another user's window, or the same user's in another session, isn't this one's to take.</summary>
    private static readonly string Name = $"Krupno-{Environment.UserName}-{Process.GetCurrentProcess().SessionId}";

    private static readonly Lock Gate = new();
    private static readonly List<(string[] Paths, DateTime ArrivedAt)> Pending = [];
    private static Action<string[], DateTime>? _handler;
    /// <summary>Held for as long as this window is the one that takes the paths.</summary>
    private static Mutex? _mutex;

    /// <summary>When this process started: its own command-line files and the ones handed over right after are one selection.</summary>
    public static DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>
    /// True if a window that is already open took <paramref name="args"/>, and this process should exit. Otherwise this process
    /// becomes the window that takes the paths of the next ones. Runs before WPF starts, so a handed-over file costs little.
    /// </summary>
    public static bool TryHandOver(string[] args)
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + Name, out bool createdNew);
        if (createdNew)
        {
            _mutex = mutex;
            _ = Task.Run(ListenAsync);
            return false;
        }
        mutex.Dispose();

        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeoutMs);
            AllowForeground(pipe);
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true))
            {
                // The other window has its own current folder. An empty line ends the list.
                foreach (string arg in args.Where(a => a.Length > 0))
                    writer.WriteLine(FullPath(arg));
                writer.WriteLine();
            }
            // The window answers once it has the files; without the answer (it was closing, say) they would be lost.
            var answer = new byte[1];
            var read = pipe.ReadAsync(answer).AsTask();
            return read.Wait(ConnectTimeoutMs) && read.Result == 1 && answer[0] == Received;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or AggregateException)
        {
            // The window doesn't answer: open one of its own rather than lose the files.
            return false;
        }
    }

    /// <summary>
    /// From now on, paths handed over by other Krupno.exe go to <paramref name="handler"/> (on a background thread), the ones that
    /// came while the window was starting first.
    /// </summary>
    public static void Attach(Action<string[], DateTime> handler)
    {
        lock (Gate)
        {
            _handler = handler;
            foreach (var (paths, arrivedAt) in Pending)
                handler(paths, arrivedAt);
            Pending.Clear();
        }
    }

    /// <summary>On exit, on the thread that started the app: the next Krupno.exe opens a window of its own.</summary>
    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread: the process is exiting anyway, and that releases it.
        }
        _mutex?.Dispose();
        _mutex = null;
    }

    private static async Task ListenAsync()
    {
        NamedPipeServerStream? server = null;
        while (true)
        {
            try
            {
                server ??= CreateServer();
                await server.WaitForConnectionAsync();
            }
            catch (Exception ex)
            {
                server?.Dispose();
                server = null;
                Log.Warning($"Can't take files from other Krupno.exe: {ex.Message}");
                await Task.Delay(2000);
                continue;
            }
            // The next one listens before this one is read: Explorer starts the processes for a selection all at once, and the
            // ones waiting would be cut off if no instance were listening for a moment (on Unix the listening socket closes
            // with the last instance).
            var connected = server;
            server = null;
            try
            {
                server = CreateServer();
            }
            catch (Exception ex)
            {
                Log.Warning($"Can't take files from other Krupno.exe: {ex.Message}");
            }
            _ = ReceiveAsync(connected);
        }
    }

    private static NamedPipeServerStream CreateServer() =>
        new(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task ReceiveAsync(NamedPipeServerStream server)
    {
        await using (server)
        {
            try
            {
                var arrivedAt = DateTime.UtcNow;
                // A process that connects and then hangs is dropped after a while.
                using var timeout = new CancellationTokenSource(ConnectTimeoutMs);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var paths = new List<string>();
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                    paths.Add(line);
                Deliver([.. paths], arrivedAt);
                server.WriteByte(Received);
                await server.FlushAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                Log.Warning($"Files from another Krupno.exe weren't received: {ex.Message}");
            }
        }
    }

    private static void Deliver(string[] paths, DateTime arrivedAt)
    {
        lock (Gate)
        {
            if (_handler is null)
                Pending.Add((paths, arrivedAt));
            else
                _handler(paths, arrivedAt);
        }
    }

    /// <summary>
    /// This process was started by the user, so it may give the foreground to the window it hands over to; without that, Windows
    /// would only flash the window's taskbar button. Best effort: the files go over either way.
    /// </summary>
    private static void AllowForeground(NamedPipeClientStream pipe)
    {
        try
        {
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint windowProcess))
                AllowSetForegroundWindow(windowProcess);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
