using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Backspace.Models;
using Backspace.Services;
using Backspace.Views;

namespace Backspace.ViewModels;

/// <summary>
/// Backups view-model — also the post-merge home of what used to be the
/// separate Dashboard tab. The two views were redundant (same content,
/// different layouts), so the user-facing model is now: one tab,
/// "Backups", showing every configured backup as a standardised card
/// (see <see cref="BackupCardViewModel"/>) with status, sources,
/// destinations, last/next run, verify state, action buttons, and a
/// live progress bar while running.
///
/// Health-banner aggregation that previously lived in DashboardViewModel
/// has moved here so the at-a-glance "everything's fine / a backup
/// failed" cue stays one click off the sidebar root.
/// </summary>
public sealed partial class BackupsViewModel : ViewModelBase, IDisposable
{
    private static Serilog.ILogger _log => AppLogger.For<BackupsViewModel>();

    public ObservableCollection<BackupCardViewModel> Cards { get; } = new();
    internal Func<Avalonia.Controls.Window?>? TestRestoreOwner { get; set; }
    [ObservableProperty] private bool _isEmpty;

    /// <summary>"1 Backup" / "2 Backups" — proper singular/plural,
    /// rendered as a section-title rather than the previous dim
    /// throwaway. Recomputed on every Refresh.</summary>
    public string BackupsCountLabel =>
        Cards.Count == 1 ? "1 Backup" : $"{Cards.Count} Backups";

    /// <summary>
    /// Singular / plural word ("Backup" / "Backups") to pair with
    /// <see cref="Cards"/>.Count in the toolbar's count display. The
    /// count display now shows the figure in large accent type with
    /// the word as a small caps label next to it, so the figure and
    /// word need to bind separately rather than as one
    /// <see cref="BackupsCountLabel"/> string.
    /// </summary>
    public string BackupsCountWord => Cards.Count == 1 ? "BACKUP" : "BACKUPS";

    // ---- health summary banner (was on Dashboard) ----
    [ObservableProperty] private bool _hasHealthBanner;
    /// <summary>Drives the sidebar warning badge: an enabled backup asks for
    /// VSS but Backspace has no administrator access, or access needs one
    /// more approval. Its flyout explains and offers the fix (U027).</summary>
    [ObservableProperty] private bool _needsAdministratorAccess;
    private BackgroundBackupViewModel? _administratorAccess;
    private BackgroundBackupViewModel AdministratorAccess => _administratorAccess ??= new BackgroundBackupViewModel();
    /// <summary>Headline summarises status COUNTS (e.g. "1 backup
    /// needs attention", "Everything looks good") — never names a
    /// specific backup. The user-facing rule: backup names appear
    /// EXACTLY ONCE in the banner, and only as clickable
    /// jump-to-backup pills below the headline.</summary>
    [ObservableProperty] private string _healthHeadline = "";
    /// <summary>Sub-line: last-activity timestamp + ok-count when
    /// there is one (e.g. "Last activity 1h ago · 1 ok"). Calm, dim,
    /// typographic — not a clickable affordance.</summary>
    [ObservableProperty] private string _healthDetail = "";
    [ObservableProperty] private string _healthClass = "ok"; // ok | warn | fail | idle

    /// <summary>Backups whose last run is in a noteworthy state (failed,
    /// warning, skipped). Surfaced as click-to-jump chips inside the
    /// health banner so a user with many backups can find the one that
    /// needs attention without scanning the list. Empty when every
    /// backup is healthy or the banner is in the "everything's fine"
    /// state.</summary>
    public ObservableCollection<HealthBackupReference> AffectedBackups { get; } = new();

    /// <summary>One entry per noteworthy backup in the health banner.
    /// Carries the id so a click can locate the matching card +
    /// scroll-into-view, and the StatusClass so the chip's leading
    /// glyph matches the card's leading glyph (visual continuity —
    /// "the warning chip in the banner is the same glyph as the
    /// warning icon on the card it points at").</summary>
    public sealed class HealthBackupReference
    {
        public string Id { get; }
        public string Name { get; }
        public string StatusClass { get; }
        // Carried by each pill so its button needs no ancestor lookup. A
        // failed $parent binding leaves Avalonia's button disabled.
        public IRelayCommand<string> Jump { get; }
        public HealthBackupReference(string id, string name, string statusClass, IRelayCommand<string> jump)
        { Id = id; Name = name; StatusClass = statusClass; Jump = jump; }
    }

    /// <summary>Coalesces a flurry of Config.Changed firings (e.g. add a
    /// backup → apply schedule fires Config.Changed twice within ~5ms)
    /// into a single Refresh on the UI thread. Without this, the
    /// second-and-later events do redundant work and can clobber a
    /// scroll position the user just set.</summary>
    private readonly UiThreadDebouncer _refreshDebouncer;
    private readonly Avalonia.Threading.DispatcherTimer _checkAgeTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Avalonia.Threading.DispatcherTimer _serviceHistoryTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>VM-scoped cancellation for fire-and-forget background runs
    /// kicked off via Save-and-Run. Without this, a run started milliseconds
    /// before page navigation / shutdown couldn't be signalled to stop and
    /// the orchestrator kept spawning the engine binary past the VM's lifetime.</summary>
    private readonly CancellationTokenSource _backgroundRunCts = new();

