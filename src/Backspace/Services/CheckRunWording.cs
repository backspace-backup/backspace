using System;
using System.Collections.Generic;
using System.Linq;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>What a check run says, in plain words (U179, 10 November 2026).
/// Before: "Check: 1 completed, 0 with warnings, 0 failed, 0 skipped across 1
/// parts in 1201.3s." Now: "Data sample (1 GiB) passed in 20 min 1 s."</summary>
internal static class CheckRunWording
{
    internal const string FullReadUnavailable =
        "The data check of all data was not started: this version of the backup engine can only read a sample. Nothing was read.";

    /// <summary>A run's status from its parts: any failure fails it; a part
    /// not done beside a part done makes it a warning.</summary>
    internal static BackupRunStatus StatusOf(IReadOnlyCollection<CellOutcome> cells)
    {
        var ok = cells.Count(c => c.Status == BackupRunStatus.Success);
        var warned = cells.Count(c => c.Status == BackupRunStatus.Warning);
        var failed = cells.Count(c => c.Status == BackupRunStatus.Failed);
        var skipped = cells.Count(c => c.Status == BackupRunStatus.Skipped);
        return failed > 0 ? BackupRunStatus.Failed
            : warned > 0 || ok > 0 && skipped > 0 ? BackupRunStatus.Warning
            : ok > 0 ? BackupRunStatus.Success
            : BackupRunStatus.Skipped;
    }

    /// <summary>"45 s", "7 min 50 s", "1 h 05 min".</summary>
    internal static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60) return $"{Math.Max(0, (int)Math.Round(d.TotalSeconds))} s";
        if (d.TotalMinutes < 60) return d.Seconds == 0 ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalMinutes} min {d.Seconds} s";
        return $"{(int)d.TotalHours} h {d.Minutes:00} min";
    }

    internal static string Summary(BackupCheck check, RunRecord run, IReadOnlyCollection<string> waitingFor, bool metered, bool limitIgnored = false)
    {
        var took = Duration((run.EndedUtc ?? DateTime.UtcNow) - run.StartedUtc - run.QueuedFor);
        var waiting = run.Cells.Where(c => c.DestinationUnavailable).ToList();
        var done = run.Cells.Where(c => !c.DestinationUnavailable).ToList();
        string headline;
        if (metered)
            headline = $"{check.Title} stopped after {took}: the connection became metered (mobile or tethered).";
        else if (done.Count == 0 && waiting.Count > 0)
            headline = "";
        else if (done.FirstOrDefault(c => c.Status == BackupRunStatus.Failed) is { } failed)
            headline = $"{check.Title} did not pass{At(failed, done)}: {Reason(failed)}";
        else if (done.Count > 0 && done.All(c => c.Status == BackupRunStatus.Success))
            headline = $"{check.Title} passed in {took}.";
        else if (done.FirstOrDefault(c => c.Status == BackupRunStatus.Warning) is { } warned)
            headline = $"{check.Title} finished with a warning{At(warned, done)} in {took}: {Reason(warned)}";
        else if (done.Any(c => c.Status == BackupRunStatus.Success) && done.FirstOrDefault(c => c.Status == BackupRunStatus.Skipped) is { } partly)
            headline = $"{check.Title} passed at {Names(done.Where(c => c.Status == BackupRunStatus.Success).Select(Name))} in {took}, "
                + $"but was not done at {Names(done.Where(c => c.Status == BackupRunStatus.Skipped).Select(Name))}: {Reason(partly)}";
        else if (done.FirstOrDefault(c => c.Status == BackupRunStatus.Skipped) is { } skipped)
            headline = $"{check.Title} was not done{At(skipped, done)}: {Reason(skipped)}";
        else
            headline = $"{check.Title} did not run.";
        if (limitIgnored && headline.Length > 0)
            headline += " It read at most 1 GiB: this version of the backup engine does not apply another limit yet.";
        if (waitingFor.Count > 0)
            headline += (headline.Length > 0 ? "\n" : "") + $"{check.Title} waits for {Names(waitingFor)}: it runs by itself right after the next backup there.";
        return headline;
    }

    /// <summary>The cleanup's summary, as before U179.</summary>
    internal static string CleanupSummary(RunRecord run, bool metered)
    {
        var duration = ((run.EndedUtc ?? DateTime.UtcNow) - run.StartedUtc).TotalSeconds;
        return $"Prune: {run.Cells.Count(c => c.Status == BackupRunStatus.Success)} completed, "
            + $"{run.Cells.Count(c => c.Status == BackupRunStatus.Warning)} with warnings, "
            + $"{run.Cells.Count(c => c.Status == BackupRunStatus.Failed)} failed, "
            + $"{run.Cells.Count(c => c.Status == BackupRunStatus.Skipped)} skipped "
            + $"across {run.Cells.Count} parts in {duration:F1}s{(metered ? " (aborted on metered network)" : "")}.";
    }

    /// <summary>Several checks run one after another (the command line):
    /// the worst status and each check's own line.</summary>
    internal static RunRecord Combine(Backup backup, IReadOnlyList<RunRecord> records)
    {
        if (records.Count == 1) return records[0];
        var now = DateTime.UtcNow;
        return new RunRecord
        {
            BackupId = backup.Id, BackupName = backup.Name, Kind = RunKind.Check,
            StartedUtc = records.Count > 0 ? records.Min(r => r.StartedUtc) : now,
            EndedUtc = records.Count > 0 ? records.Max(r => r.EndedUtc ?? now) : now,
            Status = records.Count == 0 ? BackupRunStatus.Skipped
                : records.Any(r => r.Status == BackupRunStatus.Failed) ? BackupRunStatus.Failed
                : records.All(r => r.Status == BackupRunStatus.Success) ? BackupRunStatus.Success
                : records.All(r => r.Status == BackupRunStatus.Skipped) ? BackupRunStatus.Skipped
                : BackupRunStatus.Warning,
            Summary = records.Count == 0 ? "No check ran." : string.Join("\n", records.Select(r => r.Summary)),
            Errors = records.SelectMany(r => r.Errors).ToList(),
            Warnings = records.SelectMany(r => r.Warnings).ToList(),
        };
    }

    private static string At(CellOutcome cell, IReadOnlyCollection<CellOutcome> done)
        => done.Select(Name).Distinct().Count() > 1 ? $" at «{Name(cell)}»" : "";

    /// <summary>The destination, with its storage name when it isn't the default.</summary>
    private static string Name(CellOutcome cell) => string.IsNullOrEmpty(cell.StorageName) || cell.StorageName == "default"
        ? cell.DestinationName : $"{cell.DestinationName} ({cell.StorageName})";

    private static string Reason(CellOutcome cell)
    {
        var text = (cell.LastError ?? cell.Summary ?? "").Trim();
        if (text.Length == 0) return "see the log for details.";
        return text.EndsWith('.') ? text : text + ".";
    }

    private static string Names(IEnumerable<string> names) => string.Join(", ", names.Distinct().Select(n => "«" + n + "»"));
}
