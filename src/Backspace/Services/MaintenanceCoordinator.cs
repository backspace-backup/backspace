using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>Lifetime reader/writer gate for repository operations. Readers keep a
/// lease until their daemon work has stopped. An exclusive file handle also gates
/// other Backspace processes using this portable configuration directory.</summary>
public sealed class MaintenanceCoordinator
{
    public static readonly TimeSpan DefaultCancelGracePeriod = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Action?> _readers = new();
    // Strong roots prevent finalizers releasing an uncertain daemon's OS gate
    // if a coordinator is replaced. Successful engine shutdown releases these.
    private static readonly HashSet<MaintenanceCoordinator> QuarantinedOwners = new();
    private FileStream? _quarantinedHandle;
    private bool _engineStopped;
    private bool _active;
    private bool _shutdownRequested;
    private string? _unsafeReason;
    public event Action<bool, string>? MaintenanceStateChanged;
    public bool IsActive { get { lock (_sync) return _active; } }
    public bool HasReaders { get { lock (_sync) return _readers.Count > 0; } }
    public bool IsBlocked { get { lock (_sync) return _unsafeReason is not null; } }
    public bool IsShuttingDown { get { lock (_sync) return _shutdownRequested; } }
    public string CurrentReason { get; private set; } = "";

    /// <summary>Stops admission in this process only. Existing operations retain
    /// their leases until they stop; other scheduler/helper processes are unaffected.
    /// Keep the lease through committed exit, or dispose it to cancel shutdown.</summary>
    public bool TryBeginShutdown(out IDisposable? lease)
    {
        lock (_sync)
        {
            if (_shutdownRequested) { lease = null; return false; }
            _shutdownRequested = true;
            lease = new Lease(() => { lock (_sync) _shutdownRequested = false; });
            return true;
        }
    }

    private void ThrowIfShuttingDown()
    {
        if (_shutdownRequested)
            throw new InvalidOperationException("Backspace is closing. New work cannot start until quitting is cancelled.");
    }

    /// <summary>Compatibility preflight. Actual work must hold AcquireReadAsync.</summary>
    public async Task WaitForReadAsync(CancellationToken ct)
    {
        using var lease = await AcquireReadAsync(ct).ConfigureAwait(false);
    }

    public async Task<IDisposable> AcquireReadAsync(CancellationToken ct, Action? cancel = null)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ThrowIfShuttingDown();
                if (_unsafeReason is not null) throw new InvalidOperationException(_unsafeReason);
                if (!_active)
                {
                    var handle = TryOpenGate(exclusive: false);
                    if (handle is not null)
                    {
                        var id = Guid.NewGuid();
                        _readers.Add(id, cancel);
                        return new Lease(() => { lock (_sync) { ReleaseGate(handle); _readers.Remove(id); } });
                    }
                }
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    public void CancelReaders()
    {
        Action?[] cancellations;
        lock (_sync) cancellations = _readers.Values.ToArray();
        foreach (var cancel in cancellations) { try { cancel?.Invoke(); } catch { } }
    }

    /// <summary>Fail closed when daemon ownership is uncertain. A restart reaps
    /// the owned sidecar before operations can resume.</summary>
    public void BlockUntilRestart(string reason)
    {
        lock (_sync)
        {
            _unsafeReason = reason;
            // A reader can be logically finished while its daemon outcome is
            // unknown. Keep one real OS lease to block other processes' writers.
            if (_quarantinedHandle is null && !_engineStopped)
            {
                var guard = TryOpenGate(exclusive: false);
                if (guard is not null) ReleaseGate(guard);
                // An existing exclusive owner cannot open a shared guard; its
                // own file handle is retained when its logical lease is released.
            }
        }
    }

    /// <summary>Call only after the owned engine has successfully stopped.
    /// Does not clear the blocked state or admit more work in this process.</summary>
    public void ReleaseEngineOwnershipAfterStop()
    {
        lock (_sync)
        {
            _engineStopped = true;
            _quarantinedHandle?.Dispose();
            _quarantinedHandle = null;
            lock (QuarantinedOwners) QuarantinedOwners.Remove(this);
        }
    }

    private void ReleaseGate(FileStream handle)
    {
        if (_unsafeReason is not null && !_engineStopped && _quarantinedHandle is null)
        {
            _quarantinedHandle = handle;
            lock (QuarantinedOwners) QuarantinedOwners.Add(this);
        }
        else handle.Dispose();
    }

    public async Task<IDisposable> AcquireAsync(string reason, IProgress<string>? progress = null,
        TimeSpan? cancelGracePeriod = null, CancellationToken ct = default)
    {
        lock (_sync) ThrowIfShuttingDown();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ThrowIfShuttingDown();
                if (_unsafeReason is not null) throw new InvalidOperationException(_unsafeReason);
                _active = true;
                CurrentReason = reason;
            }
            Notify(true, reason);
            progress?.Report("Waiting for active backups and restores to stop…");
            var orch = ServiceLocator.Orchestrator;
            foreach (var id in orch.SnapshotRunningBackupIds())
                orch.CancelRun(id, $"Stopped to make way for: {reason}");
            // A held reader is real ownership, not a UI running flag. Never
            // force-finalise a flag and pretend an active daemon has stopped.
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (_unsafeReason is not null) throw new InvalidOperationException(_unsafeReason);
                    if (_readers.Count == 0 && orch.SnapshotRunningBackupIds().Count == 0)
                    {
                        var handle = TryOpenGate(exclusive: true);
                        if (handle is not null)
                            return new Lease(() => { lock (_sync) ReleaseGate(handle); Release(reason); });
                    }
                }
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        catch { Release(reason); throw; }
    }

    private static FileStream? TryOpenGate(bool exclusive)
    {
        Directory.CreateDirectory(AppPaths.ConfigRoot);
        var path = Path.Combine(AppPaths.ConfigRoot, "operations.lock");
        try
        {
            if (!File.Exists(path))
            {
                using var create = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            return new FileStream(path, FileMode.Open, exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                exclusive ? FileShare.None : FileShare.Read);
        }
        catch (IOException) { return null; }
    }

    private void Release(string reason)
    {
        lock (_sync) { _active = false; CurrentReason = ""; }
        _writeLock.Release();
        Notify(false, reason);
    }
    private void Notify(bool active, string reason)
    {
        try { MaintenanceStateChanged?.Invoke(active, reason); }
        catch (Exception ex) { AppLogger.For<MaintenanceCoordinator>().Warning(ex, "Maintenance state handler threw"); }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) release(); }
    }
}
