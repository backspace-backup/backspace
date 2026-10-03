using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// <see cref="IBackupEngine"/> implementation routing through a running
/// <see cref="BackrestSidecar"/>. Every <see cref="IBackupEngine"/>
/// member is wired: backup / forget / check route through Backrest's
/// plan- and repo-task RPCs, list + per-file restore route through
/// ListSnapshotFiles + RunCommand-driven restic, and password rotation
/// spawns the bundled restic.exe directly (the one operation that
/// can't go through Backrest because RunCommand has no stdin-redirect
/// path).
///
/// Schema mapping:
///   Backspace's per-(backup, sourcePath, storageName) cell becomes one
///   Backrest <b>Plan</b> with that source path as its only Paths
///   entry. Backspace's <see cref="Destination"/> becomes one Backrest
///   <b>Repo</b>; we put the destination's encryption password on the
///   Repo and the cloud-credentials env on Repo.env. Plan/Repo IDs are
///   deterministic so re-creating the plan from the same inputs hits
///   the same Backrest entity.
///
/// This engine reads/writes Backrest's config via GetConfig/SetConfig
/// in a read-modify-write loop keyed on <c>Config.modno</c>. Concurrent
/// modifications retry; that matches Backrest's own UI's pattern.
/// </summary>
public sealed class ResticBackrestEngine : IBackupEngine
{
    private static ILogger _log => AppLogger.For<ResticBackrestEngine>();

    private readonly BackrestSidecar _sidecar;
    private readonly SecretsStore _secrets;

    public ResticBackrestEngine(BackrestSidecar sidecar, SecretsStore secrets)
    {
        _sidecar = sidecar;
        _secrets = secrets;
    }

    public event Action<string>? LineWritten;

    public void RaiseLineWritten(string line)
    {
        try { LineWritten?.Invoke(line); } catch { /* live-tail subscriber failure shouldn't escape */ }
    }

    /// <summary>
    /// Backrest's terminal operation-status enum values, surfaced as
    /// wire-format strings. Backrest's GetOperationEvents stream
    /// publishes one of these for every op transition; we match
    /// against them in <see cref="StatusFromBackrestTerminal"/> and
    /// the wait-for-terminal pumps. Single source of truth instead
    /// of bare string literals scattered through the file.
    /// </summary>
    private static class BackrestStatus
    {
        public const string Success      = "STATUS_SUCCESS";
        public const string Warning      = "STATUS_WARNING";
        public const string Error        = "STATUS_ERROR";
        public const string UserCancelled = "STATUS_USER_CANCELLED";
        public const string SystemCancelled = "STATUS_SYSTEM_CANCELLED";
    }

    /// <summary>Backrest event-stream control codes. Currently only
    /// "keep-alive" carries no payload and is filtered out before the
    /// pump fires LineWritten / completes the terminal TCS.</summary>
    private static class BackrestEvent
    {
        public const string KeepAlive = "EVENT_KEEP_ALIVE";
    }

    /// <summary>Cap on the read-modify-write loop inside
    /// <see cref="EnsurePlanAndRepoCoreAsync"/>. Backrest's SetConfig
    /// only rejects when the daemon's modno moved between our
    /// GetConfig and our SetConfig — at single-user desktop scale
    /// even 2 rounds would be unusual. 5 is generous and bounded.</summary>
    private const int MaxModnoMismatchRetries = 5;

    /// <summary>
    /// Process-wide gate around <see cref="EnsurePlanAndRepoAsync"/>.
    /// Two cells of the same backup running concurrently (or two
    /// independent UI flows — restore + backup + revision-list — all
    /// touching the same engine) each call EnsurePlan… and race on
    /// the daemon's modno. Without serialisation we'd burn the
    /// 5-retry modno-mismatch cascade on every contended call.
    /// Config-mutation is rare enough that a single-slot semaphore
    /// is the right trade-off — the work itself is sub-50ms for the
    /// no-op path and ~100ms for the add-and-write path.
    /// </summary>
    private readonly System.Threading.SemaphoreSlim _ensureConfigGate = new(1, 1);

    /// <summary>Cache of repo id → repo Guid lookups, populated by
    /// <see cref="EnsurePlanAndRepoCoreAsync"/> when it reads the
    /// live Backrest config and reused by hot-path callers
    /// (<see cref="WaitForRepoIdleAsync"/>) so they don't have to
    /// fetch the entire BackrestConfig over Connect-RPC just to
    /// read one Guid. Round-3 review caught the previous "GetConfig
    /// per WaitForRepoIdle call" shape as ~3 redundant
    /// repo-list-over-the-wire calls per inline-maintenance
    /// invocation (Forget + Check + Unlock). The cache is
    /// invalidated implicitly when SetConfig rewrites a repo's
    /// Guid (rare; only on AddRepo or destination URI drift) by
    /// reseeding from the EnsurePlanAndRepoCoreAsync write
    /// confirmation. ConcurrentDictionary because two cells against
    /// different repos can populate concurrently.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _repoGuidCache = new(StringComparer.Ordinal);

    // ---- operations that map cleanly to Backrest's RPC ----

