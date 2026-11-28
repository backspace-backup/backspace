using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services.Platform;
using Backspace.ViewModels;

namespace Backspace.Services;

public enum ScheduleReadiness { Ready, Off, Paused, Attention, Unknown }
public sealed record ScheduleCheck(ScheduleReadiness State, string Detail);

/// <summary>One backup in the tray menu: its name and its card's line, with
/// the tone of the card's icon ("ok", "warn", "fail", "skipped", "idle").</summary>
public sealed record TrayBackupLine(string Name, string Text, string Tone);

/// <summary>What the tray menu reads from a backup's card: its Last backup
/// line and icon, its Backup health line, and what the banner reads.</summary>
internal sealed record TrayCard(string LastRun, string RunTone, string HealthLine, CardHealth Health);

/// <summary>
/// What the tray menu says (U258, owner 16 November 2026: "Try to be as
/// consistent as possible with how info is shown on the Backups tab itself
/// ... with the absolute essentials ... it should not break if a user has a
/// lot of backups"). The headline is the Backups tab's banner, computed by
/// the same code from the same cards (<see cref="BackupHealthSummary"/>; owner
/// 28 November 2026: "is it normal the context menu doesn't reflect the health
/// status issue?"). Then each backup with its card's line and icon, the ones
/// the banner names first, at most <see cref="MaxLines"/> of them. A backup
/// named for a check shows its Backup health line ("Restore check failed ·
/// 4h ago"), the others their Last backup line.
/// </summary>
public sealed record TrayScheduleStatus(ScheduleReadiness State, string Title, string Detail)
{
    /// <summary>Backups listed in the menu; more are summed up in one line.</summary>
    public const int MaxLines = 6;

    public IReadOnlyList<TrayBackupLine> Lines { get; init; } = [];
    /// <summary>Backups not listed (beyond <see cref="MaxLines"/>).</summary>
    public int MoreBackups { get; init; }