    private readonly LogStore _logs;
    public BackupsViewModel() : this(ServiceLocator.Logs) { }
    internal BackupsViewModel(BackgroundBackupViewModel administratorAccess) : this(ServiceLocator.Logs, administratorAccess) { }
    internal BackupsViewModel(LogStore logs, BackgroundBackupViewModel? administratorAccess = null)
    {
        _logs = logs;
        _administratorAccess = administratorAccess;
        // Allowing or removing access in Settings updates the badge too.
        AdministratorAccess.PropertyChanged += OnAdministratorAccessChanged;
        _refreshDebouncer = new UiThreadDebouncer(Refresh);
        _applyDebouncer = new UiThreadDebouncer(() => _ = ApproveChangesInFlowAsync(), 1500);
        _applyRetry.Tick += OnApplyRetry;
        // Changes saved while Backspace was closed, or not yet applied, go through on start.
        if (OperatingSystem.IsWindows()) _applyDebouncer.Schedule();
        Refresh();
        ServiceLocator.Config.Changed += OnConfigChanged;
        ServiceLocator.Orchestrator.RunStarted += OnRunStarted;
        ServiceLocator.Orchestrator.RunEnded += OnRunEnded;
        ServiceLocator.Orchestrator.RunQueuedReasonChanged += OnRunQueuedReasonChanged;
        ServiceLocator.Maintenance.MaintenanceStateChanged += OnMaintenanceStateChanged;
        ServiceLocator.ActiveRestores.Changed += OnTestRestoresChanged;
        _logs.RunAppended += OnCheckHistoryChanged;
        _logs.HistoryRefreshed += OnHistoryRefreshed;
        _checkAgeTimer.Tick += OnCheckAgeTick;
        _checkAgeTimer.Start();
        _serviceHistoryTimer.Tick += OnServiceHistoryTick;
        _serviceHistoryTimer.Start();
        _ = RefreshAdministratorAccessAsync();
    }

