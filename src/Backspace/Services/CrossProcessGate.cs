using System;
using System.IO;
using System.Threading;

namespace Backspace.Services;

/// <summary>A non-thread-affine gate. With the background service set up, it
/// and the desktop share file locks (SystemServiceLocks); ordinary portable
/// copies retain their existing session semaphore names.</summary>
/// <param name="holder">Who takes the gate, in words (a backup's name). It
/// is noted beside the gate while held, so a backup waiting for it can say
/// what it waits for (U156). Display only: never a reason to trust a gate.</param>
internal sealed class CrossProcessGate(string name, string? holder = null) : IDisposable
{
    private Semaphore? _semaphore;
    private FileStream? _file;
    private IDisposable? _mutex;
    private bool _held;

    internal string Name => name;

    internal bool TryAcquire()
    {
        if (_held) throw new InvalidOperationException("This operation already holds its gate.");
#if WINDOWS
        if (Platform.Windows.SystemServiceLocks.Directory() is { } shared)
        {
            _file = Platform.Windows.SystemServiceLocks.TryOpen(shared, Platform.Windows.SystemServiceLocks.Name(name), exclusive: true);
            _held = _file is not null;
            if (_held) NoteHolder();
            return _held;
        }
#endif
        // Linux and macOS have no named semaphores (U235: every backup there
        // failed with "could not lock this backup against duplicate runs"):
        // the system's named mutex instead, held by a thread of its own so
        // any thread can release the gate, and freed when the process ends.
        if (!OperatingSystem.IsWindows())
        {
            _mutex = RuntimeGate.TryHold(@"Global\Backspace-gate-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..32]);
            _held = _mutex is not null;
            if (_held) NoteHolder();
            return _held;
        }
        _semaphore ??= new Semaphore(1, 1, name);
        _held = _semaphore.WaitOne(0);
        if (_held) NoteHolder();
        return _held;
    }

    internal void Release()
    {
        if (!_held) return;
        if (holder is not null) Holders.Clear(name, holder);
        if (_mutex is not null) { _mutex.Dispose(); _mutex = null; }
        else if (_file is not null) { _file.Dispose(); _file = null; }
        else _semaphore!.Release();
        _held = false;
    }

    private void NoteHolder() { if (holder is not null) Holders.Write(name, holder); }

    public void Dispose() { Release(); _semaphore?.Dispose(); _semaphore = null; }

    /// <summary>Who holds a gate, kept in small files beside the locks.
    /// Best effort: a missing or unreadable note only makes the waiting
    /// message less specific.</summary>
    internal static class Holders
    {
        internal static string Directory()
        {
#if WINDOWS
            if (Platform.Windows.SystemServiceLocks.Directory() is { } shared) return Path.Combine(shared, "holders");
#endif
            return Path.Combine(EngineWorkPaths.MachineRoot, "locks", "holders");
        }

        private static string PathFor(string gate) => Path.Combine(Directory(), Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(gate)))[..32] + ".holder");

        internal static void Write(string gate, string holder)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory());
                File.WriteAllText(PathFor(gate), holder.Length > 512 ? holder[..512] : holder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        internal static string? Read(string gate)
        {
            try
            {
                var path = PathFor(gate);
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path).Trim();
                return text.Length == 0 ? null : text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>Removes the note if it is still ours.</summary>
        internal static void Clear(string gate, string holder)
        {
            try
            {
                var path = PathFor(gate);
                if (File.Exists(path) && File.ReadAllText(path).Trim() == holder.Trim()) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
