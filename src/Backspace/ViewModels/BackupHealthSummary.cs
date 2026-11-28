using System;
using System.Collections.Generic;
using Backspace.Models;
using Backspace.Services;

namespace Backspace.ViewModels;

/// <summary>What a card shows that decides the Backups tab's banner: its
/// Backup health tone, its last run as shown, and whether a scheduled run was
/// missed.</summary>
/// <param name="NeverBackedUp">Nothing stored a day after the backup was made (U257).</param>
internal sealed record CardHealth(string? CheckTone, BackupRunStatus RunStatus, DateTime? RunEndUtc, bool Missed, bool NeverBackedUp = false);

/// <summary>A backup the banner names, with its pill's tone and why.</summary>
internal sealed record HealthAffected(Backup Backup, string Tone, string Reason, bool Check = false);

/// <summary>
/// The Backups tab's banner, computed once for the tab and the tray menu
/// (U258, owner 28 November 2026: "is it normal the context menu doesn't
/// reflect the health status issue?" The menu said "Not backed up yet" while
/// the tab said "2 backups need attention" for a failed restore check).
/// </summary>
internal sealed record BackupHealthSummary(string Class, string Headline, string Detail, IReadOnlyList<HealthAffected> Affected)
{
    public static BackupHealthSummary Compute(IReadOnlyCollection<Backup> backups, Func<Backup, CardHealth> card, DateTime nowUtc)
    {
        if (backups.Count == 0) return new("idle", "", "", []);
        var failed = new List<Backup>();
        var failedTestRestores = 0;
        var failedChecks = 0;
        var failedRuns = 0;
        var warned = new List<Backup>();
        var incompleteChecks = 0;
        var missedRuns = 0;
        var notBackedUp = 0;
        var reasons = new Dictionary<string, string>();
        var checks = new HashSet<string>();
        var loadingChecks = 0;
        var runningCount = 0;
        var skipped = new List<Backup>();
        var neverRun = new List<Backup>();
        var okCount = 0;
        DateTime? mostRecent = null;
        foreach (var b in backups)
        {
            var facts = card(b);
            var checkTone = facts.CheckTone;
            var runStatus = facts.RunStatus;
            if (runStatus == BackupRunStatus.Failed) failedRuns++;
            if (checkTone == "fail"
                && !(b.LastVerifyUtc is not null && b.LastVerifyPass == false))
            {
                failed.Add(b);
                failedChecks++;
                checks.Add(b.Id);
                reasons[b.Id] = "A saved check is failing";
            }
            else if (b.LastVerifyUtc is DateTime verifiedAt && b.LastVerifyPass == false)
            {
                // A later successful backup does not prove that recovery works.
                // Keep the warning until a Test restore succeeds. Cancellation
                // preserves the previous completed verification result.
                failed.Add(b);
                failedTestRestores++;
                checks.Add(b.Id);
                reasons[b.Id] = "Its restore check failed";
                if (mostRecent is null || verifiedAt > mostRecent) mostRecent = verifiedAt;
            }
            else if (checkTone == "stale" && runStatus != BackupRunStatus.Failed)
            {
                // A cancelled, overdue or unreadable check is not an all-clear.
                // Keep one affected entry per backup and preserve run failures
                // as the higher-severity result.
                warned.Add(b);
                incompleteChecks++;
                checks.Add(b.Id);
                reasons[b.Id] = "A check is incomplete, overdue, or unavailable";
            }
            else switch (runStatus)
            {
                case BackupRunStatus.Failed:  failed.Add(b);  reasons[b.Id] = "Its last run failed"; break;
                case BackupRunStatus.Warning: warned.Add(b);  reasons[b.Id] = "Its last run finished with warnings"; break;
                // Never backed up a day after it was made (U257): orange, whatever its skips.
                case BackupRunStatus.NeverRun or BackupRunStatus.Skipped when facts.NeverBackedUp:
                    warned.Add(b); notBackedUp++; reasons[b.Id] = "It has not been backed up yet"; break;
                case BackupRunStatus.Skipped: skipped.Add(b); reasons[b.Id] = "Its last run was skipped"; break;
                case BackupRunStatus.Success when facts.Missed:
                    // Its tile is orange (U265): the banner cannot say all is well.
                    warned.Add(b); missedRuns++; reasons[b.Id] = "A scheduled backup was missed"; break;
                case BackupRunStatus.Success:
                    if (checkTone == "loading") loadingChecks++; else okCount++;
                    break;
                // Running backups are mid-flight: the card's live progress is
                // the signal, not the banner. Counted so the all-ok branch
                // does not say "All 0 backups are healthy".
                case BackupRunStatus.Running: runningCount++;  break;
                default: neverRun.Add(b);                      break;
            }
            if (facts.RunEndUtc is DateTime t && (mostRecent is null || t > mostRecent)) mostRecent = t;
        }

        // A backup running now is the latest activity (4 November 2026: the
        // banner said "Last activity: 19m ago" while local_c was running).
        var ago = runningCount > 0 ? "now" : mostRecent is DateTime when ? HumanFormat.Ago(when, nowUtc) : "never";
        var affectedList = new List<HealthAffected>();
        string Reason(Backup b) => reasons.TryGetValue(b.Id, out var why) ? why : "";
        // Headlines are count-based only: the pills name the backups (owner:
        // "include backup names exactly once in this card, and as clickable pills").
        if (failed.Count > 0)
        {
            // As for warnings (U199): one kind of problem says what it is;
            // several, or a check, say only how many backups need attention,
            // counting every pill, and each pill's hover says why (U214).
            var affected = failed.Count + warned.Count + skipped.Count;
            var others = warned.Count + skipped.Count;
            string headline, detail;
            if (failedChecks > 0 || others > 0 || failedRuns > 0 && failedTestRestores > 0)
            { headline = affected == 1 ? "1 backup needs attention" : $"{affected} backups need attention"; detail = ""; }
            else if (failedTestRestores > 0)
            { headline = failed.Count == 1 ? "Restore check failed for 1 backup" : $"Restore check failed for {failed.Count} backups"; detail = $"Last activity: {ago}"; }
            else
            {
                headline = failed.Count == 1
                    ? "1 backup failed on its last run and needs attention"
                    : $"{failed.Count} backups failed on their last run and need attention";
                detail = $"Last activity: {ago}";
            }
            foreach (var b in failed) affectedList.Add(new(b, "fail", Reason(b), checks.Contains(b.Id)));
            foreach (var b in warned) affectedList.Add(new(b, "warn", Reason(b), checks.Contains(b.Id)));
            foreach (var b in skipped) affectedList.Add(new(b, "skipped", Reason(b)));
            return new("fail", headline, detail, affectedList);
        }
        if (warned.Count > 0 || skipped.Count > 0)
        {
            // Several kinds of problem, or a check: "N backups need attention"
            // and the pills, each saying why on hover (U199).
            var warnedRuns = warned.Count - incompleteChecks - missedRuns - notBackedUp;
            var kinds = (incompleteChecks > 0 ? 1 : 0) + (warnedRuns > 0 ? 1 : 0) + (skipped.Count > 0 ? 1 : 0) + (missedRuns > 0 ? 1 : 0) + (notBackedUp > 0 ? 1 : 0);
            var generic = kinds > 1 || incompleteChecks > 0 || missedRuns > 0 || notBackedUp > 0;
            string headline;
            if (generic)
            {
                var affected = warned.Count + skipped.Count;
                headline = affected == 1 ? "1 backup needs attention" : $"{affected} backups need attention";
            }
            else if (warned.Count > 0)
                headline = warned.Count == 1 ? "1 backup finished with warnings" : $"{warned.Count} backups finished with warnings";
            else
                headline = skipped.Count == 1 ? "1 backup was skipped on its last run" : $"{skipped.Count} backups were skipped on their last run";
            foreach (var b in warned) affectedList.Add(new(b, "warn", Reason(b), checks.Contains(b.Id)));
            foreach (var b in skipped) affectedList.Add(new(b, "skipped", Reason(b)));
            return new("warn", headline, generic ? "" : $"Last activity: {ago}", affectedList);
        }
        if (neverRun.Count > 0)
        {
            foreach (var b in neverRun) affectedList.Add(new(b, "idle", "It hasn't run yet"));
            return new("idle", neverRun.Count == 1 ? "1 backup hasn't run yet" : $"{neverRun.Count} backups haven't run yet",
                $"Last activity: {ago}", affectedList);
        }
        if (loadingChecks > 0)
            return new("idle", "Reading backup health…", "Checking saved results before showing an all-clear.", []);
        if (okCount > 0)
            return new("ok", okCount == 1 ? "Everything looks good" : $"All {okCount} backups are healthy", $"Last successful run: {ago}", []);
        // Only running backups: the cards' live progress is the signal.
        return new("idle", "", "", []);
    }
}