    /// <summary>True when one Windows approval would fix something: access
    /// is off, or setup, removal or the latest changes are unfinished.</summary>
    private async Task<bool> AdministratorAccessActionableAsync()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { await AdministratorAccess.RefreshAsync(); }
        catch (Exception ex) { _log.Debug(ex, "Administrator access status unavailable"); return false; }
        return AdministratorAccess.HasAction && AdministratorAccess.StatusClass == "warn";
    }

    /// <summary>With administrator access on, the background service runs
    /// the settings it was last given. Apply saved changes to it right away,
    /// without a Windows prompt (U043, 31 October 2026): in the flow that
    /// made the change and after any other saved change. A run in progress
    /// delays it; Backspace tries again a minute later. Never turns access on.</summary>
    internal async Task ApproveChangesInFlowAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            await AdministratorAccess.ApplyPendingChangesAsync();
            await AdministratorAccess.RefreshAsync();
            if (AdministratorAccess.ChangesPending && AdministratorAccess.LastQuietResult == QuietApplyResult.Busy)
                OnUiThread(() => _applyRetry.Start());
            else OnUiThread(() => _applyRetry.Stop());
        }
        catch (Exception ex) { _log.Warning(ex, "Could not apply the latest changes"); }
        await RefreshAdministratorAccessAsync();
    }

    private async Task RefreshAdministratorAccessAsync()
    {
        if (!await AdministratorAccessActionableAsync()) { OnUiThread(() => NeedsAdministratorAccess = false); return; }
        OnUiThread(UpdateNeedsAdministratorAccess);
    }

    private void UpdateNeedsAdministratorAccess()
    {
        var wanted = ServiceLocator.Config.Current.Backups.Any(b => b.Enabled && b.UseVss);
        NeedsAdministratorAccess = OperatingSystem.IsWindows() && !AdministratorAccess.IsBusy
            && AdministratorAccess.HasAction && AdministratorAccess.StatusClass == "warn"
            && (wanted || !AdministratorAccess.CanOffer);
    }

    private void OnAdministratorAccessChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BackgroundBackupViewModel.StatusClass) or nameof(BackgroundBackupViewModel.HasAction)
            or nameof(BackgroundBackupViewModel.IsOn) or nameof(BackgroundBackupViewModel.IsBusy))
            OnUiThread(UpdateNeedsAdministratorAccess);
    }

    /// <summary>The sidebar badge's button: its flyout already explains, so
    /// this goes straight to Windows' prompt. Cancelling that prompt is not
    /// an error; a real failure shows its reason.</summary>
    [RelayCommand]
    private async Task AllowAdministratorAccess()
    {
        if (!await AdministratorAccessActionableAsync()) return;
        await AdministratorAccess.ApplyCommand.ExecuteAsync(null);
        if (!string.IsNullOrEmpty(AdministratorAccess.Error) && !AdministratorAccess.LastApplyCancelled && Owner() is { } owner)
            await new Views.DestructiveConfirmWindow("Administrator access was not turned on", AdministratorAccess.Error, "").ShowDialog(owner);
        await RefreshAdministratorAccessAsync();
    }

    private void OnServiceHistoryTick(object? sender, EventArgs e)
    {
        foreach (var card in Cards) _logs.RefreshBackgroundHistory(card.Id);
    }

    private void OnCheckAgeTick(object? sender, EventArgs e) { foreach (var card in Cards) card.RefreshCheckAge(); }
    private void OnCardCheckStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BackupCardViewModel.VerifyClass) or nameof(BackupCardViewModel.DisplayedRunStatus)
            or nameof(BackupCardViewModel.DisplayedRunEndUtc)) RecomputeHealth(ServiceLocator.Config.Current);
    }
    private void OnCheckHistoryChanged(RunRecord record) => OnUiThread(() =>
    {
        var card = Cards.FirstOrDefault(c => c.Id == record.BackupId);
        if (card is not null) card.ApplyVerifyState(card.Snapshot, record);
    });

    private void OnHistoryRefreshed(string backupId) => OnUiThread(() =>
    {
        var card = Cards.FirstOrDefault(c => c.Id == backupId);
        if (card is not null) card.ApplyVerifyState(card.Snapshot);
    });

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        _refreshDebouncer.Schedule();
        _ = RefreshAdministratorAccessAsync();
        // Any saved change (a pause, a setting) reaches the service too.
        if (OperatingSystem.IsWindows()) _applyDebouncer.Schedule();
    }

    private readonly UiThreadDebouncer _applyDebouncer;
    private readonly Avalonia.Threading.DispatcherTimer _applyRetry = new() { Interval = TimeSpan.FromMinutes(1) };
    private void OnApplyRetry(object? sender, EventArgs e) { _applyRetry.Stop(); _ = ApproveChangesInFlowAsync(); }
    private void OnTestRestoresChanged() => OnUiThread(() =>
    {
        foreach (var card in Cards)
            card.IsVerifying = ServiceLocator.ActiveRestores.Sessions.Any(s => s.IsTestRestore && s.BackupId == card.Id && s.IsRunning);
    });

    public void Dispose()
    {
        AdministratorAccess.PropertyChanged -= OnAdministratorAccessChanged;
        ServiceLocator.Config.Changed -= OnConfigChanged;
        ServiceLocator.Orchestrator.RunStarted -= OnRunStarted;
        ServiceLocator.Orchestrator.RunEnded -= OnRunEnded;
        ServiceLocator.Orchestrator.RunQueuedReasonChanged -= OnRunQueuedReasonChanged;
        ServiceLocator.Maintenance.MaintenanceStateChanged -= OnMaintenanceStateChanged;
        ServiceLocator.ActiveRestores.Changed -= OnTestRestoresChanged;
        _logs.RunAppended -= OnCheckHistoryChanged;
        _logs.HistoryRefreshed -= OnHistoryRefreshed;
        _checkAgeTimer.Stop();
        _checkAgeTimer.Tick -= OnCheckAgeTick;
        _serviceHistoryTimer.Stop();
        _serviceHistoryTimer.Tick -= OnServiceHistoryTick;
        try { _backgroundRunCts.Cancel(); } catch { }
        _backgroundRunCts.Dispose();
        _refreshDebouncer.Dispose();
        _applyDebouncer.Dispose();
        _applyRetry.Stop();
        _applyRetry.Tick -= OnApplyRetry;
        foreach (var c in Cards) { c.PropertyChanged -= OnCardCheckStateChanged; c.Dispose(); }
        Cards.Clear();
    }

    private void OnRunStarted(string backupId) =>
        OnUiThread(() =>
        {
            Cards.FirstOrDefault(c => c.Id == backupId)?.SetRunning();
            // A run starting may make peer cards' Run buttons need to
            // disable (shared-destination conflict). Recompute reasons
            // for every card so the user sees a tooltip-bearing disable
            // instead of the previous silent cross-card lockout.
            RecomputeRunConflicts();
        });

    /// <summary>
    /// On a run ending we mutate just the matching card and re-aggregate
    /// the health banner — full Refresh() would dispose+recreate every
    /// other card on the page, losing scroll position and resetting
    /// per-card subscriptions for backups that didn't even run. Health-
    /// banner aggregation uses each card's displayed result, including newer
    /// service history that is deliberately not written into desktop settings.
    /// </summary>
    private void OnRunEnded(string backupId, RunRecord record) =>
        OnUiThread(() =>
        {
            var card = Cards.FirstOrDefault(c => c.Id == backupId);
            if (card is not null)
            {
                // Pull a fresh Backup snapshot from config so the card's
                // header (LastRunLabel, NextRunLabel, etc.) is rebuilt
                // from the post-run state, then layer the run-specific
                // status from the RunRecord on top.
                var fresh = ServiceLocator.Config.Current.Backups
                    .FirstOrDefault(b => b.Id == backupId);
                if (fresh is not null) card.Apply(fresh);
                card.Apply(record);
            }
            RecomputeHealth(ServiceLocator.Config.Current);
            // A run ending may unblock peer cards whose Run buttons
            // were disabled by a destination collision. Re-derive
            // every card's RunBlockedReason from the now-shrunk set
            // of in-flight backups.
            RecomputeRunConflicts();
        });

    /// <summary>
    /// Routes the orchestrator's "this backup is queued waiting for
    /// destination X" signal to the matching card so its inline label
    /// flips to "Queued — …". Called with an empty reason once the
    /// gate is acquired so the label flips back to "Running…".
    /// </summary>
    private void OnRunQueuedReasonChanged(string backupId, string reason) =>
        OnUiThread(() => Cards.FirstOrDefault(c => c.Id == backupId)?.SetQueuedReason(reason));

    /// <summary>
    /// Mirror the global maintenance state into every card's
    /// RunBlockedReason so the Run Now / Test restore buttons grey
    /// out while an Erase / Wipe / Prune is in flight. The user
    /// reported: "ensure that NO backup or restore at all can run …
    /// while one of these sensitive operations is in progress."
    /// On release, RecomputeRunConflicts re-derives the per-card
    /// reasons from the current set of in-flight runs (which is
    /// empty by definition just after maintenance released — the
    /// coordinator cancelled them on entry).
    /// </summary>
    private void OnMaintenanceStateChanged(bool active, string reason) =>
        OnUiThread(() =>
        {
            if (active)
            {
                var msg = string.IsNullOrEmpty(reason)
                    ? "A storage-maintenance task is running. Please wait."
                    : $"A storage-maintenance task is running ({reason}). Please wait.";
                foreach (var c in Cards) c.RunBlockedReason = msg;
            }
            else
            {
                RecomputeRunConflicts();
            }
        });

    /// <summary>
    /// Diff-based refresh: keeps existing card instances for backups
    /// still present in the config, removes cards for backups deleted,
    /// inserts cards for new backups, and reorders to match the config's
    /// alphabetical order. The previous implementation tore down ALL
    /// cards on every config change — losing scroll position, resetting
    /// per-card subscriptions, and producing a visible flicker on a list
    /// of N backups when only one had changed.
    ///
    /// Card identity is by Backup.Id (not name) so a rename keeps the
    /// same instance (and its progress/run subscriptions) rather than
    /// disposing+recreating.
    /// </summary>
    public void Refresh()
    {
        var cfg = ServiceLocator.Config.Current;
        var desired = cfg.Backups.OrderBy(b => b.Name).ToList();
        var existing = Cards.ToDictionary(c => c.Id);

        // Step 1: remove cards whose backup is no longer in config.
        // Walk in reverse so RemoveAt indices stay valid.
        var keep = new HashSet<string>(desired.Select(b => b.Id));
        for (int i = Cards.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Cards[i].Id))
            {
                Cards[i].PropertyChanged -= OnCardCheckStateChanged;
                Cards[i].Dispose();
                Cards.RemoveAt(i);
            }
        }

        // Step 2: insert / update / move to match the desired order.
        for (int i = 0; i < desired.Count; i++)
        {
            var b = desired[i];
            if (existing.TryGetValue(b.Id, out var card))
            {
                // Update in place — preserves the card's event
                // subscriptions and its visual state (e.g. expanded
                // progress bars).
                card.Apply(b);
                var currentIndex = Cards.IndexOf(card);
                if (currentIndex != i) Cards.Move(currentIndex, i);
            }
            else
            {
                var fresh = new BackupCardViewModel(b, showActions: true, showProgress: true, logs: _logs);
                fresh.PropertyChanged += OnCardCheckStateChanged;
                fresh.EditDestinationRequested = id => EditDestinationByIdAsync(id);
                Cards.Insert(i, fresh);
            }
        }

        IsEmpty = Cards.Count == 0;
        OnPropertyChanged(nameof(BackupsCountLabel));
        OnPropertyChanged(nameof(BackupsCountWord));
        RecomputeHealth(cfg);
        RecomputeRunConflicts();
        OnTestRestoresChanged();
    }

    /// <summary>
    /// Clears every card's <see cref="BackupCardViewModel.RunBlockedReason"/>
    /// for shared-destination conflicts. Earlier this method gated
    /// the Run button when another backup was using the same storage,
    /// but the orchestrator now serialises shared-destination runs at
    /// the per-(DestId, Storage) named-semaphore layer, so a
    /// concurrent click is safe — it just queues. The user
    /// explicitly asked for this: "If a backup is running, it should
    /// be possible to Run now another one (right now it can be
    /// disabled if a common storage is used), simply queue it."
    ///
    /// Once the orchestrator picks up the queued run, it fires
    /// RunQueuedReasonChanged with a "waiting for «X»" message that
    /// flips the card's inline label to "Queued — …" — the user gets
    /// the queueing context EX POST instead of having the button
    /// pre-disabled. The maintenance gate (Erase / Wipe / Prune)
    /// still sets RunBlockedReason via OnMaintenanceStateChanged
    /// because those operations genuinely can't overlap with backups.
    /// </summary>
    private void RecomputeRunConflicts()
    {
        // Maintenance state, if active, has already stamped its own
        // RunBlockedReason via OnMaintenanceStateChanged — don't
        // clobber it here. Otherwise: a recompute happens on every
        // RunStarted / RunEnded, and we don't want to clear a
        // maintenance gate just because a backup happened to start
        // or end.
        if (ServiceLocator.Maintenance.IsActive) return;

        foreach (var c in Cards)
        {
            // Idle and no-maintenance: nothing blocks the Run button.
            // Shared-destination contention is now handled at the
            // orchestrator level (queueing), not the UI level.
            c.RunBlockedReason = "";
        }
    }

    private void RecomputeHealth(Models.AppConfig cfg)
    {
        if (cfg.Backups.Count == 0)
        {
            HasHealthBanner = false;
            HealthHeadline = "";
            HealthDetail = "";
            HealthClass = "idle";
            return;
        }

        var failed = new System.Collections.Generic.List<Backup>();
        var failedTestRestores = 0;
        var failedChecks = 0;
        var failedRuns = 0;
        var warned = new System.Collections.Generic.List<Backup>();
        var incompleteChecks = 0;
        var loadingChecks = 0;
        var runningCount = 0;
        var skipped = new System.Collections.Generic.List<Backup>();
        var neverRun = new System.Collections.Generic.List<Backup>();
        var okCount = 0;
        DateTime? mostRecent = null;
        foreach (var b in cfg.Backups)
        {
            var card = Cards.FirstOrDefault(c => c.Id == b.Id);
            var checkTone = card?.VerifyClass;
            var runStatus = card?.DisplayedRunStatus ?? b.LastRunStatus;
            if (runStatus == BackupRunStatus.Failed) failedRuns++;
            if (checkTone == "fail"
                && !(b.LastVerifyUtc is not null && b.LastVerifyPass == false))
            {
                failed.Add(b);
                failedChecks++;
            }
            else if (b.LastVerifyUtc is DateTime verifiedAt && b.LastVerifyPass == false)
            {
                // A later successful backup does not prove that recovery works.
                // Keep the warning until a Test restore succeeds. Cancellation
                // preserves the previous completed verification result.
                failed.Add(b);
                failedTestRestores++;
                if (mostRecent is null || verifiedAt > mostRecent) mostRecent = verifiedAt;
            }
            else if (checkTone == "stale" && runStatus != BackupRunStatus.Failed)
            {
                // A cancelled, overdue or unreadable check is not an all-clear.
                // Keep one affected entry per backup and preserve run failures
                // as the higher-severity result.
                warned.Add(b);
                incompleteChecks++;
            }
            else switch (runStatus)
            {
                case BackupRunStatus.Failed:  failed.Add(b);   break;
                case BackupRunStatus.Warning: warned.Add(b);   break;
                case BackupRunStatus.Skipped: skipped.Add(b);  break;
                case BackupRunStatus.Success:
                    if (checkTone == "loading") loadingChecks++; else okCount++;
                    break;
                // Running backups are mid-flight — the per-card live
                // progress UI is the primary signal, not the banner.
                // Counted separately so the all-ok branch below can
                // suppress its "All N backups are healthy" copy when
                // the only thing happening is a run in progress (the
                // user reported "1 backup hasn't run yet" on a
                // freshly-running backup; the all-ok branch then
                // misclassified the count as zero after we excluded
                // Running from neverRun).
                case BackupRunStatus.Running: runningCount++;  break;
                default: neverRun.Add(b);                      break;
            }
            if ((card?.DisplayedRunEndUtc ?? b.LastRunEndUtc) is DateTime t && (mostRecent is null || t > mostRecent)) mostRecent = t;
        }

        var ago = mostRecent is DateTime when ? HumanFormat.Ago(when) : "never";
        // Sub-line carries last-activity + an "ok N" tail when there
        // are healthy backups to mention. Stays calm + typographic;
        // the chips below carry the actionable info.
        var okTail = okCount > 0 ? $" · {okCount} ok" : "";

        AffectedBackups.Clear();
        // Headlines are count-based ONLY — they never name a specific
        // backup. The user explicitly asked: "include backup names
        // exactly once in this card, and as clickable pills". So the
        // headline says "1 backup skipped on its last run" while the
        // pill row below carries the actual «name» as the single
        // place the user clicks to jump.
        if (failed.Count > 0)
        {
            HealthClass = "fail";
            if (failedChecks > 0)
            {
                HealthHeadline = failed.Count == 1 ? "1 backup needs attention" : $"{failed.Count} backups need attention";
                HealthDetail = "A saved check is still failing. Open an affected backup's Check history for the result.";
            }
            else if (failedTestRestores > 0)
            {
                HealthHeadline = failedRuns == 0
                    ? failed.Count == 1 ? "Test restore failed for 1 backup" : $"Test restore failed for {failed.Count} backups"
                    : failed.Count == 1 ? "1 backup needs attention" : $"{failed.Count} backups need attention";
                HealthDetail = failedRuns == 0
                    ? $"Open an affected backup to review its test result. Last activity: {ago}{okTail}"
                    : $"A backup run or Test restore failed. Last activity: {ago}{okTail}";
            }
            else
            {
                HealthHeadline = failed.Count == 1
                    ? "1 backup failed on its last run and needs attention"
                    : $"{failed.Count} backups failed on their last run and need attention";
                HealthDetail = $"Last activity: {ago}{okTail}";
            }
            foreach (var b in failed)
                AffectedBackups.Add(new HealthBackupReference(b.Id, b.Name, "fail", JumpToBackupCommand));
            foreach (var b in warned)  AffectedBackups.Add(new HealthBackupReference(b.Id, b.Name, "warn", JumpToBackupCommand));
            foreach (var b in skipped) AffectedBackups.Add(new HealthBackupReference(b.Id, b.Name, "skipped", JumpToBackupCommand));
        }
        else if (warned.Count > 0 || skipped.Count > 0)
        {
            HealthClass = "warn";
            if (incompleteChecks > 0)
            {
                var affected = warned.Count + skipped.Count;
                HealthHeadline = affected == 1 ? "1 backup needs attention" : $"{affected} backups need attention";
            }
            else if (warned.Count > 0 && skipped.Count > 0)
            {
                var noun = (warned.Count + skipped.Count) == 1 ? "backup" : "backups";
                HealthHeadline = $"{warned.Count + skipped.Count} {noun} need attention";
            }
            else if (warned.Count > 0)
            {
                HealthHeadline = warned.Count == 1
                    ? "1 backup finished with warnings"
                    : $"{warned.Count} backups finished with warnings";
            }
            else
            {
                HealthHeadline = skipped.Count == 1
                    ? "1 backup was skipped on its last run"
                    : $"{skipped.Count} backups were skipped on their last run";
            }
            HealthDetail = incompleteChecks > 0
                ? $"A check is incomplete, overdue, or unavailable. Review the affected backup's check status below.{okTail}"
                : $"Last activity: {ago}{okTail}";
            foreach (var b in warned)  AffectedBackups.Add(new HealthBackupReference(b.Id, b.Name, "warn", JumpToBackupCommand));
            foreach (var b in skipped) AffectedBackups.Add(new HealthBackupReference(b.Id, b.Name, "skipped", JumpToBackupCommand));
        }
        else if (neverRun.Count > 0)
        {
            HealthClass = "idle";
            HealthHeadline = neverRun.Count == 1
                ? "1 backup hasn't run yet"
                : $"{neverRun.Count} backups haven't run yet";
            HealthDetail = $"Last activity: {ago}{okTail}";
            foreach (var b in neverRun) AffectedBackups.Add(new HealthBackupReference(b.Id, b.Name, "idle", JumpToBackupCommand));
        }
        else if (loadingChecks > 0)
        {
            HealthClass = "idle";
            HealthHeadline = "Reading backup health…";
            HealthDetail = "Checking saved results before showing an all-clear.";
        }
        else if (okCount > 0)
        {
            HealthClass = "ok";
            HealthHeadline = okCount == 1
                ? "Everything looks good"
                : $"All {okCount} backups are healthy";
            HealthDetail = $"Last successful run: {ago}";
            // No chips in the all-green case — nothing needs attention.
        }
        else
        {
            // Only running backups (no failed/warned/skipped/never-
            // run/ok). The card's own live progress UI is the
            // signal — suppress the banner so we don't render
            // "All 0 backups are healthy" or similar nonsense.
            // runningCount is tracked but only used here to
            // distinguish "config has only running backups" from
            // "config has nothing to report".
            _ = runningCount;
            HealthHeadline = "";
            HealthDetail = "";
            HealthClass = "idle";
        }
        HasHealthBanner = !string.IsNullOrEmpty(HealthHeadline);
    }

    /// <summary>Raised by a click on an AffectedBackups chip — the view
    /// listens and scrolls the matching BackupCard into view. Routed
    /// through an event (not a command) so the view can do scrolling /
    /// flash-highlight which is purely a UI concern.</summary>
    public event Action<string>? JumpToBackupRequested;

    [RelayCommand]
    private void JumpToBackup(string backupId)
    {
        if (string.IsNullOrEmpty(backupId)) return;
        JumpToBackupRequested?.Invoke(backupId);
    }

    // HumanAgo promoted to HumanFormat.Ago — see BackupCardViewModel.

    [RelayCommand]
    private async Task AddNew()
    {
        var owner = Owner();
        if (owner is null) return;

        // Unified creation flow: opens BackupCreationWindow (Easy mode by
        // default) for new users; the user can flip to Advanced from the
        // window's top-right toggle and we'll preserve the partial state
        // across modes. For first-backup empty-state, the title/copy
        // adjusts via the CreationContext.IsFirstBackup flag.
        var dlg = new BackupCreationWindow(isFirstBackup: Cards.Count == 0);
        var result = await dlg.ShowDialog<Backup?>(owner);
        if (result is not null)
        {
            // Upsert: the Easy-mode wizard persists itself before
            // returning (so the orchestrator's RunBackupAsync has a
            // canonical config entry to read). For Advanced mode, this
            // is the first time the backup hits config. FindIndex+
            // replace handles both with one path.
            ServiceLocator.Config.SaveBackupEdit(result);
            // With access already on, approve before registering the
            // schedule, so it isn't reported as waiting for approval (U043).
            await ApproveChangesInFlowAsync();
            ApplyScheduleIfPossible(result);
            _log.Information("Backup created: name={Name} sources={SourceCount} targets={TargetCount} enabled={Enabled} runAfterSave={RunAfterSave}",
                result.Name, result.SourcePaths.Count, result.Targets.Count, result.Enabled, dlg.RunAfterSave);
            // The person chose about administrator access while creating the
            // backup; turn it on now that the backup is saved, before the
            // first run, so that run can include files in use.
            var accessError = dlg.AdministratorAccessError;
            if (dlg.AdministratorAccessToApply is { } access) accessError ??= await access.ApplyIfChosenAsync(result.UseVss);
            if (accessError is not null)
                await new DestructiveConfirmWindow("Administrator access was not turned on",
                    accessError + " Your backup was saved. Files that are open may be skipped until you allow access from Your backups.", "").ShowDialog(owner);
            await RefreshAdministratorAccessAsync();
            if (dlg.RunAfterSave) FireBackgroundRun(result);
        }
        else
        {
            _log.Debug("Backup creation cancelled");
        }
    }

    [RelayCommand]
    private async Task Edit(BackupCardViewModel card)
    {
        if (card is null) return;
        var owner = Owner();
        if (owner is null) return;
        var b = card.Snapshot;

        // Reuse the same unified creation window for editing — it
        // detects "edit" mode by being constructed with a non-null
        // existing backup and opens in the mode the user last used for
        // that backup (Backup.LastEditMode).
        var dlg = new BackupCreationWindow(b);
        var result = await dlg.ShowDialog<Backup?>(owner);
        if (result is not null)
        {
            ServiceLocator.Config.SaveBackupEdit(result);
            await ApproveChangesInFlowAsync();
            ApplyScheduleIfPossible(result);
            _log.Information("Backup edited: name={Name}", result.Name);
            if (dlg.RunAfterSave) FireBackgroundRun(result);
        }
    }

    [RelayCommand]
    private async Task Delete(BackupCardViewModel card)
    {
        if (card is null) return;
        var owner = Owner();
        if (owner is null) return;
        var b = card.Snapshot;
        var ok = await RemovalConfirmation(b).ShowDialog<bool>(owner);
        if (!ok) return;

        BackupRemoval.Outcome? outcome = null;
        try
        {
            await ErasingProgressWindow.RunAsync(owner, $"Removing «{b.Name}»",
                async (progress, percent, ct) => outcome = await ServiceLocator.Removals.RemoveAsync(b, progress, percent, ct));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Removing {Name} did not finish", b.Name);
            await DestructiveConfirmWindow.ShowInfoAsync(owner, "The backup could not be removed completely",
                ex.Message + " Anything not yet deleted is retried while Backspace is open.");
            return;
        }
        _log.Information("Backup removed: {Name} erased={Erased} deferred={Deferred}", b.Name,
            outcome?.Erased.Count ?? 0, outcome?.Deferred.Count ?? 0);
        await ApproveChangesInFlowAsync();
        if (outcome is { Deferred.Count: > 0 })
        {
            var (title, detail) = DeferredRemovalNotice(b.Name, outcome.Deferred);
            await DestructiveConfirmWindow.ShowInfoAsync(owner, title, detail);
        }
    }

    /// <summary>Removing a backup deletes what it stored, so nothing is left
    /// behind that Backspace can no longer show or clean up (U029).</summary>
    internal static DestructiveConfirmWindow RemovalConfirmation(Backup b)
    {
        var places = string.Join(", ", b.Targets
            .Select(t => ServiceLocator.Config.Current.Destinations.FirstOrDefault(d => d.Id == t.DestinationId)?.Name)
            .Where(n => n is not null).Distinct().Select(n => $"«{n}»"));
        return new DestructiveConfirmWindow(
            title: $"Remove «{b.Name}»?",
            subtitle:
                $"It stops running, and every copy it stored{(places.Length > 0 ? " at " + places : "")} is deleted. " +
                "You won't be able to restore files from it afterwards. Other backups are not affected. " +
                "A destination that isn't connected is cleaned up the next time Backspace can reach it.",
            confirmString: "OK",
            caseInsensitive: true);
    }

    internal static (string Title, string Detail) DeferredRemovalNotice(string name, IReadOnlyList<string> deferred) =>
        ($"«{name}» was removed",
         $"Its copies at {string.Join(", ", deferred.Select(n => $"«{n}»"))} couldn't be reached yet. " +
         "Backspace deletes them automatically the next time it can reach them while it's open.");

    /// <summary>
    /// Run-now is shared by every card via the parent VM. Async
    /// <see cref="RelayCommandAttribute"/> defaults to
    /// <c>AllowConcurrentExecutions = false</c>, which means while ONE
    /// card's run is awaiting inside this command, the framework
    /// disables the bound button on every OTHER card too — without a
    /// tooltip, without a reason. The user just sees a dead button.
    /// Setting concurrent-allowed restores per-card independence: each
    /// invocation runs its own task, the orchestrator's per-backup
    /// semaphore (<see cref="BackupOrchestrator.RunBackupAsync"/>) still
    /// protects against double-running the SAME backup, and the
    /// destination-conflict guard below surfaces a readable reason
    /// rather than a silent disable when two backups would collide on a
    /// shared destination.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunNow(BackupCardViewModel card)
    {
        if (card is null) return;
        card.SetRunning();
        try
        {
            // Null: the background service ran it and the card follows its activity.
            if (await DesktopBackupRun.RunAsync(card.Snapshot, CancellationToken.None) is { } record) card.Apply(record);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Run-now threw for {Name}", card.Snapshot.Name);
        }
    }

    /// <summary>Backups that already have a stop-watchdog armed. Guards
    /// against duplicate Stop clicks stacking N watchdog tasks (each
    /// would race on TryRemove inside ForceFinaliseRun; the loser arms
    /// fire harmlessly but waste CPU + log noise + risk double-
    /// AppendRun if the registry race window is wider than expected).
    /// Cleared inside the watchdog task so a SECOND legitimate Stop
    /// after a finished+restarted run can re-arm.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte>
        _stopWatchdogArmed = new();

    [RelayCommand]
    private async Task CancelRun(BackupCardViewModel card)
    {
        if (card is null) return;
#if WINDOWS
        if (card.BackgroundRunId is string backgroundRun && ServiceLocator.BackgroundActivity is { } activity)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await activity.CancelAsync(backgroundRun, timeout.Token);
                card.LastRunLabel = "Stopping background backup…";
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "The background stop request could not be confirmed");
                card.LastRunLabel = "The stop request could not be confirmed. The backup may still be running; try Stop again.";
            }
            return;
        }
