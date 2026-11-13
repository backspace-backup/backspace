using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Backspace.Models;
using Backspace.Services;

namespace Backspace.ViewModels;

/// <summary>
/// Standardised "what you need to know about one backup at a glance"
/// view-model. Powers both the unified Backups view (status + actions +
/// live progress) and the Restore wizard's pick-a-backup step (no
/// progress, no actions).
///
/// One VM per backup row. Subscribes to <see cref="BackupProgressService.Updated"/>
/// while a run is in flight and projects per-source progress into the
/// observable bag the UI binds to. Disposed eagerly when the parent
/// list refreshes — see <see cref="Dispose"/>.
/// </summary>
public sealed partial class BackupCardViewModel : ObservableObject, IDisposable
{
    private readonly bool _showActions;
    private readonly bool _showProgress;
    private readonly Action<string>? _progressHandler;
    private readonly Services.WebDav.ISnapshotMountRegistry? _mountRegistry;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MountActionLabel))]
    [NotifyPropertyChangedFor(nameof(MountTooltip))]
    [NotifyPropertyChangedFor(nameof(CanMountOrUnmount))]
    [NotifyPropertyChangedFor(nameof(MountedLine))]
    [NotifyPropertyChangedFor(nameof(ShowMountLine))]
    private string? _mountedDrive;

    /// <summary>What the mounted drive shows ("7 Nov 2026 14:30", "3 revisions", U162).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MountedLine))]
    private string? _mountedDescription;

    /// <summary>What the mount in progress is doing, from the click until the
    /// drive is ready (U158); null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMounting))]
    [NotifyPropertyChangedFor(nameof(MountActionLabel))]
    [NotifyPropertyChangedFor(nameof(MountTooltip))]
    [NotifyPropertyChangedFor(nameof(CanMountOrUnmount))]
    [NotifyPropertyChangedFor(nameof(ShowMountLine))]
    private string? _mountProgress;

    /// <summary>Why the last mount failed, shown on the card until the next
    /// attempt or until dismissed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMountLine))]
    private string? _mountError;

    /// <summary>How far the mount in progress has got, 0-100, when it can be
    /// estimated from the revisions' file counts (U166); null keeps the bar
    /// moving without a percentage.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMountPercent))]
    [NotifyPropertyChangedFor(nameof(MountPercentValue))]
    [NotifyPropertyChangedFor(nameof(MountPercentLabel))]
    private int? _mountPercent;

    public bool HasMountPercent => MountPercent is not null;
    public double MountPercentValue => MountPercent ?? 0;
    public string MountPercentLabel => MountPercent is { } p ? $"{p}%" : "";

    public bool IsMounting => MountProgress is not null;
    public string MountActionLabel => IsMounting ? "Mounting…" : MountedDrive is null ? "Mount" : "Unmount";
    public string MountTooltip => IsMounting ? "Mounting the revisions you chose. The drive opens in Explorer when it is ready."
        : MountedDrive is null
        ? HasRestorableRun ? "Choose a revision, then browse it as a read-only drive in Explorer." : NothingToRestoreTooltip
        : $"Unmount {MountedDrive}. Your snapshots stay safely in the backup.";
    /// <summary>Mount needs something stored; Unmount is always possible,
    /// even while a backup runs (it only adds a new snapshot, U159).</summary>
    public bool CanMountOrUnmount => !IsMounting && (MountedDrive is not null || HasRestorableRun);
    public string MountedLine => MountedDrive is null ? "" : $"Mounted as {MountedDrive}" + (MountedDescription is { } shows ? $" ({shows})" : "") + ", read-only. Backspace unmounts it when you quit.";
    public bool ShowMountLine => IsMounting || MountedDrive is not null || MountError is not null;

    public string Id { get; }
    public Backup Snapshot { get; private set; }
    private readonly LogStore _logs;
#if WINDOWS
    private readonly Services.Platform.Windows.BackgroundServiceActivity? _backgroundActivity;
    internal string? BackgroundRunId { get; private set; }
    private string? _lastBackgroundResult;
