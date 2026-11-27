using System;
using System.Collections.Generic;
using System.Linq;
using Backspace.Models;
using Backspace.Services.Platform;

namespace Backspace.Services;

public enum ScheduleReadiness { Ready, Off, Paused, Attention, Unknown }
public sealed record ScheduleCheck(ScheduleReadiness State, string Detail);

/// <summary>One backup in the tray menu: its name and its Last backup line,
/// as on its card, with the tone of the card's icon ("ok", "warn", "fail",
/// "skipped", "idle").</summary>
public sealed record TrayBackupLine(string Name, string Text, string Tone);

/// <summary>
/// What the tray menu says (U258, owner 16 November 2026: "Try to be as
/// consistent as possible with how info is shown on the Backups tab itself
/// ... with the absolute essentials ... it should not break if a user has a
/// lot of backups"). A headline as on the Backups tab ("Everything looks
/// good", "2 backups need attention"), then each backup with its Last backup
/// line and its icon, the ones needing attention first, at most
/// <see cref="MaxLines"/> of them.
/// </summary>
public sealed record TrayScheduleStatus(ScheduleReadiness State, string Title, string Detail)
{
    /// <summary>Backups listed in the menu; more are summed up in one line.</summary>
    public const int MaxLines = 6;

    public IReadOnlyList<TrayBackupLine> Lines { get; init; } = [];
    /// <summary>Backups not listed (beyond <see cref="MaxLines"/>).</summary>
    public int MoreBackups { get; init; }

    /// <param name="busy">Backups running or waiting their turn now: not missed.</param>
    public static TrayScheduleStatus Inspect(IEnumerable<Backup> backups, IScheduler scheduler,
        string exePath, DateTime nowUtc, IReadOnlyCollection<string>? busy = null)
    {
        var all = backups.ToArray();
        var enabled = all.Where(b => b.Enabled).ToArray();
        var active = enabled.Where(b => b.Schedule.PausedUntilUtc is not { } until || until <= nowUtc).ToArray();
        var checks = active.ToDictionary(b => b.Id, Check);

        var missed = new HashSet<string>();
        var lines = all.Select(b => Line(b)).ToList();
        var ordered = lines.OrderBy(l => l.Tone switch { "fail" => 0, "warn" or "skipped" => 1, "idle" => 2, _ => 3 })
            .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        TrayScheduleStatus With(ScheduleReadiness state, string title) => new(state, title, "")
        { Lines = ordered.Take(MaxLines).ToList(), MoreBackups = Math.Max(0, ordered.Count - MaxLines) };

        if (all.Length == 0) return new(ScheduleReadiness.Off, "No backups set up yet", "");
        if (enabled.Length == 0) return With(ScheduleReadiness.Off, "Automatic backups off");
        if (active.Length == 0) return With(ScheduleReadiness.Paused, "Automatic backups paused");

        // As on the Backups tab: a failure or a schedule that cannot run needs
        // attention; warnings and skips are said as such.
        var needs = lines.Where(l => l.Tone is "fail" or "warn" or "skipped").ToList();
        if (needs.Count > 0 && needs.All(l => l.Tone == "warn") && missed.Count == 0)
            return With(ScheduleReadiness.Attention, needs.Count == 1 ? "1 backup finished with warnings" : $"{needs.Count} backups finished with warnings");
        if (needs.Count > 0)
            return With(ScheduleReadiness.Attention, needs.Count == 1 ? "1 backup needs attention" : $"{needs.Count} backups need attention");
        if (checks.Values.Any(c => c.State != ScheduleReadiness.Ready))
            return With(ScheduleReadiness.Unknown, "Schedules not verified");
        // A ready schedule is not a protected backup (U084).
        if (active.Any(b => b.LastSuccessUtc is null && b.LastRunStatus is BackupRunStatus.NeverRun or BackupRunStatus.Skipped))
            return With(ScheduleReadiness.Unknown, "Not backed up yet");
        var pausedCount = enabled.Length - active.Length;
        return With(ScheduleReadiness.Ready, pausedCount > 0 ? $"Everything looks good · {pausedCount} paused" : "Everything looks good");

        TrayBackupLine Line(Backup b)
        {
            if (checks.TryGetValue(b.Id, out var check) && check.State == ScheduleReadiness.Attention)
                return new(b.Name, Short(check.Detail), "fail");
            if (!b.Enabled) return new(b.Name, "Automatic backups off", "idle");
            if (b.Schedule.PausedUntilUtc is { } until && until > nowUtc) return new(b.Name, "Paused", "idle");
            if (b.LastRunEndUtc is not { } ended || b.LastRunStatus is BackupRunStatus.NeverRun or BackupRunStatus.Running)
                return new(b.Name, "Not backed up yet", "idle");
            var status = b.LastRunStatus;
            // As on its card (U269): within the quiet period a skipped run
            // (drive not plugged in, no network) warns nowhere; the line says
            // when the last backup ran, and the slots count from the skip.
            if (b.IsQuietSkip(status, b.LastRunOnlyNetworkWaits, nowUtc))
            {
                if (status == BackupRunStatus.Warning) status = BackupRunStatus.Success;
                else
                {
                    var line = b.LastSuccessUtc is { } success && success < ended
                        ? "Backed up " + HumanFormat.Ago(success, nowUtc) + " · then skipped" : "Not backed up yet";
                    if (busy?.Contains(b.Id) != true && b.Schedule.Missed(ended, nowUtc)) { missed.Add(b.Id); return new(b.Name, line, "warn"); }
                    return new(b.Name, line, line == "Not backed up yet" ? "idle" : "ok");
                }
            }
            // As on its card (U265): a scheduled run missed is orange.
            if (status is BackupRunStatus.Success or BackupRunStatus.Warning && busy?.Contains(b.Id) != true && b.Schedule.Missed(ended, nowUtc))
            {
                missed.Add(b.Id);
                return new(b.Name, LastRunLine.For(b.LastRunSummary ?? "", status, ended, nowUtc).Label, "warn");
            }
            // The card's Last backup line (LastRunLine), the reason kept short.
            var text = status is BackupRunStatus.Success or BackupRunStatus.Warning
                ? LastRunLine.For(b.LastRunSummary ?? "", status, ended, nowUtc).Label
                : Short(b.LastRunSummary) + " · " + HumanFormat.Ago(ended, nowUtc);
            return new(b.Name, text, status switch
            {
                BackupRunStatus.Success => "ok", BackupRunStatus.Warning => "warn",
                BackupRunStatus.Failed => "fail", BackupRunStatus.Skipped => "skipped", _ => "idle",
            });
        }

        // The first sentence of a reason, short enough for a menu line.
        static string Short(string? text)
        {
            var t = (text ?? "").Trim();
            var end = t.IndexOf(". ", StringComparison.Ordinal);
            if (end > 0) t = t[..end];
            t = t.TrimEnd('.');
            if (t.Length == 0) t = "The last run did not finish";
            return t.Length > 60 ? t[..59].TrimEnd() + "…" : t;
        }

        ScheduleCheck Check(Backup b)
        {
            if (b.SourcePaths.Count == 0 || b.Targets.Count == 0)
                return new(ScheduleReadiness.Attention, "Choose at least one source and destination.");
            try { return scheduler.InspectBackup(b, exePath); }
            catch { return new(ScheduleReadiness.Unknown, "The operating system could not be queried."); }
        }
    }
}
