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

/// <param name="Kinds">One line per check of the backup, with its latest
/// result: "Data sample · passed 2d ago" (U179).</param>
public sealed record BackupCheckState(string Label, string Tone, string Scope, string Details)
{
    public IReadOnlyList<string> Kinds { get; init; } = [];
}

/// <summary>Uses saved observations, never infers old check depth from current settings.</summary>
public static class BackupCheckHistory
{
    public static string Describe(CheckDepth depth) => depth switch
    {
        CheckDepth.RepositoryStructure => "Structure check · stored data not read",
        CheckDepth.SavedDataSample => "Data sample · part of the stored data read and checked",
        CheckDepth.AllData => "Full data read · all stored data read and checked",
        CheckDepth.RestoreSample => "Sample test restore · files rebuilt from the latest backup",
        CheckDepth.RestoreAllFiles => "Full test restore · every file rebuilt from the latest backup",
        CheckDepth.RestoreUnspecified => "Test restore · sample/full scope was not recorded",
        _ => "Repository check · depth was not recorded",
    };

    /// <summary>The recorded results that answer for a check of this kind.
    /// A data sample also checks the structure; an old check of unknown
    /// depth counts as a structure check.</summary>
    internal static bool Answers(CheckKind kind, CheckDepth depth) => kind switch
    {
        CheckKind.Structure => depth is CheckDepth.RepositoryStructure or CheckDepth.SavedDataSample or CheckDepth.AllData or CheckDepth.Unknown,
        CheckKind.DataSample => depth is CheckDepth.SavedDataSample or CheckDepth.AllData,
        CheckKind.FullDataRead => depth is CheckDepth.AllData,
        _ => depth is CheckDepth.RestoreSample or CheckDepth.RestoreAllFiles or CheckDepth.RestoreUnspecified,
    };

