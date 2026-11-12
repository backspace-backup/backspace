using System;
using System.Collections.Generic;

namespace Backspace.Models;

/// <summary>
/// What flavour of run this record describes. Defaults to
/// <see cref="Backup"/> so existing JSON history files (that pre-date
/// this field) deserialise unchanged. <see cref="Restore"/> and
/// <see cref="TestRestore"/> are surfaced in the Activity &amp; logs RUN
/// dropdown alongside backup runs — the user explicitly asked:
/// "Restore logs should show up in Activity &amp; Logs … There should
/// be a dedicated entry in RUN."
/// </summary>
public enum RunKind
{
    Backup,
    Restore,
    TestRestore,
    /// <summary>
    /// "Erase backup at destination" or "Delete destination entirely"
    /// — covers both flavours of cleanup that drive
    /// <see cref="Backspace.Services.StorageCleaner"/>. For
    /// destination-wide wipes the parent <see cref="RunRecord"/>'s
    /// <c>BackupName</c> is the literal <c>[Destinations]</c>
    /// sentinel; for per-backup erases it's the real backup name.
    /// </summary>
    Erase,
    /// <summary>Stand-alone retention/prune run, fired by a
    /// <see cref="Backspace.Models.Backup.PruneSchedule"/> entry in
    /// the OS scheduler. Distinct from <see cref="Backup"/> so
    /// Activity &amp; Logs can show "prune ran Sunday 03:00, took
    /// 14m" separately from the actual backup history.</summary>
    Prune,
    /// <summary>Stand-alone integrity check, fired by a
    /// <see cref="Backspace.Models.Backup.CheckSchedule"/> entry.
    /// Same separation rationale as <see cref="Prune"/>.</summary>
    Check,
}

/// <summary>
/// A single backup / restore / test-restore run outcome, serialized to disk
/// next to the log file. Lets the dashboard render recent history without
/// parsing the full log.
/// </summary>
public sealed class RunRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string BackupId { get; set; } = "";
    public string BackupName { get; set; } = "";

    /// <summary>
    /// What flavour of run this is. Defaults to <see cref="RunKind.Backup"/>
    /// so existing per-backup history.json files (which never wrote a
    /// Kind field) round-trip cleanly.
    /// </summary>
    public RunKind Kind { get; set; } = RunKind.Backup;
    public bool TriggeredAutomatically { get; set; }

    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }

    public BackupRunStatus Status { get; set; }

    /// <summary>Short human-readable summary ("1,234 new, 56 MB uploaded in 2m 11s").</summary>
    public string Summary { get; set; } = "";

    /// <summary>Absolute path to the stored log file.</summary>
    public string LogPath { get; set; } = "";

    /// <summary>For the "last revision" views in the UI.</summary>
    public int? RevisionNumber { get; set; }

    public long BytesUploaded { get; set; }
    public int FilesNew { get; set; }
    public int FilesModified { get; set; }
    public int FilesRemoved { get; set; }

    /// <summary>What the cleanup after this run removed (U142): old backups
    /// the keep rules no longer keep, and the storage that freed.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int SnapshotsRemoved { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long BytesFreed { get; set; }

    /// <summary>Online-only files the backup skipped (U168): restic skips
    /// them quietly, so this run's listing of the sources counts them.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long CloudOnlySkipped { get; set; }

    /// <summary>How long the run waited for its turn ("Queued: waiting for
    /// local_c", U156) before and between its parts (U173). Its duration
    /// leaves this out; the hover shows it separately.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public TimeSpan QueuedFor { get; set; }

    /// <summary>The time the run spent working: from start to end, less
    /// the time it waited for its turn.</summary>
    public TimeSpan WorkingTime(DateTime nowUtc)
    {
        var total = (EndedUtc ?? nowUtc) - StartedUtc - QueuedFor;
        return total < TimeSpan.Zero ? TimeSpan.Zero : total;
    }

    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// One outcome per (source × destination) cell in the run fan-out.
    /// A backup with two sources and two destinations produces four
    /// entries. Populated incrementally as cells complete, so the
    /// dashboard tile can show partial progress while long runs are
    /// still live.
    /// </summary>
    public List<CellOutcome> Cells { get; set; } = new();
    public List<BackupCheckResult> Checks { get; set; } = new();

    /// <summary>The check entry this check run was (U179). Null for checks
    /// recorded before checks were a list: those count for every entry.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CheckId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CheckKind? CheckKind { get; set; }
}

/// <summary>
/// Outcome of a single (source × destination) cell within a
/// multi-source / multi-target run.
/// </summary>
public sealed class CellOutcome
{
    public List<BackupCheckResult> Checks { get; set; } = new();
    /// <summary>Absolute source path this cell covered.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Human-readable destination name (e.g. "Dropbox — personal").</summary>
    public string DestinationName { get; set; } = "";

    /// <summary>Which BackupTarget.StorageName the cell ran against
    /// (per-target alias; part of the repo id).</summary>
    public string StorageName { get; set; } = "default";

    public BackupRunStatus Status { get; set; }

    /// <summary>Set when this destination's part waits for the network
    /// (U226): "unmetered" (the connection is metered), "connection" (no
    /// network) or "reachable" (it couldn't be reached). A scheduled backup
    /// runs it again once that changes, the other destinations meanwhile
    /// keeping their schedule.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? WaitingFor { get; set; }

    /// <summary>The destination's id, for a part waiting for the network (U226).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DestinationId { get; set; }

    /// <summary>Short one-line summary for this cell; surfaced in notifications and the tile.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Number of retry attempts this cell burned before settling on Status.</summary>
    public int AttemptsMade { get; set; }

    /// <summary>Last error message captured from the engine's
    /// stdout/stderr, if the cell ultimately failed.</summary>
    public string? LastError { get; set; }

    /// <summary>How long this cell took, start to last attempt's end.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Path to the per-cell engine log file, when one was
    /// created. Set to the LogPath of the underlying RunRecord
    /// produced by ResticEngine so LogsView can offer it as
    /// the Selected Run's history entry — earlier this stayed empty
    /// for cells that failed during init, leaving "Selected run"
    /// blank for the user.</summary>
    public string LogPath { get; set; } = "";

    /// <summary>What this cell's backup added, from restic's summary (U142):
    /// new and changed files, and the data added to the storage.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int FilesNew { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int FilesModified { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long BytesAdded { get; set; }
    /// <summary>What this cell's cleanup removed (U142).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int SnapshotsRemoved { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long BytesFreed { get; set; }
    /// <summary>Online-only files this cell's backup skipped (U168).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long CloudOnlySkipped { get; set; }

    /// <summary>A check part not started because its destination was
    /// unavailable (drive not connected, share offline): the check waits for
    /// the next backup there (U174, <see cref="Services.CheckOverdue"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool DestinationUnavailable { get; set; }
}