    public async Task<RunRecord> BackupPruneCheckAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct, Action<string>? lineSink = null)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        var planId = MakePlanId(backup, sourcePath, destination, storageName);
        await EnsurePlanAndRepoAsync(backup, sourcePath, destination, storageName, ct).ConfigureAwait(false);
        var started = DateTime.UtcNow;
        var terminal = await RunTrackedRepoRpcAsync(client, repoId, "/v1.Backrest/Backup",
            new BackupRequest { Value = planId }, op => op.PlanId == planId && op.OperationBackup is not null,
            ct, lineSink).ConfigureAwait(false);
        var status = StatusFromBackrestTerminal(terminal.Status);
        var details = terminal.DisplayMessage;
        if (status is BackupRunStatus.Success or BackupRunStatus.Warning && !ct.IsCancellationRequested)
        {
            async Task RunPhase<T>(string endpoint, T request, Func<BackrestOperation, bool> matches) where T : class
            {
                try
                {
                    var result = await RunTrackedRepoRpcAsync(client, repoId, endpoint, request, matches, ct, lineSink).ConfigureAwait(false);
                    if (result.Status is not BackrestStatus.Success)
                    {
                        status = BackupRunStatus.Warning;
                        details += $" Backup content completed; maintenance reported {result.Status}: {result.DisplayMessage}";
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (ServiceLocator.Maintenance.IsBlocked) throw;
                    status = BackupRunStatus.Warning;
                    details += " Backup content completed; maintenance failed: " + ex.Message;
                }
            }
            if (backup.PruneSchedule is null)
                await RunPhase("/v1.Backrest/Forget", new ForgetRequest { RepoId = repoId, PlanId = planId },
                    op => op.PlanId == planId && op.OperationForget is not null).ConfigureAwait(false);
            if (backup.CheckSchedule is null && !ct.IsCancellationRequested)
                await RunPhase("/v1.Backrest/DoRepoTask", new DoRepoTaskRequest { RepoId = repoId, Task = (int)BackrestRepoTask.TaskCheck },
                    op => op.OperationCheck is not null).ConfigureAwait(false);
        }
        return new RunRecord
        {
            BackupId = backup.Id, BackupName = backup.Name, Kind = RunKind.Backup,
            StartedUtc = started, EndedUtc = DateTime.UtcNow, Status = status,
            Summary = $"Backrest reported {terminal.Status}. {details}".Trim(),
        };
    }

    public Task<RunRecord> RunPruneOnlyAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct, Action<string>? lineSink = null) =>
        RunMaintenanceRpcAsync(backup, sourcePath, destination, storageName, RunKind.Prune, ct, lineSink);

    public Task<RunRecord> RunCheckOnlyAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct, Action<string>? lineSink = null) =>
        RunMaintenanceRpcAsync(backup, sourcePath, destination, storageName, RunKind.Check, ct, lineSink);

    private async Task<RunRecord> RunMaintenanceRpcAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        RunKind kind, CancellationToken ct, Action<string>? lineSink)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        var planId = MakePlanId(backup, sourcePath, destination, storageName);
        await EnsurePlanAndRepoAsync(backup, sourcePath, destination, storageName, ct).ConfigureAwait(false);
        var started = DateTime.UtcNow;
        var terminal = kind == RunKind.Prune
            ? await RunTrackedRepoRpcAsync(client, repoId, "/v1.Backrest/Forget",
                new ForgetRequest { RepoId = repoId, PlanId = planId },
                op => op.PlanId == planId && op.OperationForget is not null, ct, lineSink).ConfigureAwait(false)
            : await RunTrackedRepoRpcAsync(client, repoId, "/v1.Backrest/DoRepoTask",
                new DoRepoTaskRequest { RepoId = repoId, Task = (int)BackrestRepoTask.TaskCheck },
                op => op.OperationCheck is not null, ct, lineSink).ConfigureAwait(false);
        return new RunRecord
        {
            BackupId = backup.Id, BackupName = backup.Name, Kind = kind,
            StartedUtc = started, EndedUtc = DateTime.UtcNow,
            Status = StatusFromBackrestTerminal(terminal.Status),
            Summary = $"Backrest {kind.ToString().ToLowerInvariant()} reported {terminal.Status}. {terminal.DisplayMessage}".Trim(),
        };
    }

    // Task RPCs keep HTTP open until completion. Cancelling HTTP does not stop
    // the task. Ownership lasts until a terminal state or a fail-closed guard.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _repoTaskGates = new();

    internal async Task<BackrestOperation> RunTrackedRepoRpcAsync<T>(BackrestClient client, string repoId,
        string endpoint, T request, Func<BackrestOperation, bool> matches,
        CancellationToken ct, Action<string>? lineSink = null, TimeSpan? uncertaintyTimeout = null) where T : class
    {
        var gate = _repoTaskGates.GetOrAdd(repoId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        using var transport = new CancellationTokenSource();
        Task? submission = null;
        string? ownedId = null;
        var acknowledged = false;
        try
        {
            var repoGuid = await ResolveRepoGuidAsync(client, repoId, ct).ConfigureAwait(false);
            var before = await GetRepoOperationsAsync(client, repoGuid, ct).ConfigureAwait(false);
            var previousIds = before.Select(op => op.Id).ToHashSet(StringComparer.Ordinal);
            ct.ThrowIfCancellationRequested();
            submission = client.CallVoidAsync(endpoint, request, transport.Token);
            DateTime? recoveryDeadline = null;
            string lastStatus = "";
            while (true)
            {
                if ((ct.IsCancellationRequested || submission.IsFaulted || submission.IsCanceled) && recoveryDeadline is null)
                    recoveryDeadline = DateTime.UtcNow + (uncertaintyTimeout ?? TimeSpan.FromSeconds(30));
                IReadOnlyList<BackrestOperation>? operations = null;
                try
                {
                    using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    operations = await GetRepoOperationsAsync(client, repoGuid, probe.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "Could not poll repository operation");
                    recoveryDeadline ??= DateTime.UtcNow + (uncertaintyTimeout ?? TimeSpan.FromSeconds(30));
                }
                var owned = operations?.FirstOrDefault(op => ownedId is not null ? op.Id == ownedId :
                    !previousIds.Contains(op.Id) && matches(op));
                if (owned is not null)
                {
                    ownedId = owned.Id;
                    if (!ct.IsCancellationRequested && !submission.IsFaulted && !submission.IsCanceled)
                        recoveryDeadline = null;
                    if (lastStatus != owned.Status)
                    {
                        lastStatus = owned.Status;
                        var line = $"[backrest] op={owned.Id} status={owned.Status} {owned.DisplayMessage}".TrimEnd();
                        RaiseLineWritten(line);
                        lineSink?.Invoke(line);
                    }
                    if (IsTerminalStatus(owned.Status))
                    {
                        await DrainFollowUpOperationsAsync(client, repoGuid, previousIds, ct).ConfigureAwait(false);
                        acknowledged = true;
                        return owned;
                    }
                    if (ct.IsCancellationRequested)
                    {
                        await TrySendCancelAsync(client, owned.Id).ConfigureAwait(false);
                        using var acknowledgement = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        var terminal = await WaitForOperationTerminalAsync(client, owned.Id, acknowledgement.Token, lineSink).ConfigureAwait(false);
                        await DrainFollowUpOperationsAsync(client, repoGuid, previousIds, ct).ConfigureAwait(false);
                        acknowledged = true;
                        return terminal;
                    }
                }
                else if (operations is not null && submission.IsCompleted &&
                    (submission.IsCompletedSuccessfully || submission.Exception?.GetBaseException() is BackrestRpcException))
                {
                    acknowledged = true;
                    await submission.ConfigureAwait(false);
                    throw new IOException("The backup engine finished without recording the requested operation.");
                }
                if (recoveryDeadline is not null && DateTime.UtcNow >= recoveryDeadline.Value)
                    throw new IOException("The backup engine could not confirm that the repository operation stopped.");
                await Task.Delay(150).ConfigureAwait(false);
            }
        }
        finally
        {
            if (submission is not null && !acknowledged)
            {
                if (ownedId is not null)
                {
                    await TrySendCancelAsync(client, ownedId).ConfigureAwait(false);
                    using var acknowledgement = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await WaitForOperationTerminalAsync(client, ownedId, acknowledgement.Token).ConfigureAwait(false); acknowledged = true; }
                    catch { }
                }
                if (!acknowledged)
                    ServiceLocator.Maintenance.BlockUntilRestart("The backup engine did not confirm that a repository operation stopped. Restart Backspace before continuing.");
            }
            transport.Cancel();
            if (submission is not null) try { await submission.ConfigureAwait(false); } catch { }
            gate.Release();
        }
    }

    private async Task DrainFollowUpOperationsAsync(BackrestClient client, string repoGuid,
        HashSet<string> previousIds, CancellationToken callerToken)
    {
        DateTime? uncertaintyDeadline = null;
        while (true)
        {
            if (callerToken.IsCancellationRequested) uncertaintyDeadline ??= DateTime.UtcNow.AddSeconds(30);
            try
            {
                using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var operations = await GetRepoOperationsAsync(client, repoGuid, probe.Token).ConfigureAwait(false);
                // Backup creates index/retention/stat jobs before its terminal
                // event. The per-repository gate excludes our other task RPCs;
                // pre-existing work and unrelated RunCommand restores are not
                // owned by this call and must never be cancelled here.
                var pending = operations.Where(op => !previousIds.Contains(op.Id) &&
                    (op.OperationRunCommand is null || string.IsNullOrEmpty(op.OperationRunCommand.Command)) &&
                    !IsTerminalStatus(op.Status)).ToArray();
                if (pending.Length == 0) return;
                if (callerToken.IsCancellationRequested)
                    foreach (var operation in pending) await TrySendCancelAsync(client, operation.Id).ConfigureAwait(false);
                else uncertaintyDeadline = null;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Waiting for repository follow-up tasks to stop");
                uncertaintyDeadline ??= DateTime.UtcNow.AddSeconds(30);
            }
            if (uncertaintyDeadline is not null && DateTime.UtcNow >= uncertaintyDeadline)
            {
                const string reason = "The backup engine did not confirm that repository cleanup stopped. Restart Backspace before continuing.";
                ServiceLocator.Maintenance.BlockUntilRestart(reason);
                throw new IOException(reason);
            }
            await Task.Delay(150).ConfigureAwait(false);
        }
    }

    private async Task<string> ResolveRepoGuidAsync(BackrestClient client, string repoId, CancellationToken ct)
    {
        if (_repoGuidCache.TryGetValue(repoId, out var cached) && !string.IsNullOrEmpty(cached)) return cached;
        var config = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
        var guid = config.Repos.FirstOrDefault(repo => repo.Id == repoId)?.Guid;
        if (string.IsNullOrEmpty(guid)) throw new IOException("The backup engine could not identify this repository.");
        _repoGuidCache[repoId] = guid;
        return guid;
    }

    private static async Task<IReadOnlyList<BackrestOperation>> GetRepoOperationsAsync(
        BackrestClient client, string repoGuid, CancellationToken ct)
    {
        var result = await client.CallAsync<GetOperationsRequest, BackrestOperationList>(
            "/v1.Backrest/GetOperations", new GetOperationsRequest
            {
                Selector = new OpSelector { RepoGuid = repoGuid }, LastN = 256,
            }, ct).ConfigureAwait(false);
        return result.Operations ?? new List<BackrestOperation>();
    }

    private async Task TrySendCancelAsync(BackrestClient client, string opId)
    {
        if (string.IsNullOrEmpty(opId) || opId == "0") return;
        try
        {
            using var cancelCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.CallVoidAsync("/v1.Backrest/Cancel", new Int64Value { Value = opId }, cancelCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.Debug(ex, "Cancel request for operation {Operation} failed; awaiting terminal state", opId); }
    }

    /// <summary>
    /// Map Backrest's terminal status enum to our internal status.
    /// Unknown / empty status maps to Failed — a backup we can't
    /// confirm succeeded is NOT a success. Earlier this defaulted
    /// to Success, which combined with the 1.5 s pump cutoff to
    /// produce silent data loss: real backups that hadn't actually
    /// finished within 1.5 s showed green in the UI.
    /// </summary>
    internal static BackupRunStatus StatusFromBackrestTerminal(string status) => status switch
    {
        BackrestStatus.Success       => BackupRunStatus.Success,
        BackrestStatus.Warning       => BackupRunStatus.Warning,
        BackrestStatus.Error         => BackupRunStatus.Failed,
        BackrestStatus.UserCancelled => BackupRunStatus.Skipped,
        BackrestStatus.SystemCancelled => BackupRunStatus.Skipped,
        _                            => BackupRunStatus.Failed,
    };

    public async Task<RunRecord> ListRevisionsAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        var planId = MakePlanId(backup, sourcePath, destination, storageName);

        var resp = await client.CallAsync<ListSnapshotsRequest, ResticSnapshotList>(
            "/v1.Backrest/ListSnapshots",
            new ListSnapshotsRequest { RepoId = repoId, PlanId = planId },
            ct).ConfigureAwait(false);

        return new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            Kind = RunKind.Backup,
            StartedUtc = DateTime.UtcNow,
            EndedUtc = DateTime.UtcNow,
            Status = BackupRunStatus.Success,
            Summary = $"{resp.Snapshots.Count} snapshot(s)",
        };
    }

    public async Task<IReadOnlyList<RevisionSummary>> ListSnapshotsAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        var sorted = await ListSnapshotsSortedAsync(backup, sourcePath, destination, storageName, ct)
            .ConfigureAwait(false);
        var revisions = new List<RevisionSummary>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            var s = sorted[i];
            var when = DateTimeOffset.FromUnixTimeMilliseconds(s.UnixTimeMs).UtcDateTime;
            revisions.Add(new RevisionSummary(
                Number: sorted.Count - i,
                CreatedUtc: when,
                FileCount: 0,
                TotalBytes: 0));
        }
        return revisions;
    }

    public async Task<IReadOnlyList<RevisionFile>> ListSnapshotFilesAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var (snapshotId, repoGuid) = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct).ConfigureAwait(false);

        var collected = new List<RevisionFile>();
        await WalkSnapshotTreeAsync(client, repoGuid, snapshotId, rootPath: "/", collected, ct)
            .ConfigureAwait(false);

        // Return paths SOURCE-RELATIVE rather than absolute-snapshot
        // (e.g. "blue_recipe_001.json" not
        // "C/Users/.../sources/notes/blue_recipe_001.json"). Every
        // downstream caller — restore wizard, cherry-pick request,
        // bytes-on-disk validator — treats `RevisionFile.Path` as
        // "what the user picked relative to the backup source"; the
        // in-memory engine already returns that shape and the
        // mismatch caused a round-trip to end up with files buried
        // under the original Windows path under target. Translate
        // here, in the only spot that knows BOTH paths.
        var srcRel = SourcePathToSnapshotPrefix(sourcePath);
        var rebased = new List<RevisionFile>(collected.Count);
        foreach (var f in collected)
        {
            var p = f.Path;
            if (!string.IsNullOrEmpty(srcRel))
            {
                // A similarly named sibling (source-old) is not inside source.
                if (!p.StartsWith(srcRel.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) continue;
                p = p.Substring(srcRel.Length).TrimStart('/');
            }
            rebased.Add(new RevisionFile(p, f.SizeBytes, f.ModifiedUtc));
        }
        rebased.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return rebased;
    }

    /// <summary>
    /// Translate a backup-source path on disk to the prefix restic
    /// stores it under in the snapshot. Windows
    /// <c>C:\Users\me\Documents</c> → <c>C/Users/me/Documents</c>;
    /// UNC <c>\\server\share\dir</c> → <c>server/share/dir</c>;
    /// POSIX <c>/data/photos</c> → <c>data/photos</c>. Forward slashes
    /// throughout because that's how restic writes them. No leading
    /// slash because RevisionFile.Path is already stripped of one.
    /// </summary>
    internal static string SourcePathToSnapshotPrefix(string sourcePath)
    {
        var s = (sourcePath ?? "").Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(s)) return "";
        if (s.StartsWith("//", StringComparison.Ordinal))
            return s.Substring(2);
        s = s.TrimStart('/');
        if (s.Length >= 2 && s[1] == ':')
            s = s[0] + "/" + s.Substring(2).TrimStart('/');
        return s;
    }

    public async Task<IReadOnlyList<RevisionDirEntry>?> ListSnapshotDirectoryAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, string dirRelativePath, CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var (snapshotId, repoGuid) = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct).ConfigureAwait(false);

        // Translate source-relative request path → absolute snapshot
        // path. Backrest's ListSnapshotFiles wants "/" for the source
        // root and "/<srcPrefix>/<rel>" for everything else.
        var srcPrefix = SourcePathToSnapshotPrefix(sourcePath);
        var rel = (dirRelativePath ?? "").Replace('\\', '/').Trim('/');
        var snapshotDirPath = "/" + (
            srcPrefix.Length == 0
                ? rel
                : (rel.Length == 0 ? srcPrefix : srcPrefix + "/" + rel));
        // Normalize to "/" for the very root case.
        if (snapshotDirPath == "/" + "" && srcPrefix.Length == 0 && rel.Length == 0)
            snapshotDirPath = "/";

        ListSnapshotFilesResponse resp;
        try
        {
            resp = await client.CallAsync<ListSnapshotFilesRequest, ListSnapshotFilesResponse>(
                "/v1.Backrest/ListSnapshotFiles",
                new ListSnapshotFilesRequest { RepoGuid = repoGuid, SnapshotId = snapshotId, Path = snapshotDirPath },
                ct).ConfigureAwait(false);
        }
        catch
        {
            // Backrest returns an RPC error for "no such path in
            // snapshot". Treat as not-found rather than propagating —
            // WinFSP just needs a clean null to surface "no such
            // file" to the kernel.
            return null;
        }
        if (resp.Entries is null) return Array.Empty<RevisionDirEntry>();

        // Backrest includes the QUERIED dir as the first entry; skip
        // it. Everything else is an immediate child.
        var children = new List<RevisionDirEntry>(resp.Entries.Count);
        foreach (var entry in resp.Entries)
        {
            if (string.Equals(entry.Path, snapshotDirPath, StringComparison.OrdinalIgnoreCase))
                continue;
            // entry.Path is absolute under the snapshot — keep only
            // the leaf name; the caller already knows the parent.
            var leaf = entry.Path;
            var slash = leaf.LastIndexOf('/');
            if (slash >= 0) leaf = leaf.Substring(slash + 1);
            if (leaf.Length == 0) continue;
            var modified = DateTime.TryParse(entry.Mtime, out var mtime) ? mtime.ToUniversalTime() : DateTime.MinValue;
            var isDir = string.Equals(entry.Type, "dir", StringComparison.OrdinalIgnoreCase);
            children.Add(new RevisionDirEntry(leaf, isDir, isDir ? 0 : entry.Size, modified));
        }
        children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return children;
    }

    public async Task<IReadOnlyList<RevisionTreeNode>?> ListSnapshotTreeAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, CancellationToken ct)
    {
        // Resolve the snapshot ID (via Backrest) and pull the live
        // repo stanza so we know the URI, env vars, and per-repo
        // flags Backrest would have set for any restic invocation.
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var (snapshotId, repoGuid) = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct).ConfigureAwait(false);
        var cfg = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
        var repo = cfg.Repos.FirstOrDefault(r =>
            string.Equals(r.Guid, repoGuid, StringComparison.OrdinalIgnoreCase));
        if (repo is null) return null;

        // Cloud destinations route through rclone — restic's URI
        // looks like "rclone:remote:path" and the rclone binary
        // needs to be discoverable AND aware of the rclone.conf
        // RCLONE_CONFIG points at. Backrest manages all that
        // internally; spawning restic ourselves would have to
        // replicate it. The caller falls back to the lazy per-
        // directory gRPC path for cloud, which is slow but
        // correct. Local destinations are the common case (the one
        // the user reported regressing), so optimise that.
        if (repo.Uri.StartsWith("rclone:", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!File.Exists(AppPaths.ResticBinary))
            return null;

        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.ResticBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // restic on Windows emits UTF-8 to stdout regardless of
            // the console codepage. Pin both encodings here so JSON
            // entries with non-ASCII filenames (the user has
            // accented paths) parse cleanly.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(repo.Uri);
        foreach (var f in repo.Flags ?? new List<string>())
            psi.ArgumentList.Add(f);
        psi.ArgumentList.Add("ls");
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add(snapshotId);
        // Password — only set when not in insecure-no-password mode.
        if (!string.IsNullOrEmpty(repo.Password))
            psi.Environment["RESTIC_PASSWORD"] = repo.Password;
        // Pass through repo.Env (RCLONE_CONFIG, AWS_*, etc.). Each
        // entry is "KEY=VALUE" — the BackrestRepo schema flattens
        // them this way to mirror restic's expected env.
        foreach (var e in repo.Env ?? new List<string>())
        {
            var eq = e.IndexOf('=');
            if (eq <= 0) continue;
            psi.Environment[e.Substring(0, eq)] = e.Substring(eq + 1);
        }

        // restic emits one JSON object per line: the first line is
        // the snapshot summary (struct_type=snapshot) which we
        // skip; every subsequent line is a node (struct_type=node)
        // with name/type/path/size/mtime fields.
        var nodes = new List<RevisionTreeNode>(2048);
        var srcPrefix = SourcePathToSnapshotPrefix(sourcePath); // e.g. "C/ntws"
        var rootBound = "/" + srcPrefix; // e.g. "/C/ntws"

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        // Drain stderr concurrently so a chatty restic doesn't
        // block on its stderr pipe filling up.
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            ResticLsLine? parsed;
            try { parsed = System.Text.Json.JsonSerializer.Deserialize<ResticLsLine>(line); }
            catch { continue; }
            if (parsed is null) continue;
            if (parsed.struct_type != "node") continue; // skip snapshot summary
            // restic emits absolute snapshot paths like "/C/ntws/foo".
            // Rebase to source-relative ("foo"). Anything OUTSIDE the
            // source's subtree we skip — defensive, shouldn't happen
            // for a single-source-paths snapshot but the snapshot
            // might cover multiple paths.
            var path = parsed.path ?? "";
            if (string.IsNullOrEmpty(path)) continue;
            string rel;
            if (string.Equals(path, rootBound, StringComparison.OrdinalIgnoreCase))
            {
                rel = ""; // the source root itself
            }
            else if (rootBound.Length > 1 &&
                path.StartsWith(rootBound + "/", StringComparison.OrdinalIgnoreCase))
            {
                rel = path.Substring(rootBound.Length + 1);
            }
            else if (rootBound == "/")
            {
                rel = path.TrimStart('/');
            }
            else
            {
                continue; // path outside our source — skip
            }
            var isDir = string.Equals(parsed.type, "dir", StringComparison.OrdinalIgnoreCase);
            var modified = DateTime.TryParse(parsed.mtime, out var mt) ? mt.ToUniversalTime() : DateTime.MinValue;
            nodes.Add(new RevisionTreeNode(rel, isDir, isDir ? 0 : parsed.size, modified));
        }

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0 && nodes.Count == 0)
        {
            // Non-zero exit with no output means we got nothing
            // useful. Log stderr and let the caller fall back.
            _log.Warning("restic ls --json {Snap} exited {Code}: {Err}",
                snapshotId, proc.ExitCode, stderr.Length > 500 ? stderr.Substring(0, 500) : stderr);
            return null;
        }
        return nodes;
    }

    public async Task<bool> RestoreSnapshotSubtreeAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, string targetDir, CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var (snapshotId, repoGuid) = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct).ConfigureAwait(false);
        var cfg = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
        var repo = cfg.Repos.FirstOrDefault(r =>
            string.Equals(r.Guid, repoGuid, StringComparison.OrdinalIgnoreCase));
        if (repo is null) return false;

        // Cloud destinations route through rclone — see
        // ListSnapshotTreeAsync for the same shape and why we punt
        // for those. Caller falls back to the per-file restore path.
        if (repo.Uri.StartsWith("rclone:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!File.Exists(AppPaths.ResticBinary))
            return false;

        Directory.CreateDirectory(targetDir);

        // restic's "snapshot:subpath" syntax restores the snapshot
        // subtree AND strips the subpath prefix from the output paths.
        // So `restic restore SNAP:/C/ntws --target cache` writes
        // C/ntws/foo.txt as cache/foo.txt — matching our cacheDir
        // layout exactly. Without the subpath, restic would write
        // cache/C/ntws/foo.txt and the per-file File.Exists checks
        // would miss every file (which is what hit the user-reported
        // 2 hour copy stall before this fast-path landed).
        var srcPrefix = SourcePathToSnapshotPrefix(sourcePath); // e.g. "C/ntws"
        var snapshotSubpath = "/" + srcPrefix;

        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.ResticBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(repo.Uri);
        foreach (var f in repo.Flags ?? new List<string>())
            psi.ArgumentList.Add(f);
        psi.ArgumentList.Add("restore");
        psi.ArgumentList.Add(snapshotId + ":" + snapshotSubpath);
        psi.ArgumentList.Add("--target");
        psi.ArgumentList.Add(targetDir);
        if (!string.IsNullOrEmpty(repo.Password))
            psi.Environment["RESTIC_PASSWORD"] = repo.Password;
        foreach (var e in repo.Env ?? new List<string>())
        {
            var eq = e.IndexOf('=');
            if (eq <= 0) continue;
            psi.Environment[e.Substring(0, eq)] = e.Substring(eq + 1);
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        // Drain both streams concurrently — restic emits progress
        // lines to stderr and finished-file lines to stdout. Letting
        // either buffer fill blocks the process.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            _log.Warning("restic restore {Snap}:{Sub} exited {Code}: {Err}",
                snapshotId, snapshotSubpath, proc.ExitCode,
                stderr.Length > 500 ? stderr.Substring(0, 500) : stderr);
            return false;
        }
        _log.Information("Bulk-restored snapshot subtree {Snap}:{Sub} to {Dir}", snapshotId, snapshotSubpath, targetDir);
        return true;
    }

    /// <summary>One line from <c>restic ls --json</c>. restic emits one
    /// JSON object per line: the FIRST line is the snapshot summary
    /// (<c>struct_type=snapshot</c>) which we skip, every line after
    /// is a node (<c>struct_type=node</c>). The fields here are the
    /// subset we need — restic emits more (uid, gid, mode,
    /// permissions, atime, ctime) but they don't apply to a
    /// read-only WinFSP volume that presents an everyone-read SD.
    /// Lower-case field names match restic's JSON shape verbatim.</summary>
    private sealed class ResticLsLine
    {
        public string? struct_type { get; set; }
        public string? name { get; set; }
        public string? type { get; set; }
        public string? path { get; set; }
        public long size { get; set; }
        public string? mtime { get; set; }
    }

    public async Task<RestoreOpResult> RestoreFilesAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, IReadOnlyList<string> includePatterns, string targetPath,
        Action<RestoreProgressUpdate>? onProgress, CancellationToken ct,
        IReadOnlyList<string>? expectedLeaves = null)
    {
        if (includePatterns.Count == 0)
            return new RestoreOpResult(0, 0, 0, 0, RestoreTerminalStatus.Success, null);
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var (snapshotId, repoGuid) = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct).ConfigureAwait(false);
        Directory.CreateDirectory(targetPath);
        var repoId = MakeRepoId(destination, storageName);
        var srcPrefix = SourcePathToSnapshotPrefix(sourcePath);
        var includes = includePatterns.Select(NormalizeRestoreRelativePath).ToArray();
        var exact = expectedLeaves?.Select(NormalizeRestoreRelativePath)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // Never restore wildcard overmatches into an occupied destination or
        // infer success from a file that existed before this operation. Stage
        // on the target volume, then move only requested output into place.
        var staging = Path.Combine(targetPath, ".backspace-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var includeFile = Path.Combine(Path.GetTempPath(), $"backspace-restore-include-{Guid.NewGuid():N}.txt");
        var safeToCleanup = true;
        string? opId = null;
        var status = RestoreTerminalStatus.Error;
        string? error = null;
        int restored = 0;
        long restoredBytes = 0;
        var total = exact?.Count ?? 0;
        long totalBytes = 0;
        using var logCts = new CancellationTokenSource();
        using var submissionCts = new CancellationTokenSource();
        Task<string>? logTask = null;
        Task<Int64Value>? submission = null;
        try
        {
            await File.WriteAllLinesAsync(includeFile,
                includes.Select(p => "/" + SubstituteBracketsWithQuestionMark(p)), ct).ConfigureAwait(false);
            var command = new System.Text.StringBuilder("restore ")
                .Append(QuoteArg(snapshotId + ":/" + srcPrefix))
                .Append(" --target ").Append(QuoteArg(staging))
                .Append(" --json --quiet --include-file ").Append(QuoteArg(includeFile));
            // Quiet JSON retains the final summary and errors but suppresses
            // periodic/per-file output. Thus a healthy multi-day restore cannot
            // overflow Backrest's 2 MB command log merely because time passed.
            if (backup.BandwidthLimitKBps > 0)
                command.Append(" --limit-download ").Append(backup.BandwidthLimitKBps);
            ct.ThrowIfCancellationRequested();
            // RunCommand is synchronous: its HTTP response comes only AFTER
            // restic exits, and an error response contains no operation id.
            // Discover the operation by this unique command while the request
            // remains pending so progress and cancellation work during the run.
            safeToCleanup = false;
            submission = client.CallAsync<RunCommandRequest, Int64Value>(
                "/v1.Backrest/RunCommand", new RunCommandRequest { RepoId = repoId, Command = command.ToString() }, submissionCts.Token);
            opId = await DiscoverRunCommandOperationAsync(client, repoGuid, command.ToString(), submission,
                ct, () => safeToCleanup = true).ConfigureAwait(false);
            BackrestOperation? terminal = null;
            try
            {
                terminal = await WaitForOperationTerminalAsync(client, opId, ct, observed: op =>
                {
                    var logRef = op.OperationRunCommand?.OutputLogref;
                    if (logTask is null && !string.IsNullOrEmpty(logRef))
                        logTask = CaptureRestoreLogAsync(client, logRef, onProgress, logCts.Token, exact, staging);
                }).ConfigureAwait(false);
                safeToCleanup = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation of the caller's HTTP wait does not cancel a
                // queued daemon operation. Use an independent cleanup token.
                await TrySendCancelAsync(client, opId).ConfigureAwait(false);
                using var acknowledgement = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    terminal = await WaitForOperationTerminalAsync(client, opId, acknowledgement.Token).ConfigureAwait(false);
                    safeToCleanup = true;
                }
                catch (Exception ex)
                {
                    error = $"The backup engine did not confirm that restore stopped. Temporary output is kept at {staging}. Restart Backspace before running other operations.";
                    ServiceLocator.Maintenance.BlockUntilRestart(error);
                    _log.Warning(ex, "Restore cancellation acknowledgement failed for {Operation}", opId);
                }
            }
            if (terminal is not null)
            {
                status = ct.IsCancellationRequested ? RestoreTerminalStatus.Cancelled : terminal.Status switch
                {
                    BackrestStatus.Success => RestoreTerminalStatus.Success,
                    BackrestStatus.Warning => RestoreTerminalStatus.Warning,
                    BackrestStatus.UserCancelled or BackrestStatus.SystemCancelled => RestoreTerminalStatus.Cancelled,
                    _ => RestoreTerminalStatus.Error,
                };
                error = status == RestoreTerminalStatus.Success ? null :
                    string.IsNullOrWhiteSpace(terminal.DisplayMessage) ? $"Restore ended with {status}." : terminal.DisplayMessage;
            }

            string captured = "";
            if (logTask is not null && safeToCleanup)
            {
                try { captured = await logTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception ex) { _log.Debug(ex, "Restore log tail did not finish"); }
            }
            ParseResticRestoreSummary(captured, out var completedFiles, out var completedBytes,
                out var reportedTotal, out totalBytes, out var errors);
            totalBytes = Math.Max(0, totalBytes);
            if (exact is null && reportedTotal >= 0) total = (int)Math.Min(int.MaxValue, reportedTotal);

            // A success summary never overrides a backend error or cancellation.
            // Metadata warnings remain warnings even when all content recovered.
            var leaves = safeToCleanup ? EnumerateRestoreLeaves(staging).ToArray() : Array.Empty<string>();
            // Restic summary "files" includes directories and its byte total
            // deduplicates hardlinks, so neither equals our leaf-file counts.
            // A partial recovery after ERROR requires explicit completion for
            // every produced leaf, with no error for that leaf, plus a final
            // summary. An incomplete/preallocated file never replaces originals.
            var completedLeaves = ParseCompletedRestoreLeaves(captured);
            var stagedDirectories = CountRestoreDirectories(staging);
            var allOutputConfirmed = completedFiles == leaves.Length + stagedDirectories &&
                completedBytes >= 0 && completedBytes == totalBytes;
            if (status == RestoreTerminalStatus.Error && reportedTotal >= 0 && leaves.Length > 0 &&
                (allOutputConfirmed || leaves.All(path => completedLeaves.Contains(Path.GetRelativePath(staging, path).Replace('\\', '/')))))
                status = RestoreTerminalStatus.Warning;
            if (safeToCleanup && status is RestoreTerminalStatus.Success or RestoreTerminalStatus.Warning)
            {
                var output = leaves.Where(path =>
                {
                    var relative = Path.GetRelativePath(staging, path).Replace('\\', '/');
                    return exact?.Contains(relative) ?? includes.Any(p => p.Length == 0 || relative == p || relative.StartsWith(p.TrimEnd('/') + "/", StringComparison.Ordinal));
                }).ToArray();
                if (exact is null && total == 0) total = output.Length;
                foreach (var path in output)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(staging, path);
                    var destinationPath = SafeRestorePath(targetPath, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    var attributes = File.GetAttributes(path);
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    if (isDirectory)
                        Directory.Move(path, destinationPath); // directory symlink: do not traverse
                    else
                    {
                        var length = (attributes & FileAttributes.ReparsePoint) == 0 ? new FileInfo(path).Length : 0;
                        File.Move(path, destinationPath, overwrite: true);
                        restoredBytes += length;
                    }
                    restored++;
                }
                if (status == RestoreTerminalStatus.Success && (errors > 0 || restored < total))
                {
                    status = restored == 0 ? RestoreTerminalStatus.Error : RestoreTerminalStatus.Warning;
                    error = $"Recovered {restored} of {total} requested files. {Math.Max(errors, total - restored)} item(s) could not be restored.";
                }
                if (status == RestoreTerminalStatus.Success && restored == 0)
                {
                    status = RestoreTerminalStatus.Error;
                    error = "The backup engine completed without producing any requested files.";
                }
            }
        }
        catch (OperationCanceledException) when (safeToCleanup)
        {
            status = RestoreTerminalStatus.Cancelled;
            error = "Restore cancelled.";
        }
        catch (Exception ex)
        {
            status = RestoreTerminalStatus.Error;
            error = ex.Message;
            if (!safeToCleanup && opId is not null)
            {
                await TrySendCancelAsync(client, opId).ConfigureAwait(false);
                using var acknowledgement = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await WaitForOperationTerminalAsync(client, opId, acknowledgement.Token).ConfigureAwait(false);
                    safeToCleanup = true;
                }
                catch
                {
                    error += $" Engine stop is unconfirmed; temporary output is retained at {staging}. Restart Backspace before continuing.";
                    ServiceLocator.Maintenance.BlockUntilRestart(error);
                }
            }
            else if (!safeToCleanup)
            {
                error += $" Submission could not be confirmed. Temporary output is retained at {staging}; restart Backspace before continuing.";
                ServiceLocator.Maintenance.BlockUntilRestart(error);
            }
        }
        finally
        {
            // Terminal acknowledgement, or the fail-closed ownership guard,
            // controls cleanup. Cancelling this transport never stops restic.
            submissionCts.Cancel();
            if (submission is not null) try { await submission.ConfigureAwait(false); } catch { }
            logCts.Cancel();
            if (logTask is not null) try { await logTask.ConfigureAwait(false); } catch { }
            if (safeToCleanup)
            {
                try { File.Delete(includeFile); } catch { }
                try { DeleteRestoreStaging(staging); } catch (Exception ex) { _log.Debug(ex, "Restore staging cleanup failed"); }
            }
        }
        onProgress?.Invoke(new RestoreProgressUpdate(total, restored, Math.Max(totalBytes, restoredBytes), restoredBytes,
            total == 0 ? 0 : (double)restored / total));
        return new RestoreOpResult(total, restored, Math.Max(totalBytes, restoredBytes), restoredBytes, status, error);
    }

    private async Task<string> DiscoverRunCommandOperationAsync(BackrestClient client, string repoGuid,
        string command, Task<Int64Value> submission, CancellationToken callerToken, Action rejectedBeforeRun)
    {
        DateTime? ownershipDeadline = null;
        while (true)
        {
            if (submission.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(submission.Result.Value))
                return submission.Result.Value;
            // This deadline bounds only ownership recovery after a cancellation
            // or transport failure. It is never a restore-duration timeout.
            if ((callerToken.IsCancellationRequested || submission.IsFaulted || submission.IsCanceled) && ownershipDeadline is null)
                ownershipDeadline = DateTime.UtcNow.AddSeconds(30);
            try
            {
                using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var operations = await client.CallAsync<GetOperationsRequest, BackrestOperationList>(
                    "/v1.Backrest/GetOperations", new GetOperationsRequest
                    {
                        Selector = new OpSelector { RepoGuid = repoGuid }, LastN = 256,
                    }, probe.Token).ConfigureAwait(false);
                var owned = operations.Operations?.FirstOrDefault(op =>
                    string.Equals(op.OperationRunCommand?.Command, command, StringComparison.Ordinal));
                if (owned is not null) return owned.Id;
                // A completed application-level response plus a successful
                // operation-log query proves the handler rejected this command
                // before it created an operation. Network loss cannot prove it.
                if (submission.IsFaulted && submission.Exception?.GetBaseException() is BackrestRpcException)
                {
                    rejectedBeforeRun();
                    return (await submission.ConfigureAwait(false)).Value;
                }
            }
            catch (Exception ex) when (ex is not BackrestRpcException || !submission.IsFaulted)
            {
                _log.Debug(ex, "Waiting to discover the restore operation");
                ownershipDeadline ??= DateTime.UtcNow.AddSeconds(30);
            }
            if (ownershipDeadline is not null && DateTime.UtcNow >= ownershipDeadline.Value)
                throw new IOException("The backup engine could not confirm ownership of the restore operation.");
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private async Task<string> CaptureRestoreLogAsync(BackrestClient client, string logRef,
        Action<RestoreProgressUpdate>? progress, CancellationToken ct, HashSet<string>? expected = null, string? staging = null)
    {
        var captured = new System.Text.StringBuilder();
        var pending = new System.Text.StringBuilder();
        var decoder = System.Text.Encoding.UTF8.GetDecoder();
        await foreach (var chunk in client.StreamAsync<GetLogsRequest, RestoreLogChunk>(
            "/v1.Backrest/GetLogs", new GetLogsRequest { Ref = logRef }, ct).ConfigureAwait(false))
        {
            // GetLogs returns protobuf BytesValue (base64 in JSON), not
            // StringValue. Decode UTF-8 across chunk boundaries before parsing.
            var chars = new char[System.Text.Encoding.UTF8.GetMaxCharCount(chunk.Value.Length)];
            var count = decoder.GetChars(chunk.Value, 0, chunk.Value.Length, chars, 0, flush: false);
            captured.Append(chars, 0, count);
            pending.Append(chars, 0, count);
            var text = pending.ToString();
            var end = text.LastIndexOf('\n');
            if (end < 0) continue;
            foreach (var line in text[..(end + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                RaiseLineWritten(line);
                if (TryParseRestoreProgress(line, out var update))
                {
                    if (expected is not null)
                    {
                        // Summary counts include directories. Subtract every
                        // created ordinary directory; even if its metadata is
                        // unfinished this remains a conservative leaf count.
                        var directories = staging is null ? 0 : CountRestoreDirectories(staging);
                        update = update with { TotalFiles = expected.Count,
                            FilesRestored = Math.Clamp(update.FilesRestored - directories, 0, expected.Count) };
                    }
                    progress?.Invoke(update);
                }
            }
            pending.Clear().Append(text[(end + 1)..]);
        }
        if (pending.Length > 0 && TryParseRestoreProgress(pending.ToString(), out var last)) progress?.Invoke(last);
        return captured.ToString();
    }

    private sealed class RestoreLogChunk
    {
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public byte[] Value { get; set; } = Array.Empty<byte>();
    }

    internal static bool TryParseRestoreProgress(string line, out RestoreProgressUpdate progress)
    {
        progress = default!;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(line);
            var root = json.RootElement;
            if (!root.TryGetProperty("message_type", out var type) || type.GetString() is not ("status" or "summary")) return false;
            long Read(string name) => root.TryGetProperty(name, out var p) && p.TryGetInt64(out var n) ? n : 0;
            var total = Read("total_files");
            var restored = Read("files_restored") + Read("files_skipped");
            var bytes = Read("bytes_restored") + Read("bytes_skipped");
            var fraction = root.TryGetProperty("percent_done", out var done) ? done.GetDouble() : total == 0 ? 0 : (double)restored / total;
            progress = new RestoreProgressUpdate((int)total, (int)restored, Read("total_bytes"), bytes, Math.Clamp(fraction, 0, 1));
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    internal static HashSet<string> ParseCompletedRestoreLeaves(string output)
    {
        var completed = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var failed = new HashSet<string>(completed.Comparer);
        foreach (var line in output.Split('\n'))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var item = doc.RootElement;
                if (!item.TryGetProperty("item", out var path) || path.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var relative = NormalizeRestoreRelativePath(path.GetString()!);
                var type = item.GetProperty("message_type").GetString();
                if (type == "error") failed.Add(relative);
                else if (type == "verbose_status" && item.TryGetProperty("action", out var action) &&
                    action.GetString() is "restored" or "updated" or "unchanged") completed.Add(relative);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        completed.ExceptWith(failed);
        return completed;
    }

    private static int CountRestoreDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        while (pending.Count > 0)
        {
            foreach (var directory in Directory.EnumerateDirectories(pending.Pop()))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                count++;
                pending.Push(directory);
            }
        }
        return count;
    }

    internal static string NormalizeRestoreRelativePath(string path)
    {
        var relative = path.Replace('\\', '/').TrimStart('/');
        if (relative.Split('/').Any(p => p is ".." or ".") || relative.Contains(':') || relative.Contains('\n') || relative.Contains('\r'))
            throw new InvalidOperationException($"Unsafe snapshot path: {path}");
        return relative;
    }

    private static string SafeRestorePath(string root, string relative)
    {
        relative = NormalizeRestoreRelativePath(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!result.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Restore path escapes its destination.");
        for (var parent = Path.GetDirectoryName(result); parent is not null && parent.Length >= fullRoot.Length; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Cannot restore through the linked folder {parent}. Choose an ordinary folder as the destination.");
        return result;
    }

    private static IEnumerable<string> EnumerateRestoreLeaves(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
            {
                foreach (var leaf in EnumerateRestoreLeaves(entry)) yield return leaf;
            }
            else yield return entry;
        }
    }

    private static void DeleteRestoreStaging(string root)
    {
        // Directory.Delete does not recurse through reparse points. The root
        // is a fresh GUID directory created by this operation, never a user target.
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
    /// <summary>Parse the final <c>{"message_type":"summary"}</c> line
    /// out of restic's <c>--json</c> stream. Restic emits other
    /// message types (status, error, verbose_status) before the
    /// summary; we only care about the LAST line whose
    /// <c>message_type</c> is <c>summary</c>. Returns -1 in every
    /// out param when no summary was found, so callers can cleanly
    /// fall back to coarse estimates.</summary>
    internal static void ParseResticRestoreSummary(
        string capturedOutput,
        out long filesRestored, out long bytesRestored,
        out long totalFiles, out long totalBytes,
        out long errors)
    {
        filesRestored = bytesRestored = totalFiles = totalBytes = errors = -1;
        if (string.IsNullOrEmpty(capturedOutput)) return;
        foreach (var rawLine in capturedOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("message_type", out var t)) continue;
                if (t.GetString() != "summary") continue;
                var root = doc.RootElement;
                if (root.TryGetProperty("files_restored", out var fr)) filesRestored = fr.GetInt64();
                if (root.TryGetProperty("bytes_restored", out var br)) bytesRestored = br.GetInt64();
                if (root.TryGetProperty("total_files", out var tf)) totalFiles = tf.GetInt64();
                if (root.TryGetProperty("total_bytes", out var tb)) totalBytes = tb.GetInt64();
                if (root.TryGetProperty("errors", out var er)) errors = er.GetInt64();
                // Don't break — keep the LAST summary line, restic
                // could in principle emit more than one.
            }
            catch { /* not a summary line, skip */ }
        }
    }

    /// <summary>
    /// True iff <paramref name="name"/> contains a glob metachar that
    /// restic's <c>filepath.Match</c> on Windows cannot escape via
    /// any documented mechanism (<c>\</c> is disabled, class self-
    /// escape <c>[[]</c> is rejected as "invalid pattern"). Names
    /// matching this go through the bracket-fallback path:
    /// restore the whole subfolder, then sweep extras.
    /// <para>
    /// <c>*</c> and <c>?</c> are forbidden in Windows filenames by
    /// the Win32 API itself, but we check for them anyway because
    /// the call site doesn't enforce a platform constraint and a
    /// future filename arriving from a non-Windows snapshot could
    /// carry them.
    /// </para>
    /// </summary>
    private static bool HasGlobMetachars(string name) =>
        name.IndexOfAny(_globMetacharsForInclude) >= 0;

    private static readonly char[] _globMetacharsForInclude = new[] { '[', ']', '*', '?' };

    /// <summary>
    /// Replace every <c>[</c> and <c>]</c> in <paramref name="rel"/>
    /// with <c>?</c> (Go filepath.Match single-char wildcard). The
    /// result is an <c>--include</c> pattern that restic accepts on
    /// every platform — bypassing the Windows-restic limitation
    /// where backslash escapes are disabled and bracket characters
    /// always parse as glob character classes.
    /// <para>
    /// The substituted pattern matches the original bracket file AND
    /// any sibling file of the SAME LENGTH whose name differs only
    /// at the two bracket positions. In practice such siblings are
    /// rare; the post-restore sweep
    /// (inside the isolated staging directory) excludes them. We use
    /// <c>?</c> instead of <c>*</c> because <c>?</c> matches exactly
    /// one character — the over-match radius is bounded to
    /// same-length filenames, whereas <c>*</c> matches any number of
    /// characters and would also pull in shorter / longer siblings.
    /// </para>
    /// </summary>
    private static string SubstituteBracketsWithQuestionMark(string rel)
    {
        if (rel.IndexOfAny(_bracketsOnly) < 0) return rel;
        var sb = new System.Text.StringBuilder(rel.Length);
        foreach (var c in rel)
            sb.Append(c is '[' or ']' ? '?' : c);
        return sb.ToString();
    }

    private static readonly char[] _bracketsOnly = new[] { '[', ']' };

    // ---- snapshot-id resolution + tree walk ----

    /// <summary>Fetch + newest-first sort of every snapshot for this
    /// (plan, repo) pair. Shared between <see cref="ListSnapshotsAsync"/>
    /// and <see cref="ResolveSnapshotIdAsync"/> so they agree on the
    /// "revision number" ordering.</summary>
    private async Task<List<ResticSnapshotDto>> ListSnapshotsSortedAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        var planId = MakePlanId(backup, sourcePath, destination, storageName);

        // ListSnapshots invokes restic directly via Backrest. Backrest's
        // auto-scheduled IndexSnapshots task that fires after every
        // backup holds restic's exclusive repo lock for a few seconds;
        // a ListSnapshots that races it gets "exit status 11" (repo
        // already locked). Retry with WaitForRepoIdle in between so the
        // caller sees a clean answer instead of a transient failure.
        // The race is unavoidable from the caller's side — Backup's
        // /Backup RPC returns when the backup itself terminates, NOT
        // when the post-backup index task does.
        ResticSnapshotList resp;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                resp = await client.CallAsync<ListSnapshotsRequest, ResticSnapshotList>(
                    "/v1.Backrest/ListSnapshots",
                    new ListSnapshotsRequest { RepoId = repoId, PlanId = planId },
                    ct).ConfigureAwait(false);
                break;
            }
            catch (BackrestRpcException ex) when (attempt < 2 && IsRepoLockedExit11(ex))
            {
                _log.Information(
                    "ListSnapshots hit restic exit-11 (repo locked) on attempt {Attempt} — waiting for repo idle before retry",
                    attempt + 1);
                await WaitForRepoIdleAsync(client, repoId, ct).ConfigureAwait(false);
            }
        }

        // Restic snapshot ids are 64-char hex; the GUI surfaces a short
        // "revision number" instead. Sort newest-first (by unix_time_ms)
        // so callers can use the resulting index as a stable, human-
        // readable identifier where "latest = highest number" matches
        // what the user sees in the revision picker.
        return resp.Snapshots.OrderByDescending(s => s.UnixTimeMs).ToList();
    }

    /// <summary>True iff the RPC exception message indicates restic
    /// returned exit code 11 — its documented signal for "repository
    /// already locked exclusively". Backrest surfaces this as the
    /// underlying command-failed error text; we match on the suffix
    /// so a non-zero exit from an unrelated restic invocation doesn't
    /// trigger a wait-and-retry on a permanent failure.</summary>
    private static bool IsRepoLockedExit11(BackrestRpcException ex)
        => (ex.Message ?? "").Contains("exit status 11", StringComparison.Ordinal);

    /// <summary>Translate a Backspace "revision number" (1 = oldest,
    /// N = newest) into the (snapshot_id, repo_guid) tuple Backrest's
    /// per-snapshot RPCs need. Throws <see cref="ArgumentOutOfRangeException"/>
    /// if the revision doesn't exist.</summary>
    internal async Task<(string SnapshotId, string RepoGuid)> ResolveSnapshotIdAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, CancellationToken ct)
    {
        var sorted = await ListSnapshotsSortedAsync(backup, sourcePath, destination, storageName, ct)
            .ConfigureAwait(false);
        var pickedId = PickSnapshotIdForRevision(sorted, revision);

        // Backrest 1.13's ListSnapshotFiles RPC requires the canonical
        // 64-char restic config-id as repo_guid; the older permissive
        // behavior of accepting the human repo id was tightened. Read
        // the live config and look up the persisted Guid for this
        // (destination, storageName) repo. AddRepo stamps it at
        // registration time, so it's always present by the time a
        // restore-side call gets here.
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        _log.Debug("[resolve] GetConfig before to look up guid for {RepoId}", repoId);
        var cfg = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
        _log.Debug("[resolve] GetConfig done, repos={Count}", cfg.Repos.Count);
        var repo = cfg.Repos.FirstOrDefault(r => r.Id == repoId);
        if (repo is null || string.IsNullOrEmpty(repo.Guid))
            throw new EngineConfigOutOfSyncException(
                $"Backrest's stored config doesn't have a registered guid for repo {repoId}. " +
                "Most often this means the backup was never run against this destination. " +
                "Run the backup first, then restore.");
        _log.Debug("[resolve] returning snapshot={SnapshotId} guid={Guid}", pickedId, repo.Guid);
        return (pickedId, repo.Guid);
    }

    /// <summary>Pure helper: pick the snapshot that corresponds to the
    /// caller's "revision number" given a list of snapshots already
    /// sorted newest-first. Highest revision number = newest =
    /// sorted[0]; revision 1 = oldest = sorted[count-1]. Throws when
    /// the revision is out of range so callers don't accidentally
    /// hand a stale revision id to the engine.</summary>
    internal static string PickSnapshotIdForRevision(IReadOnlyList<ResticSnapshotDto> newestFirstSorted, int revision)
    {
        if (revision < 1 || revision > newestFirstSorted.Count)
            throw new ArgumentOutOfRangeException(nameof(revision),
                $"Revision {revision} doesn't exist; this snapshot chain has {newestFirstSorted.Count} revision(s).");
        return newestFirstSorted[newestFirstSorted.Count - revision].Id;
    }

    /// <summary>Depth-first walk of a snapshot's file tree via Backrest's
    /// ListSnapshotFiles RPC. Each call returns one directory's
    /// immediate children; we recurse into "dir"-typed entries.
    /// Anything non-"dir" (files, symlinks, fifos, devices) lands on
    /// <paramref name="collected"/> as a leaf.</summary>
    private static async Task WalkSnapshotTreeAsync(
        BackrestClient client, string repoGuid, string snapshotId, string rootPath,
        List<RevisionFile> collected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AppLogger.For<ResticBackrestEngine>().Debug(
            "[walk] ListSnapshotFiles before guid={Guid} snap={Snap} path={Path}",
            repoGuid, snapshotId, rootPath);
        var resp = await client.CallAsync<ListSnapshotFilesRequest, ListSnapshotFilesResponse>(
            "/v1.Backrest/ListSnapshotFiles",
            new ListSnapshotFilesRequest { RepoGuid = repoGuid, SnapshotId = snapshotId, Path = rootPath },
            ct).ConfigureAwait(false);
        AppLogger.For<ResticBackrestEngine>().Debug(
            "[walk] ListSnapshotFiles done entries={Count}", resp.Entries?.Count ?? 0);

        if (resp.Entries is null) return;
        foreach (var entry in resp.Entries)
        {
            ct.ThrowIfCancellationRequested();
            // Backrest's ListSnapshotFiles returns the QUERIED directory
            // as its own first entry (restic ls includes the path
            // itself, not just its children). Recursing into that
            // entry produces an infinite loop because the recursive
            // call asks for the same path back. Skip any entry whose
            // path equals the path we just asked for.
            if (string.Equals(entry.Path, rootPath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(entry.Type, "dir", StringComparison.OrdinalIgnoreCase))
            {
                await WalkSnapshotTreeAsync(client, repoGuid, snapshotId, entry.Path, collected, ct)
                    .ConfigureAwait(false);
                continue;
            }
            var modified = DateTime.TryParse(entry.Mtime, out var mtime) ? mtime.ToUniversalTime() : DateTime.MinValue;
            // Strip the leading "/" so paths match the relative-path
            // contract RestoreEngine + the in-memory engine emit.
            var rel = entry.Path.TrimStart('/');
            collected.Add(new RevisionFile(rel, entry.Size, modified));
        }
    }

    /// <summary>Block until Backrest reports the given operation id has
    /// reached a terminal status (SUCCESS / WARNING / ERROR / CANCELLED).
    /// Polls GetOperations rather than subscribing to GetOperationEvents
    /// because fast ops can reach a terminal status BEFORE the stream
    /// subscription lands (the bracket-fix restore shape regularly
    /// completes in &lt; 200 ms).</summary>
    private async Task<BackrestOperation> WaitForOperationTerminalAsync(
        BackrestClient client, string opIdString, CancellationToken ct,
        Action<string>? lineSink = null, Action<BackrestOperation>? observed = null)
    {
        if (!long.TryParse(opIdString, out _))
            throw new InvalidOperationException($"RunCommand returned an unparseable op id: '{opIdString}'");
        // Lifetime is owned by the caller, not a five-minute wall clock. Large
        // restores may legitimately run for hours. RPC calls remain cancellable.
        string lastStatus = "";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var response = await client.CallAsync<GetOperationsRequest, BackrestOperationList>(
                "/v1.Backrest/GetOperations",
                new GetOperationsRequest { Selector = new OpSelector { Ids = new[] { opIdString } } }, ct).ConfigureAwait(false);
            var operation = response.Operations?.FirstOrDefault(o => o.Id == opIdString);
            if (operation is not null)
            {
                observed?.Invoke(operation);
                if (operation.Status != lastStatus)
                {
                    lastStatus = operation.Status;
                    var line = $"[backrest] op={operation.Id} status={operation.Status} {operation.DisplayMessage}".TrimEnd();
                    RaiseLineWritten(line);
                    lineSink?.Invoke(line);
                }
                if (IsTerminalStatus(operation.Status)) return operation;
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }
    /// <summary>Wait for the repository queue to drain before retrying a read.
    /// A busy repository is never assumed idle just because a timer expired.</summary>
    private async Task WaitForRepoIdleAsync(BackrestClient client, string repoId, CancellationToken ct)
    {
        var guid = await ResolveRepoGuidAsync(client, repoId, ct).ConfigureAwait(false);
        while (true)
        {
            var operations = await GetRepoOperationsAsync(client, guid, ct).ConfigureAwait(false);
            if (!operations.Any(op => op.Status is "STATUS_PENDING" or "STATUS_INPROGRESS")) return;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    private static bool IsTerminalStatus(string status) => status is
        BackrestStatus.Success or BackrestStatus.Warning or BackrestStatus.Error or BackrestStatus.UserCancelled or BackrestStatus.SystemCancelled;

    /// <summary>
    /// Quote an argv element for Backrest's RunCommand RPC. Backrest
    /// passes the command string through <c>github.com/google/shlex</c>
    /// (POSIX-shell tokenizer) before spawning restic — which means
    /// any unquoted Windows backslash gets eaten as an escape char.
    /// A path like <c>C:\Users\erwin\Dropbox</c> arrives at restic
    /// as <c>C:Userserwindropbox</c>, restore fails with exit 1, and
    /// the only signal back is the bare exit code. We single-quote
    /// every argument: shlex preserves backslashes inside single
    /// quotes verbatim, and embedded single quotes are escaped using
    /// the standard POSIX <c>'\''</c> trick.
    /// </summary>
    private static string QuoteArg(string s)
        => "'" + s.Replace("'", "'\\''") + "'";

    internal static string? FindRestoredFile(string targetPath, string sourcePath, string relativePath)
    {
        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var bare = Path.Combine(targetPath, rel);
        if (File.Exists(bare)) return bare;

        // Restic preserves the absolute source path layout under the
        // target. We strip the source's anchor so the result re-anchors
        // at <target>. Three anchor shapes to handle:
        //   * UNC path: "\\server\share\foo" -> "server\share\foo".
        //     Detected BEFORE the leading-separator trim because that
        //     trim would otherwise eat the two backslashes that
        //     distinguish UNC from drive-letter paths.
        //   * Drive-letter Windows path: "C:\Users\me" -> "Users\me".
        //   * POSIX absolute / forward-slash: "/data/photos" -> "data/photos".
        var normalised = sourcePath.Replace('/', Path.DirectorySeparatorChar);
        string sourceNoRoot;
        if (normalised.StartsWith(@"\\", StringComparison.Ordinal))
        {
            sourceNoRoot = normalised.Substring(2);
        }
        else
        {
            sourceNoRoot = normalised.TrimStart(Path.DirectorySeparatorChar);
            if (sourceNoRoot.Length >= 2 && sourceNoRoot[1] == ':')
                sourceNoRoot = sourceNoRoot.Substring(2).TrimStart(Path.DirectorySeparatorChar);
        }
        var withSourcePrefix = Path.Combine(targetPath, sourceNoRoot, rel);
        if (File.Exists(withSourcePrefix)) return withSourcePrefix;

        return null;
    }

    public async Task PruneSnapshotIdAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        string snapshotId, CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        var planId = MakePlanId(backup, sourcePath, destination, storageName);
        var result = await RunTrackedRepoRpcAsync(client, repoId, "/v1.Backrest/Forget",
            new ForgetRequest { RepoId = repoId, PlanId = planId, SnapshotId = snapshotId },
            op => op.PlanId == planId && op.OperationForget is not null, ct).ConfigureAwait(false);
        if (result.Status is not BackrestStatus.Success)
            throw new IOException($"Snapshot removal reported {result.Status}: {result.DisplayMessage}");
    }

    public async Task PruneExhaustiveAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        var result = await RunTrackedRepoRpcAsync(client, repoId, "/v1.Backrest/DoRepoTask",
            new DoRepoTaskRequest { RepoId = repoId, Task = (int)BackrestRepoTask.TaskPrune },
            op => op.OperationPrune is not null, ct).ConfigureAwait(false);
        if (result.Status is not BackrestStatus.Success)
            throw new IOException($"Repository cleanup reported {result.Status}: {result.DisplayMessage}");
    }

    public async Task EnsureInitializedAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        await EnsurePlanAndRepoAsync(backup, sourcePath, destination, storageName, ct).ConfigureAwait(false);
    }

    // ---- operations with a real Restic analogue routed via RunCommand ----

    /// <summary>
    /// Forgets all snapshots except the most recent one, then prunes
    /// the unreferenced chunks. Implemented as a single restic invocation
    /// (<c>forget --keep-last 1 --prune</c>) routed through Backrest's
    /// RunCommand RPC. Backrest doesn't expose a "forget with overridden
    /// retention" task in its plan-driven API, so RunCommand is the
    /// honest tool — it bypasses Backrest's plan/retention machinery
    /// without mutating the user's configured retention policy.
    /// </summary>
    public async Task<RunRecord> PruneToLatestAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        await EnsurePlanAndRepoAsync(backup, sourcePath, destination, storageName, ct).ConfigureAwait(false);

        var started = DateTime.UtcNow;
        RaiseLineWritten($"[backspace] prune-to-latest started for {destination.Name}/{storageName}");
        const string command = "forget --keep-last 1 --prune";
        var terminal = await RunTrackedRepoRpcAsync(client, MakeRepoId(destination, storageName), "/v1.Backrest/RunCommand",
            new RunCommandRequest { RepoId = MakeRepoId(destination, storageName), Command = command },
            op => op.OperationRunCommand?.Command == command, ct).ConfigureAwait(false);

        return new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            Kind = RunKind.Backup,
            StartedUtc = started,
            EndedUtc = DateTime.UtcNow,
            Status = StatusFromBackrestTerminal(terminal.Status),
            Summary = $"prune-to-latest: {terminal.Status} (op {terminal.Id}) {terminal.DisplayMessage}",
        };
    }

    // EnumOnlyAsync (restic "backup --dry-run" via RunCommand) used to
    // live here for the Filters > Advanced Simulate pane. It was
    // removed once FilterSimulator switched to a local filesystem walk:
    // the dry-run ran UNFILTERED (RunCommand bypasses the Backrest
    // plan, so the plan's excludes never reached restic), required the
    // repo to be reachable just to preview filters, and restic has no
    // output mode that names the matching rule per excluded path. Any
    // future RunCommand caller that embeds a path or glob MUST wrap it
    // with QuoteArg — see that helper's doc for the shlex trap.

    /// <summary>
    /// Rotates the master-key encryption password on the repo by
    /// spawning <c>restic.exe key passwd</c> directly (Backrest's
    /// RunCommand RPC doesn't expose stdin, but we have the bundled
    /// restic binary on disk via <see cref="AppPaths.ResticBinary"/>).
    ///
    /// The current password goes into <c>RESTIC_PASSWORD</c> on the
    /// env; the new password is written to a 0700-mode temp file and
    /// fed via <c>--new-password-file</c> so it never appears on
    /// argv. Both temp files are deleted on every code path.
    /// </summary>
    public async Task<(int exitCode, string output)> ChangeStoragePasswordAsync(
        Destination destination, string storageName,
        string oldPassword, string newPassword, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(destination.StoragePasswordRef))
            return (1, "Cannot change the repository password without a saved credential reference. Save the destination first.");
        if (!File.Exists(AppPaths.ResticBinary))
            return (1, $"restic binary not found at {AppPaths.ResticBinary}. Run a non-skip build " +
                       "(drop -p:SkipBackrestDownload) or place restic(.exe) next to the Backspace exe.");

        using var maintenance = await ServiceLocator.Maintenance.AcquireAsync("Changing destination password", ct: ct).ConfigureAwait(false);
        var rclone = new RcloneConfigBuilder(_secrets);
        await rclone.EnsureDriveIdentityAsync(destination, ct, ServiceLocator.Config).ConfigureAwait(false);
        var rclonePath = RcloneConfigBuilder.NeedsRcloneRemote(destination)
            ? rclone.MaterialiseDestination(destination, ServiceLocator.Config?.Current?.RcloneOAuthApps) : RcloneConfigBuilder.DefaultPath;
        var resticEnv = ResticUriResolver.Resolve(destination, _secrets, rclonePath);

        // restic --new-password-file <path> reads the new password
        // from that file's first line. On Unix, /tmp is world-readable
        // by default and the file would be created at the user's umask
        // (typically 0644). Set UnixCreateMode at file-create time so
        // the bits are 0600 atomically — no race window where another
        // local user could open and read the password.
        var newPwdFile = Path.Combine(Path.GetTempPath(), $"backspace-restic-newpwd-{Guid.NewGuid():N}.txt");
        try
        {
            var pwdFileOpts = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                pwdFileOpts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var fs = new FileStream(newPwdFile, pwdFileOpts))
            await using (var sw = new StreamWriter(fs))
                await sw.WriteAsync(newPassword.AsMemory(), ct).ConfigureAwait(false);

            // Keep real repository locks intact. Restic remains the final
            // authority for conflicts with external tools or other installs.
            var args = new[] { "key", "passwd", "--retry-lock", "30s", "--new-password-file", newPwdFile };
            var env = BuildResticPasswordChangeEnv(resticEnv, oldPassword);
            ct.ThrowIfCancellationRequested();
            // Once key mutation starts, finish it and save the matching secret
            // under this same exclusive lease. Preserve a durable recovery copy
            // before touching the repository in case final persistence fails.
            var passwordRef = destination.StoragePasswordRef;
            if (!string.IsNullOrEmpty(passwordRef)) _secrets.Set(passwordRef + ":pending-rotation", newPassword);
            var (exit, output) = await SpawnResticAsync(args, env, CancellationToken.None).ConfigureAwait(false);
            if (exit == 0 && !string.IsNullOrEmpty(passwordRef))
            {
                try { _secrets.Set(passwordRef, newPassword); }
                catch (Exception ex)
                {
                    var reason = "The repository password changed but its saved credential could not be updated. The new password is preserved in the encrypted recovery entry. Restart after correcting the configuration folder's write access.";
                    ServiceLocator.Maintenance.BlockUntilRestart(reason);
                    _log.Error(ex, "Could not persist rotated repository password");
                    return (1, reason);
                }
                try { _secrets.Remove(passwordRef + ":pending-rotation"); }
                catch (Exception ex) { _log.Debug(ex, "Recovery credential cleanup deferred"); }
            }
            if (exit == 11)
                output += "\nThe repository is in use. Wait for the other operation to finish and try again; its lock was preserved.";
            return (exit, output);
        }
        finally
        {
            try { File.Delete(newPwdFile); } catch { /* best-effort */ }
        }
    }

    /// <summary>Build the env block restic needs to talk to the repo
    /// without prompting. The user's current password goes into
    /// <c>RESTIC_PASSWORD</c>; everything else (URI-specific cloud
    /// credentials, rclone config path) comes from the resolver.</summary>
    internal static Dictionary<string, string> BuildResticPasswordChangeEnv(ResticEnvironment resticEnv, string oldPassword)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RESTIC_REPOSITORY"] = resticEnv.Uri,
            ["RESTIC_PASSWORD"]   = oldPassword,
        };
        foreach (var line in resticEnv.Env)
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            env[line.Substring(0, eq)] = line.Substring(eq + 1);
        }
        return env;
    }

    private static async Task<(int exitCode, string output)> SpawnResticAsync(
        string[] args, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(AppPaths.ResticBinary)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        var buf = new System.Text.StringBuilder();
        var gate = new object();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) buf.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (gate) buf.AppendLine("[err] " + e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } }))
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        return (proc.ExitCode, buf.ToString());
    }

    // ---- internals ----

    /// <summary>Lazy bootstrap: launches the BackrestSidecar on first
    /// use (cheap no-op on subsequent calls), then returns the
    /// configured client. Callers don't need to know about
    /// StartAsync — touching the engine is enough.</summary>
    internal async Task<BackrestClient> EnsureClientAsync(CancellationToken ct)
    {
        await _sidecar.EnsureStartedAsync(ct).ConfigureAwait(false);
        if (_sidecar.Client is null)
            throw new InvalidOperationException("BackrestSidecar reported running but the client is null. This is a bug in the sidecar lifecycle.");
        return _sidecar.Client;
    }

    /// <summary>Stable repo id for a (Destination, storageName) pair.
    /// Used as the human-readable Backrest repo ID; uniqueness across
    /// the user's whole config is required by Backrest's schema.</summary>
    internal static string MakeRepoId(Destination destination, string storageName)
    {
        var name = string.IsNullOrEmpty(storageName) ? "default" : storageName;
        return Slug($"{destination.Id}-{name}");
    }

    /// <summary>
    /// Deterministic 64-char lowercase-hex Backrest repo guid derived
    /// from the human <c>repoId</c>. Backrest 1.13.0's SetConfig
    /// validator rejects any guid that isn't exactly 64 chars
    /// ("guid '…' invalid: must be 64 characters") — the format
    /// mirrors restic's own repo-config-id (sha256 hex, also 64
    /// chars). The previous canonical-UUID shape (36 chars with
    /// dashes) is now rejected.
    ///
    /// This helper is the fallback used by tests and forward-looking
    /// code paths. In production
    /// <see cref="BuildRepoFromDestination"/> emits an empty guid
    /// and lets Backrest's auto-init flow assign the real
    /// restic-config-id as the canonical guid — see the comment on
    /// that method for why.
    /// </summary>
    internal static string MakeRepoGuid(string repoId)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(repoId));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Backrest 1.13.0 accepts guids that are exactly 64
    /// hex chars. Older Backspace builds wrote a 36-char UUID; any
    /// repo carrying the legacy shape would now fail SetConfig
    /// validation. <see cref="EnsurePlanAndRepoCoreAsync"/> uses
    /// this check to detect and drop such entries so Backrest can
    /// re-adopt the on-disk restic repo via auto-init and persist
    /// the canonical guid.</summary>
    internal static bool IsValidBackrestGuid(string? guid)
    {
        if (string.IsNullOrEmpty(guid) || guid.Length != 64) return false;
        for (int i = 0; i < guid.Length; i++)
        {
            var c = guid[i];
            var isHex = (c >= '0' && c <= '9')
                     || (c >= 'a' && c <= 'f')
                     || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
    }

    /// <summary>Stable plan id for a (Backup, sourcePath, Destination,
    /// storageName) quadruple.</summary>
    internal static string MakePlanId(Backup backup, string sourcePath, Destination destination, string storageName)
    {
        var name = string.IsNullOrEmpty(storageName) ? "default" : storageName;
        var sourceTag = ShortHash(sourcePath);
        return Slug($"{backup.Id}-{destination.Id}-{name}-{sourceTag}");
    }

    private async Task EnsurePlanAndRepoAsync(
        Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        var planId = MakePlanId(backup, sourcePath, destination, storageName);

        await _ensureConfigGate.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsurePlanAndRepoCoreAsync(backup, sourcePath, destination, storageName, client, repoId, planId, ct).ConfigureAwait(false); }
        finally { _ensureConfigGate.Release(); }
    }

    private async Task EnsurePlanAndRepoCoreAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        BackrestClient client, string repoId, string planId, CancellationToken ct)
    {
        await new RcloneConfigBuilder(_secrets).EnsureDriveIdentityAsync(destination, ct, ServiceLocator.Config).ConfigureAwait(false);

        // Phase 1: get the repo into Backrest's config with a real
        // 64-char guid IF it isn't already there. Backrest 1.13 has
        // two layers of validation that pin us into using the
        // dedicated /AddRepo RPC for new repos rather than the bulk
        // /SetConfig path:
        //
        //   • SetConfig's per-repo validator says: Guid + AutoInit
        //     are mutually exclusive — either send a 64-char Guid
        //     OR set AutoInitialize=true; not both.
        //   • The orchestrator's applyConfig step (the IN-MEMORY
        //     reload that decides which plans Backup RPC can find)
        //     says: every repo must carry a non-empty Guid, full
        //     stop — AutoInitialize is irrelevant at this layer.
        //
        // So an empty-Guid + AutoInit=true repo passes SetConfig's
        // validator (the response even comes back with our stanza),
        // but applyConfig drops the whole config on the floor with
        // "guid is required for all repos" — and the very next
        // Backup RPC fails with "plan not found" because the
        // orchestrator never accepted our plan. That's the bug a
        // brand-new Backspace install hit on its first run.
        //
        // /AddRepo runs `restic init` (or adopts an already-init'd
        // on-disk repo), reads the canonical 64-char restic
        // config-id, stamps it as Guid, and persists the result —
        // satisfying both validation layers in one shot.
        var existingRepo = await GetExistingRepoAsync(client, repoId, ct).ConfigureAwait(false);

        // Migration: pre-1.13 Backspace builds wrote 36-char UUIDs
        // here. Backrest 1.13 rejects anything that isn't 64 hex
        // chars. Re-add via /AddRepo so Backrest can replace the
        // legacy guid with restic's actual config-id.
        var needsReAdd = existingRepo is not null && !IsValidBackrestGuid(existingRepo.Guid);
        if (needsReAdd)
        {
            _log.Information(
                "EnsurePlanAndRepo: dropping legacy-format guid '{Guid}' for repo {RepoId} and re-adding via /AddRepo so Backrest can re-assign the canonical 64-char id",
                existingRepo!.Guid, repoId);
            existingRepo = null;
        }

        if (existingRepo is null)
        {
            var repoStanza = BuildRepoFromDestination(repoId, destination, backup);
            try
            {
                var added = await client.CallAsync<AddRepoRequest, BackrestConfig>(
                    "/v1.Backrest/AddRepo",
                    new AddRepoRequest { Repo = repoStanza }, ct).ConfigureAwait(false);
                var persistedRepo = added.Repos.FirstOrDefault(r => r.Id == repoId);
                if (persistedRepo is null || string.IsNullOrEmpty(persistedRepo.Guid))
                {
                    throw new EngineConfigOutOfSyncException(
                        $"Backrest accepted AddRepo but the response doesn't contain a repo with id {repoId} (or its guid is empty). " +
                        "Wipe Backspace.config\\backrest and let the daemon rebuild from scratch.");
                }
                _log.Debug("EnsurePlanAndRepo: AddRepo succeeded for {RepoId}, canonical guid={Guid}", repoId, persistedRepo.Guid);
                existingRepo = persistedRepo;
                _repoGuidCache[repoId] = persistedRepo.Guid;
            }
            catch (BackrestRpcException ex)
            {
                throw TranslateRpcError(ex, "AddRepo");
            }
        }

        // Phase 2: ensure the plan exists, and that the repo's
        // per-op Flags reflect THIS backup's current bandwidth
        // limit. Plans don't carry their own validation pitfall —
        // SetConfig accepts them cleanly. The read-modify-write
        // loop here handles the rare case where another caller
        // mutated config between our reads.
        for (int attempt = 0; attempt < MaxModnoMismatchRetries; attempt++)
        {
            var cfg = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
                "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
            var liveRepo = cfg.Repos.FirstOrDefault(r => r.Id == repoId);
            var existingPlan = cfg.Plans.FirstOrDefault(p => p.Id == planId);
            _log.Debug(
                "EnsurePlanAndRepo: GetConfig modno={Modno} repos={RepoCount} plans={PlanCount}; want repo={RepoId} (found={RepoFound}, guid={Guid}) plan={PlanId} (found={PlanFound})",
                cfg.Modno, cfg.Repos.Count, cfg.Plans.Count, repoId, liveRepo is not null, liveRepo?.Guid ?? "", planId, existingPlan is not null);
            if (liveRepo is not null && !string.IsNullOrEmpty(liveRepo.Guid))
            {
                _repoGuidCache[repoId] = liveRepo.Guid;
            }

            if (liveRepo is null)
            {
                // Lost a race: AddRepo above ran but somebody else
                // (typically a parallel cell) deleted the repo
                // between Phase 1 and now. Retry by re-entering the
                // method from the caller — the semaphore in
                // EnsurePlanAndRepoAsync serialises us, so the next
                // cell will hit our just-added repo cleanly.
                throw new EngineConfigOutOfSyncException(
                    $"Repo {repoId} disappeared between AddRepo and SetConfig: config raced or daemon restarted. Retry the run.");
            }

            var wantedRepoFlags = BuildRepoFlagsForBackup(backup, destination);
            var flagsMatch = RepoFlagsEqual(liveRepo.Flags, wantedRepoFlags);

            // Password drift: when the user rotates the storage
            // password (ChangeStoragePasswordAsync + secrets-store
            // update), the next backup hits Backrest with the same
            // (Destination, storageName) tuple but a different
            // secret. The Backup RPC then fails with "auto unlock
            // repo: wrong password" because Backrest's config still
            // carries the pre-rotation password. Detect the drift
            // and rewrite the repo's Password in place — same
            // SetConfig pass that handles Flags drift, so it costs
            // nothing extra on the steady-state no-drift path.
            var wantedPassword = destination.StoragePasswordRef is null
                ? ""
                : _secrets.Get(destination.StoragePasswordRef);
            var passwordMatch = string.Equals(liveRepo.Password, wantedPassword, StringComparison.Ordinal);

            // URI drift: the user edited the Destination's PathOrSubpath
            // (mid-life rename of the local folder, change of bucket
            // prefix, change of rclone remote subfolder). Backrest's
            // repo stanza still points at the OLD URI, so restic gets
            // routed to a path that no longer exists and fails with
            // "repository does not exist" (exit 10). Detect the drift
            // and rewrite the repo's Uri (and the matching Env, since
            // a cloud-URI change can shift the rclone remote stanza
            // we materialise alongside) in the same SetConfig pass.
            // ResticUriResolver.Resolve is the canonical URI builder
            // — call it once and compare.
            var wantedRclonePath = RcloneConfigBuilder.NeedsRcloneRemote(destination)
                ? new RcloneConfigBuilder(_secrets).MaterialiseDestination(destination, ServiceLocator.Config?.Current?.RcloneOAuthApps)
                : RcloneConfigBuilder.DefaultPath;
            var wantedResticEnv = ResticUriResolver.Resolve(destination, _secrets, wantedRclonePath);
            // Path-backed destinations (LocalFolder, ExternalDrive,
            // NetworkShare) produce bare filesystem-path URIs that
            // restic routes through its "local" backend; on Windows
            // and macOS these paths are case-insensitive, so a
            // user who retypes "D:\Backups" as "d:\backups" or a
            // FolderPicker that returns a normalised case must NOT
            // be treated as drift (rewriting the URI would log a
            // misleading "path/subpath rename detected" line and
            // burn a SetConfig RPC on every cell start). Scheme-
            // prefixed URIs (s3:, b2:, rclone:, sftp:) DO need
            // ordinal compare — S3 bucket names, B2 keys, and
            // rclone remote names ARE case-sensitive at the
            // endpoint level.
            var uriComparison = destination.IsLocalLike && !OperatingSystem.IsLinux()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var uriMatch = string.Equals(liveRepo.Uri, wantedResticEnv.Uri, uriComparison);
            var wantedEnv = wantedResticEnv.Env.Concat(new[] { "RESTIC_PROGRESS_FPS=0" }).ToList();
            var envMatch = (liveRepo.Env ?? new()).SequenceEqual(wantedEnv);

            if (existingPlan is not null && flagsMatch && passwordMatch && uriMatch && envMatch)
                return; // nothing to do

            if (!flagsMatch)
            {
                _log.Information(
                    "EnsurePlanAndRepo: rewriting repo {RepoId} Flags to match backup {Backup}'s current limits (was [{Was}], now [{Now}])",
                    repoId, backup.Name, string.Join(' ', liveRepo.Flags ?? (IEnumerable<string>)Array.Empty<string>()), string.Join(' ', wantedRepoFlags));
                liveRepo.Flags = wantedRepoFlags;
            }
            if (!passwordMatch)
            {
                _log.Information(
                    "EnsurePlanAndRepo: rewriting repo {RepoId} Password (rotation detected) for backup {Backup}",
                    repoId, backup.Name);
                liveRepo.Password = wantedPassword;
            }
            if (!uriMatch || !envMatch)
            {
                _log.Information(
                    "EnsurePlanAndRepo: rewriting repo {RepoId} Uri (path/subpath rename detected) for backup {Backup} (was {Was}, now {Now})",
                    repoId, backup.Name, liveRepo.Uri, wantedResticEnv.Uri);
                liveRepo.Uri = wantedResticEnv.Uri;
                liveRepo.Env = wantedEnv;
            }
            if (existingPlan is null)
                cfg.Plans.Add(BuildPlanFromBackup(planId, repoId, backup, sourcePath));

            try
            {
                var written = await client.CallAsync<BackrestConfig, BackrestConfig>(
                    "/v1.Backrest/SetConfig", cfg, ct).ConfigureAwait(false);
                var persistedRepo = written.Repos.FirstOrDefault(r => r.Id == repoId);
                var persistedPlan = written.Plans.FirstOrDefault(p => p.Id == planId);
                if (persistedRepo is null || persistedPlan is null)
                {
                    throw new EngineConfigOutOfSyncException(
                        $"Backrest accepted SetConfig (modno {cfg.Modno} → {written.Modno}) but the response doesn't contain " +
                        $"the {(persistedRepo is null ? "repo" : "plan")} we just sent " +
                        $"(repo={repoId}, plan={planId}). " +
                        "This usually means the daemon's config schema differs from what Backspace expects. " +
                        "Wipe Backspace.config\\backrest and let the daemon rebuild from scratch.");
                }
                _log.Debug(
                    "EnsurePlanAndRepo: SetConfig succeeded, modno={Modno}, persisted repo={RepoOk} plan={PlanOk}",
                    written.Modno, persistedRepo is not null, persistedPlan is not null);
                return;
            }
            catch (BackrestRpcException ex) when (IsModnoMismatch(ex))
            {
                _log.Information("SetConfig modno mismatch on attempt {Attempt}; retrying with fresh GetConfig", attempt + 1);
                continue;
            }
        }
        throw new InvalidOperationException(
            "Backrest config write kept hitting a modno mismatch after 5 retries; another process is mutating config concurrently.");
    }

    /// <summary>Look up the live config and return the entry for
    /// <paramref name="repoId"/>, or null if it isn't there yet. A
    /// helper extracted so Phase 1 of <see cref="EnsurePlanAndRepoCoreAsync"/>
    /// can decide between calling <c>/AddRepo</c> (new repo path)
    /// and skipping straight to plan-only SetConfig (existing repo
    /// path) without duplicating the GetConfig boilerplate.</summary>
    private async Task<BackrestRepo?> GetExistingRepoAsync(BackrestClient client, string repoId, CancellationToken ct)
    {
        var cfg = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
        return cfg.Repos.FirstOrDefault(r => r.Id == repoId);
    }

    /// <summary>
    /// Backrest's SetConfig surfaces its modno-conflict as an RPC
    /// error whose message contains "modno". We classify by message
    /// rather than status code because the surface across Connect-Go
    /// versions has been inconsistent.
    /// </summary>
    private static bool IsModnoMismatch(BackrestRpcException ex) =>
        ex.Message.Contains("modno", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Detect Backrest's "plan not found" / "repo not found" errors.
    /// Backrest surfaces them as plain messages like
    /// <c>get plan "&lt;id&gt;": plan not found</c> or
    /// <c>get repo "&lt;id&gt;": repo not found</c>. Match on the
    /// trailing phrase so we catch both shapes without coupling to
    /// the verb. Surfaces as <see cref="EngineConfigOutOfSyncException"/>
    /// which is <see cref="INonRetriable"/> — re-running the same
    /// cell won't put the missing entry back in Backrest's config.
    /// </summary>
    private static bool IsMissingPlanOrRepo(BackrestRpcException ex)
    {
        var m = ex.Message;
        return m.Contains("plan not found", StringComparison.OrdinalIgnoreCase)
            || m.Contains("repo not found", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Translate a Backrest RPC error into the right typed exception.
    /// Connect-Go codes that indicate a client-side issue (wrong
    /// credentials, malformed request, missing-in-config) all get
    /// mapped to <see cref="INonRetriable"/> subtypes so the
    /// orchestrator's retry cascade gives up immediately. Anything
    /// transient (Internal, Unavailable, DeadlineExceeded) flows
    /// through unchanged so the cascade can recover from a
    /// momentary glitch. Single source of truth — every RPC call
    /// site that catches <see cref="BackrestRpcException"/> should
    /// route through this so the classification stays consistent.
    /// </summary>
    internal static Exception TranslateRpcError(BackrestRpcException ex, string context)
    {
        if (IsMissingPlanOrRepo(ex))
        {
            return new EngineConfigOutOfSyncException(
                $"{context}: Backrest's stored config doesn't have the plan / repo Backspace just registered. " +
                "Most often this means the Backspace.config\\backrest data dir is from a previous install whose " +
                "ids don't line up. Stop Backspace, delete Backspace.config\\backrest, restart, and try again. " +
                "(Underlying error: " + ex.Message + ")", ex);
        }
        // Connect-Go canonical codes:
        //   https://connectrpc.com/docs/protocol#error-codes
        if (string.Equals(ex.Code, "unauthenticated", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ex.Code, "permission_denied", StringComparison.OrdinalIgnoreCase))
        {
            return new EngineAuthFailedException(
                $"{context}: the backup engine reported an authentication failure (Connect code {ex.Code}). " +
                "Check the repository password / cloud OAuth token / storage credentials for this destination. " +
                "(Underlying error: " + ex.Message + ")", ex);
        }
        if (string.Equals(ex.Code, "invalid_argument", StringComparison.OrdinalIgnoreCase))
        {
            return new EngineInvalidArgumentException(
                $"{context}: the backup engine rejected the request as malformed (Connect code invalid_argument). " +
                "Check the destination's URI / region / bucket / subpath configuration. " +
                "(Underlying error: " + ex.Message + ")", ex);
        }
        // Anything else (Internal, Unavailable, DeadlineExceeded, …)
        // — let it bubble so the retry cascade can have a go.
        return ex;
    }

    internal BackrestRepo BuildRepoFromDestination(string repoId, Destination destination, Backup? backup = null)
    {
        var password = destination.StoragePasswordRef is null
            ? ""
            : _secrets.Get(destination.StoragePasswordRef);

        // Cloud-kind destinations need rclone.conf materialised before
        // restic spawns rclone; the resolver knows whether this
        // destination kind reaches into rclone and emits the matching
        // RCLONE_CONFIG / AWS_* env vars onto Repo.env. Custom OAuth
        // apps (if any) come along for the ride so the user's higher
        // rate-limit quota lands in rclone.conf.
        var rclonePath = RcloneConfigBuilder.DefaultPath;
        if (RcloneConfigBuilder.NeedsRcloneRemote(destination))
        {
            var oauthApps = ServiceLocator.Config?.Current?.RcloneOAuthApps;
            rclonePath = new RcloneConfigBuilder(_secrets).MaterialiseDestination(destination, oauthApps);
        }
        var resticEnv = ResticUriResolver.Resolve(destination, _secrets, rclonePath);

        // Repo-level Flags apply to EVERY restic invocation Backrest
        // spawns against this repo: backup, restore, check, prune,
        // stats, forget, snapshots. Routing the bandwidth limit
        // through here covers the maintenance ops (which previously
        // ran unthrottled because they go through TaskCheck /
        // TaskPrune RPCs that don't read plan.backup_flags). Plan-
        // level backup_flags still carries --limit-upload too for
        // the backup-specific path; restic accepts repeated flags
        // and uses the last occurrence, so the per-op flag wins
        // when both are present.
        var repoFlags = BuildRepoFlagsForBackup(backup, destination);

        return new BackrestRepo
        {
            Id = repoId,
            // Sent through /AddRepo (see EnsurePlanAndRepoCoreAsync's
            // Phase 1) rather than SetConfig — the handler runs
            // `restic init` and stamps the canonical 64-char
            // config-id into Guid, returning the persisted stanza.
            // We leave Guid empty and AutoInitialize=false on the
            // wire because AddRepo writes the Guid for us; even
            // sending AutoInitialize=true here would be ignored
            // (AddRepo's path always runs init). After Phase 1 the
            // live config carries the canonical Guid and Phase 2's
            // SetConfig leaves it untouched.
            Guid = "",
            Uri = resticEnv.Uri,
            Password = password,
            // A inherited positive FPS override bypasses restic's --quiet.
            // Zero leaves normal commands at their default rate and allows
            // quiet restores to suppress duration-proportional output.
            Env = resticEnv.Env.Concat(new[] { "RESTIC_PROGRESS_FPS=0" }).ToList(),
            Flags = repoFlags,
            AutoInitialize = false,
            AutoUnlock = true, // recover from stale locks left by crashed processes
        };
    }

    /// <summary>Per-repo global flags that should be passed to every
    /// restic invocation Backrest runs against this repo (covers
    /// backup, restore, check, prune, stats, forget, snapshots,
    /// AND init via AddRepo). Bumped out to a helper so
    /// <see cref="EnsurePlanAndRepoCoreAsync"/> can rewrite
    /// existing-repo Flags in place when a per-backup setting
    /// changes without needing to compare List equality inline.</summary>
    internal static List<string> BuildRepoFlagsForBackup(Backup? backup, Destination? destination = null)
    {
        var flags = new List<string>();
        if (backup is not null && backup.BandwidthLimitKBps > 0)
        {
            var rate = backup.BandwidthLimitKBps.ToString(System.Globalization.CultureInfo.InvariantCulture);
            flags.Add("--limit-upload");
            flags.Add(rate);
            flags.Add("--limit-download");
            flags.Add(rate);
        }
        // restic 0.17+ refuses to operate on an empty-password repo
        // unless --insecure-no-password is explicit at EVERY
        // invocation — init, backup, restore, check, prune, the lot.
        // Earlier versions accepted "" silently, which is what
        // Backspace's Encrypted=false destinations have been relying
        // on. Plumb the flag through the repo-level Flags so it
        // reaches every restic command Backrest spawns (including
        // the init that runs as part of /AddRepo). Tests and the UI
        // copy treat Encrypted=false as "no password" — the empty
        // string was never going to provide real protection, and
        // the explicit flag makes the contract honest at the
        // engine boundary.
        if (destination is not null
            && string.IsNullOrEmpty(destination.StoragePasswordRef)
            && !destination.Encrypted)
        {
            flags.Add("--insecure-no-password");
        }
        // Cloud destinations: we DELIBERATELY do not pin rclone retry
        // knobs here. Code review caught the previous "retries=1 +
        // low-level-retries=1" shape as a silent-failure footgun — a
        // single transient HTTP 503 from a B2/S3 throttle burst
        // (a documented condition that's expected to retry within
        // seconds) would fail the cell on first attempt and the
        // orchestrator's outer cascade would re-run restic from
        // scratch, repeating 99 packs because of one transient on
        // pack 100. Per direction: transient errors should retry
        // forever (with a wall-clock cap that's only reached under
        // genuine problems, never under normal load). Rclone's
        // defaults (3 outer retries + 10 low-level with exponential
        // backoff) handle that already. Two layers:
        //   • rclone (transient, sub-minute): blip → seconds → ok.
        //   • orchestrator cascade + grace period (persistent,
        //     hours/days): network down → Skipped → grace window →
        //     escalate to Failed.
        // Leaving rclone's defaults intact lets each layer do its job.
        return flags;
    }

    /// <summary>Order-sensitive equality so the drift check
    /// distinguishes "same flags, same order" from any change.
    /// Backrest accepts the flags in the order we sent them, so order
    /// matters for the round-trip read back.</summary>
    internal static bool RepoFlagsEqual(List<string>? a, List<string>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return (a?.Count ?? 0) == 0 && (b?.Count ?? 0) == 0;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    internal static BackrestPlan BuildPlanFromBackup(string planId, string repoId, Backup backup, string sourcePath)
    {
        var paths = new List<string> { sourcePath };
        // FilterTranslator turns Backspace's filter text (a superset of
        // Duplicacy + native syntax) into the two-list shape Backrest /
        // restic actually consumes. The earlier hand-rolled parser
        // silently dropped every Duplicacy-style `e:` line — which
        // meant RecommendedFilters and any imported Duplicacy filters
        // were no-ops, and backups ran without the exclusions the user
        // thought they'd applied.
        var translated = FilterTranslator.Translate(backup.FiltersText);
        var backupFlags = BuildBackupFlags(backup);
        return new BackrestPlan
        {
            Id = planId,
            Repo = repoId,
            Paths = paths,
            Excludes = new List<string>(translated.Excludes),
            IExcludes = new List<string>(translated.IExcludes),
            BackupFlags = backupFlags,
            // Backrest 1.13's Forget RPC refuses to run without a
            // retention policy on the plan ("forget called with nil
            // retention policy"). Materialise the policy from the
            // backup's KeepPolicy string so both the inline-after-each-
            // backup path and the standalone PruneSchedule path have
            // something restic-compatible to apply. Empty/unset →
            // "keep everything" (KeepAll) so Forget no-ops safely.
            Retention = ParseRetentionPolicy(backup.KeepPolicy),
            SkipIfUnchanged = false,
        };
    }

    /// <summary>
    /// Translate Backspace's legacy <see cref="Backup.KeepPolicy"/>
    /// string into the Backrest-shaped retention policy a plan
    /// carries. Supported inputs:
    /// <list type="bullet">
    /// <item>A bare integer "N" → keep last N snapshots (KeepLastN).</item>
    /// <item>The Duplicacy-style "0:365 30:90 7:30 1:7" syntax —
    ///       parsed as time-bucketed counts; conservatively
    ///       interpreted because Duplicacy's semantics don't 1:1
    ///       map onto restic's keep-X counts.</item>
    /// <item>Null / empty → keep everything (Backrest skips Forget
    ///       work but the RPC won't error).</item>
    /// </list>
    /// </summary>
    internal static BackrestRetentionPolicy ParseRetentionPolicy(string? keepPolicy)
    {
        var s = (keepPolicy ?? "").Trim();
        if (string.IsNullOrEmpty(s))
            return new BackrestRetentionPolicy { PolicyKeepAll = true };

        if (int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            return new BackrestRetentionPolicy
            {
                PolicyTimeBucketed = new BackrestTimeBucketedCounts { KeepLastN = n }
            };
        }

        // Duplicacy-style "M:N" pairs — translate into a coarse time-
        // bucketed counts. The exact mapping isn't a clean 1:1 (Duplicacy
        // counts every-N-days, restic bucket-N), so we keep this
        // generous: enough granularity for tests / smoke; users who
        // care about exact semantics can re-edit via the keep-policy
        // UI once Backspace exposes one.
        var bucket = new BackrestTimeBucketedCounts
        {
            Daily = 7,
            Weekly = 4,
            Monthly = 12,
            Yearly = 5,
            KeepLastN = 10,
        };
        return new BackrestRetentionPolicy { PolicyTimeBucketed = bucket };
    }

    /// <summary>Restic-side <c>backup</c> command flags derived from
    /// the user's per-backup toggles. Windows-only flags are guarded
    /// behind <see cref="Services.Platform.PlatformInfo.IsWindows"/>
    /// so a portable Backspace install running on macOS/Linux doesn't
    /// pass them through and trip restic's "unknown flag" error.
    /// </summary>
    internal static List<string> BuildBackupFlags(Backup backup)
    {
        var flags = new List<string>();
        if (Services.Platform.PlatformInfo.IsWindows)
        {
            if (backup.UseVss)
                flags.Add("--use-fs-snapshot");
            if (backup.ExcludeCloudFiles)
                flags.Add("--exclude-cloud-files");
        }
        // Bandwidth throttling. Restic exposes --limit-upload (KiB/s)
        // and --limit-download as global flags accepted either before
        // or after the subcommand. The model field is KB/s, restic
        // wants KiB/s — close enough (≈2.4% delta) to pass the user's
        // number through verbatim rather than confuse them with
        // base-2 conversion. Backup is upload-dominated, so wire the
        // upload limit here; the matching download limit goes onto
        // the restore command line in RestoreFilesAsync.
        if (backup.BandwidthLimitKBps > 0)
        {
            flags.Add("--limit-upload");
            // InvariantCulture so a tr-TR or fr-FR system doesn't emit a
            // grouping separator the moment the value crosses 1000 KBps —
            // restic's KiB/s parser rejects "1,024".
            flags.Add(backup.BandwidthLimitKBps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return flags;
    }

    /// <summary>Translates a Backspace <see cref="Destination"/> into a
    /// restic-style repo URI. Thin wrapper over
    /// <see cref="ResticUriResolver.Resolve"/> kept for backwards
    /// compatibility with the existing tests; new call sites should
    /// reach for the resolver directly so they also get the env-var
    /// list.</summary>
    internal static string ResticUriFor(Destination destination)
        => ResticUriResolver.Resolve(destination, secrets: new SecretsStore(), rcloneConfigPath: RcloneConfigBuilder.DefaultPath).Uri;

    // Exclude parsing previously lived inline here as a hand-rolled
    // single-format parser that only recognised the `-glob` shape.
    // RecommendedFilters and any Duplicacy-imported filter files use
    // `e:(?i)regex` patterns, so every one of those lines was silently
    // no-op'd before restic ever saw them. Translation now lives in
    // <see cref="FilterTranslator"/> — a single shared parser used
    // here and by the preview path in FilterSimulator so the two
    // can't diverge again.

    private static string Slug(string input)
    {
        var chars = new char[input.Length];
        var n = 0;
        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') chars[n++] = char.ToLowerInvariant(c);
            else if (c == ' ' || c == '/' || c == '\\' || c == '.') chars[n++] = '-';
            // skip anything else
        }
        var s = new string(chars, 0, n);
        // Collapse runs of '-' and trim
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Trim('-');
    }

    private static string ShortHash(string s)
    {
        // 8-char hex of SHA-256, lowercase. Deterministic, collision-
        // safe-enough for "one source path per plan" within a user's
        // own config. Plan IDs collide only if two different source
        // paths hash to the same 32 bits, which doesn't happen in any
        // realistic single-user config.
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s ?? ""));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}
