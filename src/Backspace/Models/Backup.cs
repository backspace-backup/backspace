using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Backspace.Models;

/// <summary>
/// A backup = one or more source folders + one or more destinations + policy.
///
/// Multi-source semantics: each entry in <see cref="SourcePaths"/>
/// becomes its own (source × destination × storageName) plan in
/// the Backrest sidecar — see <see cref="Services.ResticBackrestEngine.MakePlanId"/>.
/// The orchestrator loops over sources × destinations, so a backup
/// named "computer" with sources
/// <c>[C:\Users\me\Documents, C:\Users\me\Pictures]</c> and one
/// Dropbox destination produces two plans (sharing one restic repo)
/// but one scheduled run. Per-source retention applies correctly
/// because restic's snapshot.tags carry the plan id.
/// </summary>
/// <summary>
/// Which UI mode the user last opened this backup's editor in. Used by
/// the unified BackupCreationWindow to re-open in the same mode the
/// user committed in last time, so Edit doesn't toss them between Easy
/// and Advanced mid-task. Easy = the 3-step wizard; Advanced = the
/// tabbed editor with all the knobs. Default Advanced for backups
/// created before this field existed (their schedule may include
/// Advanced-only settings the Easy mode wouldn't reproduce faithfully).
/// </summary>
public enum BackupEditMode
{
    Advanced = 0,
    Easy = 1,
}

