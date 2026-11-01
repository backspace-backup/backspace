using System;
using System.IO;
using System.Threading;

namespace Backspace.Services;

/// <summary>A non-thread-affine gate. Activated Windows services share protected
/// file locks with the desktop; ordinary portable copies retain their existing
/// session semaphore names.</summary>
internal sealed class CrossProcessGate(string name) : IDisposable
{
    private Semaphore? _semaphore;
    private FileStream? _file;
    private bool _held;

    internal bool TryAcquire()
    {
        if (_held) throw new InvalidOperationException("This operation already holds its gate.");
#if WINDOWS
        var context = Platform.Windows.ServiceCoordinationFiles.Current();
        if (context is not null)
        {
            _file = Platform.Windows.ServiceCoordinationFiles.TryOpen(context,
                Platform.Windows.ServiceCoordinationFiles.Name(name), exclusive: true);
            return _held = _file is not null;
        }
#endif
        _semaphore ??= new Semaphore(1, 1, name);
        return _held = _semaphore.WaitOne(0);
    }

    internal void Release()
    {
        if (!_held) return;
        if (_file is not null) { _file.Dispose(); _file = null; }
        else _semaphore!.Release();
        _held = false;
    }

    public void Dispose() { Release(); _semaphore?.Dispose(); _semaphore = null; }
}
