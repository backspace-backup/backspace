using System;
using System.Collections.Generic;
using System.Linq;
using Backspace.Models;

namespace Backspace.Services;

public sealed record BackupCheckEntry(string RunId, DateTime When, BackupRunStatus Status,
    CheckDepth Depth, string Summary)
{
    public string Scope => BackupCheckHistory.Describe(Depth);
}

public sealed record BackupCheckState(string Label, string Tone, string Scope, string Details);

/// <summary>Uses saved observations, never infers old check depth from current settings.</summary>
public static class BackupCheckHistory
{
    public static string Describe(CheckDepth depth) => depth switch
    {
        CheckDepth.RepositoryStructure => "Repository structure · file contents not read",
        CheckDepth.SavedDataSample => "Repository structure + saved-data sample",
        CheckDepth.RestoreSample => "Sample restore + original-file comparison",
        CheckDepth.RestoreAllFiles => "Full restore + original-file comparison",
        CheckDepth.RestoreUnspecified => "Test restore · sample/full scope was not recorded",
        _ => "Repository check · depth was not recorded",
    };

    public static List<BackupCheckEntry> Entries(Backup backup, IEnumerable<RunRecord> history)
    {
        var entries = new List<BackupCheckEntry>();
        foreach (var run in history.Where(r => r.BackupId == backup.Id && r.EndedUtc is not null))
        {
            if (run.Checks.Count > 0)
            {
                foreach (var group in run.Checks.GroupBy(c => c.Depth))
                {
                    var checks = group.ToList();
                    var status = checks.Any(c => c.Status == BackupRunStatus.Failed) ? BackupRunStatus.Failed
                        : checks.All(c => c.Status == BackupRunStatus.Success) ? BackupRunStatus.Success : BackupRunStatus.Warning;
                    if (run.Kind == RunKind.Check && run.Status != BackupRunStatus.Success
                        && checks.Select(c => c.TargetKey).Distinct().Count() < backup.Targets.Select(t => t.DestinationId + "/" + t.StorageName).Distinct().Count())
                        status = run.Status;
                    if (run.Kind == RunKind.Backup)
                    {
                        var observed = checks.Select(c => c.TargetKey).ToHashSet(StringComparer.Ordinal);
                        if (backup.Targets.Any(t => !observed.Contains(t.DestinationId + "/" + t.StorageName)) && status == BackupRunStatus.Success)
                            status = BackupRunStatus.Warning;
                    }
                    var detail = string.Join("\n", checks.Select(c => string.IsNullOrEmpty(c.DestinationName) ? c.Summary : c.DestinationName + ": " + c.Summary));
                    if (status == BackupRunStatus.Warning) detail += "\nThis check did not establish success for every configured destination.";
                    entries.Add(new(run.Id, checks.Max(c => c.EndedUtc), status, group.Key, detail));
                }
            }
            else if (run.Kind is RunKind.Check or RunKind.TestRestore)
            {
                // Old restore records did not preserve sample/full scope.
                entries.Add(new(run.Id, run.EndedUtc!.Value, run.Status,
                    run.Kind == RunKind.TestRestore ? CheckDepth.RestoreUnspecified : CheckDepth.Unknown,
                    run.Summary + (run.Kind == RunKind.TestRestore ? "\nHistorical record: the sample/full setting was not saved." : "")));
            }
        }
        if (backup.LastVerifyUtc is { } when && backup.LastVerifyPass is { } passed
            && !entries.Any(e => e.Depth is CheckDepth.RestoreSample or CheckDepth.RestoreAllFiles or CheckDepth.RestoreUnspecified && Math.Abs((e.When - when).TotalSeconds) < 2))
            entries.Add(new("", when, passed ? BackupRunStatus.Success : BackupRunStatus.Failed,
                backup.LastVerifyWasSample ? CheckDepth.RestoreSample : CheckDepth.RestoreAllFiles,
                backup.LastVerifySummary ?? "Saved test restore result; its detailed log is no longer available."));
        return entries.OrderByDescending(e => e.When).ToList();
    }

    public static BackupCheckState State(Backup backup, IReadOnlyList<BackupCheckEntry> entries, DateTime nowUtc)
    {
        if (entries.Count == 0) return new("No checks recorded", "never", "", "The first scheduled check or Test restore will appear here.");
        var latest = entries[0];
        // A shallower success does not erase a deeper failure. A full restore
        // can supersede a sample restore, but neither checks every old pack.
        var unresolved = entries.Where(e => e.Status is BackupRunStatus.Success or BackupRunStatus.Failed)
            .GroupBy(e => e.Depth).Select(g => g.First())
            .FirstOrDefault(e => e.Status == BackupRunStatus.Failed
                && !(e.Depth is CheckDepth.RestoreSample or CheckDepth.RestoreUnspecified && entries.Any(x => x.Depth == CheckDepth.RestoreAllFiles && x.Status == BackupRunStatus.Success && x.When > e.When)));
        var age = nowUtc - latest.When;
        var ago = age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago" : $"{(int)age.TotalDays}d ago";
        if (unresolved is not null)
            return new($"Check needs attention · {ago}", "fail", latest.Scope,
                $"Latest check: {latest.Summary}\n\nUnresolved failed check: {unresolved.Scope}\n{unresolved.Summary}");
        if (latest.Status != BackupRunStatus.Success)
            return new($"Check incomplete · {ago}", "stale", latest.Scope, latest.Summary);
        var repository = entries.FirstOrDefault(e => e.Depth is CheckDepth.RepositoryStructure or CheckDepth.SavedDataSample or CheckDepth.Unknown);
        var due = backup.Enabled && backup.CheckSchedule is { } schedule
            ? schedule.ComputeNextFire((repository?.When ?? backup.CreatedUtc).ToLocalTime())?.ToUniversalTime() : null;
        var afterBackupMissing = backup.CheckSchedule is null && backup.LastRunStartUtc is { } lastBackup
            && (repository is null || repository.When < lastBackup);
        if (due is { } expected && nowUtc > expected || afterBackupMissing)
            return new($"Check overdue · {ago}", "stale", latest.Scope,
                latest.Summary + "\nA repository check is due. A manual restore check does not replace the scheduled repository check.");
        var reassurance = latest.Depth switch
        {
            CheckDepth.RepositoryStructure => "Backup records intact",
            CheckDepth.SavedDataSample => "Saved data sample intact",
            CheckDepth.RestoreSample => "Sample recovery tested",
            CheckDepth.RestoreAllFiles => "Full recovery tested",
            CheckDepth.RestoreUnspecified => "Recovery tested",
            _ => "Recorded check passed",
        };
        return new($"{reassurance} · {ago}", "ok", latest.Scope, latest.Scope + "\n" + latest.Summary);
    }
}