#endif
    [ObservableProperty] private BackupRunStatus _displayedRunStatus;
    [ObservableProperty] private DateTime? _displayedRunEndUtc;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _statusClass = "idle";
    [ObservableProperty] private string _statusLabel = "Idle";
    [ObservableProperty] private string _lastRunLabel = "Never run";
    [ObservableProperty] private string _nextRunLabel = "";
    [ObservableProperty] private string _verifyLabel = "No checks recorded";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HealthTileClass))] private string _verifyClass = "never";
    /// <summary>The Backup health tile's tone: running while a check runs (U197).</summary>
    public string HealthTileClass => ShowsHealthActivity ? "running" : VerifyClass;
    [ObservableProperty] private string _verifyDetails = "The first check will appear here.";
    [ObservableProperty] private string _checkScopeLabel = "";
    private IReadOnlyList<BackupCheckEntry> _checkEntries = Array.Empty<BackupCheckEntry>();
    private IReadOnlyList<RunRecord> _checkRuns = Array.Empty<RunRecord>();
    private bool _checkHistoryLoaded;
    private int _checkGeneration;
    private bool _checkHistoryReadFailed;
    /// <summary>
    /// Tone class for the NEXT-row badge. "scheduled" when the backup
    /// has auto-run enabled (accent blue clock); "manual" when only
    /// run-on-demand (gray pause). The card's metadata strip uses this
    /// in lockstep with StatusClass / VerifyClass to drive an icon-led
    /// row design where the user can read "ok / fail / scheduled" at a
    /// glance without parsing the value text.
    /// </summary>
    [ObservableProperty] private string _nextRunClass = "scheduled";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBackingUp))] private bool _isRunning;
    /// <summary>What the running operation is: "backup", "check" or
    /// "prune" (a check run through the background service, U183).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBackingUp), nameof(RunningLabel))] private string _runningOperation = "backup";
    /// <summary>A backup is running: only then do the progress bar and the
    /// sources' percentages mean something (a check reads the storage).</summary>
    public bool IsBackingUp => IsRunning && RunningOperation == "backup";
    /// <summary>Which check runs now ("Sample test restore (2 of 2)"), U183.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RunningLabel))] private string _runningActivity = "";
    /// <summary>The running check's step ("Reading the data sample · 12 of 64
    /// packs"), its destination, and its share done, -1 unknown (U198).</summary>
    [ObservableProperty] private string _runningActivityStep = "";
    [ObservableProperty] private string _runningActivityWhere = "";
    [ObservableProperty] private double _runningActivityPercent = -1;
    partial void OnRunningOperationChanged(string value) => NotifyCheckState();
    partial void OnRunningActivityChanged(string value) => NotifyCheckState();
    partial void OnRunningActivityStepChanged(string value) => NotifyCheckState();
    partial void OnRunningActivityWhereChanged(string value) => NotifyCheckState();
    partial void OnRunningActivityPercentChanged(double value) => NotifyCheckState();

    /// <summary>A check runs (U197): it shows under Backup health, with its
    /// progress, and Last backup keeps the last backup's result.</summary>
    public bool IsChecking => IsRunning && RunningOperation == "check";
    /// <summary>Last backup says Running: a backup or a cleanup runs, not a check.</summary>
    public bool ShowsRunInLastBackup => IsActuallyRunning && !IsChecking;
    public bool ShowsCheckProgress => IsChecking && !IsQueued || ShowsTestRestore;
    /// <summary>A test restore you started (not a scheduled check) shows like
    /// a check: under Backup health, with its bar and step (U205).</summary>
    public bool ShowsTestRestore => IsVerifying && !IsChecking && _testSession is not null;
    /// <summary>Backup health shows something running: a check or a test restore.</summary>
    public bool ShowsHealthActivity => IsChecking || ShowsTestRestore;
    /// <summary>Stop shows for a run, and for a test restore, which is
    /// stopped here now that Restores doesn't list it (U205).</summary>
    public bool ShowsStop => IsRunning || ShowsTestRestore;

    /// <summary>"Data check (1 of 2) · 7 min" (U197, U221): the check's sizes
    /// are in the hover.</summary>
    public string CheckRunLabel
    {
        get
        {
            if (ShowsTestRestore && _testSession is { } test)
                return "Restore check" + (test.IsPaused ? " · paused" : "") + " · " + Elapsed(UtcNow() - test.StartedUtc);
            // Short, the reason in the hover, as for Last backup (U203, owner,
            // 12 November 2026: "it should show the reason on hover").
            if (IsQueued) return (RunningActivity.Length > 0 ? ShortCheckName(RunningActivity) : "Check") + " · queued";
            // The check's name alone beside the turning icon: "Data check (1 of
            // 2) · 7 min", never "Checking · Data check" (U197: "check twice").
            var what = RunningActivity.Length > 0 ? ShortCheckName(RunningActivity) : "Checking";
            return RunStartedUtc is { } started ? what + " · " + Elapsed(UtcNow() - started - QueuedTime()) : what;
        }
    }

    /// <summary>"Data check (1 GiB sample) (1 of 2)" → "Data check (1 of 2)":
    /// the card line names the check; its size is in the hover. "(all data)"
    /// stays: it is what kind of data check it is (U221).</summary>
    internal static string ShortCheckName(string activity)
        => System.Text.RegularExpressions.Regex.Replace(activity, @" \((?:[0-9.,]+ ?[KMGT]i?B[^)]*|[0-9.,]+%[^)]*)\)", "");

    /// <summary>The step under the check's bar; with several destinations,
    /// which one.</summary>
    public string CheckStepLine => RunningActivityWhere.Contains(" of ", StringComparison.Ordinal) && RunningActivityStep.Length > 0
        ? RunningActivityWhere + " · " + RunningActivityStep
        : RunningActivityStep;
    public bool CheckProgressKnown => RunningActivityPercent >= 0;
    public double CheckPercent => Math.Max(0, RunningActivityPercent);

    /// <summary>Everything about the running check, on hover (U198).</summary>
    public string CheckTooltip
    {
        get
        {
            var lines = new List<string> { RunningActivity.Length > 0 ? RunningActivity : "A check" };
            if (IsQueued && QueuedReason.Length > 0) lines.Add("Queued: " + QueuedReason);
            if (RunningActivityWhere.Length > 0) lines.Add("On " + RunningActivityWhere);
            if (RunningActivityStep.Length > 0) lines.Add(RunningActivityStep);
            if (CheckProgressKnown) lines.Add($"About {RunningActivityPercent.ToString("0", System.Globalization.CultureInfo.CurrentCulture)}% of this check done");
            if (ShowsTestRestore && _testSession is { } test) lines.Add("Running for " + Elapsed(UtcNow() - test.StartedUtc));
            else if (RunStartedUtc is { } started) lines.Add("Running for " + Elapsed(UtcNow() - started - QueuedTime()));
            lines.Add("");
            lines.Add(CheckExplanation(RunningActivity));
            if (VerifyLabel.Length > 0) lines.Add("Latest result: " + VerifyLabel);
            return string.Join("\n", lines);
        }
    }

    internal static string CheckExplanation(string activity) =>
        activity.StartsWith("Structure check", StringComparison.Ordinal)
            ? "Is the backup complete? Reads its records and confirms every piece of data they point to is there. Reads no file contents."
        : activity.StartsWith("Data check (all data)", StringComparison.Ordinal)
            ? "Is the stored data intact? Checks the structure, then downloads all the stored data and checks every piece."
        : activity.StartsWith("Data check", StringComparison.Ordinal)
            ? "Is the stored data intact? Checks the structure, then downloads a sample of the stored data and checks every piece of it."
        : activity.StartsWith("Restore check", StringComparison.Ordinal)
            ? "Can your files come back? Restores some files into a temporary folder, as a restore would, and checks them. Your original files are never changed."
        : "Checks this backup's stored data.";

    private void NotifyCheckState()
    {
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(ShowsTestRestore));
        OnPropertyChanged(nameof(ShowsHealthActivity));
        OnPropertyChanged(nameof(ShowsStop));
        OnPropertyChanged(nameof(HealthTileClass));
        OnPropertyChanged(nameof(ShowsRunInLastBackup));
        OnPropertyChanged(nameof(ShowsCheckProgress));
        OnPropertyChanged(nameof(CheckRunLabel));
        OnPropertyChanged(nameof(CheckStepLine));
        OnPropertyChanged(nameof(CheckProgressKnown));
        OnPropertyChanged(nameof(CheckPercent));
        OnPropertyChanged(nameof(CheckTooltip));
    }
    [ObservableProperty] private bool _isVerifying;
    /// <summary>True while the run is registered (IsRunning) but
    /// parked on the orchestrator's per-destination queue waiting
    /// for a sibling backup to free up the storage. The card's
    /// top-right indicator swaps "Running…" for "Queued…" when this
    /// is true so the user can see at a glance which of the two
    /// states the backup is actually in. Set by
    /// <see cref="SetQueuedReason"/>; cleared by it once the gate
    /// releases.</summary>
    [ObservableProperty] private bool _isQueued;
    [ObservableProperty] private double _overallPercent;
    /// <summary>
    /// Phase chip text shown next to the Overall progress bar while
    /// running — "Backing up", "Pruning", "Checking", etc.
    /// Without this, a backup that finished uploading but was still
    /// in its post-pass prune/check phase looked stuck at 100% with
    /// no explanation. Empty when idle, populated by
    /// <see cref="ApplyProgressSnapshot"/>.
    /// </summary>
    [ObservableProperty] private string _currentPhaseLabel = "";
    /// <summary>What the running backup is doing, shown beside its progress
    /// bar and in its tooltip: bytes and files done of the totals found so far,
    /// and the speed (U114, 3 November 2026).</summary>
    [ObservableProperty] private string _progressDetail = "";
    public string ProgressTooltip => OverallPercent.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + "% done"
        + (ProgressDetail.Length > 0 ? "\n" + ProgressDetail.Replace(" · ", "\n") : "")
        // Why the data uploaded is much less than the changed files backed up (U155).
        + (ProgressDetail.Contains("uploaded", StringComparison.Ordinal) || ProgressDetail.Contains("changed files", StringComparison.Ordinal)
            ? "\n\n" + UploadNote : "");

    internal const string UploadNote = "Only data the backup doesn't already hold is uploaded, compressed, so it is usually much less than the changed size.";
    partial void OnOverallPercentChanged(double value) => OnPropertyChanged(nameof(ProgressTooltip));
    partial void OnProgressDetailChanged(string value) { OnPropertyChanged(nameof(ProgressTooltip)); OnPropertyChanged(nameof(ProgressCardLine)); }

    /// <summary>The card's short form of <see cref="ProgressDetail"/> (U193,
    /// 11 November 2026): the line had grown to two lines with the hover
    /// saying the same. The card keeps the files checked, the changes backed
    /// up and the time left; the hover keeps everything, the source's size,
    /// the data uploaded and its speed included.</summary>
    public string ProgressCardLine => CardLine(ProgressDetail);

    internal static string CardLine(string detail)
    {
        var parts = new List<string>();
        foreach (var part in (detail ?? "").Split(" · "))
        {
            // The percentage is beside the bar, the action above it.
            if (part is "Backing up" || part.Contains("new data uploaded", StringComparison.Ordinal)
                || (part.EndsWith('%') && double.TryParse(part[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
                continue;
            var text = part;
            // "122,087 of ~428,634 files checked (~164 GB)": the size is in the hover.
            if (text.Contains("files checked (", StringComparison.Ordinal) && text.EndsWith(')'))
                text = text[..text.LastIndexOf(" (", StringComparison.Ordinal)];
            // "1.2 of ~25 GB changed files backed up" → "1.2 of ~25 GB of changes backed up".
            if (text.EndsWith(" changed files backed up", StringComparison.Ordinal))
                text = text[..^" changed files backed up".Length] + " of changes backed up";
            if (text.Length > 0) parts.Add(text);
        }
        return string.Join(" · ", parts);
    }
    public string OperationPhaseLabel => IsRunning && IsVerifying ? "Testing restored files" : CurrentPhaseLabel;
    partial void OnCurrentPhaseLabelChanged(string value) => OnPropertyChanged(nameof(OperationPhaseLabel));

    /// <summary>
    /// Tooltip shown on the Delete button (and used as the running-state
    /// branch by the per-action tooltip properties below). Empty string
    /// when the backup is idle so Avalonia hides the tooltip altogether
    /// for Delete (the trash icon + "Delete" label is unambiguous on its
    /// own — no idle hover hint is needed).
    /// </summary>
    public string RunningActionTooltip => IsRunning
        ? "This backup is currently running. Stop it first (Stop button above) to make changes, restore files, or run a check."
        : "";

    /// <summary>Hover hint for the Edit button. While running, explain
    /// why it's locked. While idle, briefly state what Edit opens — the
    /// previous binding reused <see cref="RunningActionTooltip"/>, which
    /// is empty when idle and rendered as an empty grey rectangle on
    /// hover.</summary>
    public string EditTooltip => IsRunning
        ? RunningActionTooltip
        : "Edit this backup's name, schedule, sources, destinations, or filters.";

    /// <summary>Hover hint for the Restore Files button. Same idle/running
    /// split as <see cref="EditTooltip"/>.</summary>
    public string RestoreFilesTooltip => IsRunning
        ? RunningActionTooltip
        : !HasRestorableRun ? NothingToRestoreTooltip
        : "Restore files from a previous revision of this backup to a folder you choose.";

    /// <summary>Whether this backup has stored anything to restore, mount or
    /// browse: an earlier success, or a run in which at least one source
    /// reached a destination. A backup that never ran has nothing yet (U079,
    /// 2 November 2026), and neither does one whose only runs failed:
    /// local_d, whose destination isn't connected, offered Mount and Restore
    /// (4 November 2026).</summary>
    public bool HasRestorableRun => Snapshot.LastSuccessUtc is not null || _displayedRunStoredSomething;

    private bool _displayedRunStoredSomething;

    /// <summary>Mount and Restore files: idle and with something stored.</summary>
    public bool CanUseSnapshots => !IsRunning && HasRestorableRun;

    public const string NothingToRestoreTooltip = "Nothing to restore yet: this backup hasn't stored anything. Run it first.";

    partial void OnDisplayedRunEndUtcChanged(DateTime? value) => RaiseSnapshotAvailability();
    partial void OnDisplayedRunStatusChanged(BackupRunStatus value) => RaiseSnapshotAvailability();

    private void RaiseSnapshotAvailability()
    {
        OnPropertyChanged(nameof(HasRestorableRun));
        OnPropertyChanged(nameof(CanUseSnapshots));
        OnPropertyChanged(nameof(RestoreFilesTooltip));
        OnPropertyChanged(nameof(MountTooltip));
        OnPropertyChanged(nameof(CanMountOrUnmount));
        OnPropertyChanged(nameof(TestRestoreEnabled));
        OnPropertyChanged(nameof(TestRestoreTooltip));
    }

    /// <summary>Hover hint for the Delete button. Earlier the button bound
    /// to <see cref="RunningActionTooltip"/> directly — empty when idle,
    /// which Avalonia rendered as an empty grey tooltip rectangle on
    /// hover. Same idle/running split as <see cref="EditTooltip"/>: when
    /// running, surface the "stop first" explanation; when idle, briefly
    /// describe what Delete will do so the user has a clear sense of the
    /// destructive scope before they click.</summary>
    public string DeleteTooltip => IsRunning
        ? RunningActionTooltip
        : "Remove this backup and delete every copy it stored at its destinations. Other backups are not affected.";

    /// <summary>Negated form of <see cref="IsRunning"/> for XAML bindings
    /// — Avalonia's binding-path parser refuses <c>!IsRunning</c> when
    /// it sits after a complex parent-relative path
    /// (<c>$parent[ItemsControl].((vm:Foo)DataContext).!IsRunning</c>),
    /// which is exactly how the Destinations ItemsControl reaches up to
    /// the card's IsRunning flag. Bind to this instead.</summary>
    public bool CanEditWhileIdle => !IsRunning;

    /// <summary>More holds only actions that wait for the run to end, so it
    /// is greyed out with them and says why (U191, 11 November 2026).</summary>
    public string MoreActionsTooltip => CanEditWhileIdle ? "Edit settings or remove this backup."
        : "Editing and removing this backup are available once it is no longer running.";

    /// <summary>
    /// Populated by the parent <see cref="BackupsViewModel"/> when
    /// another currently-running backup shares one of this card's
    /// destinations — restic serialises writes to a single repository,
    /// so two backups racing on the same target risk corrupting the
    /// snapshot chain. Empty string when there's no conflict (the Run
    /// button is enabled). Surfaced as a tooltip on the disabled Run
    /// button so the user understands WHY they're blocked, instead of
    /// the previous silent-disable behaviour.
    /// </summary>
    [ObservableProperty] private string _runBlockedReason = "";

    /// <summary>True iff Run-now is allowed: the backup itself isn't
    /// running AND no sibling run is holding a destination this backup
    /// also targets. Drives the Run button's IsEnabled gate.</summary>
    public bool CanRunNow => !IsRunning && string.IsNullOrEmpty(RunBlockedReason);

    /// <summary>Tooltip shown on the Run button — the conflict reason
    /// when blocked, otherwise a brief description so a hover on the
    /// idle button explains what it does.</summary>
    public string RunNowTooltip => string.IsNullOrEmpty(RunBlockedReason)
        ? "Start a backup run now."
        : RunBlockedReason;

    partial void OnRunBlockedReasonChanged(string value)
    {
        OnPropertyChanged(nameof(CanRunNow));
        OnPropertyChanged(nameof(RunNowTooltip));
    }

    /// <summary>A running backup blocks starting a separate check, but
    /// its automatic restore comparison can always be opened to view progress.</summary>
    public bool TestRestoreEnabled => !IsVerifying && !IsRunning && HasRestorableRun;
    /// <summary>"Verify" (U213): this backup's checks now, or another once.
    /// A running test restore is followed and stopped on the card (U205).</summary>
    public string TestRestoreActionLabel => "Verify";
    public string TestRestoreTooltip => IsVerifying
        ? "A restore check is running: follow it here, or stop it with Stop."
        : IsRunning
        ? "Available once this backup is no longer running."
        : !HasRestorableRun ? NothingToRestoreTooltip
        : "Run this backup's checks now, or another check once: a structure, data or restore check. Your original files are never changed.";

    /// <summary>Positive-named negation of <see cref="IsRunning"/>. The
    /// per-source SourceLine bindings inside an ItemsControl can't use
    /// <c>!$parent[...].IsRunning</c> — Avalonia's parser doesn't accept
    /// the leading <c>!</c> after a $parent indexer (the user has been
    /// bitten by this before; see the Avalonia binding-negation memory
    /// note). Exposing the negated form as its own observable property
    /// makes the SourceLine "show size after backup ends" binding
    /// trivial.</summary>
    public bool IsIdle => !IsRunning;

    /// <summary>True iff the orchestrator picked up the run AND has
    /// already cleared the destination queue, i.e. the backup is
    /// actually executing backup work (not parked waiting). Drives the
    /// card's top-right "Running…" inline indicator; the alternative
    /// state, <see cref="IsQueued"/>, drives a parallel "Queued…"
    /// indicator. Mutually-exclusive within IsRunning=true.</summary>
    public bool IsActuallyRunning => IsRunning && !IsQueued;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditWhileIdle));
        OnPropertyChanged(nameof(MoreActionsTooltip));
        OnPropertyChanged(nameof(CanUseSnapshots));
        OnPropertyChanged(nameof(RunningActionTooltip));
        OnPropertyChanged(nameof(EditTooltip));
        OnPropertyChanged(nameof(RestoreFilesTooltip));
        OnPropertyChanged(nameof(DeleteTooltip));
        OnPropertyChanged(nameof(TestRestoreEnabled));
        OnPropertyChanged(nameof(TestRestoreTooltip));
        OnPropertyChanged(nameof(CanRunNow));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsActuallyRunning));
        OnPropertyChanged(nameof(OperationPhaseLabel));
        NotifyCheckState();
        if (!value) { RunStartedUtc = null; RunningActivityStep = ""; RunningActivityWhere = ""; RunningActivityPercent = -1; }
        UpdateElapsedTimer();
    }
    partial void OnIsQueuedChanged(bool value)
    {
        OnPropertyChanged(nameof(LastRunDetail));
        // The time running leaves out the time spent queued (U156).
        if (value) _queuedSinceUtc ??= UtcNow();
        else if (_queuedSinceUtc is { } since)
        {
            _queuedFor += UtcNow() - since;
            _queuedSinceUtc = null;
        }
        OnPropertyChanged(nameof(IsActuallyRunning));
        OnPropertyChanged(nameof(RunningLabel));
        NotifyCheckState();
        UpdateElapsedTimer();
    }

    private DateTime? _queuedSinceUtc;
    private TimeSpan _queuedFor;
    // What the background service reports its worker waited (U156).
    private TimeSpan? _reportedQueued;

    /// <summary>The run's time queued so far: from the background service,
    /// from this window's orchestrator, or as this card saw it.</summary>
    private TimeSpan QueuedTime()
    {
        if (_reportedQueued is { } reported) return reported;
        if (ServiceLocator.Orchestrator.IsRunning(Id)) return ServiceLocator.Orchestrator.QueuedTime(Id);
        return _queuedFor + (_queuedSinceUtc is { } since ? UtcNow() - since : TimeSpan.Zero);
    }

    /// <summary>When the run on screen started: the orchestrator's run or
    /// the background service's activity. Null when nothing runs.</summary>
    [ObservableProperty] private DateTime? _runStartedUtc;
    partial void OnRunStartedUtcChanged(DateTime? value)
    {
        // Another run (or none): its time queued starts from nothing.
        _queuedFor = TimeSpan.Zero;
        _reportedQueued = null;
        _queuedSinceUtc = IsQueued && value is not null ? UtcNow() : null;
        OnPropertyChanged(nameof(RunningLabel));
        UpdateElapsedTimer();
    }

    /// <summary>The clock for the time running; tests hold it still.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>"Running · 12 min": how long the run has taken so far, live
    /// (U155, 7 November 2026), for desktop and background runs alike. Time
    /// spent queued, waiting for another backup, doesn't count (U156): the
    /// clock stands still while the card says "Queued: …".</summary>
    public string RunningLabel
    {
        get
        {
            var what = RunningOperation switch { "check" => "Checking", "prune" => "Cleaning up", _ => "Running" };
            if (RunningOperation != "backup" && RunningActivity.Length > 0) what += " · " + RunningActivity;
            return RunStartedUtc is { } started ? what + " · " + Elapsed(UtcNow() - started - QueuedTime()) : what;
        }
    }

    /// <summary>"40 s", "12 min", "1 h 05 min".</summary>
    internal static string Elapsed(TimeSpan time) =>
        time < TimeSpan.FromMinutes(1) ? $"{Math.Max(0, (int)time.TotalSeconds)} s"
        : time < TimeSpan.FromHours(1) ? $"{(int)time.TotalMinutes} min"
        : $"{(int)time.TotalHours} h {time.Minutes:00} min";

    private Avalonia.Threading.DispatcherTimer? _elapsedTimer;

    /// <summary>Refreshes the time running every second, only while the
    /// card shows a run actually going.</summary>
    private void UpdateElapsedTimer()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateElapsedTimer);
            return;
        }
        if (!_disposed && IsActuallyRunning && RunStartedUtc is not null)
        {
            _elapsedTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), Avalonia.Threading.DispatcherPriority.Background,
                (_, _) => { OnPropertyChanged(nameof(RunningLabel)); if (IsChecking) { OnPropertyChanged(nameof(CheckRunLabel)); OnPropertyChanged(nameof(CheckTooltip)); } });
            _elapsedTimer.Start();
        }
        else _elapsedTimer?.Stop();
    }
    partial void OnIsVerifyingChanged(bool value)
    {
        OnPropertyChanged(nameof(TestRestoreEnabled));
        OnPropertyChanged(nameof(TestRestoreActionLabel));
        OnPropertyChanged(nameof(TestRestoreTooltip));
        OnPropertyChanged(nameof(OperationPhaseLabel));
        FollowTestRestore(value);
    }

    private ActiveRestoreSession? _testSession;

    /// <summary>A test restore you started shows on the card as it runs
    /// (U205): its progress comes from its session.</summary>
    private void FollowTestRestore(bool running)
    {
        var session = running ? ServiceLocator.ActiveRestores?.RunningTestFor(Id) : null;
        if (!ReferenceEquals(session, _testSession))
        {
            if (_testSession is not null) _testSession.PropertyChanged -= OnTestSessionChanged;
            _testSession = session;
            if (session is not null) session.PropertyChanged += OnTestSessionChanged;
        }
        ApplyTestSession();
    }

    private void OnTestSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyTestSession();

    private void ApplyTestSession()
    {
        if (!IsChecking)
        {
            if (_testSession is { } s && IsVerifying)
            {
                RunningActivity = s.SourcePath;
                RunningActivityWhere = Destinations.Count == 1 ? Destinations[0].Name : "";
                RunningActivityStep = s.IsPaused || s.IsPausing || s.IsCancelling ? s.StatusLabel
                    : s.IsProgressIndeterminate ? "Preparing the files to restore" : $"Restoring and comparing files · {s.RestoredCount:N0} of {s.TotalFiles:N0} files";
                RunningActivityPercent = s.IsProgressIndeterminate ? -1 : s.PercentComplete * 100;
            }
            else if (!IsRunning)
            {
                RunningActivity = ""; RunningActivityStep = ""; RunningActivityWhere = ""; RunningActivityPercent = -1;
            }
        }
        NotifyCheckState();
    }

    /// <summary>Sources rendered as a scrollable per-row list. The row
    /// shows path + (when running) a thin progress bar.</summary>
    public ObservableCollection<SourceLine> Sources { get; } = new();

    /// <summary>Destinations rendered as small pills with the per-kind
    /// class icon + name. Each pill is clickable — opens the matching
    /// Edit Destination dialog. Backed by a per-pill VM so the icon
    /// can bind directly to the destination's Kind.</summary>
    public ObservableCollection<DestinationPill> Destinations { get; } = new();

    /// <summary>"1 DESTINATION" / "2 DESTINATIONS" — proper plural,
    /// upper-case so the BackupCard view renders it with the same
    /// label-caps style as SOURCES.</summary>
    public string DestinationsCountLabel =>
        Destinations.Count == 1 ? "1 DESTINATION" : $"{Destinations.Count} DESTINATIONS";

    /// <summary>"1 SOURCE" / "2 SOURCES" matching the destination
    /// counterpart — replaces the static SOURCES label so a singular
    /// source reads naturally.</summary>
    public string SourcesCountLabel =>
        Sources.Count == 1 ? "1 SOURCE" : $"{Sources.Count} SOURCES";

    /// <summary>Hover-tooltip text listing every destination on its own
    /// line — surfaced in the card by the consuming view.</summary>
    public string DestinationsTooltip => string.Join(Environment.NewLine, Destinations.Select(d => d.Name));

    /// <summary>Hover-tooltip text listing every source on its own line.</summary>
    public string SourcesTooltip => string.Join(Environment.NewLine, Sources.Select(s => s.Path));

    /// <summary>True iff the consuming view should render the action
    /// strip (Run/Stop/Edit/Test/Delete). False when this VM is used by
    /// the Restore wizard pick-step.</summary>
    public bool ShowActions => _showActions;

    /// <summary>True iff the consuming view should render live progress
    /// bars while a run is in flight. Restore wizard uses false; main
    /// list uses true.</summary>
    public bool ShowProgress => _showProgress;

    /// <param name="showActions">Wire Run / Stop / Edit / Delete / Test buttons.</param>
    /// <param name="showProgress">Subscribe to <see cref="BackupProgressService"/>
    /// and surface per-source live progress.</param>
    public BackupCardViewModel(Backup b, bool showActions, bool showProgress,
        Services.WebDav.ISnapshotMountRegistry? mountRegistry = null, LogStore? logs = null)
    {
        _logs = logs ?? ServiceLocator.Logs;
        Id = b.Id;
        Snapshot = b;
        _mountRegistry = mountRegistry ?? ServiceLocator.SnapshotMounts;
        if (_mountRegistry is not null)
        {
            _mountRegistry.Changed += OnMountChanged;
            MountedDrive = _mountRegistry.GetMountedDrive(Id);
            MountedDescription = _mountRegistry.GetMountedDescription(Id);
            MountProgress = _mountRegistry.GetMountProgress(Id);
            MountPercent = MountProgress is null ? null : _mountRegistry.GetMountPercent(Id);
        }
        _showActions = showActions;
        _showProgress = showProgress;
        Apply(b);
        if (ServiceLocator.Orchestrator.IsRunning(b.Id)) SetRunning();
#if WINDOWS
        _backgroundActivity = ServiceLocator.BackgroundActivity;
        if (_backgroundActivity is not null)
        {
            _backgroundActivity.Changed += OnBackgroundActivity;
            OnBackgroundActivity();
        }
#endif

        // If the orchestrator's maintenance gate is currently held
        // (Erase / Wipe / Prune in progress), stamp the
        // RunBlockedReason on this card up-front. Without this, a
        // brand-new card created mid-maintenance (e.g. config change
        // adding a new backup right after the user clicked Erase)
        // would have an empty RunBlockedReason and the Run Now
        // button would be enabled — directly contradicting the
        // global "no backups during maintenance" contract. The
        // BackupsViewModel.OnMaintenanceStateChanged broadcaster
        // already covers cards that exist when maintenance starts;
        // this constructor branch covers cards that come into
        // existence DURING maintenance.
        if (ServiceLocator.Maintenance.IsActive)
        {
            var reason = ServiceLocator.Maintenance.CurrentReason;
            RunBlockedReason = string.IsNullOrEmpty(reason)
                ? "A storage-maintenance task is running. Please wait."
                : $"A storage-maintenance task is running ({reason}). Please wait.";
        }

        // Note: this VM no longer subscribes to Orchestrator.RunStarted /
        // RunEnded itself. With N cards on screen, the previous per-card
        // subscription meant every event walked N delegates (each doing
        // an `if (backupId != Id) return;` early-return). The hosts —
        // <see cref="BackupsViewModel"/> for the unified list,
        // <see cref="RestoreViewModel"/> for the wizard's pick-step —
        // subscribe ONCE each and route the event to the matching card
        // by id, dropping multicast cost from O(N) to O(1).
        if (showProgress)
        {
            _progressHandler = OnProgressUpdated;
            ServiceLocator.Progress.Updated += _progressHandler;
            ApplyProgressSnapshot();
        }
    }

    /// <summary>"New filter suggestions: Review" (U165): Backspace's updated
    /// recommendations bring changes to a group this backup ticked, and the
    /// backup doesn't take them automatically.</summary>
    [ObservableProperty] private bool _hasFilterSuggestions;

    public void Apply(Backup b)
    {
        Snapshot = b;
        Name = b.Name;
        HasFilterSuggestions = Services.RecommendedFilters.HasSuggestions(b);

        // Sources column. Preserve existing entries by path so the
        // progress-bar binding doesn't flicker on each parent refresh.
        // Fast path: if the source list is identical to what we already
        // show, skip the rebuild entirely. The orchestrator's RunEnded
        // calls Apply(b) then Apply(record) back-to-back; without this
        // a 10-source backup fires 22 CollectionChanged events per run
        // for zero structural change.
        bool sourcesUnchanged =
            Sources.Count == b.SourcePaths.Count
            && SourcesMatch(Sources, b.SourcePaths);
        if (!sourcesUnchanged)
        {
            var existing = Sources.ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
            Sources.Clear();
            foreach (var p in b.SourcePaths)
            {
                if (!existing.TryGetValue(p, out var line))
                    line = new SourceLine(p);
                Sources.Add(line);
            }
            OnPropertyChanged(nameof(SourcesTooltip));
            OnPropertyChanged(nameof(SourcesCountLabel));
        }

        // Per-source size labels from the last successful run. Always
        // refreshed (not just on structural change) so a Config.Changed
        // landing AFTER a successful run picks up the new sizes
        // immediately. SourceLine.SizeLabel uses HumanSize formatting
        // — empty string when no size has been captured yet so the
        // BackupCard doesn't render "(0 B)" for never-run backups.
        foreach (var line in Sources)
        {
            if (b.SourceLastSizeBytes.TryGetValue(line.Path, out var bytes) && bytes > 0) line.ShowMeasured(bytes);
            // A backup the background service ran keeps its size there.
            else if (ServiceMeasuredSizes.For(b.Id, line.Path) is > 0 and var measured) line.ShowMeasured(measured);
            else line.ShowEstimate(SourceSizeRules.For(b));
        }

        // Destinations column. Each pill carries the class kind so the
        // pill view can bind the per-kind icon, AND the destination id
        // so a click can open the matching Edit Destination dialog.
        // The tooltip is per-pill so hovering shows the actual location
        // (e.g. "Local folder · D:\Backups · Click to edit") instead of
        // the parent card's RunningActionTooltip, which is empty when
        // the backup is idle and rendered as a blank tooltip.
        // Same fast-path-skip as Sources above.
        var desiredPills = new List<DestinationPill>(b.Targets.Count);
        foreach (var t in b.Targets)
        {
            var d = ServiceLocator.Config.Current.Destinations.FirstOrDefault(x => x.Id == t.DestinationId);
            if (d is not null)
                desiredPills.Add(new DestinationPill(d.Id, d.Name, d.Kind, BuildDestinationTooltip(d), d.PathOrSubpath));
            else
                desiredPills.Add(new DestinationPill(
                    "", "(missing)", DestinationKind.LocalFolder,
                    "This destination has been deleted from the app's destinations list. Edit the backup to remove it or pick a different destination."));
        }
        if (!DestinationsMatch(Destinations, desiredPills))
        {
            // Carry over any in-flight PercentComplete by destination Id
            // so a Config.Changed-driven rebuild (e.g. tooltip refresh
            // because the underlying Destination's path was edited)
            // doesn't visibly snap the pill back to 0% mid-run before
            // the next progress flush. PercentComplete isn't part of
            // DestinationsMatch — we only rebuild on identity / kind /
            // tooltip changes, none of which invalidate the live
            // percent. Use TryAdd-style insertion so a (theoretically
            // impossible but defensively-handled) duplicate Id doesn't
            // throw — the first occurrence wins, matching the order
            // the user added the targets.
            var priorPct = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var p in Destinations)
            {
                if (!string.IsNullOrEmpty(p.Id))
                    priorPct.TryAdd(p.Id, p.PercentComplete);
            }
            Destinations.Clear();
            foreach (var p in desiredPills)
            {
                if (!string.IsNullOrEmpty(p.Id) && priorPct.TryGetValue(p.Id, out var pct))
                    p.PercentComplete = pct;
                Destinations.Add(p);
            }
            OnPropertyChanged(nameof(DestinationsCountLabel));
            OnPropertyChanged(nameof(DestinationsTooltip));
        }

        // Status badges + labels. The status-icon class set drives both
        // the leading PathIcon (check / warn-triangle / x-circle / pause /
        // spinner / empty-circle) AND its colour via styled selectors —
        // single source of truth so renaming a class doesn't desync the
        // glyph from the tone.
        // Config changes (including refreshed cloud credentials) describe the
        // previous completed run. Keep the current Running/Queued state until
        // the orchestrator supplies its terminal RunRecord.
        if (!IsRunning)
        {
            ApplyStoredRunState(b);
        }
        NextRunLabel = !b.Enabled
            ? "Manual only (auto-run is off)"
            : b.Schedule.DescribeNextRun(DateTime.Now);
        NextRunClass = b.Enabled ? "scheduled" : "manual";
        ApplyVerifyState(b);
    }

    public void ApplyVerifyState(Backup b, RunRecord? completedRun = null)
    {
        // A config refresh must not replace a known cancellation or failure
        // with the legacy LastVerify success while the disk read is pending.
        // RunAppended already supplies the persisted result; display it now.
        if (completedRun is { EndedUtc: not null } && completedRun.BackupId == Id)
            _checkRuns = _checkRuns.Where(r => r.Id != completedRun.Id).Append(completedRun).ToArray();
        ApplyStoredRunState(b);
        _checkEntries = BackupCheckHistory.Entries(b, _checkRuns);
        RefreshCheckAge();
        var generation = ++_checkGeneration;
        var logs = _logs;
        _ = Task.Run(() => logs.LoadTimelineByBackupId(Id)).ContinueWith(task =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || generation != _checkGeneration) return;
                if (!task.IsCompletedSuccessfully)
                {
                    _ = task.Exception; VerifyLabel = "Check history could not be read"; VerifyClass = "stale";
                    _checkHistoryReadFailed = true;
                    VerifyDetails = "The saved files were preserved. Open History again to see the error.";
                    return;
                }
                // A live service result may arrive before the history page
                // containing it. A slower disk read must not erase that result.
                _checkRuns = _checkRuns.Concat(task.Result).GroupBy(r => r.Id).Select(g => g.Last()).ToArray();
                ApplyStoredRunState(Snapshot);
                _checkHistoryLoaded = true;
                _checkHistoryReadFailed = false;
                _checkEntries = BackupCheckHistory.Entries(Snapshot, _checkRuns);
                RefreshCheckAge();
            });
        });
    }

    private void ApplyStoredRunState(Backup saved)
    {
        if (IsRunning && !IsChecking) return;
        var latest = _checkRuns.Where(r => r.BackupId == saved.Id && r.Kind == RunKind.Backup
            && r.EndedUtc is not null && r.EndedUtc >= r.StartedUtc
            && r.Status is BackupRunStatus.Success or BackupRunStatus.Warning or BackupRunStatus.Failed or BackupRunStatus.Skipped)
            .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.EndedUtc).FirstOrDefault();
        var useHistory = latest is not null && latest.StartedUtc >= (saved.LastRunStartUtc ?? DateTime.MinValue)
            && latest.EndedUtc >= (saved.LastRunEndUtc ?? DateTime.MinValue);
        var status = useHistory ? latest!.Status : saved.LastRunStatus;
        var end = useHistory ? latest!.EndedUtc : saved.LastRunEndUtc;
        var summary = useHistory ? latest!.Summary : saved.LastRunSummary;
        // Any earlier run that stored something counts, background runs
        // included: the latest run may have failed (U157).
        _displayedRunStoredSomething = end is not null && (status is BackupRunStatus.Success or BackupRunStatus.Warning
            || useHistory && latest!.Cells.Any(c => c.Status is BackupRunStatus.Success or BackupRunStatus.Warning))
            || RestorableRuns.Any(saved, _checkRuns);
        StatusClass = status switch
        {
            BackupRunStatus.Success => "ok", BackupRunStatus.Warning => "warn", BackupRunStatus.Failed => "fail",
            BackupRunStatus.Skipped => "skipped", BackupRunStatus.Running => "running", _ => "idle",
        };
        StatusLabel = status == BackupRunStatus.Success ? "OK" : status == BackupRunStatus.NeverRun ? "Idle" : status.ToString();
        _lastRun = end is DateTime when ? (NormaliseSuccessSummary(summary, status), when, status) : null;
        LastRunLabel = _lastRunAgeLabel = LastRunText();
        DisplayedRunEndUtc = end;
        DisplayedRunStatus = status;
    }

    // The last run shown and its label, so the minute timer can redo "3h ago" (U136).
    private (string Summary, DateTime When, BackupRunStatus Status)? _lastRun;
    private string? _lastRunAgeLabel;
    private string? _lastRunHover;

    /// <summary>"15m ago · 27.4 MB uploaded"; the counts and duration are on
    /// hover (U160, 7 November 2026).</summary>
    private string LastRunText()
    {
        if (_lastRun is not { } last) { _lastRunHover = null; return "Never run"; }
        var (label, hover) = LastRunLine.For(last.Summary, last.Status, last.When);
        _lastRunHover = hover;
        return label;
    }

    /// <summary>Redoes the labels that read the clock, every minute (U136,
    /// 4 November 2026): the card said "1h ago" for a backup finished three
    /// hours before, while its health line, already redone, said "3h ago";
    /// and "Today 20:00" stayed after 20:00 had passed. A label showing
    /// something else (running, queued, stopping, an error) is left alone.</summary>
    public void RefreshTimes()
    {
        if ((!IsRunning || IsChecking) && _lastRunAgeLabel is not null && LastRunLabel == _lastRunAgeLabel)
            LastRunLabel = _lastRunAgeLabel = LastRunText();
        if (Snapshot.Enabled) NextRunLabel = Snapshot.Schedule.DescribeNextRun(DateTime.Now);
    }

    public void RefreshCheckAge()
    {
        if (_checkHistoryReadFailed) return;
        var state = BackupCheckHistory.State(Snapshot, _checkEntries, DateTime.UtcNow, nothingStored: !HasRestorableRun);
        // A check that waits for its drive says so calmly (U174).
        if (ServiceLocator.Config?.Current is { } config)
            state = CheckOverdue.Shown(state, Snapshot, CheckOverdue.For(Snapshot, _checkRuns, config.Destinations));
        // A fresh card has not yet ruled out a newer failure in saved history.
        // Known warnings can be shown immediately, but success must wait.
        if (!_checkHistoryLoaded && state.Tone is "ok" or "never")
        {
            VerifyLabel = "Reading check history…"; VerifyClass = "loading";
            CheckScopeLabel = ""; VerifyDetails = "Checking the saved results before showing the backup's health.";
            return;
        }
        VerifyLabel = state.Label; VerifyClass = state.Tone;
        CheckScopeLabel = state.Scope; VerifyDetails = state.Details;
    }

    /// <param name="startedUtc">When the run started, if known (the
    /// background service's activity); otherwise the orchestrator's run, or
    /// the time already shown, or now.</param>
    public void SetRunning(DateTime? startedUtc = null, string operation = "backup")
    {
        RunningOperation = operation;
        RunStartedUtc = startedUtc
            ?? (ServiceLocator.Orchestrator.TryGetRunningInfo(Id, out var started) ? started : (DateTime?)null)
            ?? (IsRunning ? RunStartedUtc : null)
            ?? DateTime.UtcNow;
        OnPropertyChanged(nameof(RunningLabel));
        var wasChecking = IsChecking;
        IsRunning = true;
        IsQueued = false;
        // A check shows under Backup health (U197): Last backup keeps the
        // last backup's result, read again with the history (the backup
        // before it may have just ended, in the same background job).
        if (operation == "check")
        {
            if (!wasChecking) ApplyVerifyState(Snapshot);
            return;
        }
        DisplayedRunStatus = BackupRunStatus.Running;
        StatusClass = "running";
        StatusLabel = "Running";
        LastRunLabel = "Running…";
    }

    /// <summary>Flip the inline label to a "Queued: {reason}" message
    /// while the orchestrator waits for a shared destination to free
    /// up. Called from <see cref="BackupsViewModel"/>'s
    /// RunQueuedReasonChanged handler. Empty reason flips back to
    /// "Running…" — that's the orchestrator signalling that the
    /// destination gate was just acquired and the run is now actually
    /// executing. Also flips <see cref="IsQueued"/> so the card's
    /// top-right indicator can swap "Running…" for "Queued…" — the
    /// user explicitly asked for the indicator to differentiate
    /// "actually running" from "waiting in line".</summary>
    public void SetQueuedReason(string reason)
    {
        if (!IsRunning) return; // shouldn't happen — orchestrator only fires after RunStarted
        var queued = !string.IsNullOrEmpty(reason);
        IsQueued = queued;
        QueuedReason = queued ? reason : "";
        // The card says just "Queued"; why is on hover (7 November 2026).
        // A check says it under Backup health (U197).
        if (!IsChecking) LastRunLabel = queued ? "Queued" : "Running…";
    }

    /// <summary>A run that waited for its conditions and was stopped before
    /// it started (U226): the card shows its last result again.</summary>
    public void EndQueuedWait()
    {
        IsQueued = false;
        QueuedReason = "";
        RunningActivity = "";
        IsRunning = false;
        ApplyStoredRunState(Snapshot);
        ApplyVerifyState(Snapshot);
    }

    /// <summary>Why a queued run waits ("waiting for local_c (same destination)").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastRunDetail), nameof(CheckRunLabel))]
    private string _queuedReason = "";

    /// <summary>The status line's hover text: for a queued run, why it waits
    /// and that it starts by itself; otherwise the line itself.</summary>
    public string LastRunDetail => IsQueued && QueuedReason.Length > 0
        ? $"Queued: {QueuedReason}"
        : LastRunLabel == _lastRunAgeLabel && _lastRunHover is { } hover ? hover
        : LastRunLabel;

    partial void OnLastRunLabelChanged(string value) => OnPropertyChanged(nameof(LastRunDetail));

    public void Apply(RunRecord r)
    {
        if (r.BackupId != Id || r.EndedUtc is null || r.EndedUtc < r.StartedUtc
            || r.Status is BackupRunStatus.Running or BackupRunStatus.NeverRun) return;
        _checkRuns = _checkRuns.Where(previous => previous.Id != r.Id).Append(r).ToArray();
        IsRunning = false;
        ApplyStoredRunState(Snapshot);
        IsQueued = false;
        OverallPercent = 0;
        // Clear the phase chip alongside the bars so a finished run
        // doesn't leave a stale "Pruning…" pill flashing the moment a
        // new run kicks off (the IsRunning gate hides it post-run, but
        // the next ApplyProgressSnapshot would briefly re-surface the
        // old phase before the orchestrator emits its first marker).
        CurrentPhaseLabel = "";
        foreach (var s in Sources) { s.PercentComplete = 0; s.IsRunning = false; s.Detail = ""; }
        foreach (var d in Destinations) d.PercentComplete = 0;
        // The backup's checks may already have started (U183).
        if (_progressHandler is not null) ApplyCheckActivity(ServiceLocator.Progress.Snapshot(Id));
    }

    /// <summary>Coalesce multiple progress-event bursts into one
    /// dispatcher Post. Engine stdout can emit hundreds of lines/sec
    /// during a large backup; without this guard each line would queue
    /// its own ApplyProgressSnapshot call, each cloning the whole
    /// BackupProgress dictionary. The flag is reset INSIDE the
    /// dispatched lambda so any line that arrives after the snapshot
    /// read still triggers a fresh post.</summary>
    private int _snapshotApplyPending;

    private void OnProgressUpdated(string backupId)
    {
        if (backupId != Id) return;
        if (_disposed) return;
        // Progress events fire on the engine's stdout pump thread; the
        // VM's observable mutations need the UI thread. Skip the post
        // if one is already queued — the latest snapshot will be read
        // when it dispatches anyway.
        if (System.Threading.Interlocked.CompareExchange(ref _snapshotApplyPending, 1, 0) != 0)
            return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            System.Threading.Volatile.Write(ref _snapshotApplyPending, 0);
            ApplyProgressSnapshot();
        });
    }

    private void ApplyProgressSnapshot()
    {
        // A dispatcher Post scheduled BEFORE Dispose may run AFTER it.
        // Mutating Sources / Destinations on a disposed card visibly
        // resets percent on parent cards that re-create with the same
        // Id later (BackupsViewModel diffs by Id, so a deleted+
        // recreated backup with the same name lands here). Cheap
        // guard, no-op when alive.
        if (_disposed) return;
#if WINDOWS
        if (BackgroundRunId is not null && !ServiceLocator.Orchestrator.IsRunning(Id)) return;
#endif
        var snap = ServiceLocator.Progress.Snapshot(Id);
        ApplyCheckActivity(snap);
        if (snap is null)
        {
            OverallPercent = 0;
            CurrentPhaseLabel = "";
            ProgressDetail = "";
            foreach (var s in Sources) { s.PercentComplete = 0; s.IsRunning = false; s.Detail = ""; }
            foreach (var d in Destinations) d.PercentComplete = 0;
            return;
        }
        OverallPercent = snap.OverallPercent;
        foreach (var s in Sources)
        {
            if (snap.Sources.TryGetValue(s.Path, out var sp))
            {
                s.PercentComplete = sp.PercentComplete;
                s.IsRunning = sp.IsRunning;
                s.Detail = sp.Detail;
                s.ShowCounted(sp.Detail);
            }
        }
        // Per-destination progress for the pill bar fill. Match by
        // Name (the same key the orchestrator passes to
        // ReportCellStarted), and leave pills not yet seen at 0%.
        foreach (var d in Destinations)
        {
            d.PercentComplete = snap.Destinations.TryGetValue(d.Name, out var dp)
                ? dp.PercentComplete
                : 0;
        }
        // Phase chip: take the first running cell's phase (most
        // common case is all cells in lockstep, but if they're not
        // we surface the loudest one). Empty when no cell is live —
        // typically the brief gap between cells.
        var livePhase = snap.Sources.Values
            .SelectMany(s => s.Cells.Values)
            .FirstOrDefault(c => c.IsRunning && !string.IsNullOrEmpty(c.Phase))
            ?.Phase ?? "";
        CurrentPhaseLabel = livePhase;
        ProgressDetail = Sources.FirstOrDefault(s => s.IsRunning && s.Detail.Length > 0)?.Detail ?? "";
    }

    /// <summary>A check run in this app (scheduled, or after a backup): the
    /// card says "Checking · {check}" while it runs, then shows its stored
    /// state again. The background service's runs come through its activity.</summary>
    private void ApplyCheckActivity(BackupProgressService.BackupProgress? snap)
    {
#if WINDOWS
        if (BackgroundRunId is not null) return;
#endif
        var activity = snap?.Activity ?? "";
        if (activity.Length > 0)
        {
            if (!IsRunning || RunningOperation == "check")
            {
                if (!IsRunning) SetRunning(operation: "check");
                RunningActivity = activity;
                RunningActivityStep = snap!.ActivityStep;
                RunningActivityWhere = snap.ActivityWhere;
                RunningActivityPercent = snap.ActivityPercent;
            }
        }
        else if (IsRunning && RunningOperation == "check" && !ServiceLocator.Orchestrator.IsRunning(Id))
        {
            IsRunning = false;
            RunningActivity = "";
            RunningOperation = "backup";
            ApplyStoredRunState(Snapshot);
        }
    }

    /// <summary>Volatile so the read in <see cref="OnProgressUpdated"/>
    /// (which fires on the runner's stdout pump thread, not the UI
    /// thread) is guaranteed to observe the write from
    /// <see cref="Dispose"/> on weak memory models like ARM64 — without
    /// the keyword the JIT could re-order or cache, leaving the pump
    /// thread posting to a defunct VM after Dispose ran.</summary>
    private volatile bool _disposed;