    private static CheckDepth DepthOf(CheckKind kind) => kind switch
    {
        CheckKind.Structure => CheckDepth.RepositoryStructure,
        CheckKind.DataSample => CheckDepth.SavedDataSample,
        CheckKind.FullDataRead => CheckDepth.AllData,
        _ => CheckDepth.RestoreSample,
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
            // A sample test restore entry (U179): its test restore saved its
            // own record with the result. Only a run that could not start the
            // test restore stands for it here.
            else if (run.Kind == RunKind.Check && run.CheckKind is { } kind)
            {
                if (kind == CheckKind.TestRestore && run.Cells.Any(c => !c.DestinationUnavailable && c.Status is BackupRunStatus.Success)) continue;
                if (run.Cells.Count > 0 && run.Cells.All(c => c.DestinationUnavailable)) continue;
                entries.Add(new(run.Id, run.EndedUtc!.Value, run.Status, DepthOf(kind), run.Summary));
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

    private static string Ago(DateTime nowUtc, DateTime when)
    {
        var age = nowUtc - when;
        return age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago" : $"{(int)age.TotalDays}d ago";
    }

    /// <summary>When this check was last due and has not run since, or null:
    /// a scheduled check past its next time after its latest result, or one
    /// after each backup older than the latest backup.</summary>
    private static bool Overdue(Backup backup, BackupCheck check, BackupCheckEntry? latest, DateTime nowUtc)
    {
        // A backup with automatic backups off runs no scheduled checks either.
        if (check.Schedule is { } schedule)
            return backup.Enabled && schedule.ComputeNextFire((latest?.When ?? backup.CreatedUtc).ToLocalTime())?.ToUniversalTime() is { } due && nowUtc > due;
        return backup.LastRunStartUtc is { } lastBackup && (latest is null || latest.When < lastBackup)
            && backup.LastRunStatus is BackupRunStatus.Success or BackupRunStatus.Warning;
    }

    /// <summary>One line per check of the backup: its latest result (U179).</summary>
    public static IReadOnlyList<string> KindLines(Backup backup, IReadOnlyList<BackupCheckEntry> entries, DateTime nowUtc)
        => BackupCheck.InRunOrder(backup.Checks).Select(check =>
        {
            var latest = entries.FirstOrDefault(e => Answers(check.Kind, e.Depth));
            var name = BackupCheck.Name(check.Kind);
            if (latest is null) return $"{name} · not run yet";
            var result = latest.Status switch
            {
                BackupRunStatus.Success => "passed",
                BackupRunStatus.Failed => "failed",
                BackupRunStatus.Warning => "incomplete",
                _ => "not done",
            };
            return $"{name} · {result} {Ago(nowUtc, latest.When)}" + (Overdue(backup, check, latest, nowUtc) ? " · due again" : "");
        }).Distinct().ToList();

    public static BackupCheckState State(Backup backup, IReadOnlyList<BackupCheckEntry> entries, DateTime nowUtc)
    {
        var kinds = KindLines(backup, entries, nowUtc);
        if (entries.Count == 0)
            return backup.Checks.Count == 0
                ? new("No checks set up", "never", "", "Add checks in this backup's settings, under Checks.") { Kinds = kinds }
                : new("No checks recorded", "never", "", "The first scheduled check or Test restore will appear here.") { Kinds = kinds };
        var latest = entries[0];
        // A shallower success does not erase a deeper failure. A full restore
        // can supersede a sample restore, but neither checks every old pack.
        var unresolved = entries.Where(e => e.Status is BackupRunStatus.Success or BackupRunStatus.Failed)
            .GroupBy(e => e.Depth).Select(g => g.First())
            .FirstOrDefault(e => e.Status == BackupRunStatus.Failed
                && !(e.Depth is CheckDepth.RestoreSample or CheckDepth.RestoreUnspecified && entries.Any(x => x.Depth == CheckDepth.RestoreAllFiles && x.Status == BackupRunStatus.Success && x.When > e.When)));
        var ago = Ago(nowUtc, latest.When);
        if (unresolved is not null)
            return new($"{DepthName(unresolved.Depth)} failed · {Ago(nowUtc, unresolved.When)}", "fail", unresolved.Scope,
                Hover($"Unresolved failed check: {unresolved.Scope}\n{unresolved.Summary}\n\nLatest check: {latest.Summary}", kinds)) { Kinds = kinds };
        if (latest.Status != BackupRunStatus.Success)
            return new($"{DepthName(latest.Depth)} incomplete · {ago}", "stale", latest.Scope, Hover(latest.Summary, kinds)) { Kinds = kinds };
        {
            var overdue = BackupCheck.InRunOrder(backup.Checks)
                .FirstOrDefault(c => Overdue(backup, c, entries.FirstOrDefault(e => Answers(c.Kind, e.Depth)), nowUtc));
            if (overdue is not null)
                return new($"{BackupCheck.Name(overdue.Kind)} overdue · {ago}", "stale", latest.Scope,
                    Hover(latest.Summary + $"\nThe {BackupCheck.Name(overdue.Kind).ToLowerInvariant()} is due. Another kind of check does not replace it.", kinds)) { Kinds = kinds };
        }
        return new($"{DepthName(latest.Depth)} passed · {ago}", "ok", latest.Scope, Hover(latest.Scope + "\n" + latest.Summary, kinds)) { Kinds = kinds };
    }

    /// <summary>A recorded check by the name the Checks list uses (U179).</summary>
    internal static string DepthName(CheckDepth depth) => depth switch
    {
        CheckDepth.RepositoryStructure => "Structure check",
        CheckDepth.SavedDataSample => "Data sample",
        CheckDepth.AllData => "Full data read",
        CheckDepth.RestoreSample => "Sample test restore",
        CheckDepth.RestoreAllFiles => "Full test restore",
        CheckDepth.RestoreUnspecified => "Test restore",
        _ => "Check",
    };

    /// <summary>The card's hover: the result, then each check's latest result.</summary>
    private static string Hover(string text, IReadOnlyList<string> kinds)
        => kinds.Count == 0 ? text : text + "\n\nEach check:\n" + string.Join("\n", kinds);
}