public sealed class Backup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Last edit mode used. See <see cref="BackupEditMode"/>.</summary>
    public BackupEditMode LastEditMode { get; set; } = BackupEditMode.Advanced;

    /// <summary>
    /// User-facing display name. Free-form text — the user can set
    /// this to anything (including spaces and punctuation) up to
    /// <see cref="MaxNameLength"/> chars. Filesystem-embedded usages
    /// (repo / log directory names, scheduled-task names) sanitise
    /// independently via <c>AppPaths.Sanitize</c>; the snapshot id
    /// (per-storage tag passed to restic) is derived from <see cref="SnapshotIdSeed"/>
    /// (locked at first save) so a rename here doesn't fork the
    /// snapshot chain. The user reported that the prior regex-locked
    /// shape was replacing spaces with underscores on every save
    /// when they renamed; that normalisation was wrong for a
    /// purely-display field.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Hard cap on <see cref="Name"/> length. 255 matches the typical
    /// filesystem max-segment length on Windows / macOS / Linux —
    /// even though we sanitise into a derived path, keeping the user
    /// input under the same cap means the on-screen text never
    /// exceeds what could survive a round-trip into a directory name.
    /// </summary>
    public const int MaxNameLength = 255;

    /// <summary>
    /// Immutable seed for the engine's plan/snapshot id. Set ONCE at
    /// backup creation from the initial name (sanitised) and never
    /// updated thereafter, so a rename of <see cref="Name"/> doesn't
    /// silently fork a new snapshot chain on the storage and orphan
    /// every prior revision. Restic groups snapshots by tag, so
    /// keeping the tag (= plan id derived from this seed) stable
    /// across renames preserves history.
    /// <para>
    /// Empty string is the legacy/migration value: backups created
    /// before this field existed have no seed, and the engine
    /// falls back to the current Name for those — preserving their
    /// existing chain. The first edit of a legacy backup populates
    /// this field from the then-current Name so subsequent renames
    /// are safe.
    /// </para>
    /// </summary>
    public string SnapshotIdSeed { get; set; } = "";

    /// <summary>
    /// Returns true iff <paramref name="name"/> is acceptable as a
    /// display Name: non-empty after trimming and within
    /// <see cref="MaxNameLength"/> chars. The legacy regex
    /// (<c>^[A-Za-z0-9_-]+$</c>) was relaxed when the Name became
    /// purely-display — filesystem and snapshot-id consumers do their
    /// own sanitisation downstream.
    /// </summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength;

    /// <summary>
    /// Coerce an arbitrary string into a valid Backup.Name: whitespace
    /// becomes '_', non-letter / non-digit / non-(_-) characters are
    /// dropped, leading/trailing '_' or '-' trimmed, empty result falls
    /// back to "my-backup". Idempotent for already-valid names.
    /// Use this anywhere <see cref="Name"/> is set from outside the
    /// regex-enforcing UI editor.
    /// </summary>
    public static string SanitizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "my-backup";
        var chars = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            if (char.IsLetterOrDigit(ch)) chars.Append(ch);
            else if (ch == '-' || ch == '_') chars.Append(ch);
            else if (char.IsWhiteSpace(ch)) chars.Append('_');
            // anything else is dropped silently
        }
        var clean = chars.ToString().Trim('_', '-');
        return clean.Length == 0 ? "my-backup" : clean;
    }

    /// <summary>
    /// Absolute paths to back up. At least one is required; the editor UI
    /// enforces that. Order matters only cosmetically (the list displays
    /// and iterates in this order). Each path becomes its own Backrest
    /// plan id (<c>&lt;backup-id&gt;-&lt;dest-id&gt;-&lt;storage&gt;-&lt;hash(source)&gt;</c>),
    /// so renaming the backup keeps history while adding a source
    /// starts a fresh plan beside the existing ones.
    /// </summary>
    public List<string> SourcePaths { get; set; } = new();

    public List<BackupTarget> Targets { get; set; } = new();

    public string FiltersText { get; set; } = "";

    // ---- common options (surfaced up front in the UI) ----

    /// <summary>
    /// 0 means "auto" — the runner picks a good value per destination kind
    /// (1 for Local / External / Network, 4 for cloud/S3). Any non-zero
    /// value is treated as a user-chosen global override that applies to
    /// every target in the fan-out. Per-target granularity still lives on
    /// <see cref="BackupTarget.ThreadsOverride"/>.
    /// </summary>
    public int Threads { get; set; } = 0;

    /// <summary>Volume Shadow Copy — defaults on so backups of system
    /// drives capture open files (Outlook PSTs, locked databases,
    /// running browser profiles). Windows-only; the flag is harmless
    /// on Unix where restic ignores it. Auto-elevation through
    /// <see cref="Services.VssElevationCoordinator"/> handles the UAC
    /// dance for users running the GUI unelevated.</summary>
    public bool UseVss { get; set; } = true;

    /// <summary>Explicit acceptance of incomplete locked-file coverage when
    /// Windows snapshots cannot be used. Older disabled configurations need
    /// acknowledgement before their next run; they are never silently accepted.</summary>
    public bool VssDisabledAcknowledged { get; set; }

    /// <summary>
    /// Skip cloud-only / "online-only" placeholder files (OneDrive
    /// Files-On-Demand, Dropbox cloud-only, iCloud optimised storage)
    /// instead of downloading them on the fly during backup.
    /// Defaults <c>true</c> because the alternative — silently pulling
    /// gigabytes of cloud content onto the local disk every backup —
    /// is a worse default for almost every user. Off when the user
    /// genuinely wants to mirror Dropbox / OneDrive into a second
    /// destination (then the files get materialised at backup time so
    /// restic can read their contents).
    /// </summary>
    public bool ExcludeCloudFiles { get; set; } = true;

    /// <summary>
    /// Keep policy as a list of "interval:age" pairs — e.g.
    /// "0:700 30:365 7:90 1:14" reads as "keep all snapshots
    /// up to 700 days old, then thin to one every 30 days back
    /// to 365 days, one every 7 days back to 90, one a day back
    /// to 14". 0 in the interval slot means exhaustive. The
    /// migrator parses this same shape from imported Duplicacy
    /// configs; the engine maps it onto Backrest's RetentionPolicy
    /// (time-bucketed counts) at plan-build time.
    /// </summary>
    public string KeepPolicy { get; set; } = "0:365 30:90 7:30 1:7";

    /// <summary>
    /// Cadence for the prune (forget + repack) pass.
    /// • <c>null</c> = run inline after every backup (the cheap default
    ///   for small repos).
    /// • non-null = independent cadence; a separate OS-scheduler task
    ///   fires at the configured frequency and runs prune for this
    ///   backup. Inline post-backup prune is then suppressed.
    /// Power users typically graduate from inline to weekly off-hours
    /// once the repo grows past the point where prune is longer than
    /// the backup itself.
    /// </summary>
    public BackupSchedule? PruneSchedule { get; set; }

    /// <summary>
    /// Cadence for the integrity-check pass. Same shape as
    /// <see cref="PruneSchedule"/>: null = inline after every backup,
    /// non-null = OS-scheduler task. Big-repo best practice is monthly.
    /// </summary>
    public BackupSchedule? CheckSchedule { get; set; }

    /// <summary>Stop if the network becomes metered mid-run (watchdog).</summary>
    public bool AbortOnMeteredNetwork { get; set; } = true;

    // ---- advanced options (collapsed by default in the UI) ----

    /// <summary>Average chunk size in bytes. Dead in the Backrest +
    /// restic engine path — restic owns chunk sizing through its
    /// own --pack-size knob; the field stays in the model for
    /// backwards-compatible config-file load only.</summary>
    public int? ChunkSize { get; set; }

    /// <summary>Max chunk size in bytes.</summary>
    public int? MaxChunkSize { get; set; }

    /// <summary>Min chunk size in bytes.</summary>
    public int? MinChunkSize { get; set; }

    /// <summary>Bandwidth cap in KB/s (0 = unlimited). Surfaces as -limit-rate.</summary>
    public int BandwidthLimitKBps { get; set; } = 0;

    /// <summary>Extra CLI args appended verbatim. Escape hatch for power users.</summary>
    public string ExtraBackupArgs { get; set; } = "";

    // ---- schedule ----

    public BackupSchedule Schedule { get; set; } = new();

    // ---- monitoring ----

    /// <summary>Healthchecks.io check slug/UUID assigned to this backup. Null if none.</summary>
    public string? HealthcheckId { get; set; }
    /// <summary>Exact ping URL returned by the management API, including self-hosted endpoints.</summary>
    public string? HealthcheckPingUrl { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunStartUtc { get; set; }
    public DateTime? LastRunEndUtc { get; set; }
    public BackupRunStatus LastRunStatus { get; set; } = BackupRunStatus.NeverRun;
    public string? LastRunSummary { get; set; }

    /// <summary>
    /// Wall-clock UTC of the last run whose status was Success or
    /// Warning. Drives the "tolerable-skip grace period" reference
    /// clock: a run that ends Skipped (drive offline, NAS unreachable)
    /// stays orange until <see cref="SkipNotificationGraceDays"/> have
    /// elapsed since this timestamp — at which point the run is
    /// followed by a warning notification so the user notices something's
    /// wrong rather than tuning out the orange.
    ///
    /// Null = never succeeded. New backups start with this as null;
    /// the grace clock uses <see cref="CreatedUtc"/> as the fallback
    /// reference, so a brand-new backup that never works gets the
    /// same grace window as a once-working one.
    ///
    /// Updated by the orchestrator when a run finishes Success or
    /// Warning. Reset of the grace period (auto on success or manual
    /// via the "I'll be away" button) flows through this field plus
    /// <see cref="SkipNotificationGraceResetUtc"/>.
    /// </summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>
    /// How many days of consecutive Skipped runs the orchestrator
    /// tolerates before warning about the next Skipped run. The stored
    /// status remains Skipped. Per-backup because backups have
    /// different urgency: a daily photo-backup probably wants a tight
    /// 1-2 day window, a weekly cold-storage probably wants a month.
    /// Default 7 covers the common case of "user travels for the
    /// weekend"; users can dial up or down per backup in Easy Mode.
    ///
    /// 0 = no grace (Skipped notifications are immediately eligible).
    /// </summary>
    public int SkipNotificationGraceDays { get; set; } = 7;

    /// <summary>
    /// Manual "restart the grace period" timestamp — set when the user
    /// clicks "Reset grace period" in Easy Mode before a known absence
    /// ("away 2 weeks, my grace is only 7 days"). The effective grace
    /// reference is <c>max(LastSuccessUtc, SkipNotificationGraceResetUtc, CreatedUtc)</c>
    /// so a manual reset extends the window without faking a success
    /// (which would corrupt run-history accounting).
    ///
    /// Null = never manually reset; the natural last-success clock
    /// applies. Auto-cleared (logically) the next time a real success
    /// updates <see cref="LastSuccessUtc"/> past this timestamp.
    /// </summary>
    public DateTime? SkipNotificationGraceResetUtc { get; set; }

    /// <summary>
    /// Wall-clock UTC of the most recent notification the orchestrator
    /// fired for this backup, regardless of severity. Used together
    /// with <see cref="LastNotifiedStatus"/> by the daily-cap path so
    /// a status TRANSITION (Success→Failed) always breaks through the
    /// 24h dedupe — code-review caught the previous "single timestamp,
    /// any severity" shape silently suppressing a Failed toast at 17:00
    /// after a Success at 09:00.
    /// </summary>
    public DateTime? LastNotificationFiredUtc { get; set; }

    /// <summary>
    /// The <see cref="BackupRunStatus"/> recorded at the same moment
    /// as <see cref="LastNotificationFiredUtc"/>. The daily-cap path
    /// suppresses only when the NEW status equals this (or is "less
    /// severe" — a Success notification after a previously-suppressed
    /// Failed should still break through, because that's the "we're
    /// back" signal). Null = never notified; treat as "any new status
    /// is a transition" so the first ever notification always fires.
    /// </summary>
    public BackupRunStatus? LastNotifiedStatus { get; set; }

    /// <summary>
    /// Per-source bytes-on-disk reported by the engine after the last
    /// successful run (restic's summary "Added to the repository: X bytes"
    /// folded into a per-source figure by the orchestrator),
    /// keyed by source path. Surfaced inline next to each source on the
    /// BackupCard so the user can see at a glance which paths are big.
    /// Cleared when a source is removed from <see cref="SourcePaths"/>;
    /// stale entries for paths the user removed don't render anywhere.
    /// </summary>
    public Dictionary<string, long> SourceLastSizeBytes { get; set; } = new();

    // ---- verify (test-restore drill) ----

    /// <summary>
    /// When the last "Test restore" verify drill ran. Null = never
    /// verified. The Dashboard surfaces a warning banner when this is
    /// older than 30 days for any backup, nudging the user to run a
    /// drill before they need to actually restore.
    /// </summary>
    public DateTime? LastVerifyUtc { get; set; }

    /// <summary>
    /// True if the last drill restored every sampled file successfully
    /// AND every byte-comparable file matched its source. Null = never
    /// verified. False = drill detected a problem; the user should
    /// re-run and inspect <see cref="LastVerifySummary"/>.
    /// </summary>
    public bool? LastVerifyPass { get; set; }

    /// <summary>
    /// One-line user-facing summary of the last drill outcome —
    /// "Restored 10 of 10 files, all byte-identical" or
    /// "2 of 10 files failed: …". Surfaced in the Dashboard tile.
    /// </summary>
    public string? LastVerifySummary { get; set; }

    // ---- grace-period helpers (tolerable-skip handling) ----

    /// <summary>
    /// Reference timestamp the grace-period clock starts from. The
    /// effective "we last knew this backup was healthy" moment is the
    /// latest of: last successful run, manual grace reset, and
    /// CreatedUtc as a fallback for never-run backups. Computed
    /// rather than persisted so a stale field can't desync from the
    /// three real timestamps.
    /// </summary>
    [JsonIgnore]
    public DateTime SkipGraceReferenceUtc
    {
        get
        {
            var t = CreatedUtc;
            if (LastSuccessUtc is { } s && s > t) t = s;
            if (SkipNotificationGraceResetUtc is { } r && r > t) t = r;
            return t;
        }
    }

    /// <summary>
    /// True iff a Skipped run AT <paramref name="nowUtc"/> should stay
    /// quiet (grace active) before warning notifications become eligible. Pure
    /// function of model state; tested independently of the
    /// orchestrator. <see cref="SkipNotificationGraceDays"/> of 0
    /// disables grace entirely (every Skipped is eligible for notification).
    /// </summary>
    public bool IsWithinSkipGrace(DateTime nowUtc)
    {
        if (SkipNotificationGraceDays <= 0) return false;
        var elapsed = nowUtc - SkipGraceReferenceUtc;
        return elapsed.TotalDays < SkipNotificationGraceDays;
    }

    /// <summary>
    /// Both editors always offer grace configuration, including before
    /// the first skipped run and after a window expires. The timestamp
    /// is retained for callers of the original visibility predicate;
    /// only notification suppression uses <see cref="IsWithinSkipGrace"/>.
    /// </summary>
    public bool ShouldShowGraceBlock(DateTime nowUtc) => true;

    /// <summary>
    /// True iff the orchestrator must suppress a notification for
    /// <paramref name="newStatus"/>. Cap suppresses only when the
    /// new status is the SAME as the last notified status AND we
    /// fired within the last 24h. Status TRANSITIONS — Success→Failed,
    /// Failed→Skipped, Skipped→Failed, etc. — always break through
    /// so the user sees state changes (the original "single
    /// timestamp, any severity" cap silently swallowed a Failed
    /// notification at 17:00 after a Success at 09:00; code-review
    /// flagged it as a real silent-failure footgun).
    /// </summary>
    public bool IsWithinNotificationDailyCap(DateTime nowUtc, BackupRunStatus newStatus)
    {
        if (LastNotificationFiredUtc is not { } last) return false;
        // First fire of a NEW status always breaks through; cap only
        // kicks in for repeats of the same status within 24h.
        // Upgrade scenario: pre-LastNotifiedStatus build left
        // LastNotificationFiredUtc populated but the status field
        // null. ROUND-3 attempted to handle this by treating null
        // as "cap active for 24h" — but that REINTRODUCED the
        // original silent-failed-after-success footgun for upgrades
        // (a Success notification pre-upgrade silenced a Failed
        // notification 2h after upgrade). Round-4 reverses: when
        // LastNotifiedStatus is null we don't know what the prior
        // severity was, so we BREAK THROUGH. The worst case is one
        // duplicate toast on the first post-upgrade run — strictly
        // less bad than swallowing a real Failed transition.
        if (LastNotifiedStatus is not BackupRunStatus prev) return false;
        if (prev != newStatus) return false;
        return (nowUtc - last).TotalHours < 24;
    }
}

public sealed class BackupTarget
{
    /// <summary>FK into AppConfig.Destinations by Destination.Id.</summary>
    public string DestinationId { get; set; } = "";

    /// <summary>
    /// Storage label that distinguishes targets when one backup
    /// fans out to several destinations. Defaults to "default";
    /// the engine layer maps (DestinationId, StorageName) onto a
    /// single Backrest repo id, so two targets pointing at the
    /// same Destination with different StorageNames produce two
    /// distinct repo chains. Imported from the equivalent "name"
    /// field in legacy yaml storage[] arrays.
    /// </summary>
    public string StorageName { get; set; } = "default";

    /// <summary>Per-target thread override. Null = inherit Backup.Threads.</summary>
    public int? ThreadsOverride { get; set; }
}

public enum BackupRunStatus
{
    NeverRun,
    Running,
    Success,
    Skipped,
    Warning,
    Failed,
}