    /// <summary>The menu from the backups' cards, as the Backups tab shows
    /// them. <paramref name="schedules"/> is each active backup's schedule as
    /// registered (null when it could not be read); a schedule that cannot
    /// run also needs attention.</summary>
    internal static TrayScheduleStatus Compose(IReadOnlyList<Backup> backups, Func<Backup, TrayCard> card, DateTime nowUtc,
        IReadOnlyDictionary<string, ScheduleCheck>? schedules = null)
    {
        if (backups.Count == 0) return new(ScheduleReadiness.Off, "No backups set up yet", "");
        var cards = backups.ToDictionary(b => b.Id, card);
        var health = BackupHealthSummary.Compute(backups, b => cards[b.Id].Health, nowUtc);
        var named = health.Affected.Where(a => a.Tone != "idle").ToDictionary(a => a.Backup.Id);
        var enabled = backups.Where(b => b.Enabled).ToArray();
        var active = enabled.Where(b => b.Schedule.PausedUntilUtc is not { } until || until <= nowUtc).ToArray();
        // A schedule that cannot run (no source or destination, a service
        // registration that is missing) is not on a card's tiles.
        var broken = active.Where(b => !named.ContainsKey(b.Id) && Schedule(b).State == ScheduleReadiness.Attention).ToList();

        var lines = backups.Select(b =>
        {
            var c = cards[b.Id];
            if (named.TryGetValue(b.Id, out var a)) return (Line: new TrayBackupLine(b.Name, a.Check ? c.HealthLine : c.LastRun, a.Tone), Rank: a.Tone == "fail" ? 0 : 1);
            if (broken.Contains(b)) return (Line: new TrayBackupLine(b.Name, Short(Schedule(b).Detail), "fail"), Rank: 0);
            if (!b.Enabled) return (Line: new TrayBackupLine(b.Name, "Automatic backups off", "idle"), Rank: 2);
            if (b.Schedule.PausedUntilUtc is { } until && until > nowUtc) return (Line: new TrayBackupLine(b.Name, "Paused", "idle"), Rank: 2);
            var tone = c.RunTone is "ok" or "warn" or "fail" or "skipped" ? c.RunTone : "idle";
            return (Line: new TrayBackupLine(b.Name, c.LastRun, tone), Rank: tone == "ok" ? 3 : 2);
        }).OrderBy(l => l.Rank).ThenBy(l => l.Line.Name, StringComparer.CurrentCultureIgnoreCase).Select(l => l.Line).ToList();
        TrayScheduleStatus With(ScheduleReadiness state, string title) => new(state, title, health.Detail)
        { Lines = lines.Take(MaxLines).ToList(), MoreBackups = Math.Max(0, lines.Count - MaxLines) };

        if (broken.Count > 0)
        {
            var count = named.Count + broken.Count;
            return With(ScheduleReadiness.Attention, count == 1 ? "1 backup needs attention" : $"{count} backups need attention");
        }
        if (health.Class is "fail" or "warn") return With(ScheduleReadiness.Attention, health.Headline);
        if (enabled.Length == 0) return With(ScheduleReadiness.Off, "Automatic backups off");
        if (active.Length == 0) return With(ScheduleReadiness.Paused, "Automatic backups paused");
        if (health.Class == "idle") return With(ScheduleReadiness.Unknown, health.Headline.Length > 0 ? health.Headline : "Backing up now");
        if (active.Any(b => Schedule(b).State != ScheduleReadiness.Ready)) return With(ScheduleReadiness.Unknown, "Schedules not verified");
        var pausedCount = enabled.Length - active.Length;
        return With(ScheduleReadiness.Ready, pausedCount > 0 ? $"{health.Headline} · {pausedCount} paused" : health.Headline);

        ScheduleCheck Schedule(Backup b)
        {
            if (b.SourcePaths.Count == 0 || b.Targets.Count == 0)
                return new(ScheduleReadiness.Attention, "Choose at least one source and destination.");
            return schedules?.TryGetValue(b.Id, out var check) == true ? check : new(ScheduleReadiness.Ready, "");
        }

        // The first sentence of a reason, short enough for a menu line.
        static string Short(string? text)
        {
            var t = (text ?? "").Trim();
            var end = t.IndexOf(". ", StringComparison.Ordinal);
            if (end > 0) t = t[..end];
            t = t.TrimEnd('.');
            return t.Length > 60 ? t[..59].TrimEnd() + "…" : t;
        }
    }

    /// <summary>Each active backup's registered schedule (off the UI thread:
    /// it may ask Windows).</summary>
    internal static Dictionary<string, ScheduleCheck> Schedules(IEnumerable<Backup> backups, IScheduler scheduler, string exePath, DateTime nowUtc)
        => backups.Where(b => b.Enabled && (b.Schedule.PausedUntilUtc is not { } until || until <= nowUtc)
                && b.SourcePaths.Count > 0 && b.Targets.Count > 0)
            .ToDictionary(b => b.Id, b =>
            {
                try { return scheduler.InspectBackup(b, exePath); }
                catch { return new ScheduleCheck(ScheduleReadiness.Unknown, "The operating system could not be queried."); }
            });

    /// <summary>The menu from cards made as the Backups tab makes them, once
    /// each has read its check history (at most <paramref name="wait"/>).
    /// On the UI thread.</summary>
    internal static async Task<TrayScheduleStatus> FromCardsAsync(IReadOnlyList<Backup> backups, DateTime nowUtc,
        IReadOnlyDictionary<string, ScheduleCheck>? schedules, TimeSpan wait)
    {
        var cards = backups.Select(b => new BackupCardViewModel(b, showActions: false, showProgress: false)).ToList();
        try
        {
            try { await Task.WhenAll(cards.Select(c => c.CheckHistoryRead)).WaitAsync(wait); }
            catch (TimeoutException) { }
            var byId = cards.ToDictionary(c => c.Id);
            return Compose(backups, b => Read(byId[b.Id]), nowUtc, schedules);
        }
        finally { foreach (var c in cards) c.Dispose(); }
    }

    internal static TrayCard Read(BackupCardViewModel c)
        => new(c.LastRunLabel, c.StatusClass, c.VerifyLabel, c.Health);
}
