using System;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Run now and Save and run from the desktop. With administrator access on,
/// the background service runs the backup, so files in use are included and
/// Windows shows no prompt; the card follows that run through the service's
/// activity. Otherwise this process runs it, without VSS unless Backspace
/// was started as administrator (U026, 29 October 2026).
/// </summary>
internal static class DesktopBackupRun
{
    /// <summary>The run's record, or null when the service ran it and its
    /// result arrives through the background activity instead.</summary>
    /// <param name="queued">Hears "Queued" reasons while the service can't
    /// start the run yet, then "" (U156).</param>
    /// <param name="waiveConditions">"Run now anyway" (U226): its conditions
    /// neither hold it back nor stop it.</param>
    internal static async Task<RunRecord?> RunAsync(Backup backup, CancellationToken ct, Action<string>? queued = null, bool waiveConditions = false)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            try
            {
                string? notStarted = null;
                if (await Platform.Windows.ServiceCommandLineRun.TryRunFromDesktopAsync(
                        AppContext.BaseDirectory.TrimEnd('\\'), backup, ct, reason => notStarted = reason, queued, waiveConditions).ConfigureAwait(false))
                    return notStarted is null ? null : new RunRecord
                    {
                        BackupId = backup.Id, BackupName = backup.Name, Kind = RunKind.Backup,
                        StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow, Status = BackupRunStatus.Skipped,
                        Summary = notStarted,
                    };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // The start may have reached the service, so never retry here.
                AppLogger.For(nameof(DesktopBackupRun)).Warning(ex, "The background service could not run {Backup}", backup.Name);
                return new RunRecord
                {
                    BackupId = backup.Id, BackupName = backup.Name, Kind = RunKind.Backup,
                    StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow, Status = BackupRunStatus.Failed,
                    Summary = "The background service could not run this backup: " + ex.Message,
                };
            }
        }
#endif
        BackupOrchestrator.ConditionsWaived.Value = waiveConditions;
        try { return await ServiceLocator.Orchestrator.RunBackupAsync(backup, ct).ConfigureAwait(false); }
        finally { BackupOrchestrator.ConditionsWaived.Value = false; }
    }
}
