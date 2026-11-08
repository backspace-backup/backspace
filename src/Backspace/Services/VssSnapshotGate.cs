using System;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>
/// One Windows snapshot (VSS) being made at a time, across every Backspace
/// process (U156). Windows refuses a second snapshot set while one is being
/// made (VSS_E_SNAPSHOT_SET_IN_PROGRESS); restic then retries every five
/// seconds until its VSS timeout and, past it, backs up without a snapshot,
/// so open files could be missed. With two backups at once, two background
/// workers start together. So a backup that uses VSS takes this gate before
/// its engine starts and keeps it until restic has made its snapshot (it
/// reads files: <see cref="BackupLiveProgress.Reading"/>), or until the
/// engine holds the backup back, or after <see cref="Hold"/> at the latest.
/// Restic's own retry, given <see cref="ResticTimeout"/>, covers what the
/// gate can't (another program making a snapshot, a backup the engine
/// starts later).
/// </summary>
internal sealed class VssSnapshotGate : IDisposable
{
    /// <summary>How long restic keeps retrying while another snapshot is made.</summary>
    internal const string ResticTimeout = "30m";

    /// <summary>The gate is let go after this, whatever happens.</summary>
    internal static TimeSpan Hold = TimeSpan.FromMinutes(5);

    internal const string Name = @"Local\Backspace-VssSnapshot";

    private CrossProcessGate? _gate;
    private Timer? _limit;

    private VssSnapshotGate(CrossProcessGate gate)
    {
        _gate = gate;
        _limit = new Timer(_ => Dispose(), null, Hold, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Waits for the gate; <paramref name="waiting"/> hears a reason
    /// once while it waits, then "" when it has it.</summary>
    internal static async Task<VssSnapshotGate> AcquireAsync(string holder, Action<string>? waiting, CancellationToken ct, string name = Name)
    {
        var gate = new CrossProcessGate(name, holder);
        var told = false;
        try
        {
            while (!gate.TryAcquire())
            {
                if (!told) { told = true; waiting?.Invoke(RepositoryAccess.SnapshotReason); }
                await Task.Delay(RepositoryAccess.PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch { gate.Dispose(); throw; }
        if (told) waiting?.Invoke("");
        return new VssSnapshotGate(gate);
    }

    internal bool Held => Volatile.Read(ref _gate) is not null;

    public void Dispose()
    {
        Interlocked.Exchange(ref _limit, null)?.Dispose();
        Interlocked.Exchange(ref _gate, null)?.Dispose();
    }
}