#if WINDOWS
    private void OnBackgroundActivity() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed && _backgroundActivity is not null)
            ApplyBackgroundActivity(Services.Platform.Windows.ServiceActivity.ForBackup(_backgroundActivity.Current, Id), _backgroundActivity.Error);
    });

    internal void ApplyBackgroundActivity(Services.Platform.Windows.ServiceActivity.Snapshot? activity, string? error)
    {
        if (_disposed || ServiceLocator.Orchestrator.IsRunning(Id)) return;
        // The service reports one run at a time. When it has moved on to
        // another run (another backup's), this card's run is over even
        // though its end was never seen: local_d failed in half a second
        // while Backspace opened, the service went straight on to
        // local_dropbox, and local_d's card said Running… (U128, 4 November
        // 2026). Its result comes from the service's history.
        if (BackgroundRunId is { } ours && activity is not null && error is null && activity.RunId != ours)
        {
            BackgroundRunId = null;
            _awaitingBackgroundResult = true;
            ShowLatestBackgroundResult();
            _logs.RefreshBackgroundHistory(Id);
        }
        if (activity?.BackupId != Id) return;
        if (activity.State is "starting" or "running" or "stopping" or "unknown")
        {
            BackgroundRunId = activity.RunId;
            // A backup's checks run once its own work is over, in the same
            // job: the card then says which check runs, not the backup's 100%.
            SetRunning(activity.StartedUtc, activity.Operation is "check" or "prune" ? activity.Operation
                : string.IsNullOrEmpty(activity.Progress?.Activity) ? "backup" : "check");
            RunningActivity = activity.Progress?.Activity ?? "";
            RunningActivityStep = activity.Progress?.ActivityStep ?? "";
            RunningActivityWhere = activity.Progress?.ActivityWhere ?? "";
            RunningActivityPercent = activity.Progress?.ActivityPercent ?? -1;
            _reportedQueued = TimeSpan.FromSeconds(activity.Progress?.QueuedSeconds ?? 0);
            SetQueuedReason(activity.Progress?.Queued ?? "");
            if (activity.State == "stopping")
            {
                if (IsChecking) RunningActivityStep = "Stopping the check…";
                else LastRunLabel = "Stopping background backup…";
            }
            if (error is not null) LastRunLabel = error;
            var progress = activity.Progress;
            OverallPercent = progress?.Percent ?? 0;
            foreach (var source in Sources)
            {
                var match = progress?.Sources.FirstOrDefault(s => string.Equals(s.Path, source.Path, StringComparison.OrdinalIgnoreCase));
                source.PercentComplete = match?.Percent ?? 0; source.IsRunning = match?.Running ?? false; source.Detail = match?.Detail ?? "";
                source.ShowCounted(source.Detail);
            }
            foreach (var destination in Destinations)
                destination.PercentComplete = progress?.Destinations.FirstOrDefault(d => string.Equals(d.Name, destination.Name, StringComparison.OrdinalIgnoreCase))?.Percent ?? 0;
            CurrentPhaseLabel = progress?.Sources.FirstOrDefault(s => s.Running && s.Phase.Length > 0)?.Phase ?? "";
            ProgressDetail = progress?.Sources.FirstOrDefault(s => s.Running && s.Detail.Length > 0)?.Detail ?? "";
            return;
        }
        if (error is not null || activity.RunId == _lastBackgroundResult) return;
        BackgroundRunId = null;
        var result = activity.Progress?.Result;
        if (result is not null) Apply(result);
        else if (activity.State is "failed" or "failed-to-start" or "interrupted" or "completed")
            Apply(new RunRecord { Id = activity.RunId!, BackupId = Id, BackupName = Snapshot.Name,
                Kind = activity.Operation == "check" ? RunKind.Check : activity.Operation == "prune" ? RunKind.Prune : RunKind.Backup,
                StartedUtc = activity.StartedUtc!.Value, EndedUtc = DateTime.UtcNow, Status = BackupRunStatus.Failed,
                Summary = "The background operation ended without a confirmed result. Check its run details before relying on this backup." });
        _lastBackgroundResult = activity.RunId;
        _logs.RefreshBackgroundHistory(Id);
        ApplyVerifyState(Snapshot, result);
    }

    private bool _awaitingBackgroundResult;

    /// <summary>Shows the latest finished run in this backup's history, or
    /// the saved state when the service's history hasn't arrived yet.</summary>
    private void ShowLatestBackgroundResult()
    {
        var latest = _logs.LoadHistoryByBackupId(Id).Where(r => r.EndedUtc is not null && r.Status is not (BackupRunStatus.Running or BackupRunStatus.NeverRun))
            .OrderBy(r => r.EndedUtc).LastOrDefault();
        // The run just measured its sources: show those sizes, not the estimate.
        ServiceMeasuredSizes.Invalidate();
        foreach (var line in Sources)
            if (!Snapshot.SourceLastSizeBytes.ContainsKey(line.Path) && ServiceMeasuredSizes.For(Id, line.Path) is > 0 and var measured)
                line.ShowMeasured(measured);
        if (latest is not null) { Apply(latest); _awaitingBackgroundResult = false; return; }
        IsRunning = false;
        IsQueued = false;
        OverallPercent = 0;
        ApplyStoredRunState(Snapshot);
    }

    /// <summary>The service's history for this backup was read again.</summary>
    internal void OnBackgroundHistoryRefreshed()
    {
        if (_awaitingBackgroundResult && BackgroundRunId is null && !ServiceLocator.Orchestrator.IsRunning(Id)) ShowLatestBackgroundResult();
    }
