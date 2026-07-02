using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.ViewModels;

namespace Backspace.Services;

// Post-run reporting half of BackupOrchestrator: toast, email,
// Healthchecks ping, history persistence, summary/body formatting.
// Triggered from FinaliseRunOwnershipAsync in the main file once the
// per-cell loop's RunRecord is sealed.
public sealed partial class BackupOrchestrator
{
    private async Task SendAlertsAsync(Backup backup, RunRecord record)
    {
        // Daily cap: at most one notification per backup per 24h
        // window FOR THE SAME STATUS. A status transition (Success →
        // Failed, Skipped → Failed, Failed → Success "we're back") is
        // always a fresh fire — the original "any severity within 24h"
        // shape silently suppressed a Failed at 17:00 after a Success
        // at 09:00, which code-review correctly flagged as a real
        // silent-failure footgun. Repeat fires of the same status get
        // capped: an hourly backup that keeps failing stops at one
        // toast/email/HC ping per 24h until the status changes.
        var now = DateTime.UtcNow;
        if (backup.IsWithinNotificationDailyCap(now, record.Status))
        {
            _log.Information(
                "Notification suppressed for {Name} ({Status}): within the 24h same-status cap (last fired {Last:o})",
                backup.Name, record.Status, backup.LastNotificationFiredUtc);
            return;
        }
        var severity = record.Status switch
        {
            BackupRunStatus.Success => NotificationSeverity.Success,
            BackupRunStatus.Warning => NotificationSeverity.Warning,
            BackupRunStatus.Failed  => NotificationSeverity.Failure,
            BackupRunStatus.Skipped => NotificationSeverity.Info,
            _                       => NotificationSeverity.Info,
        };

        // Toast title: lead with the app name so the user sees who the
        // notification is from in the action-center list (Windows just
        // shows the title in the strip, not the app icon's owner). The
        // backup's own name + verdict goes in the title body.
        var verdict = record.Status switch
        {
            BackupRunStatus.Success => "completed",
            BackupRunStatus.Warning => "completed with warnings",
            BackupRunStatus.Failed  => "FAILED",
            BackupRunStatus.Skipped => "skipped",
            _                       => record.Status.ToString().ToLowerInvariant(),
        };
        var title = $"Backspace — «{backup.Name}» {verdict}";

        // Toast body: useful one-liner. For failures, surface the most
        // common reason instead of the generic summary, plus a hint
        // that the user can see logs in Activity & logs.
        string detail;
        if (record.Status == BackupRunStatus.Failed)
        {
            var firstError = record.Errors.Count > 0 ? record.Errors[0] : null;
            detail = firstError is null
                ? $"{record.Summary}. Open Backspace → Activity & logs for details."
                : $"{firstError}\nOpen Backspace → Activity & logs for the full run history.";
            // Cap toast detail length — Windows truncates aggressively.
            if (detail.Length > 350) detail = detail[..347] + "...";
        }
        else
        {
            detail = $"{record.Summary}";
        }

        // Click-to-logs: clicking the toast body navigates to the
        // Backups → Activity & logs tab and pre-selects this backup's
        // most recent run. We capture backup.Id by value so the closure
        // is safe across runs. AppNavigator.Go marshals to UI thread,
        // and SelectBackupById is a no-op when the run isn't available
        // yet (e.g. config hasn't been re-read), so the action degrades
        // gracefully if anything's mid-flight.
        var backupIdForToast = backup.Id;
        Action onClick = () =>
        {
            AppNavigator.Go(NavItem.Logs);
            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime l
                    && l.MainWindow is { } mw)
                {
                    // Bring the main window to the foreground so the
                    // user actually sees the tab swap. WindowState =
                    // Normal handles a minimized window; Activate
                    // pulls it forward.
                    if (mw.WindowState == Avalonia.Controls.WindowState.Minimized)
                        mw.WindowState = Avalonia.Controls.WindowState.Normal;
                    mw.Show();
                    mw.Activate();
                    if (mw.DataContext is ViewModels.MainWindowViewModel vm)
                        vm.Logs.SelectBackupById(backupIdForToast);
                }
            }
            catch (Exception ex) { _log.Warning(ex, "Click-to-logs handler threw"); }
        };
        // Pick the per-event channel matrix the user configured for
        // this verdict. Warning runs ride the Failure row (they share
        // the "something needs your attention" semantics) — keeps the
        // matrix to three rows in Settings instead of four.
        var notifications = _config.Current.Notifications;
        var channels = record.Status switch
        {
            BackupRunStatus.Success => notifications.OnSuccess,
            BackupRunStatus.Warning => notifications.OnFailure,
            BackupRunStatus.Failed  => notifications.OnFailure,
            BackupRunStatus.Skipped => notifications.OnSkipped,
            _ => notifications.OnSuccess,
        };

        // Fire-and-forget the toast: NotifyAsync's "Failure" path
        // shows a topmost alert whose Task completes only when the
        // user dismisses it. Awaiting that pinned RunBackupAsync's
        // own Task open, which in turn pinned UI state ("Running…",
        // "Verifying…", "Queued — …") until the user closed the
        // alert. The user reported: "Notification is fire and
        // forget, it should not prevent the UI from being updated."
        // Errors land in the app log via the lambda's catch.
        _ = Task.Run(async () =>
        {
            try { await _notify.NotifyAsync(title, detail, severity, onClick, dismissAfter: null, channelsOverride: channels); }
            catch (Exception ex) { _log.Warning(ex, "NotifyAsync threw for {Name}", backup.Name); }
        });

        // Email gating moved into the same per-event matrix as toast +
        // sound (was previously a separate Mail.OnSuccess/Failure/Skip
        // triplet). The Mail provider config still lives in MailSettings
        // — that's the SMTP / API side, not the "when to send" side.
        if (channels.Email)
            await _mail.SendAsync(title, BuildEmailBody(backup, record), CancellationToken.None);

        // Healthchecks ping.
        if (backup.HealthcheckId is { } hcUuid)
        {
            var body = BuildEmailBody(backup, record);
            if (record.Status is BackupRunStatus.Success or BackupRunStatus.Warning)
                await _hc.PingSuccessAsync(hcUuid, body, CancellationToken.None);
            else if (record.Status == BackupRunStatus.Failed)
                await _hc.PingFailureAsync(hcUuid, body, CancellationToken.None);
            // "Skipped" intentionally pings neither — the drive wasn't there; that isn't a health signal.
        }

        // Stamp the daily-cap clock NOW that we've actually fired the
        // notifications above. Stash BOTH the timestamp and the status
        // — the cap is per-(backup, status) so a status TRANSITION
        // always breaks through, but two failures within 24h of each
        // other get capped. Bump even when the matrix had every
        // channel off — the user's intent is "I've been told about
        // this status today," not "a transport-layer succeeded today."
        // Suppressed-by-cap branches earlier return without reaching
        // here, so the timestamp only moves on a real fire attempt.
        _config.UpdateQuiet(cfg =>
        {
            var b = cfg.Backups.FirstOrDefault(x => x.Id == backup.Id);
            if (b is null) return;
            b.LastNotificationFiredUtc = now;
            b.LastNotifiedStatus = record.Status;
        });
    }

    private void UpdateBackupLastRun(Backup backup, RunRecord record)
    {
        // UpdateQuiet (NOT Update) — Config.Changed fanout is redundant
        // here because BackupsViewModel.OnRunEnded already handles the
        // per-card update directly off the orchestrator's RunEnded
        // event. Without the suppression, every run-completion fired
        // both events and forced a full Refresh on every subscribed VM
        // (Backups, Destinations, Logs, Restore) for state they had no
        // reason to re-read. The save still happens atomically under
        // the gate; only the broadcast is skipped.
        // Read the per-source size hints the progress service captured
        // from BACKUP_STATS lines BEFORE the run-finished purge clears
        // them. We persist them on the Backup so the BackupCard's
        // sources column can render "C:\Jts (1.2 GB)" inline next to
        // each path even after the run is long done. The user reported:
        // "in the sources column, once a successful backup has been
        // completed, after each Path it should indicate the size of
        // the source within parentheses" — surfaced from the figure
        // restic reports at completion.
        //
        // Only mutate on Success / Warning runs: a Failed/Skipped run
        // either never finished a backup phase or never reached
        // BACKUP_STATS, so any size hint would be stale or partial.
        Dictionary<string, long>? freshSizes = null;
        if (record.Status is BackupRunStatus.Success or BackupRunStatus.Warning)
        {
            try
            {
                var snap = _progress.Snapshot(backup.Id);
                if (snap is not null && snap.SourceWeightBytes.Count > 0)
                {
                    freshSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (path, bytes) in snap.SourceWeightBytes)
                        if (bytes > 0) freshSizes[path] = bytes;
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Reading per-source size snapshot threw for {Name}", backup.Name);
            }
        }

        _config.UpdateQuiet(cfg =>
        {
            var b = cfg.Backups.FirstOrDefault(x => x.Id == backup.Id);
            if (b is null) return;
            b.LastRunStartUtc = record.StartedUtc;
            b.LastRunEndUtc = record.EndedUtc;
            b.LastRunStatus = record.Status;
            b.LastRunSummary = record.Summary;
            // Auto-reset the grace-period clock on every healthy run.
            // The orchestrator already escalates Skipped to Failed
            // after the grace window — without this reset, the clock
            // would only ever count UP, and a backup that ran cleanly
            // for 6 months then went offline for a weekend would have
            // its grace measured from CreatedUtc (or the last manual
            // reset) instead of "last time we knew this was working".
            // Success+Warning both count: Warning is "succeeded with
            // some unreadable source files," not a destination
            // problem, so it's healthy from the grace clock's POV.
            if (record.Status is BackupRunStatus.Success or BackupRunStatus.Warning)
            {
                b.LastSuccessUtc = record.EndedUtc ?? DateTime.UtcNow;
            }
            if (freshSizes is not null)
            {
                foreach (var (path, bytes) in freshSizes)
                    b.SourceLastSizeBytes[path] = bytes;
                // Drop entries for paths the user has since removed
                // from the backup so the card never shows phantom
                // sizes for paths that aren't in the source list.
                var live = new HashSet<string>(b.SourcePaths, StringComparer.OrdinalIgnoreCase);
                var stale = b.SourceLastSizeBytes.Keys.Where(k => !live.Contains(k)).ToList();
                foreach (var k in stale) b.SourceLastSizeBytes.Remove(k);
            }
        });
    }

    internal static string BuildSummary(RunRecord r, string? orchestratorCancelReason = null,
        Backup? backup = null, AppConfig? config = null)
    {
        var d = (r.EndedUtc ?? DateTime.UtcNow) - r.StartedUtc;
        var dur = HumanFormat.Duration(d);
        if (r.Status == BackupRunStatus.Skipped)
        {
            // Pick the most informative cell summary as the run-level
            // skip reason — every Skipped cell carries a precise reason
            // (manual stop, metered network, drive unplugged, etc.) set
            // by the cell loop. Falls back to the orchestrator's recorded
            // CancelReason when no cell ran (early Stop, metered-watchdog
            // before the first cell). Without that fallback an early
            // Stop produced a generic "Skipped in {dur}" line that hid
            // which actor cancelled the run.
            var cellReason = r.Cells
                .Where(c => c.Status == BackupRunStatus.Skipped && !string.IsNullOrWhiteSpace(c.Summary))
                .Select(c => c.Summary)
                .FirstOrDefault();
            var reason = !string.IsNullOrWhiteSpace(cellReason)
                ? cellReason
                : (string.IsNullOrWhiteSpace(orchestratorCancelReason) ? null : orchestratorCancelReason);
            if (string.IsNullOrEmpty(reason))
                return $"Skipped after {dur}";

            // Fold redundant phrasing so the row doesn't read
            // "Skipped · Manually skipped · skipped after 5s" — the
            // user reported three different "skipped" tokens stacking
            // up. Strip a leading "Manually skipped"/"Skipped"/etc.
            // and keep the remaining context (e.g. the cause) before
            // the duration. Examples:
            //   "Manually skipped"          → "Manually skipped after 5s"
            //   "Skipped — drive unplugged" → "Skipped — drive unplugged · 5s"
            //   "Network became metered"    → "Network became metered · skipped after 5s"
            // We only collapse when the reason already contains
            // "skipped" (case-insensitive) — otherwise the cell-level
            // reason has its own vocabulary and we still need to spell
            // out that the run was skipped.
            var trimmedReason = reason.TrimEnd('.', ' ', '·');
            var saysSkipped = trimmedReason.Contains("skipped", StringComparison.OrdinalIgnoreCase);
            var baseSummary = saysSkipped
                ? $"{trimmedReason} after {dur}"
                : $"{trimmedReason} · skipped after {dur}";
            // Cross-reference the "Locate moved repository" editor flow
            // when the conditions match: backup has previously succeeded
            // (so "this used to work" suggests something moved, vs. a
            // never-run backup that's just freshly mis-configured) AND
            // at least one Skipped cell's destination is a local-like
            // kind where the button is available. Cloud kinds get a
            // different recovery path (Subpath edit + re-auth) and
            // shouldn't be told to look for a button that isn't there
            // for them.
            var hint = LocateMovedHintIfApplicable(r, backup, config);
            return hint is null ? baseSummary : $"{baseSummary} — {hint}";
        }
        var parts = new List<string>();
        if (r.BytesUploaded > 0) parts.Add($"{HumanFormat.Size(r.BytesUploaded)} uploaded");
        if (r.FilesNew + r.FilesModified + r.FilesRemoved > 0)
            parts.Add($"{r.FilesNew} new / {r.FilesModified} mod / {r.FilesRemoved} rem");
        if (r.Errors.Count > 0)    parts.Add($"{r.Errors.Count} errors");
        if (r.Warnings.Count > 0)  parts.Add($"{r.Warnings.Count} warnings");
        // No-op success runs (everything already up-to-date — no
        // bytes uploaded, no file changes) used to surface as
        // "in 6m 10s", which the BackupCard rendered as
        // "LAST in 6m 10s" — reads confusingly since "LAST" is the
        // row label not the verb. The user explicitly asked for
        // "LAST Completed in 4m 10s" instead. Skipped runs already
        // get their own "Manually skipped after Xs" phrasing in the
        // branch above; here we cover the success-no-op case
        // symmetrically. Runs that DID upload or change files keep
        // the bare "in {dur}" suffix because the leading parts
        // already describe what happened.
        parts.Add(parts.Count == 0 ? $"Completed in {dur}" : $"in {dur}");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Returns the "Locate moved repository" cross-reference hint to
    /// append to a Skipped run summary, or null if it shouldn't be
    /// shown. The hint only applies when (a) the backup has previously
    /// succeeded (so this WAS working and now isn't — most likely the
    /// destination was relocated), (b) at least one skipped cell's
    /// destination is local-like (LocalFolder / ExternalDrive /
    /// NetworkShare), since cloud kinds don't have the Locate moved
    /// affordance. Null-tolerant on backup / config so existing
    /// callers that don't supply them get the legacy plain summary.
    /// </summary>
    internal static string? LocateMovedHintIfApplicable(RunRecord r, Backup? backup, AppConfig? config)
    {
        if (backup is null || config is null) return null;
        if (backup.LastSuccessUtc is null) return null;
        // At least one Skipped cell on a local-like destination — the
        // user has to be able to see the button to be told to use it.
        // Match by (DestinationId, StorageName) via the backup's own
        // Targets list rather than CellOutcome.DestinationName, which
        // is a free-text label the user can rename AND which collides
        // when two destinations happen to share a display name (real
        // case: "Backups" used for both a local USB and an S3 bucket).
        // The cell loop stamps DestinationName + StorageName on every
        // CellOutcome; we use that pair to look up the canonical id
        // in the backup's Targets, then resolve to the live config
        // entry.
        foreach (var cell in r.Cells)
        {
            if (cell.Status != BackupRunStatus.Skipped) continue;
            // Round-6 review caught: the hint used to fire for ANY
            // Skipped cell on a local-like destination, including
            // transient network blips ("Network share unreachable"
            // → Tolerable → Skipped). The user got "use Locate
            // moved repository to repoint it" advice for a
            // destination that hadn't actually moved — and
            // panicked into re-pointing at the wrong path. Round-7
            // refines the gate by reusing EngineErrorClassifier: a
            // hint is only honest advice when the cell's LastError
            // does NOT classify as Tolerable. A Tolerable signal
            // (network unreachable / DNS timeout / metered) means
            // "come back later," not "your destination has moved."
            if (string.IsNullOrEmpty(cell.LastError)) continue;
            if (EngineErrorClassifier.Classify(cell.LastError) == EngineErrorClassifier.Verdict.Tolerable)
                continue;
            // Walk the backup's Targets and resolve each into a live
            // Destination, then accept the match ONLY if the resolved
            // destination's display name matches the cell's recorded
            // DestinationName. Round-2 review caught the previous
            // FirstOrDefault(StorageName) shape: with two targets both
            // using StorageName="default" (the very common case), the
            // wrong target won every time and the hint surfaced
            // against a still-mounted local drive when the actually-
            // missing destination was a different target. Matching on
            // (StorageName + display-name) closes the gap — the cell
            // outcome carries both, so a unique pair almost always
            // exists even when two destinations share a Name.
            Destination? resolved = null;
            foreach (var target in backup.Targets)
            {
                if (!string.Equals(target.StorageName, cell.StorageName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var dest = config.Destinations.FirstOrDefault(d =>
                    string.Equals(d.Id, target.DestinationId, StringComparison.Ordinal));
                if (dest is null) continue;
                if (!string.Equals(dest.Name, cell.DestinationName, StringComparison.Ordinal)) continue;
                resolved = dest;
                break;
            }
            if (resolved is null)
            {
                // Target row was deleted mid-run (user editing
                // Targets while a backup is in flight). Fall back to
                // matching by cell.DestinationName alone — BUT only
                // when the resolution is unambiguous (exactly one
                // destination has this name). Round-5 review caught
                // that round-4's fallback re-introduced the round-2
                // collision: two destinations sharing a Name (real
                // case: "Backups" for both a local USB and an S3
                // bucket) → FirstOrDefault returned the wrong one
                // and the hint surfaced against the wrong kind.
                // Skipping the hint when ambiguous is strictly safer
                // than guessing wrong.
                var byName = config.Destinations
                    .Where(d => string.Equals(d.Name, cell.DestinationName, StringComparison.Ordinal))
                    .Take(2)
                    .ToList();
                if (byName.Count != 1) continue;
                resolved = byName[0];
            }
            if (resolved.Kind == DestinationKind.LocalFolder
                || resolved.Kind == DestinationKind.ExternalDrive
                || resolved.Kind == DestinationKind.NetworkShare)
            {
                return "if you've moved this backup folder, use Destinations → Edit → Locate moved repository to repoint it";
            }
        }
        return null;
    }

    private static string BuildEmailBody(Backup b, RunRecord r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Backup:   {b.Name}");
        if (b.SourcePaths.Count == 1)
        {
            sb.AppendLine($"Source:   {b.SourcePaths[0]}");
        }
        else
        {
            sb.AppendLine($"Sources:  {b.SourcePaths.Count}");
            foreach (var s in b.SourcePaths) sb.AppendLine($"            {s}");
        }
        sb.AppendLine($"Machine:  {Environment.MachineName}");
        sb.AppendLine($"Started:  {r.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        if (r.EndedUtc is DateTime e) sb.AppendLine($"Ended:    {e.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Status:   {r.Status}");
        sb.AppendLine($"Summary:  {r.Summary}");

        // Per-cell breakdown — for a multi-source or multi-target
        // backup this is where the user actually learns what happened.
        if (r.Cells.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine("Details by source × destination:");
            foreach (var c in r.Cells)
            {
                // Path.GetFileName returns "" for a drive root like
                // "C:\" (TrimEnd → "C:" → GetFileName("C:") → ""). Use
                // the trimmed full path as the fallback so the email
                // renders "[Status   ] C: → Dest" instead of
                // "[Status   ]   → Dest" — the previous `??` over the
                // non-nullable GetFileName return was dead code.
                var trimmedSrc = c.SourcePath.TrimEnd('\\', '/');
                var srcLabel = Path.GetFileName(trimmedSrc);
                if (string.IsNullOrEmpty(srcLabel)) srcLabel = trimmedSrc;
                if (string.IsNullOrEmpty(srcLabel)) srcLabel = c.SourcePath;
                var line = $"  [{c.Status,-8}] {srcLabel} → {c.DestinationName}";
                if (c.AttemptsMade > 1) line += $" (after {c.AttemptsMade} attempts)";
                if (!string.IsNullOrWhiteSpace(c.Summary)) line += $" — {c.Summary}";
                sb.AppendLine(line);
                if (!string.IsNullOrWhiteSpace(c.LastError))
                    sb.AppendLine($"             error: {c.LastError}");
            }
        }

        if (r.Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Errors:");
            foreach (var l in r.Errors) sb.AppendLine("  " + l);
        }
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            foreach (var l in r.Warnings) sb.AppendLine("  " + l);
        }
        sb.AppendLine();
        sb.AppendLine($"Full log: {r.LogPath}");
        return sb.ToString();
    }
}
