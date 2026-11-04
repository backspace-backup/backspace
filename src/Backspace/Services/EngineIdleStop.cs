using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>
/// Stops the backup engine (Backrest, and with it any restic or rclone it
/// started) once nothing has used it for a while (U125, 4 November 2026: "make
/// sure that restic and backrest and rclone processes are no longer running
/// once all backups are done"). The open app used to keep it running for as
/// long as it was open. Any later engine call starts it again, which takes a
/// few seconds. Background-service runs already stop it when each run ends.
/// </summary>
internal static class EngineIdleStop
{
    internal static TimeSpan IdleAfter = TimeSpan.FromMinutes(5);
    private static Timer? _timer;
    private static int _checking;

    internal static void Start()
    {
        _timer ??= new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>True when nothing needs the engine: no run, restore, test
    /// restore or mounted backup, and no call for <see cref="IdleAfter"/>.</summary>
    internal static bool Idle(DateTime lastUseUtc, DateTime nowUtc, bool runs, bool readers, bool mounts)
        => !runs && !readers && !mounts && nowUtc - lastUseUtc >= IdleAfter;

    internal static async Task<bool> CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return false;
        try
        {
            if (ServiceLocator.Engine is not ResticBackrestEngine engine || engine.Sidecar is not { IsRunning: true } sidecar) return false;
            bool Now() => Idle(engine.LastUseUtc, DateTime.UtcNow, ServiceLocator.Orchestrator.AnyRunning, ServiceLocator.Maintenance.HasReaders,
                ServiceLocator.SnapshotMounts is { } mounts && ServiceLocator.Config.Current.Backups.Any(b => mounts.GetMountedDrive(b.Id) is not null));
            if (!Now()) return false;
            var stopped = await sidecar.StopIfIdleAsync(Now).ConfigureAwait(false);
            if (stopped) AppLogger.For("EngineIdleStop").Information("Stopped the backup engine: unused for {Minutes} minutes", IdleAfter.TotalMinutes);
            return stopped;
        }
        catch (Exception ex)
        {
            AppLogger.For("EngineIdleStop").Warning(ex, "Could not stop the idle backup engine");
            return false;
        }
        finally { Volatile.Write(ref _checking, 0); }
    }
}