#endif

    public void Dispose()
    {
        _disposed = true;
        UpdateElapsedTimer();
#if WINDOWS
        if (_backgroundActivity is not null) _backgroundActivity.Changed -= OnBackgroundActivity;
#endif
        if (_mountRegistry is not null) _mountRegistry.Changed -= OnMountChanged;
        if (_progressHandler is not null)
            ServiceLocator.Progress.Updated -= _progressHandler;
    }

    /// <summary>
    /// Bound to the destination pill's Click. Opens the matching
    /// Destination editor by raising an event the parent
    /// BackupsViewModel handles (cards don't open windows themselves
    /// because they're hosted inside DataTemplates and have no direct
    /// access to a parent Window). Parameter is the destination id;
    /// parent VM resolves the actual model + opens the editor.
    /// </summary>
    [RelayCommand]
    private async Task EditDestination(string destinationId)
    {
        if (string.IsNullOrEmpty(destinationId)) return;
        var handler = EditDestinationRequested;
        if (handler is null) return;
        await handler(destinationId);
    }

    /// <summary>True iff the last run finished in a state worth
    /// drawing the user's attention to. Drives the inline "View logs"
    /// pill on the card header — clicking it jumps to Activity &amp;
    /// logs preselected on this backup so the user can see WHY without
    /// hunting. The user reported: "Would be good if there is an
    /// error/warning to add a View logs button/pill next to the backup
    /// name so users can click and be sent straight to the relevant
    /// logs."</summary>
    public bool HasAttentionStatus =>
        string.Equals(StatusClass, "warn", StringComparison.OrdinalIgnoreCase)
        || string.Equals(StatusClass, "fail", StringComparison.OrdinalIgnoreCase);

    partial void OnStatusClassChanged(string value) =>
        OnPropertyChanged(nameof(HasAttentionStatus));

    /// <summary>Navigate to Activity &amp; logs and pre-select this
    /// backup so the most recent run's persisted log opens
    /// automatically. Mirrors the click-to-logs handler in
    /// <see cref="BackupOrchestrator.SendAlertsAsync"/>'s toast
    /// action.</summary>
    [RelayCommand]
    private void ViewLogs()
    {
        if (string.IsNullOrEmpty(Id)) return;
        AppNavigator.Go(NavItem.Logs);
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime l
                && l.MainWindow?.DataContext is MainWindowViewModel vm)
            {
                vm.Logs.SelectBackupById(Id);
            }
        }
        catch (Exception ex) { AppLogger.For<BackupCardViewModel>().Warning(ex, "ViewLogs handler threw for {Id}", Id); }
    }

    /// <summary>True iff the host is Windows AND the WebDAV-mount
    /// registry is alive. Bound to the IsVisible of the "Mount in
    /// Explorer" button so it disappears on macOS / Linux entirely
    /// rather than rendering a disabled action with no obvious
    /// reason.</summary>
    public bool CanMountSnapshot =>
        Services.Platform.PlatformInfo.IsWindows
        && _mountRegistry is not null;

    private void OnMountChanged(string backupId)
    {
        if (backupId != Id || _disposed) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshMount);
    }

    private void RefreshMount()
    {
        if (_disposed || _mountRegistry is null) return;
        MountedDrive = _mountRegistry.GetMountedDrive(Id);
        MountedDescription = _mountRegistry.GetMountedDescription(Id);
        // Keep "Starting" from the click until the registry reports.
        var progress = _mountRegistry.GetMountProgress(Id);
        MountProgress = progress ?? (_mountClicked ? MountProgress : null);
        MountPercent = progress is null ? null : _mountRegistry.GetMountPercent(Id);
    }

    private bool _mountClicked;

    /// <summary>Asks which revisions to mount (U162): the picker window, wired
    /// by the backups page. Null (tests, a card shown elsewhere) mounts the
    /// latest revision; a null answer means the person cancelled.</summary>
    public Func<Task<Services.WebDav.MountSelection?>>? ChooseMountRevisions { get; set; }

    /// <summary>Mount the chosen revisions of this backup as a Windows drive,
    /// then open Explorer at it; Unmount when mounted. The picker comes first
    /// (U162); from then on the card says "Mounting…" with what it is doing,
    /// then "Mounted as Z:" with Open in Explorer; a failure is shown on the
    /// card (U158).</summary>
    [RelayCommand]
    private async Task MountInExplorer()
    {
        var registry = _mountRegistry;
        if (registry is null) return; // not Windows; the button is hidden anyway
        MountError = null;
        try
        {
            if (registry.GetMountedDrive(Id) is not null)
            {
                await registry.UnmountAsync(Id);
                return;
            }
            var choose = ChooseMountRevisions;
            if (choose is null) { _mountClicked = true; MountProgress = "Starting"; }
            if (!await VaultGate.EnsureReadableAsync("Backspace needs your saved passwords to open this backup.").ConfigureAwait(true)) return;
            Services.WebDav.MountSelection? selection = null;
            if (choose is not null && (selection = await choose().ConfigureAwait(true)) is null)
                return; // cancelled in the picker
            _mountClicked = true;
            MountProgress = "Starting";
            var result = selection is null
                ? await registry.MountForAsync(Snapshot).ConfigureAwait(true)
                : await registry.MountForAsync(Snapshot, selection).ConfigureAwait(true);
            if (!result.Succeeded && !_disposed) MountError = "Couldn't mount: " + result.Message;
        }
        catch (OperationCanceledException) { /* Cancel on the card */ }
        catch (Exception ex)
        {
            // The mount path crosses several can-throw seams (the engine,
            // the destination, the WinFsp driver). Never an async-void crash.
            AppLogger.For<BackupCardViewModel>().Error(ex, "MountInExplorer threw for {Name}", Snapshot.Name);
            if (!_disposed) MountError = "Couldn't mount: " + ex.Message;
        }
        finally
        {
            _mountClicked = false;
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) RefreshMount();
            else OnMountChanged(Id);
        }
    }

    [RelayCommand]
    private void OpenMountedDrive() => _mountRegistry?.OpenInExplorer(Id);

    [RelayCommand]
    private void CancelMount() => _mountRegistry?.CancelPending();

    [RelayCommand]
    private void DismissMountError() => MountError = null;

    /// <summary>
    /// Wired by the parent BackupsViewModel at construction. Raised
    /// when the user clicks a destination pill on this card; the
    /// handler opens the Edit Destination dialog and persists changes.
    /// </summary>
    public Func<string, Task>? EditDestinationRequested { get; set; }

    /// <summary>Compose the per-pill hover tooltip: a friendly kind
    /// label + the actual location + a click hint. The location string
    /// varies by kind: a folder path for local-like destinations, the
    /// cloud sub-path for OAuth destinations, and bucket/region for S3.
    /// Earlier the pill bound to the parent card's RunningActionTooltip,
    /// which is empty when the backup isn't running, so hovering on the
    /// pill while idle showed an empty grey rectangle.</summary>
    private static string BuildDestinationTooltip(Destination d)
    {
        var kind = d.Kind switch
        {
            DestinationKind.LocalFolder       => "Local folder",
            DestinationKind.ExternalDrive     => "External drive",
            DestinationKind.NetworkShare      => "Network share",
            DestinationKind.DropboxAppScoped  => "Dropbox (app-scoped)",
            DestinationKind.DropboxFullAccess => "Dropbox (full access)",
            DestinationKind.OneDrivePersonal  => "OneDrive (personal)",
            DestinationKind.OneDriveBusiness  => "OneDrive (business)",
            DestinationKind.GoogleDrive       => "Google Drive",
            DestinationKind.S3Compatible      => "S3-compatible",
            _ => d.Kind.ToString(),
        };
        string location;
        if (d.Kind == DestinationKind.S3Compatible)
        {
            var bucket = string.IsNullOrWhiteSpace(d.S3Bucket) ? "(no bucket)" : d.S3Bucket;
            var endpoint = string.IsNullOrWhiteSpace(d.S3Endpoint) ? "" : $" @ {d.S3Endpoint}";
            var sub = string.IsNullOrWhiteSpace(d.PathOrSubpath) ? "" : $"/{d.PathOrSubpath.TrimStart('/')}";
            location = $"{bucket}{sub}{endpoint}";
        }
        else
        {
            location = string.IsNullOrWhiteSpace(d.PathOrSubpath) ? "(no location set)" : d.PathOrSubpath;
        }
        return $"{d.Name}\n{kind} · {location}\nClick to edit this destination.";
    }

    /// <summary>True iff the existing SourceLine collection's paths
    /// sequence-equal the new source list (case-insensitive). Hot-path
    /// short-circuit for the common case where Apply(b) is called with
    /// the same backup snapshot.</summary>
    private static bool SourcesMatch(
        IReadOnlyList<SourceLine> existing,
        IReadOnlyList<string> desired)
    {
        for (int i = 0; i < existing.Count; i++)
            if (!string.Equals(existing[i].Path, desired[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    /// <summary>True iff the existing destination pills sequence-equal
    /// the desired set on every visible field. DestinationPill is
    /// immutable so a field-by-field equality check is sufficient — no
    /// need to compare ToolTip text since it's derived from kind +
    /// location.</summary>
    private static bool DestinationsMatch(
        IReadOnlyList<DestinationPill> existing,
        IReadOnlyList<DestinationPill> desired)
    {
        if (existing.Count != desired.Count) return false;
        for (int i = 0; i < existing.Count; i++)
        {
            var a = existing[i];
            var b = desired[i];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.DisplayLabel, b.DisplayLabel, StringComparison.Ordinal)) return false;
            if (a.Kind != b.Kind) return false;
            if (!string.Equals(a.Tooltip, b.Tooltip, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    // HumanAgo lived here as a private static until code-review flagged
    // three duplicate copies across the VM layer; promoted to
    // HumanFormat.Ago. Call sites above were updated in lockstep.

    /// <summary>
    /// Display-time rewrite of a stored RunRecord summary. New
    /// successful no-op runs get "Completed in Xs" from
    /// <see cref="BackupOrchestrator.BuildSummary"/>, but the
    /// persisted history.json still holds the older bare "in Xs"
    /// shape — rendering that as "LAST in 4s · 7d ago" reads as a
    /// missing-verb sentence. The user reported: "I am still seeing
    /// LAST in 4s — 7d ago (no Completed), make sure the changes
    /// were applied everywhere relevant." This catches up old
    /// records at render time so they look the same as new ones.
    /// Only applies to Success runs; Skipped / Warning / Failed
    /// already have descriptive prefixes ("Manually skipped after",
    /// "5 errors · in", etc.).
    /// </summary>
    private static string NormaliseSuccessSummary(string? raw, BackupRunStatus status)
    {
        var s = raw is null ? "—" : RunSummaryWording.ForDisplay(raw, status);
        if (status != BackupRunStatus.Success) return s;
        // Bare "in 4s" / "in 1h 12m" / "in 1m 12s" with no leading
        // descriptive parts → prepend "Completed". The regex
        // requires a digit immediately after "in " so we don't
        // accidentally rewrite something like "in case of …" — the
        // duration pattern always starts with a number.
        // Other shapes ("1.2 GB uploaded · in 5s", "Manually skipped
        // after 2s", etc.) pass through unchanged because they
        // already read fine.
        if (NormaliseSuccessRx.IsMatch(s))
            return "Completed " + s;
        return s;
    }

    private static readonly System.Text.RegularExpressions.Regex NormaliseSuccessRx =
        new(@"^in \d", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>One row in the Sources column. Observable so the
    /// per-source percent / running flag drive a live progress bar
    /// behind the path label.</summary>
    public sealed partial class SourceLine : ObservableObject
    {
        public string Path { get; }
        [ObservableProperty] private double _percentComplete;
        [ObservableProperty] private bool _isRunning;
        [ObservableProperty] private string _detail = "";
        /// <summary>Short, human-formatted size of the source from the
        /// last successful run ("1.2 GB", "84 MB", …). Empty until the
        /// first successful backup populates the per-source size cache
        /// in <see cref="Backup.SourceLastSizeBytes"/>; rendered in
        /// parentheses next to the path on the BackupCard.</summary>
        [ObservableProperty] private string _sizeLabel = "";
        [ObservableProperty] private string _sizeTooltip = "";
        public bool HasSize => !string.IsNullOrEmpty(SizeLabel);
        partial void OnSizeLabelChanged(string value) =>
            OnPropertyChanged(nameof(HasSize));
        public SourceLine(string path) { Path = path; }

        private bool _measured;
        private bool _estimating;

        public void ShowMeasured(long bytes)
        {
            _measured = true;
            SizeLabel = BackupLiveProgress.Bytes(bytes);
            SizeTooltip = "Size measured by the last backup.";
        }

        /// <summary>While a backup runs, the size it counted, once its count is
        /// complete: the last backup's size next to a different total on the
        /// progress line read as a contradiction (U141, 5 November 2026: "179 GB"
        /// on the source, "172 / 190 GB" below it).</summary>
        public void ShowCounted(string detail)
        {
            if (BackupLiveProgress.CountedTotal(detail) is not { } total || total == SizeLabel) return;
            _measured = true;
            SizeLabel = total;
            SizeTooltip = "Size counted by the backup in progress.";
        }

        /// <summary>Before a first successful backup, shows the size of the
        /// files this backup will hold: its exclusion rules applied, files
        /// kept only in the cloud left out (U123). Replaced by the measured
        /// size once a backup has run (U106). The probe is shared and cached,
        /// so each folder is read at most once per session and set of rules.</summary>
        public void ShowEstimate(SourceSizeRules rules)
        {
            _measured = false;
            if (ServiceLocator.SizeProbe is not { } probe) return;
            if (probe.TryGetCached(Path, rules) is { } cached) { ApplyEstimate(cached); return; }
            if (_estimating) return;
            _estimating = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await probe.GetOrComputeAsync(Path, CancellationToken.None, rules: rules);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyEstimate(result));
                }
                catch (Exception) { }
                finally { _estimating = false; }
            });
        }

        private void ApplyEstimate(SourceSizeResult result)
        {
            if (_measured || !result.Completed || result.Bytes <= 0) return;
            // "~178 GB" and "178+ GB", as on the progress line (U133, 4 November
            // 2026: "about 178.04 GB" next to "30 / ~178 GB").
            var size = BackupLiveProgress.Bytes(result.Bytes);
            (SizeLabel, SizeTooltip) = result.Approximate
                ? ($"~{size}", "Space used on the whole drive, as reading every folder took too long. The first backup measures what it actually saves.")
                : result.Truncated
                    ? ($"{size[..size.LastIndexOf(' ')]}+ {size[(size.LastIndexOf(' ') + 1)..]}", "At least this much: reading every folder took too long. The first backup measures it exactly.")
                    : ($"~{size}", "Estimated before the first backup from the files this backup includes, its exclusions applied. The first backup measures it exactly.");
            if (result.UnreadableNote is { } note) SizeTooltip += Environment.NewLine + note;
            if (result.CloudOnlyNote is { } cloud) SizeTooltip += Environment.NewLine + cloud;
        }
    }

    /// <summary>One pill in the Destinations column. Identity fields
    /// (Id, Name, Kind, Tooltip) are immutable — only the live progress
    /// fill (<see cref="PercentComplete"/>) mutates. Pills are replaced
    /// wholesale by <see cref="Apply(Backspace.Models.Backup)"/> only
    /// when the structural set changes (different destinations, ids,
    /// kinds); otherwise the existing pills are kept so their progress
    /// state survives a Config.Changed-driven refresh that didn't
    /// actually touch the destinations list. <see cref="IsClickable"/>
    /// is false for "(missing)" pills (the underlying destination was
    /// deleted from config), so the view can disable the button
    /// instead of letting the click silently no-op.
    /// </summary>
    public sealed partial class DestinationPill : ObservableObject
    {
        public string Id { get; }
        public string Name { get; }
        public DestinationKind Kind { get; }
        public string Tooltip { get; }
        public string DisplayLabel { get; }
        public bool IsClickable => !string.IsNullOrEmpty(Id);

        /// <summary>0..100 — drives the per-pill progress fill so the
        /// user can see at a glance which destination is lagging in a
        /// multi-destination run. Updated from the
        /// <see cref="BackupProgressService"/> snapshot's
        /// <see cref="BackupProgressService.BackupProgress.Destinations"/>
        /// dictionary keyed by destination Name.</summary>
        [ObservableProperty] private double _percentComplete;

        public DestinationPill(string id, string name, DestinationKind kind, string tooltip, string? location = null)
        {
            Id = id; Name = name; Kind = kind; Tooltip = tooltip;
            DisplayLabel = (kind is DestinationKind.LocalFolder or DestinationKind.ExternalDrive or DestinationKind.NetworkShare)
                && !string.IsNullOrWhiteSpace(location) ? location : name;
        }
    }
}
