using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Backend-agnostic contract for the bytes-on-disk part of a backup engine.
/// The live implementation is <see cref="ResticEngine"/>, which
/// runs the bundled restic (and, for cloud destinations, rclone) directly
/// (U175).
///
/// The surface is deliberately narrow: every method here has at least
/// one live call site in the app. Engine-specific helpers (command lines,
/// error translation, snapshot-id minting, restic URI shaping) stay on the concrete
/// <see cref="ResticEngine"/> — they're engine details, not
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

    /// <summary>Per-cell backup. The implementation runs the cleanup
    /// (forget and prune) inline when <see cref="Backup.PruneSchedule"/> is
    /// null (the user picked "After each backup" rather than a standalone
    /// cadence). Checks are entries of their own (U179), run through
    /// <see cref="RunCheckOnlyAsync"/>, never inside a backup.</summary>
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

    /// <summary>True once <see cref="RunCheckOnlyAsync"/> honours
    /// <see cref="Backup.DataCheckLimitBytes"/> (U179): a data sample's own
    /// limit, and <see cref="long.MaxValue"/> for a full data read
    /// (<c>check --read-data</c>). Until then the engine reads 1% of the
    /// stored data, up to 1 GiB, whatever the limit, and the orchestrator
    /// does not start a full data read.</summary>
    bool HonoursDataCheckLimit => false;

    /// <summary>Stand-alone check for a single (source × target) cell, one
    /// check entry at a time (<see cref="Backup.Checks"/>, U179): a structure
    /// check, or with <see cref="Backup.CheckReadDataSample"/> a data read
    /// limited by <see cref="Backup.DataCheckLimitBytes"/>.</summary>
    Task<RunRecord> RunCheckOnlyAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        CancellationToken ct,
        Action<string>? lineSink = null,
        Action<CheckStep, double>? progress = null);

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

    /// <summary>Lazy single-directory listing — one restic listing, immediate
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
    /// for warming a WinFSP mount: a per-directory walk costs ~500 ms per
    /// folder (each listing starts restic, which loads the repo, dumps one
    /// dir's children and exits — totally dominated by spawn overhead). Issuing
    /// <c>restic ls --json &lt;snap&gt;</c> ONCE returns the same
    /// information for ~1 second total, regardless of depth or count
    /// (benchmarked on a real 2579-entry snapshot:
    /// 480 ms × 180 dirs vs. 962 ms for the whole tree). Paths are
    /// source-relative with forward slashes and no leading slash;
    /// directories report SizeBytes = 0. Returns null when the
    /// implementation can't do a bulk listing for this destination
    /// shape (the caller falls back to the lazy per-directory path).
    /// <paramref name="progress"/> hears how many files (not folders) have
    /// been read so far, for a large snapshot's mount (U158): comparable with
    /// the snapshot summary's file count, for a percentage (U166).</summary>
    Task<IReadOnlyList<RevisionTreeNode>?> ListSnapshotTreeAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        int revision,
        CancellationToken ct, string? expectedSnapshotId = null, IProgress<int>? progress = null);

    /// <summary>Bulk restore — materialise EVERY file in a snapshot's
    /// source subtree onto <paramref name="targetDir"/> in one restic
    /// invocation. The mounted drive no longer uses it: opening two files
    /// of a 600,000-file snapshot started restoring all of it (U158); the
    /// mount fetches one small folder at a time instead.
    ///
    /// Files land at source-relative paths under
    /// <paramref name="targetDir"/>.
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
    /// Overall progress (total / restored counts, bytes) is the only
    /// payload — no per-file results. <paramref name="onProgress"/> fires for each
    /// progress update emitted by restic's stdout. The returned task
    /// completes once the restic process reaches a terminal status;
    /// the returned <see cref="RestoreOpResult"/> carries the final
    /// numbers + any terminal error message.
    /// <paramref name="onStaging"/> receives the temporary folder where
    /// output arrives once it exists, and <c>null</c> once the restore has
    /// finished with it (U154).</summary>
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
        IReadOnlyList<string>? expectedLeaves = null, string? expectedSnapshotId = null, bool overwrite = true, bool preserveStructure = true,
        RestoreControl? control = null, Action<string?>? onStaging = null);

    /// <summary>Writes one file of a snapshot, in order, to
    /// <paramref name="output"/> as its bytes arrive from the destination,
    /// without restoring it anywhere (U167: a file copied out of the mounted
    /// drive starts at once and moves with the download).
    /// <paramref name="fileRelativePath"/> is source-relative, forward slashes,
    /// the snapshot's own spelling. Returns false, having written nothing, when
    /// this engine cannot (the caller restores the file instead).</summary>
    Task<bool> DumpSnapshotFileAsync(
        Backup backup,
        string sourcePath,
        Destination destination,
        string storageName,
        int revision,
        string fileRelativePath,
        Stream output,
        CancellationToken ct, string? expectedSnapshotId = null)
        => Task.FromResult(false);

    /// <summary>Prepares what the engine keeps for this (backup, source,
    /// destination) before it is read. The restic engine keeps nothing
    /// (each command carries the repository) and never creates a repository
    /// by looking; the in-memory test engine creates its state here.</summary>
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
/// Final summary of a restore operation: overall numbers only, plus a
/// terminal-status field carrying restic's exit verdict and any error
/// message. restic's restore reports no per-file results, and neither
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
