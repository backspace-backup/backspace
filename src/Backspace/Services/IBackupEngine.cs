using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Backend-agnostic contract for the bytes-on-disk part of a backup engine.
/// The live implementation is <see cref="ResticBackrestEngine"/>, which
/// drives a bundled Backrest daemon (which in turn drives restic +
/// rclone) over Connect-Go RPC.
///
/// The surface is deliberately narrow: every method here has at least
/// one live call site in the app. Engine-specific helpers (RPC plumbing,
/// error translation, snapshot-id minting, restic URI shaping,
/// JsonExtensionData round-trip) stay on the concrete
/// <see cref="ResticBackrestEngine"/> — they're engine details, not
/// part of the contract.
/// </summary>
public interface IBackupEngine
{
    /// <summary>Fires for every line of engine stdout (stamped with HH:mm:ss).
    /// Live-tail subscribers (LogsViewModel, StorageCleaner's progress
    /// hooks) attach here.</summary>
    event Action<string>? LineWritten;

    /// <summary>Re-broadcast a line into <see cref="LineWritten"/>. Used by
    /// services that spawn the engine binary directly (RestoreEngine,
    /// RevisionBrowser, BackupVerifier) so their output also surfaces in
    /// the live-tail.</summary>
    void RaiseLineWritten(string line);

    /// <summary>Per-cell backup. The implementation runs prune and/or
    /// check inline when <see cref="Backup.PruneSchedule"/> /
    /// <see cref="Backup.CheckSchedule"/> are null (i.e. the user picked
    /// "After each backup" rather than a standalone cadence). Whether
    /// those are folded into a single CLI invocation or sequenced
    /// separately is the implementation's choice.</summary>
    Task<RunRecord> BackupPruneCheckAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        CancellationToken ct,
        Action<string>? lineSink = null);

    /// <summary>Prune everything except the latest snapshot. Used by the
    /// BackupEditor's "free up space" action when retention is set very
    /// tight.</summary>
    Task<RunRecord> PruneToLatestAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        CancellationToken ct);

    /// <summary>Stand-alone prune (forget + repack) for a single
    /// (source × target) cell. Used by the orchestrator's independent
    /// prune cadence (<see cref="Backup.PruneSchedule"/>). The cell
    /// fan-out + RunRecord aggregation happens above this call.</summary>
    Task<RunRecord> RunPruneOnlyAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        CancellationToken ct,
        Action<string>? lineSink = null);

    /// <summary>Stand-alone integrity check for a single
    /// (source × target) cell. Used by the orchestrator's independent
    /// check cadence (<see cref="Backup.CheckSchedule"/>).</summary>
    Task<RunRecord> RunCheckOnlyAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        CancellationToken ct,
        Action<string>? lineSink = null);

    /// <summary>Remove every revision of a specific snapshot id and
    /// garbage-collect the chunks that only belonged to that chain.
    /// Chunks shared with other snapshots are left alone. Invoked by
    /// the cascading-delete flow in StorageCleaner.</summary>
    Task PruneSnapshotIdAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        string snapshotId,
        CancellationToken ct);

    /// <summary>Delete every chunk that isn't referenced by a current
    /// snapshot. Final step of a destination-wipe after all snapshot ids
    /// have been pruned.</summary>
    Task PruneExhaustiveAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        CancellationToken ct);

    /// <summary>List revisions on the storage. Read-only; safe to call
    /// concurrently with backups.</summary>
    Task<RunRecord> ListRevisionsAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        CancellationToken ct);

    /// <summary>Enumerate every snapshot the engine has on the storage
    /// for this (backup, source) pair. The list is sorted newest-first
    /// (highest <see cref="RevisionSummary.Number"/> at index 0); the
    /// Browser surface and the UI both rely on that ordering.</summary>
    Task<IReadOnlyList<RevisionSummary>> ListSnapshotsAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        CancellationToken ct);

    /// <summary>Walk the file tree captured in a specific revision.
    /// Used by the restore picker to populate its tree view and by
    /// integration tests to assert what's been backed up.</summary>
    Task<IReadOnlyList<RevisionFile>> ListSnapshotFilesAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        int revision,
        CancellationToken ct, string? expectedSnapshotId = null);

    /// <summary>Lazy single-directory listing — one RPC, immediate
    /// children only, no recursion. Drives the WinFSP snapshot mount
    /// (which can't tolerate enumerating the entire tree upfront
    /// because that's O(N) round-trips and stalls the kernel
    /// dispatcher for tens of seconds on a real backup).
    /// <paramref name="dirRelativePath"/> is source-relative, with
    /// forward slashes and no leading slash; "" means the source
    /// root. Returns null when the path doesn't exist or isn't a
    /// directory in the snapshot.</summary>
    Task<IReadOnlyList<RevisionDirEntry>?> ListSnapshotDirectoryAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        int revision,
        string dirRelativePath,
        CancellationToken ct, string? expectedSnapshotId = null);

    /// <summary>Bulk recursive snapshot tree — returns EVERY file and
    /// directory in the snapshot in one call. This is the fast-path
    /// for warming a WinFSP mount: the previous per-directory RPC
    /// walk costs ~500 ms per call (each Backrest RPC fork+execs
    /// restic, restic loads the repo, dumps one dir's children, exits
    /// — totally dominated by spawn overhead). Issuing
    /// <c>restic ls --json &lt;snap&gt;</c> ONCE returns the same
    /// information for ~1 second total, regardless of depth or count
    /// (benchmarked on a real 2579-entry snapshot:
    /// 480 ms × 180 dirs vs. 962 ms for the whole tree). Paths are
    /// source-relative with forward slashes and no leading slash;
    /// directories report SizeBytes = 0. Returns null when the
    /// implementation can't do a bulk listing for this destination
    /// shape (e.g. cloud destinations where restic shells to rclone
    /// — the caller falls back to the lazy per-directory path).</summary>
    Task<IReadOnlyList<RevisionTreeNode>?> ListSnapshotTreeAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        int revision,
        CancellationToken ct, string? expectedSnapshotId = null);

    /// <summary>Bulk restore — materialise EVERY file in a snapshot's
    /// source subtree onto <paramref name="targetDir"/> in one restic
    /// invocation. This is the fast-path for WinFSP mounts: the per-
    /// file <see cref="RestoreFilesAsync"/> path costs hundreds of
    /// milliseconds PER FILE (each pattern restore fork+execs restic,
    /// loads the repo, restores one file, exits), so a copy of a
    /// 2500-file snapshot takes hours. One bulk restore of the same
    /// subtree completes in seconds.
    ///
    /// Files land at source-relative paths under
    /// <paramref name="targetDir"/> (matching the same layout the per-
    /// file path produces, so the mount-cache check
    /// <c>File.Exists(Path.Combine(cache, rel))</c> sees them).
    /// Returns false when the implementation can't do a bulk restore
    /// for this destination shape (cloud → the caller falls back to
    /// per-file restore via <see cref="RestoreFilesAsync"/>).</summary>
    Task<bool> RestoreSnapshotSubtreeAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        int revision,
        string targetDir,
        CancellationToken ct, string? expectedSnapshotId = null);

    /// <summary>Restore files matching <paramref name="includePatterns"/>
    /// from a specific snapshot into <paramref name="targetPath"/>.
    /// Patterns follow restic's <c>--include</c> shape — anchor-friendly
    /// globs relative to the snapshot subfolder. Use
    /// <see cref="RestoreSelectionMinimizer.Compute"/> to derive the
    /// minimal cover when the user picked whole folders.
    ///
    /// Matches Backrest's native restore semantics: overall progress
    /// (total / restored counts, bytes) is the only payload — no
    /// per-file results. <paramref name="onProgress"/> fires for each
    /// progress update emitted by restic's stdout. The returned task
    /// completes once the restic process reaches a terminal status;
    /// the returned <see cref="RestoreOpResult"/> carries the final
    /// numbers + any terminal error message.</summary>
    Task<RestoreOpResult> RestoreFilesAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        int revision,
        IReadOnlyList<string> includePatterns,
        string targetPath,
        Action<RestoreProgressUpdate>? onProgress,
        CancellationToken ct,
        IReadOnlyList<string>? expectedLeaves = null, string? expectedSnapshotId = null, bool overwrite = true, bool preserveStructure = true);

    /// <summary>Initialize the per-(backup,source) repo state on disk if
    /// it doesn't already exist, including registering the destination
    /// under <paramref name="storageName"/>. Cheap when already initialized;
    /// every operation that touches a storage calls this first.</summary>
    Task EnsureInitializedAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        CancellationToken ct);

    /// <summary>Rotate the master-key encryption password used to protect
    /// the storage's metadata. Touches only a small config file; chunks
    /// stay encrypted with the unchanged master key. Returns the exit
    /// code and captured stdout so the UI can surface failures verbatim.</summary>
    Task<(int exitCode, string output)> ChangeStoragePasswordAsync(
        Destination destination,
        string storageName,
        string oldPassword,
        string newPassword,
        CancellationToken ct);
}

