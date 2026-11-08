using System;
using System.Collections.Generic;
using System.Linq;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Whether a backup has stored anything to restore, mount or test (U079,
/// U157). The settings' <see cref="Backup.LastSuccessUtc"/> only records runs
/// made by this app: a backup run by the background service is in the run
/// history instead, so Restore files said "hasn't completed a successful run
/// yet" right after a successful background backup (7 November 2026). Every
/// gate asks here, with the history merged from both.
/// </summary>
public static class RestorableRuns
{
    /// <summary>A run stored something when it succeeded (or with warnings),
    /// or when at least one of its source → destination steps did.</summary>
    public static bool StoredSomething(RunRecord run)
        => run.Kind == RunKind.Backup && run.EndedUtc is not null
        && (run.Status is BackupRunStatus.Success or BackupRunStatus.Warning
            || run.Cells.Any(c => c.Status is BackupRunStatus.Success or BackupRunStatus.Warning));

    /// <summary>What the settings alone say: a recorded success, or a last
    /// run that ended successfully.</summary>
    public static bool InSettings(Backup backup)
        => backup.LastSuccessUtc is not null
        || backup.LastRunEndUtc is not null && backup.LastRunStatus is BackupRunStatus.Success or BackupRunStatus.Warning;

    public static bool Any(Backup backup, IEnumerable<RunRecord> history)
        => InSettings(backup) || history.Any(r => r.BackupId == backup.Id && StoredSomething(r));

    /// <summary>Reads the merged history (this app's and the background
    /// service's) when the settings alone don't show a success.</summary>
    public static bool Any(Backup backup, LogStore logs)
    {
        if (InSettings(backup)) return true;
        try { return Any(backup, logs.LoadHistoryByBackupId(backup.Id)); }
        catch (Exception ex)
        {
            AppLogger.For(nameof(RestorableRuns)).Warning(ex, "Run history unavailable for {Backup}", backup.Name);
            // Unknown is not "nothing stored": let the snapshot listing decide.
            return true;
        }
    }
}