#endif
        // Stamp the cancel reason so the run's final Summary reads
        // "Manually skipped · skipped in 1m 12s" instead of the
        // previous generic "Skipped in 1m 12s" / "drive unplugged or
        // network gone" line that was wrong for the manual case.
        ServiceLocator.Orchestrator.CancelRun(card.Id, "Manually skipped");
        // Hard-cancel safety net (user-reported 2026-04-29: a run that
        // failed silently in init left the card stuck on "Running…" with
        // Stop doing nothing for 7+ minutes). The cooperative cancel
        // path above signals the CTS, but if the run thread is wedged
        // somewhere non-cancellable (a stuck the engine binary wait, a
        // disk-IO blocked finally) the natural finally never fires and
        // the registry stays populated forever. After 8 seconds, force-
        // finalise: the orchestrator synthesises a Skipped RunRecord,
        // fires RunEnded, and clears the registry — the card flips back
        // to idle and the user can try again.
        // Only one watchdog per backup at a time — repeated Stop clicks
        // shouldn't stack tasks.
        var backupId = card.Id;
        if (!_stopWatchdogArmed.TryAdd(backupId, 1)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                try
                {
                    await ServiceLocator.Orchestrator.WaitForRunCompletionAsync(
                        backupId, TimeSpan.FromSeconds(8), CancellationToken.None);
                }
                catch { /* swallow — the force path below handles "still running" */ }
                if (ServiceLocator.Orchestrator.IsRunning(backupId))
                {
                    ServiceLocator.Orchestrator.ForceFinaliseRun(
                        backupId,
                        "You asked to stop this backup, but its background operation has not confirmed that it stopped. " +
                        "Backspace is keeping the destination reserved while it finishes stopping. " +
                        "If it remains stuck, save the run details and quit Backspace before trying again.");
                }
            }
            finally
            {
                _stopWatchdogArmed.TryRemove(backupId, out _);
            }
        });
    }

    [RelayCommand]
    private Task ShowCheckHistory(BackupCardViewModel card) => ShowHistoryAsync(card, true);
    [RelayCommand]
    private Task ShowBackupHistory(BackupCardViewModel card) => ShowHistoryAsync(card, false);
    [RelayCommand]
    private Task ShowActivityHistory(BackupCardViewModel card) => ShowHistoryAsync(card, false, true);
    private async Task ShowHistoryAsync(BackupCardViewModel card, bool checks, bool all = false)
    {
        var owner = TestRestoreOwner?.Invoke() ?? Owner();
        if (owner is null || card is null) return;
        try
        {
            var runs = await Task.Run(() => _logs.LoadTimelineByBackupId(card.Id));
            var history = new BackupHistoryViewModel(card.Snapshot, runs, checks);
            if (all) history.SelectedFilter = "All activity";
            void Updated(string id)
            {
                if (id == card.Id) OnUiThread(() => history.UpdateRuns(_logs.LoadTimelineByBackupId(id)));
            }
            _logs.HistoryRefreshed += Updated;
            try
            {
                // Close the race between the first disk read and subscription.
                history.UpdateRuns(_logs.LoadTimelineByBackupId(card.Id));
                await new BackupHistoryWindow { DataContext = history }.ShowDialog(owner);
            }
            finally { _logs.HistoryRefreshed -= Updated; }
        }
        catch (Exception ex) { await DestructiveConfirmWindow.ShowInfoAsync(owner, "Could not open history", ex.Message); }
    }

    [RelayCommand]
    private async Task TestRestore(BackupCardViewModel card)
    {
        if (card is null) return;
        if (ServiceLocator.TestRestores.FindRunning(card.Id) is { } running)
        {
            ServiceLocator.ActiveRestores.Open(running);
            return;
        }
        var owner = TestRestoreOwner?.Invoke() ?? Owner();
        if (owner is null) return;
        var options = await new TestRestoreOptionsWindow(card.Snapshot.Name, card.Snapshot.RestoreCheckOptions).ShowDialog<TestRestoreOptions?>(owner);
        if (options is null) return;
        try
        {
            var backup = ServiceLocator.Config.Current.Backups.FirstOrDefault(b => b.Id == card.Id);
            if (backup is null) return;
            if (ServiceLocator.Orchestrator.IsRunning(backup.Id))
                throw new InvalidOperationException("This backup started running while the options were open. Let it finish before testing the restore.");
            var session = ServiceLocator.TestRestores.Start(backup, options);
            card.IsVerifying = true;
            ServiceLocator.ActiveRestores.Open(session);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Test-restore threw for {Name}", card.Snapshot.Name);
            await DestructiveConfirmWindow.ShowInfoAsync(owner, "Could not start test restore", ex.Message);
        }
    }

    /// <summary>
    /// Per-backup "Restore files" action. Routes the user to the
    /// existing Restore wizard with this backup pre-selected. Replaces
    /// the now-removed top-level "Restore files" sidebar entry.
    /// </summary>
    [RelayCommand]
    private void RestoreFiles(BackupCardViewModel card)
    {
        if (card is null) return;
        // Navigate to the (still-existing) Restore page; the
        // RestoreViewModel auto-selects the first backup, but for an
        // explicit per-backup launch we want THIS backup pre-selected
        // regardless. Resolve the live Backup model and push it onto
        // the shared restore VM.
        var b = ServiceLocator.Config.Current.Backups.FirstOrDefault(x => x.Id == card.Id);
        if (b is null) return;
        // Navigate FIRST so MainWindowViewModel lazy-constructs the
        // RestoreViewModel and the new instance subscribes to
        // RestorePreselectRequested. Only THEN raise the preselect
        // event — otherwise the event fires into a dead AppNavigator
        // (no subscribers yet) and the wizard lands on step 1 with no
        // backup picked, defeating the whole "skip step 1" UX. Nav also
        // fires RestartWizard on the VM, which clears any prior
        // SelectedBackup; the preselect that follows immediately re-
        // populates it and auto-advances past PickBackup.
        AppNavigator.Go(NavItem.Restore);
        AppNavigator.PreselectRestoreBackup(b);
    }

    /// <summary>
    /// Click-handler for a destination pill on a BackupCard. Opens the
    /// Destination editor for the given id; persists changes via
    /// upsert (the editor returns a Destination instance, possibly
    /// with mutations on the working copy).
    /// </summary>
    private async Task EditDestinationByIdAsync(string destinationId)
    {
        var owner = Owner();
        if (owner is null) return;
        var dest = ServiceLocator.Config.Current.Destinations.FirstOrDefault(d => d.Id == destinationId);
        if (dest is null) return;
        var dlg = new DestinationEditorWindow(dest, isNew: false);
        var saved = await dlg.ShowDialog<Destination?>(owner);
        if (saved is null) return;
        ServiceLocator.Config.Update(cfg =>
        {
            var idx = cfg.Destinations.FindIndex(x => x.Id == saved.Id);
            if (idx >= 0) cfg.Destinations[idx] = saved;
        });
    }

    private static void ApplyScheduleIfPossible(Backup b)
    {
        try
        {
            var actualExe = Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            ServiceLocator.Scheduler.UpsertTask(b, actualExe);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Scheduler upsert failed for {Name}", b.Name);
        }
    }

    private void FireBackgroundRun(Backup b)
    {
        // Capture the token up-front: if Dispose runs before Task.Run
        // schedules, we want this attempt to short-circuit, not crash on
        // a disposed CTS.
        CancellationToken ct;
        try { ct = _backgroundRunCts.Token; }
        catch (ObjectDisposedException) { return; }
        _ = Task.Run(async () =>
        {
            try { await DesktopBackupRun.RunAsync(b, ct); }
            catch (OperationCanceledException) { /* VM tore down mid-run */ }
            catch (Exception ex) { _log.Error(ex, "Background run-after-save threw for {Name}", b.Name); }
        });
    }

    private static Avalonia.Controls.Window? Owner()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l)
            return l.MainWindow;
        return null;
    }
}
