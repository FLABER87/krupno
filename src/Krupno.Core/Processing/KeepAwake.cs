using System.Runtime.InteropServices;

namespace Krupno.Core.Processing;

/// <summary>
/// Keeps Windows from going to sleep on idle while a run is in progress: a batch on a heavy model works for many minutes
/// without any input, and the power plan would put a laptop to sleep halfway through. The display may still turn off,
/// and closing the lid or choosing Sleep works as usual. The request belongs to the thread that made it, so it is
/// disposed of on the same thread (the window's UI thread, the command-line tool's main thread).
/// </summary>
public sealed partial class KeepAwake : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private bool _disposed;

    public KeepAwake() => SetThreadExecutionState(EsContinuous | EsSystemRequired);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // ES_CONTINUOUS alone clears the request.
        SetThreadExecutionState(EsContinuous);
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
}