/// <summary>
/// Final summary of a restore operation, mirroring Backrest's
/// <c>RestoreProgressEntry</c>: overall numbers only, plus a
/// terminal-status field carrying restic's exit verdict and any error
/// message. See AGENT.md design principle 1: "Backrest owns its own
/// state" — Backrest doesn't synthesize per-file results, and neither
/// do we.
/// </summary>
public sealed record RestoreOpResult(
    int TotalFiles,
    int FilesRestored,
    long TotalBytes,
    long BytesRestored,
    RestoreTerminalStatus Status,
    string? ErrorMessage);

/// <summary>Streamed mid-restore progress update — fired by the engine
/// as restic emits status messages on stdout. Same overall-numbers
/// shape as <see cref="RestoreOpResult"/>; the engine emits a final
/// progress event matching the final result.</summary>
public sealed record RestoreProgressUpdate(
    int TotalFiles,
    int FilesRestored,
    long TotalBytes,
    long BytesRestored,
    double PercentDone);

public enum RestoreTerminalStatus { Success, Warning, Error, Cancelled }

/// <summary>Immediate child of a snapshot directory — the unit
/// <see cref="IBackupEngine.ListSnapshotDirectoryAsync"/> returns.
/// <see cref="Name"/> is the LEAF (no slashes), so the caller is free
/// to construct the full relative path from the directory it asked
/// about. <see cref="SizeBytes"/> is 0 for directories.</summary>
public sealed record RevisionDirEntry(
    string Name,
    bool IsDirectory,
    long SizeBytes,
    DateTime ModifiedUtc);

/// <summary>One node in a bulk recursive snapshot listing — the unit
/// <see cref="IBackupEngine.ListSnapshotTreeAsync"/> returns.
/// <see cref="Path"/> is the FULL source-relative path with forward
/// slashes and no leading slash ("Documents/notes/file.txt"). Empty
/// path = the source root (the directory at the root of this
/// snapshot). <see cref="SizeBytes"/> is 0 for directories.</summary>
public sealed record RevisionTreeNode(
    string Path,
    bool IsDirectory,
    long SizeBytes,
    DateTime ModifiedUtc);
