using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// Deterministic, on-disk <see cref="IBackupEngine"/> for tests. Treats
/// the destination's <see cref="Destination.PathOrSubpath"/> as a tiny
/// snapshot store and mirrors source files into versioned subfolders:
///
/// <code>
///   {destination.PathOrSubpath}/.backspace-fake-snapshots/
///     {snapshotId}/
///       r0001/
///         (mirrored source tree at revision 1)
///       r0002/
///         (mirrored source tree at revision 2)
///       ...
/// </code>
///
/// Backup copies the source tree into <c>r{next}</c>. List snapshots
/// enumerates the <c>r*</c> directories newest-first. Restore copies
/// files back out of a chosen revision into the caller's target. End
/// result: backup + list + restore + byte-compare round-trips work
/// the same way they would against a real engine, without any
/// backrest.exe / restic.exe binary on disk.
///
/// Operation history is captured on <see cref="Calls"/> so tests can
/// assert "the orchestrator routed through here" without parsing
/// logs.
/// </summary>
internal sealed class InMemoryBackupEngine : IBackupEngine
{
    /// <summary>Subfolder under the destination that holds all simulated
    /// snapshots. Lives under a name no real engine would write.</summary>
    private const string StoreRoot = ".backspace-fake-snapshots";

    public event Action<string>? LineWritten;

    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Backups currently in flight. Incremented at the top of
    /// <see cref="BackupPruneCheckAsync"/> and decremented after the
    /// scripted lines + file mirror finish. Tests that care about
    /// the orchestrator's parallelism contract (disjoint destinations
    /// must NOT serialise) sample <see cref="PeakInFlight"/> through
    /// the run and assert it reached &gt;= 2.</summary>
    public int CurrentInFlight => System.Threading.Volatile.Read(ref _currentInFlight);
    public int PeakInFlight    => System.Threading.Volatile.Read(ref _peakInFlight);

    /// <summary>Zero the peak counter. Tests that assert parallelism
    /// call this before starting their concurrent runs so a previous
    /// test's high-water mark doesn't satisfy this test's assertion.
    /// The engine is shared via ServiceLocator (one instance per
    /// process), so without an explicit reset, peak readings are
    /// cumulative across the whole xUnit run.</summary>
    public void ResetPeakInFlight() => System.Threading.Volatile.Write(ref _peakInFlight, 0);

    private int _currentInFlight;
    private int _peakInFlight;

    /// <summary>Lines the simulated backup emits through LineWritten.
    /// Settable per scenario when the test cares about exact stream
    /// content.</summary>
    public IReadOnlyList<string> ScriptedBackupLines { get; set; } = new[]
    {
        "[fake] starting backup",
        "[fake] enumerating source files",
        "[fake] uploading chunks",
        "[fake] writing snapshot",
    };

    public void RaiseLineWritten(string line)
    {
        try { LineWritten?.Invoke(line); } catch { /* tee failures don't propagate */ }
    }

    // ---- backup ----

