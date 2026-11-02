using System;
using System.Collections.Generic;
using System.Linq;
using Backspace.Models;
using Backspace.Services.Platform;

namespace Backspace.Services;

public enum ScheduleReadiness { Ready, Off, Paused, Attention, Unknown }
public sealed record ScheduleCheck(ScheduleReadiness State, string Detail);
public sealed record TrayScheduleStatus(ScheduleReadiness State, string Title, string Detail)
{
    public static TrayScheduleStatus Inspect(IEnumerable<Backup> backups, IScheduler scheduler,
        string exePath, DateTime nowUtc)
    {
        var all = backups.ToArray();
        var enabled = all.Where(b => b.Enabled).ToArray();
        if (enabled.Length == 0) return new(ScheduleReadiness.Off, "Schedules off",
            all.Length == 0 ? "No backups are configured." : "All backups are disabled.");
        var active = enabled.Where(b => b.Schedule.PausedUntilUtc is not { } until || until <= nowUtc).ToArray();
        var paused = enabled.Length - active.Length;
        if (active.Length == 0) return new(ScheduleReadiness.Paused, "Schedules paused", "All enabled backups are snoozed.");
        var results = active.Select(b => (Backup: b, Check: Check(b))).ToArray();
        var problem = results.FirstOrDefault(r => r.Check.State == ScheduleReadiness.Attention);
        if (problem.Check is not null) return new(ScheduleReadiness.Attention, "Schedules need attention",
            problem.Backup.Name + ": " + problem.Check.Detail);
        if (results.Any(r => r.Check.State != ScheduleReadiness.Ready)) return new(ScheduleReadiness.Unknown,
            "Schedules not verified", "Backspace could not confirm all schedules. Open Backspace to check your backups.");
        // A ready schedule is not a protected backup: the check mark means the
        // last runs worked (U084, 2 November 2026).
        if (active.FirstOrDefault(b => b.LastRunStatus == BackupRunStatus.Failed) is { } failed)
            return new(ScheduleReadiness.Attention, "Last backup failed",
                failed.Name + ": " + (string.IsNullOrWhiteSpace(failed.LastRunSummary) ? "the last run failed." : failed.LastRunSummary!.Trim()) + " Open Backspace for details.");
        var waiting = active.Where(b => b.LastSuccessUtc is null && b.LastRunStatus is BackupRunStatus.NeverRun or BackupRunStatus.Skipped).ToArray();
        if (waiting.Length > 0) return new(ScheduleReadiness.Unknown, "Not backed up yet",
            waiting.Length == 1 ? $"{waiting[0].Name} has not completed a backup yet." : $"{waiting.Length} backups have not completed a backup yet.");
        return new(ScheduleReadiness.Ready, paused > 0 ? $"{active.Length} ready · {paused} paused" : "Schedules ready",
            $"Checked at {nowUtc.ToLocalTime():HH:mm}. Drives and sign-in are checked when backups run.");

        ScheduleCheck Check(Backup b)
        {
            if (b.SourcePaths.Count == 0 || b.Targets.Count == 0)
                return new(ScheduleReadiness.Attention, "Choose at least one source and destination.");
            try { return scheduler.InspectBackup(b, exePath); }
            catch { return new(ScheduleReadiness.Unknown, "The operating system could not be queried."); }
        }
    }
}