    public async Task<RunRecord> BackupPruneCheckAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct, Action<string>? lineSink = null)
    {
        Calls.Enqueue($"BackupPruneCheck({backup.Name},{sourcePath},{destination.Name},{storageName})");

        // Track in-flight count so parallelism tests can prove
        // disjoint-destination backups overlap.
        var inFlight = System.Threading.Interlocked.Increment(ref _currentInFlight);
        UpdatePeakInFlight(inFlight);
        try
        {
            var started = DateTime.UtcNow;
            var logPath = WritePerRunLog(backup, started);
            await EmitScriptedLinesAsync(lineSink, ct, logPath).ConfigureAwait(false);

            // Refuse the backup if the destination isn't writable.
            var (ok, reason) = ProbeDestination(destination);
            if (!ok)
            {
                return FailedRun(backup, started, logPath, reason, lineSink);
            }

            // Stamp the per-source repo directory the way EnsureInitialized
            // would so callers can rely on it existing after a backup, even
            // if they skipped the explicit init step.
            await EnsureInitializedAsync(backup, sourcePath, destination, storageName, ct).ConfigureAwait(false);

            // Snapshot the source: copy every file into the next revision
            // directory under our fake store. Source trees rooted under
            // a single sourcePath; preserve their relative layout.
            var (files, bytes) = await SnapshotSourceAsync(backup, sourcePath, destination, ct).ConfigureAwait(false);

            return new RunRecord
            {
                BackupId = backup.Id,
                BackupName = backup.Name,
                Kind = RunKind.Backup,
                StartedUtc = started,
                EndedUtc = DateTime.UtcNow,
                Status = BackupRunStatus.Success,
                Summary = $"{files} file(s), {bytes} B uploaded (simulated)",
                LogPath = logPath,
                FilesNew = files,
                BytesUploaded = bytes,
            };
        }
        finally
        {
            System.Threading.Interlocked.Decrement(ref _currentInFlight);
        }
    }

    /// <summary>Atomically bump <see cref="_peakInFlight"/> upward to
    /// match the supplied current value. Standard CAS loop so two
    /// threads racing to set the peak both see a consistent monotonic
    /// max.</summary>
    private void UpdatePeakInFlight(int current)
    {
        int snapshot;
        do
        {
            snapshot = System.Threading.Volatile.Read(ref _peakInFlight);
            if (current <= snapshot) return;
        } while (System.Threading.Interlocked.CompareExchange(ref _peakInFlight, current, snapshot) != snapshot);
    }

    // ---- snapshot enumeration ----

    public Task<IReadOnlyList<RevisionSummary>> ListSnapshotsAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        Calls.Enqueue($"ListSnapshots({backup.Name},{sourcePath})");
        var snapshotDir = SnapshotDir(backup, sourcePath, destination);
        if (!Directory.Exists(snapshotDir))
            return Task.FromResult<IReadOnlyList<RevisionSummary>>(Array.Empty<RevisionSummary>());

        var revs = Directory.EnumerateDirectories(snapshotDir, "r*")
            .Select(p => (Path: p, Num: ParseRevisionNumber(System.IO.Path.GetFileName(p))))
            .Where(x => x.Num > 0)
            .OrderByDescending(x => x.Num)
            .Select(x =>
            {
                var dir = new DirectoryInfo(x.Path);
                var files = Directory.EnumerateFiles(x.Path, "*", SearchOption.AllDirectories).ToList();
                var total = files.Sum(f => new FileInfo(f).Length);
                return new RevisionSummary(
                    Number: x.Num,
                    CreatedUtc: dir.CreationTimeUtc,
                    FileCount: files.Count,
                    TotalBytes: total) { SnapshotId = System.IO.Path.GetFileName(x.Path) };
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<RevisionSummary>>(revs);
    }

    public Task<IReadOnlyList<RevisionFile>> ListSnapshotFilesAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, CancellationToken ct, string? expectedSnapshotId = null)
    {
        Calls.Enqueue($"ListSnapshotFiles({backup.Name},{sourcePath},r{revision})");
        var revDir = RevisionDir(backup, sourcePath, destination, revision);
        if (!Directory.Exists(revDir))
            return Task.FromResult<IReadOnlyList<RevisionFile>>(Array.Empty<RevisionFile>());

        var entries = Directory.EnumerateFiles(revDir, "*", SearchOption.AllDirectories)
            .Select(f =>
            {
                var fi = new FileInfo(f);
                var rel = System.IO.Path.GetRelativePath(revDir, f).Replace('\\', '/');
                return new RevisionFile(rel, fi.Length, fi.LastWriteTimeUtc);
            })
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<RevisionFile>>(entries);
    }

    public Task<bool> RestoreSnapshotSubtreeAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, string targetDir, CancellationToken ct, string? expectedSnapshotId = null)
    {
        Calls.Enqueue($"RestoreSnapshotSubtree({backup.Name},{sourcePath},r{revision},{targetDir})");
        var revDir = RevisionDir(backup, sourcePath, destination, revision);
        if (!Directory.Exists(revDir)) return Task.FromResult(false);
        Directory.CreateDirectory(targetDir);
        foreach (var src in Directory.EnumerateFiles(revDir, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(revDir, src);
            var dst = System.IO.Path.Combine(targetDir, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<RevisionTreeNode>?> ListSnapshotTreeAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, CancellationToken ct, string? expectedSnapshotId = null)
    {
        Calls.Enqueue($"ListSnapshotTree({backup.Name},{sourcePath},r{revision})");
        var revDir = RevisionDir(backup, sourcePath, destination, revision);
        if (!Directory.Exists(revDir))
            return Task.FromResult<IReadOnlyList<RevisionTreeNode>?>(null);

        var nodes = new List<RevisionTreeNode>();
        foreach (var d in Directory.EnumerateDirectories(revDir, "*", SearchOption.AllDirectories))
        {
            var di = new DirectoryInfo(d);
            var rel = System.IO.Path.GetRelativePath(revDir, d).Replace('\\', '/');
            nodes.Add(new RevisionTreeNode(rel, IsDirectory: true, 0, di.LastWriteTimeUtc));
        }
        foreach (var f in Directory.EnumerateFiles(revDir, "*", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(f);
            var rel = System.IO.Path.GetRelativePath(revDir, f).Replace('\\', '/');
            nodes.Add(new RevisionTreeNode(rel, IsDirectory: false, fi.Length, fi.LastWriteTimeUtc));
        }
        return Task.FromResult<IReadOnlyList<RevisionTreeNode>?>(nodes);
    }

    public Task<IReadOnlyList<RevisionDirEntry>?> ListSnapshotDirectoryAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, string dirRelativePath, CancellationToken ct, string? expectedSnapshotId = null)
    {
        Calls.Enqueue($"ListSnapshotDirectory({backup.Name},{sourcePath},r{revision},'{dirRelativePath}')");
        var revDir = RevisionDir(backup, sourcePath, destination, revision);
        if (!Directory.Exists(revDir))
            return Task.FromResult<IReadOnlyList<RevisionDirEntry>?>(null);

        var rel = (dirRelativePath ?? "").Replace('/', System.IO.Path.DirectorySeparatorChar).Trim(System.IO.Path.DirectorySeparatorChar);
        var dirOnDisk = rel.Length == 0 ? revDir : System.IO.Path.Combine(revDir, rel);
        if (!Directory.Exists(dirOnDisk))
            return Task.FromResult<IReadOnlyList<RevisionDirEntry>?>(null);

        var children = new List<RevisionDirEntry>();
        foreach (var sub in Directory.EnumerateDirectories(dirOnDisk))
        {
            var di = new DirectoryInfo(sub);
            children.Add(new RevisionDirEntry(di.Name, IsDirectory: true, 0, di.LastWriteTimeUtc));
        }
        foreach (var f in Directory.EnumerateFiles(dirOnDisk))
        {
            var fi = new FileInfo(f);
            children.Add(new RevisionDirEntry(fi.Name, IsDirectory: false, fi.Length, fi.LastWriteTimeUtc));
        }
        children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<RevisionDirEntry>?>(children);
    }

    // ---- restore ----

    public Task<RestoreOpResult> RestoreFilesAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, IReadOnlyList<string> includePatterns, string targetPath,
        Action<RestoreProgressUpdate>? onProgress, CancellationToken ct,
        IReadOnlyList<string>? expectedLeaves = null, string? expectedSnapshotId = null, bool overwrite = true, bool preserveStructure = true,
        RestoreControl? control = null)
    {
        // The 2026-06-12 restore-stack refactor moved the test engine to
        // overall-progress semantics. The in-memory engine continues to
        // copy file-by-file under the hood (its storage IS per-file),
        // but the OUTCOME it reports matches the real engine: counts +
        // a terminal status, no per-file callbacks. expectedLeaves
        // (when supplied) lets the engine copy only the listed leaves;
        // when null, it expands each includePatterns entry as a leaf
        // or directory and copies accordingly.
        var revDir = RevisionDir(backup, sourcePath, destination, revision);
        Directory.CreateDirectory(targetPath);
        var encryptionKey = EncryptionKeyFor(destination);

        var leaves = expectedLeaves ?? includePatterns.SelectMany(pattern =>
        {
            var relative = pattern.Replace('\\', '/').TrimStart('/');
            var full = Path.Combine(revDir, relative.Replace('/', Path.DirectorySeparatorChar));
            return Directory.Exists(full)
                ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(revDir, file).Replace('\\', '/'))
                : new[] { relative };
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Calls.Enqueue($"RestoreFiles({backup.Name},r{revision},{leaves.Count})");

        int restored = 0;
        int failed = 0;
        string? lastError = null;
        foreach (var relPath in leaves)
        {
            ct.ThrowIfCancellationRequested();
            var norm = relPath.Replace('\\', '/').TrimStart('/');
            var fromPath = System.IO.Path.Combine(revDir, norm.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var toPath = System.IO.Path.Combine(targetPath, preserveStructure
                ? norm.Replace('/', System.IO.Path.DirectorySeparatorChar) : System.IO.Path.GetFileName(norm));
            try
            {
                if (!overwrite && File.Exists(toPath)) throw new IOException("Existing file left unchanged.");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(toPath)!);
                CopyMaybeEncrypted(fromPath, toPath, encryptionKey);
                restored++;
            }
            catch (Exception ex)
            {
                failed++;
                lastError = ex.Message;
            }
        }

        var status = failed == 0 ? RestoreTerminalStatus.Success
                  : restored > 0 ? RestoreTerminalStatus.Warning
                  : RestoreTerminalStatus.Error;
        var result = new RestoreOpResult(
            TotalFiles: leaves.Count,
            FilesRestored: restored,
            TotalBytes: 0,
            BytesRestored: 0,
            Status: status,
            ErrorMessage: lastError);
        onProgress?.Invoke(new RestoreProgressUpdate(
            leaves.Count, restored, 0, 0,
            leaves.Count == 0 ? 0 : (double)restored / leaves.Count));
        return Task.FromResult(result);
    }

    // ---- maintenance ----

    public Task<RunRecord> RunPruneOnlyAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct, Action<string>? lineSink = null)
    {
        Calls.Enqueue($"RunPruneOnly({backup.Name},{sourcePath})");
        lineSink?.Invoke("[fake] prune complete (simulated)");
        return Task.FromResult(SimpleRecord(backup, "prune complete (simulated)"));
    }

    public Task<RunRecord> RunCheckOnlyAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct, Action<string>? lineSink = null)
    {
        Calls.Enqueue($"RunCheckOnly({backup.Name},{sourcePath})");
        lineSink?.Invoke("[fake] check complete (simulated)");
        return Task.FromResult(SimpleRecord(backup, "check complete (simulated)"));
    }

    public Task<RunRecord> PruneToLatestAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct)
    {
        Calls.Enqueue($"PruneToLatest({backup.Name},{sourcePath})");
        var snapshotDir = SnapshotDir(backup, sourcePath, destination);
        if (Directory.Exists(snapshotDir))
        {
            var revs = Directory.EnumerateDirectories(snapshotDir, "r*")
                .Select(p => (Path: p, Num: ParseRevisionNumber(System.IO.Path.GetFileName(p))))
                .Where(x => x.Num > 0)
                .OrderByDescending(x => x.Num)
                .Skip(1) // keep latest only
                .ToList();
            foreach (var (p, _) in revs)
                try { Directory.Delete(p, recursive: true); } catch { /* best-effort */ }
        }
        return Task.FromResult(SimpleRecord(backup, "kept latest revision only"));
    }

    public Task PruneSnapshotIdAsync(Backup backup, string sourcePath, Destination destination, string storageName, string snapshotId, CancellationToken ct)
    {
        Calls.Enqueue($"PruneSnapshotId({backup.Name},{snapshotId})");
        // Caller treats snapshot ids as opaque labels we minted in SnapshotDir.
        // Best-effort delete; missing path is fine.
        if (!System.Text.RegularExpressions.Regex.IsMatch(snapshotId, @"^r\d+$"))
            throw new ArgumentException("Expected an exact snapshot identity.");
        var dir = System.IO.Path.Combine(SnapshotDir(backup, sourcePath, destination), snapshotId);
        // A scanner can briefly hold the fake snapshot just written; real
        // engines delete remote data, so only this double needs the retry.
        for (var attempt = 0; Directory.Exists(dir); attempt++)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException ex) when (attempt < 20 && (ex.HResult & 0xFFFF) == 32) { Thread.Sleep(100); }
        }
        return Task.CompletedTask;
    }

    public Task PruneExhaustiveAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct)
    {
        Calls.Enqueue($"PruneExhaustive({backup.Name})");
        // No chunk store to GC in this engine — nothing to do beyond a
        // no-op. Real engines would compact orphaned chunks here.
        return Task.CompletedTask;
    }

    // ---- engine plumbing the orchestrator/UI still touches ----

    public Task<RunRecord> ListRevisionsAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct)
    {
        Calls.Enqueue($"ListRevisions({backup.Name},{sourcePath})");
        var snapshotDir = SnapshotDir(backup, sourcePath, destination);
        var count = Directory.Exists(snapshotDir)
            ? Directory.EnumerateDirectories(snapshotDir, "r*").Count()
            : 0;
        return Task.FromResult(SimpleRecord(backup, $"{count} snapshot(s)"));
    }

    public Task EnsureInitializedAsync(Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct)
    {
        Calls.Enqueue($"EnsureInitialized({backup.Name},{sourcePath})");
        Directory.CreateDirectory(SnapshotDir(backup, sourcePath, destination));
        // The per-source repo directory is the on-disk handle a real
        // engine drops for its local state (locks, caches, prefs).
        // Tests assert it exists post-backup; preserving that contract
        // here keeps multi-source assertions engine-agnostic.
        Directory.CreateDirectory(AppPaths.RepoDirForBackupSource(backup.Name, sourcePath));
        return Task.CompletedTask;
    }

    public Task<(int exitCode, string output)> ChangeStoragePasswordAsync(Destination destination, string storageName, string oldPassword, string newPassword, CancellationToken ct)
    {
        Calls.Enqueue($"ChangeStoragePassword({destination.Name},{storageName})");
        return Task.FromResult((0, "password changed (simulated)"));
    }

    // ---- internals ----

    private async Task EmitScriptedLinesAsync(Action<string>? lineSink, CancellationToken ct, string? logPath = null)
    {
        // LogsViewModel debounces live-tail flushes at 150ms; assertions
        // that check "the user saw activity DURING the run" require at
        // least one flush to have landed strictly before the run ends.
        // 80ms between scripted lines plus a final 250ms beat past the
        // debounce window keeps the during-run filter happy on noisy CI
        // boxes.
        foreach (var line in ScriptedBackupLines)
        {
            ct.ThrowIfCancellationRequested();
            AppendLogLine(logPath, line);
            lineSink?.Invoke(line);
            RaiseLineWritten(line);
            await Task.Delay(80, ct).ConfigureAwait(false);
        }
        await Task.Delay(250, ct).ConfigureAwait(false);
    }

    private async Task<(int files, long bytes)> SnapshotSourceAsync(
        Backup backup, string sourcePath, Destination destination, CancellationToken ct)
    {
        var snapshotDir = SnapshotDir(backup, sourcePath, destination);
        Directory.CreateDirectory(snapshotDir);

        var nextRev = NextRevisionNumber(snapshotDir);
        var revDir = System.IO.Path.Combine(snapshotDir, RevisionFolderName(nextRev));
        Directory.CreateDirectory(revDir);

        if (!Directory.Exists(sourcePath)) return (0, 0);

        // Run the file copy on a background thread so the orchestrator's
        // timing assertions don't see synchronous IO on the foreground
        // task — mirrors how a real engine would shell out to a child
        // process for the heavy work.
        var encryptionKey = EncryptionKeyFor(destination);

        return await Task.Run(() =>
        {
            int count = 0;
            long total = 0;
            foreach (var srcFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = System.IO.Path.GetRelativePath(sourcePath, srcFile);
                var dst = System.IO.Path.Combine(revDir, rel);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
                CopyMaybeEncrypted(srcFile, dst, encryptionKey);
                count++;
                total += new FileInfo(srcFile).Length;
            }
            return (count, total);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Derive a per-destination keystream byte from the
    /// destination's encryption flag + name. Non-encrypted destinations
    /// return null; encrypted ones get a single XOR byte we apply across
    /// the whole file. Not real encryption — sufficient for the in-test
    /// "no plaintext word is visible in storage" probe.</summary>
    private static byte[]? EncryptionKeyFor(Destination destination)
    {
        if (!destination.Encrypted) return null;
        // Stretch the destination name into a 64-byte rotating keystream.
        // Stable across runs (so restore decrypts back to the original)
        // and entirely test-tier — a real engine ships AES.
        var seed = string.IsNullOrEmpty(destination.Name) ? destination.Id : destination.Name;
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("backspace-fake-encryption|" + seed));
        var key = new byte[64];
        for (int i = 0; i < key.Length; i++) key[i] = bytes[i % bytes.Length];
        return key;
    }

    private static void CopyMaybeEncrypted(string from, string to, byte[]? key)
    {
        if (key is null)
        {
            File.Copy(from, to, overwrite: true);
            return;
        }
        var raw = File.ReadAllBytes(from);
        ApplyKeystream(raw, key);
        File.WriteAllBytes(to, raw);
    }

    private static void ApplyKeystream(byte[] buffer, byte[] key)
    {
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] ^= key[i % key.Length];
    }

    private RunRecord FailedRun(Backup backup, DateTime started, string logPath, string reason, Action<string>? lineSink)
    {
        var errLine = $"[fake] ERROR {reason}";
        AppendLogLine(logPath, errLine);
        lineSink?.Invoke(errLine);
        RaiseLineWritten(errLine);
        return new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            Kind = RunKind.Backup,
            StartedUtc = started,
            EndedUtc = DateTime.UtcNow,
            Status = BackupRunStatus.Failed,
            Summary = reason,
            LogPath = logPath,
            Errors = { reason },
        };
    }

    // ---- snapshot layout helpers ----

    /// <summary>Path that holds every revision for a (backup, source)
    /// pair on a destination. Source-path-aware: a multi-source backup
    /// produces one snapshot dir per source.</summary>
    private static string SnapshotDir(Backup backup, string sourcePath, Destination destination)
    {
        var id = StableSnapshotId(backup, sourcePath);
        return System.IO.Path.Combine(destination.PathOrSubpath, StoreRoot, id);
    }

    private static string RevisionDir(Backup backup, string sourcePath, Destination destination, int revision)
        => System.IO.Path.Combine(SnapshotDir(backup, sourcePath, destination), RevisionFolderName(revision));

    private static string RevisionFolderName(int rev) => $"r{rev:0000}";

    private static int ParseRevisionNumber(string folderName)
    {
        if (folderName.Length < 2 || folderName[0] != 'r') return 0;
        return int.TryParse(folderName.AsSpan(1), out var n) ? n : 0;
    }

    private static int NextRevisionNumber(string snapshotDir)
    {
        if (!Directory.Exists(snapshotDir)) return 1;
        int max = 0;
        foreach (var d in Directory.EnumerateDirectories(snapshotDir, "r*"))
        {
            var n = ParseRevisionNumber(System.IO.Path.GetFileName(d));
            if (n > max) max = n;
        }
        return max + 1;
    }

    /// <summary>Deterministic snapshot id for a (backup, source) pair.
    /// Matches the shape <see cref="StorageCleaner.SnapshotIdFor"/> would
    /// produce, so cleanup paths and engine paths share the same naming
    /// convention.</summary>
    private static string StableSnapshotId(Backup backup, string sourcePath)
    {
        var seed = !string.IsNullOrEmpty(backup.SnapshotIdSeed) ? backup.SnapshotIdSeed : backup.Name;
        var sanitized = Sanitize(seed);
        return backup.SourcePaths.Count <= 1
            ? sanitized
            : $"{sanitized}-{HashSourcePath(sourcePath)}";
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "_";
        var buf = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                      (c >= '0' && c <= '9') || c == '-' || c == '_';
            buf[i] = ok ? c : '_';
        }
        return new string(buf);
    }

    private static string HashSourcePath(string sourcePath)
    {
        var normalised = (sourcePath ?? "").TrimEnd('\\', '/');
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexString(bytes, 0, 3).ToLowerInvariant();
    }

    // ---- log file helpers ----

    private static string WritePerRunLog(Backup backup, DateTime startedUtc)
    {
        try
        {
            var dir = AppPaths.LogDirForBackup(backup.Name);
            Directory.CreateDirectory(dir);
            var stamp = startedUtc.ToString("yyyyMMddHHmmssfff");
            var rand = Guid.NewGuid().ToString("N").Substring(0, 6);
            var path = System.IO.Path.Combine(dir, $"{stamp}-fake-{rand}.log");
            File.WriteAllText(path, $"=== InMemoryBackupEngine run for backup '{backup.Name}' ==={Environment.NewLine}");
            return path;
        }
        catch
        {
            return "";
        }
    }

    private static void AppendLogLine(string? logPath, string line)
    {
        if (string.IsNullOrEmpty(logPath)) return;
        try { File.AppendAllText(logPath, line + Environment.NewLine); }
        catch { /* best-effort */ }
    }

    private static (bool ok, string reason) ProbeDestination(Destination destination)
    {
        // S3 / cloud destinations honor common "unreachable" test
        // markers in their endpoint or bucket so cell-isolation tests
        // can assert failure paths without real network state.
        // ".invalid" / ".test" are RFC-2606 reserved suffixes guaranteed
        // never to resolve; "does-not-exist" / "none" are unambiguous
        // tells.
        if (destination.Kind == DestinationKind.S3Compatible)
        {
            if (string.IsNullOrWhiteSpace(destination.S3Endpoint))
                return (false, "S3 endpoint is empty");
            if (destination.S3Endpoint.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
                || destination.S3Endpoint.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
                return (false, $"S3 endpoint unreachable: {destination.S3Endpoint}");
            if (destination.S3Bucket?.Contains("does-not-exist", StringComparison.OrdinalIgnoreCase) == true)
                return (false, $"S3 bucket missing: {destination.S3Bucket}");
            return (true, "");
        }

        // Non-S3 cloud kinds always succeed: the orchestrator's contract
        // doesn't care which wire the engine uses, just whether the cell
        // ended OK. Local-shaped destinations (LocalFolder /
        // ExternalDrive / NetworkShare) gate success on writability so
        // the unreachable-storage tests still observe the failure.
        if (destination.Kind != DestinationKind.LocalFolder &&
            destination.Kind != DestinationKind.ExternalDrive &&
            destination.Kind != DestinationKind.NetworkShare)
        {
            return (true, "");
        }

        var path = destination.PathOrSubpath;
        if (string.IsNullOrWhiteSpace(path))
            return (false, "destination path is empty");
        if (!Directory.Exists(path))
        {
            try { Directory.CreateDirectory(path); return (true, ""); }
            catch (Exception ex) { return (false, "destination unreachable: " + ex.Message); }
        }
        return (true, "");
    }

    private static RunRecord SimpleRecord(Backup backup, string summary)
    {
        var now = DateTime.UtcNow;
        return new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            Kind = RunKind.Backup,
            StartedUtc = now,
            EndedUtc = now,
            Status = BackupRunStatus.Success,
            Summary = summary,
        };
    }
}
