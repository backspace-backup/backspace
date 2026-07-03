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

        // Subscribe to the operation-event stream BEFORE kicking off
        // the Backup RPC so we don't race past the first events.
        // /v1.Backrest/Backup is fire-and-forget — the RPC returns
        // once the op is enqueued, NOT when it finishes. We watch
        // the event stream for the terminal event matching our plan
        // and wait on that before returning. Without this wait, the
        // cell returns "Success" as soon as the daemon enqueues the
        // op — even if the backup itself fails 30 minutes later.
        // That was a silent-data-loss class bug: backups that
        // actually failed showed green in the UI.
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var signals = new PumpSignals();
        var pumpTask = Task.Run(() => PumpOperationEventsAsync(
            client, planId, lineSink, signals, pumpCts.Token), pumpCts.Token);

        try
        {
            await client.CallVoidAsync("/v1.Backrest/Backup", new BackupRequest { Value = planId }, ct).ConfigureAwait(false);
        }
        catch (BackrestRpcException ex)
        {
            throw TranslateRpcError(ex, "Backup");
        }

        // Wait for the terminal event from the pump. If the caller
        // cancels (Stop button, app shutdown) we ask the daemon to
        // cancel the op too — without that the daemon kept running
        // the backup in the background even though Backspace
        // reported the cell Skipped.
        var lastStatus = "";
        try
        {
            lastStatus = await signals.Terminal.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort send Cancel for the op id we observed —
            // waits briefly for the op id to arrive if the cancel
            // raced the first event. Even if it fails we mark the
            // cell Skipped: the user's intent (Stop) is the source
            // of truth.
            await TryCancelObservedOpAsync(client, signals).ConfigureAwait(false);
            lastStatus = BackrestStatus.UserCancelled;
        }

        // Optional post-backup phases. Each runs inline only when the
        // user hasn't opted into an independent cadence — a non-null
        // schedule means a separate OS-scheduler task already runs
        // that operation, and tacking it onto every backup too would
        // double the cost. Only run when the backup itself succeeded;
        // following a failed backup with a prune would prune against
        // an inconsistent snapshot tree.
        var backupOk = lastStatus is BackrestStatus.Success or BackrestStatus.Warning;
        var runInlinePrune = backupOk && backup.PruneSchedule is null;
        var runInlineCheck = backupOk && backup.CheckSchedule is null;
        // Inline maintenance is best-effort. The user-facing run status
        // is determined by the BACKUP itself — Forget/Check/Unlock that
        // fail to complete in time (e.g. Backrest 1.13's global task
        // runner is busy with another repo's queue because two backups
        // ran concurrently) must NOT downgrade a successful backup to
        // Failed/Skipped. They get rescheduled on the next run anyway.
        // The deadline in RunRepoOpAndWaitAsync surfaces as
        // OperationCanceledException from the linked token; catch both
        // shapes so contention can't reach the cell-loop's OCE catch
        // (which would mark the cell Skipped on a successful backup).
        if (runInlinePrune)
        {
            try
            {
                await RunRepoOpAndWaitAsync(client,
                    "/v1.Backrest/Forget",
                    new ForgetRequest { RepoId = repoId, PlanId = planId },
                    repoId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.Warning("Inline prune (Forget) hit the per-RPC deadline for {Backup}/{Repo} — likely Backrest's task runner is busy with another repo. Skipping; will retry on the next run.",
                    backup.Name, repoId);
            }
            catch (BackrestRpcException ex)
            {
                _log.Warning(ex, "Inline prune (Forget) returned {Code} for {Backup}/{Repo}; skipping (best-effort).", ex.Code, backup.Name, repoId);
            }
        }
        if (runInlineCheck)
        {
            try
            {
                await RunRepoOpAndWaitAsync(client,
                    "/v1.Backrest/DoRepoTask",
                    new DoRepoTaskRequest { RepoId = repoId, Task = (int)BackrestRepoTask.TaskCheck },
                    repoId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.Warning("Inline check hit the per-RPC deadline for {Backup}/{Repo} — likely Backrest's task runner is busy. Skipping; will retry on the next run.",
                    backup.Name, repoId);
            }
            catch (BackrestRpcException ex)
            {
                _log.Warning(ex, "Inline check returned {Code} for {Backup}/{Repo}; skipping (best-effort).", ex.Code, backup.Name, repoId);
            }
        }

        // Belt-and-braces: even after our explicit Forget / Check
        // waits, Backrest can have an auto-scheduled IndexSnapshots
        // task still in flight that holds the exclusive restic lock.
        // The next caller's ListSnapshots / restore / rotate would
        // hit "repo already locked" with milliseconds of skew.
        // Run TaskUnlock here and wait for IT to terminate; that
        // drains Backrest's queue AND clears any stale lock files
        // restic left behind on a crash-recovered repo, so the
        // hand-off to the caller is reliably clean.
        if (runInlinePrune || runInlineCheck)
        {
            try
            {
                await RunRepoOpAndWaitAsync(client,
                    "/v1.Backrest/DoRepoTask",
                    new DoRepoTaskRequest { RepoId = repoId, Task = (int)BackrestRepoTask.TaskUnlock },
                    repoId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.Warning("Post-maintenance Unlock hit the per-RPC deadline for {Repo} — next caller may hit a held lock.", repoId);
            }
            catch (BackrestRpcException ex)
            {
                _log.Warning(ex, "Post-maintenance Unlock returned {Code} — next caller may hit a held lock", ex.Code);
            }
        }

        pumpCts.Cancel();
        try { await pumpTask; }
        catch (OperationCanceledException) { /* expected on pump shutdown */ }

        return new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            Kind = RunKind.Backup,
            StartedUtc = started,
            EndedUtc = DateTime.UtcNow,
            Status = StatusFromBackrestTerminal(lastStatus),
            Summary = string.IsNullOrEmpty(lastStatus)
                ? "Scheduled via Backrest"
                : $"Backrest reported {lastStatus}",
        };
    }

    /// <summary>Stand-alone prune (forget + repack). Used when the
    /// user opts into an independent prune cadence
    /// (<see cref="Backup.PruneSchedule"/> non-null). Backrest's
    /// <c>/Forget</c> RPC runs the configured retention's forget
    /// step + the repo's PrunePolicy repack in one call, so a
    /// single RPC per (source, dest, storage) cell is enough.</summary>
    public async Task<RunRecord> RunPruneOnlyAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct, Action<string>? lineSink = null)
    {
        return await RunMaintenanceRpcAsync(
            backup, sourcePath, destination, storageName, RunKind.Prune,
            doRpc: async (client, repoId, planId) =>
            {
                await client.CallVoidAsync("/v1.Backrest/Forget",
                    new ForgetRequest { RepoId = repoId, PlanId = planId }, ct).ConfigureAwait(false);
            },
            ct, lineSink).ConfigureAwait(false);
    }

    /// <summary>Stand-alone integrity check. Used when the user
    /// opts into an independent check cadence
    /// (<see cref="Backup.CheckSchedule"/> non-null). Maps to
    /// Backrest's <c>DoRepoTask(TaskCheck)</c>; the repo's
    /// CheckPolicy determines whether it's a structure-only scan
    /// or a read-data-subset verify.</summary>
    public async Task<RunRecord> RunCheckOnlyAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct, Action<string>? lineSink = null)
    {
        return await RunMaintenanceRpcAsync(
            backup, sourcePath, destination, storageName, RunKind.Check,
            doRpc: async (client, repoId, planId) =>
            {
                await client.CallVoidAsync("/v1.Backrest/DoRepoTask",
                    new DoRepoTaskRequest { RepoId = repoId, Task = (int)BackrestRepoTask.TaskCheck }, ct).ConfigureAwait(false);
            },
            ct, lineSink).ConfigureAwait(false);
    }

    /// <summary>Shared scaffolding for the standalone maintenance
    /// operations: plan + repo bootstrap, operation-event pump for
    /// live-tail wiring, terminal-status capture, RunRecord
    /// assembly. The only thing the caller varies is the RPC call
    /// itself.</summary>
    private async Task<RunRecord> RunMaintenanceRpcAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        RunKind kind, Func<BackrestClient, string, string, Task> doRpc,
        CancellationToken ct, Action<string>? lineSink)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        var planId = MakePlanId(backup, sourcePath, destination, storageName);
        await EnsurePlanAndRepoAsync(backup, sourcePath, destination, storageName, ct).ConfigureAwait(false);

        var started = DateTime.UtcNow;
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var signals = new PumpSignals();
        var pumpTask = Task.Run(() => PumpOperationEventsAsync(
            client, planId, lineSink, signals, pumpCts.Token), pumpCts.Token);

        try
        {
            await doRpc(client, repoId, planId).ConfigureAwait(false);
        }
        catch (BackrestRpcException ex) { throw TranslateRpcError(ex, $"Standalone {kind}"); }

        // Wait for the terminal event — same wait-for-truth pattern
        // BackupPruneCheckAsync uses. On caller-cancel, ask the
        // daemon to cancel the op too.
        var lastStatus = "";
        try { lastStatus = await signals.Terminal.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            await TryCancelObservedOpAsync(client, signals).ConfigureAwait(false);
            lastStatus = BackrestStatus.UserCancelled;
        }

        pumpCts.Cancel();
        try { await pumpTask; }
        catch (OperationCanceledException) { /* expected */ }

        return new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            Kind = kind,
            StartedUtc = started,
            EndedUtc = DateTime.UtcNow,
            Status = StatusFromBackrestTerminal(lastStatus),
            Summary = string.IsNullOrEmpty(lastStatus)
                ? $"{kind} via Backrest (no terminal event observed — marked failed to avoid silent success)"
                : $"Backrest {kind.ToString().ToLowerInvariant()} reported {lastStatus}",
        };
    }

    /// <summary>Pump operation events for the lifetime of a backup
    /// call. Filters the stream to events that belong to the plan we
    /// just kicked off (other plans share the same daemon and emit
    /// concurrent events; we don't want to spam our caller's live-tail
    /// with unrelated activity). Each surfaced event becomes a
    /// LineWritten line carrying the operation status + display
    /// message, so subscribers (LogsViewModel, anyone holding
    /// lineSink) see real progress in real time.</summary>
    /// <summary>
    /// Pump events for our plan until the first terminal status
    /// arrives — at which point the TCS is completed so the caller
    /// can return. Non-terminal events surface through LineWritten
    /// + lineSink so the live-tail keeps updating; keep-alives are
    /// filtered. If the stream dies before terminal, the TCS
    /// completes with the empty string and the caller's
    /// StatusFromBackrestTerminal returns Failed — the truthful
    /// answer is "we don't know it succeeded".
    /// </summary>
    /// <summary>
    /// Holds the two channels the pump publishes back to its caller:
    ///   - <see cref="Terminal"/>: completed when the pump observes
    ///     a terminal status for the watched plan (or the stream
    ///     dies).
    ///   - <see cref="OpId"/>: completed with the first observed
    ///     op id for the watched plan. Lets the caller send
    ///     /v1.Backrest/Cancel for the running op on user-cancel.
    /// </summary>
    private sealed class PumpSignals
    {
        public TaskCompletionSource<string> Terminal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> OpId { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task PumpOperationEventsAsync(
        BackrestClient client, string planId, Action<string>? lineSink,
        PumpSignals signals, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in client.StreamAsync<BackrestSidecar.EmptyRequest, BackrestOperationEvent>(
                "/v1.Backrest/GetOperationEvents", new BackrestSidecar.EmptyRequest(), ct))
            {
                _log.Debug("[pump] evt: keepAlive={KeepAlive} created={Created} updated={Updated} deleted={Deleted}",
                    evt.KeepAlive is not null,
                    evt.CreatedOperations?.Operations?.Count ?? 0,
                    evt.UpdatedOperations?.Operations?.Count ?? 0,
                    evt.DeletedOperations?.Values?.Count ?? 0);

                // Skip keep-alive frames — they exist for the proxy /
                // load-balancer layer, not for end-user log surfaces.
                if (evt.IsKeepAlive) continue;

                // The OperationEvent proto is a oneof — operations
                // arrive under createdOperations.operations[] (new
                // operations) and updatedOperations.operations[]
                // (state transitions of existing operations). We care
                // about both: a backup might appear as CREATED with
                // STATUS_PENDING then UPDATED to STATUS_INPROGRESS
                // and finally STATUS_SUCCESS, all on the same op id.
                foreach (var op in evt.GetAllOperations())
                {
                    _log.Debug("[pump] op: id={Id} planId={PlanId} status={Status} want={Want}",
                        op.Id, op.PlanId, op.Status, planId);
                    if (!string.Equals(op.PlanId, planId, StringComparison.Ordinal)) continue;

                    // First-seen op id wins — TrySetResult is a no-op
                    // after the first hit. This is what the caller
                    // sends to /v1.Backrest/Cancel on user-cancel.
                    signals.OpId.TrySetResult(op.Id);

                    var line = $"[backrest] op={op.Id} status={op.Status} {op.DisplayMessage}".TrimEnd();
                    RaiseLineWritten(line);
                    lineSink?.Invoke(line);
                    if (IsTerminalStatus(op.Status))
                    {
                        signals.Terminal.TrySetResult(op.Status);
                        return;
                    }
                }
            }
            // Stream closed before terminal — treat as "we didn't see
            // the daemon say success" and let the caller decide.
            signals.Terminal.TrySetResult("");
        }
        catch (OperationCanceledException)
        {
            signals.Terminal.TrySetResult("STATUS_USER_CANCELLED");
        }
        catch (Exception ex)
        {
            // A stream failure should not bring down the whole backup.
            // The orchestrator already has its own retry / status
            // detection; logging here lets us see WHY the live-tail
            // went quiet without burying the backup's outcome.
            _log.Warning(ex, "GetOperationEvents pump exited with an error; live-tail will be stale until the next backup");
            signals.Terminal.TrySetResult("");
        }
        finally
        {
            // Defence-in-depth: if neither path completed OpId
            // (stream closed before the first event), still complete
            // it so a caller awaiting it doesn't hang.
            signals.OpId.TrySetResult("");
        }
    }

    /// <summary>
    /// Send /v1.Backrest/Cancel for an op id. Best-effort: the daemon
    /// may have already finished the op, the RPC may be unreachable,
    /// the caller is already in a cancellation path. We log + swallow
    /// — the orchestrator will mark the cell Skipped regardless.
    /// </summary>
    private async Task TrySendCancelAsync(BackrestClient client, string opId)
    {
        if (string.IsNullOrEmpty(opId) || opId == "0") return;
        try
        {
            using var cancelCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.CallVoidAsync("/v1.Backrest/Cancel",
                new Int64Value { Value = opId }, cancelCts.Token).ConfigureAwait(false);
            _log.Information("Sent /v1.Backrest/Cancel for op {Op}", opId);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Cancel RPC for op {Op} threw (best-effort, ignoring)", opId);
        }
    }

    /// <summary>
    /// On user-cancel, send /v1.Backrest/Cancel for the op id the pump
    /// observed — or wait briefly for it to arrive if the cancel landed
    /// in the narrow window between Backup RPC return and the first
    /// operation event. Without this short wait, a Stop click that fires
    /// just after the daemon enqueues the op (but before it publishes
    /// the first event) leaves the daemon running the backup in the
    /// background while Backspace reports the cell Skipped. 500ms is
    /// enough headroom for the daemon to publish the op id in normal
    /// operation; longer would punish the user's Stop responsiveness.
    /// </summary>
    private async Task TryCancelObservedOpAsync(BackrestClient client, PumpSignals signals)
    {
        string opId;
        if (signals.OpId.Task.IsCompletedSuccessfully)
        {
            opId = signals.OpId.Task.Result;
        }
        else
        {
            try
            {
                using var brief = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                opId = await signals.OpId.Task.WaitAsync(brief.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.Warning("User-cancel landed before any op event arrived; daemon op may continue in background.");
                return;
            }
        }
        await TrySendCancelAsync(client, opId).ConfigureAwait(false);
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
            if (!string.IsNullOrEmpty(srcRel) && p.StartsWith(srcRel, StringComparison.OrdinalIgnoreCase))
                p = p.Substring(srcRel.Length).TrimStart('/');
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
        var (snapshotId, _) = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct).ConfigureAwait(false);

        Directory.CreateDirectory(targetPath);
        var repoId = MakeRepoId(destination, storageName);
        var srcPrefix = SourcePathToSnapshotPrefix(sourcePath);

        // RESTORE STRATEGY — use restic's documented
        // <c>snapshotID:subfolder</c> literal-path syntax, NOT the
        // glob-based <c>--include</c> patterns the legacy code was
        // generating. The subfolder syntax sidesteps two pre-existing
        // bugs in the restic / Backrest stack on Windows:
        //
        //   1. <c>restic --include</c> uses Go's <c>filepath.Match</c>,
        //      which on Windows disables backslash escapes (<c>\</c>
        //      is treated as a path separator) AND rejects POSIX
        //      class-self-escape <c>[[]…[]]</c> with
        //      "invalid pattern(s) provided". So a file literally
        //      named <c>"Q3 [draft].pdf"</c> CANNOT be matched by any
        //      <c>--include</c> pattern on Windows — the brackets
        //      always parse as a glob character class. Backrest's own
        //      <c>escapeGlob</c> in repo.go:457 hits this and returns
        //      the base verbatim on Windows ("escaping is not
        //      supported on Windows"), then silently produces zero
        //      matches.
        //
        //   2. <c>restic restore --include /full/path --target T</c>
        //      reconstructs the full absolute source path under T,
        //      preserving each intermediate directory's original NTFS
        //      ACL. For sources under <c>C:\Users\&lt;user&gt;</c>
        //      (the typical case), the literal <c>T\C\Users</c>
        //      mirror inherits the read-only ACL of real
        //      <c>C:\Users</c> and blocks creation of
        //      <c>T\C\Users\&lt;user&gt;</c> beneath it. Per-file
        //      include hits this trap immediately.
        //
        // The <c>snapshotID:subfolder</c> syntax avoids BOTH because:
        //   - It's a literal path, NOT a glob: brackets / asterisks /
        //     question marks in folder names round-trip exactly.
        //   - Restic strips the prefix and lays the subfolder content
        //     at the top of <c>--target</c>: no <c>\C\Users\</c>
        //     mirror, no ACL inheritance, no staging dir needed.
        //
        // Caller contract: <paramref name="files"/> is a list of
        // SOURCE-RELATIVE file paths (the shape every RevisionFile
        // exposes). We issue ONE restic call rooted at the source
        // prefix (<c>snapshotID:srcPrefix</c>), with one
        // <c>--include /&lt;rel&gt;</c> per requested file. The
        // include patterns are interpreted RELATIVE to the
        // <c>snapshotID:subfolder</c> per restic's documented
        // behavior, so a request for <c>letter_00/note.txt</c>
        // becomes <c>--include /letter_00/note.txt</c> and restic
        // drops it at <c>&lt;target&gt;/letter_00/note.txt</c> —
        // exactly the layout the caller expects.
        //
        // Bracket-fallback: if ANY requested file's path contains
        // <c>[</c> or <c>]</c> (the Windows-restic upstream gap —
        // see AGENT.md and docs/backrest-upstream-bracket-bug.md),
        // we omit <c>--include</c>. Restic then restores the whole
        // source subfolder; the engine's post-restore filter sweeps
        // any files the caller didn't ask for. This is the only
        // non-vanilla path; in the typical case (no brackets in any
        // selected path) the restore is exact and one-shot.
        // expectedLeaves drives the bracket-fallback sweep: when the
        // include pattern uses `?` substitution to dodge the Windows
        // bracket bug, restic over-fetches same-length siblings, and
        // we delete anything not in this set after the restore. When
        // expectedLeaves is null (no caller-provided leaf list), we
        // skip the sweep — the caller accepts whatever restic emits.
        var expectedLeavesSet = expectedLeaves is null
            ? null
            : new HashSet<string>(
                expectedLeaves.Select(f => f.Replace('\\', '/').TrimStart('/')),
                StringComparer.Ordinal);
        var includeRelSet = new HashSet<string>(
            includePatterns.Select(p => p.Replace('\\', '/').TrimStart('/')),
            StringComparer.Ordinal);
        var anyBracketRel = includeRelSet.Any(HasGlobMetachars);

        var snapshotSubfolder = string.IsNullOrEmpty(srcPrefix) ? "/" : "/" + srcPrefix;

        // Build the include-pattern list as plain strings — written to a
        // temp file and passed as a single `--include-file <path>` flag
        // rather than N per-file `--include` args. Windows' CreateProcess
        // caps a process' combined command line at ~32 KB; a real-world
        // restore of a few thousand files (each pattern ~40 bytes once
        // anchored + escaped) easily crosses that and fails to launch
        // with "The filename or extension is too long" — see
        // tests/Backspace.Tests/Scenarios/E2E/E34_RestoreLongIncludeList.cs
        // for the regression test. Using --include-file is also slightly
        // tidier for the engine log (one quoted path instead of a wall
        // of --include flags) and is restic-native (no Backrest detour).
        // Bracket-fallback (see docs/GOTCHAS.md and
        // docs/backrest-upstream-bracket-bug.md): substitute `[` and
        // `]` with `?` when any pattern contains brackets. NTFS forbids
        // `*` and `?` in real filenames so the wildcard can't collide
        // with a sibling whose name matches exactly; same-length
        // siblings may over-match, post-restore sweep removes them.
        IReadOnlyList<string> resticIncludeFileLines = anyBracketRel
            ? includeRelSet.Select(rel => "/" + SubstituteBracketsWithQuestionMark(rel)).ToList()
            : includeRelSet.Select(rel => "/" + rel).ToList();

        var includeFile = Path.Combine(Path.GetTempPath(),
            $"backspace-restore-include-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(includeFile, resticIncludeFileLines, ct).ConfigureAwait(false);

        var sb = new System.Text.StringBuilder();
        sb.Append("restore ");
        sb.Append(QuoteArg(snapshotId + ":" + snapshotSubfolder));
        sb.Append(" --target ").Append(QuoteArg(targetPath));
        // --json: restic emits one JSON object per progress + final
        // {message_type:"summary"} line carrying files_restored,
        // files_skipped, errors counts. The summary is what we parse
        // post-termination to distinguish a real failure (errors>0)
        // from a metadata-only failure (exit-1 with errors=0 — restic
        // wrote every byte but couldn't preserve some mtime/ACL).
        // Without --json we'd be stuck with the conservative
        // "always Warning on exit-1" heuristic.
        sb.Append(" --json");
        if (backup.BandwidthLimitKBps > 0)
            sb.Append(" --limit-download ")
              .Append(backup.BandwidthLimitKBps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(" --include-file ").Append(QuoteArg(includeFile));

        var cmd = sb.ToString();
        RestoreTerminalStatus terminalStatus;
        string? errorMessage = null;
        long opId = 0;
        try
        {
            var op = await client.CallAsync<RunCommandRequest, Int64Value>(
                "/v1.Backrest/RunCommand",
                new RunCommandRequest { RepoId = repoId, Command = cmd },
                ct).ConfigureAwait(false);
            if (op is null)
            {
                terminalStatus = RestoreTerminalStatus.Error;
                errorMessage = "Backrest RunCommand returned no operation id";
            }
            else
            {
                opId = long.Parse(op.Value, System.Globalization.CultureInfo.InvariantCulture);
                await WaitForOperationTerminalAsync(client, op.Value, ct).ConfigureAwait(false);
                terminalStatus = RestoreTerminalStatus.Success;
            }
        }
        catch (OperationCanceledException)
        {
            terminalStatus = RestoreTerminalStatus.Cancelled;
            errorMessage = "Restore cancelled by user";
        }
        catch (BackrestRpcException ex)
        {
            // Restic exits 1 on metadata-only failures (timestamp set
            // / ACL apply on intermediate dirs) AND on real failures.
            // We start with the conservative Warning classification +
            // restic's message verbatim; the JSON-summary drain below
            // tightens this when restic's --json summary actually
            // reports zero errors (→ Success with metadata-only
            // warning), or non-zero errors with zero files restored
            // (→ Error). Without --json we'd be stuck always-Warning.
            if (ex.Message.Contains("exit status 1", StringComparison.OrdinalIgnoreCase))
            {
                terminalStatus = RestoreTerminalStatus.Warning;
                errorMessage = ex.Message;
            }
            else
            {
                throw TranslateRpcError(ex, "Restore");
            }
        }
        finally
        {
            try { File.Delete(includeFile); } catch { /* best-effort */ }
        }

        // Drain restic's --json summary line from Backrest's captured
        // stdout via GetLogs. Restic emits ONE
        // {"message_type":"summary"} line at the very end with the
        // structured counts:
        //   {files_restored, files_skipped, files_unchanged,
        //    bytes_restored, total_files, total_bytes, errors, ...}
        // Best-effort: if the drain fails or the summary isn't
        // present, we fall back to the pre-drain classification
        // (Warning with restic's stderr) and the pattern-count
        // approximation below.
        long jsonFilesRestored = -1;
        long jsonBytesRestored = -1;
        long jsonTotalFiles = -1;
        long jsonTotalBytes = -1;
        long jsonErrors = -1;
        if (opId > 0)
        {
            try
            {
                var opResp = await client.CallAsync<GetOperationsRequest, BackrestOperationList>(
                    "/v1.Backrest/GetOperations",
                    new GetOperationsRequest { Selector = new OpSelector { Ids = new[] { opId.ToString() } } },
                    ct).ConfigureAwait(false);
                var logRef = opResp.Operations?.FirstOrDefault()?.OperationRunCommand?.OutputLogref;
                if (!string.IsNullOrEmpty(logRef))
                {
                    var summary = new System.Text.StringBuilder();
                    await foreach (var chunk in client.StreamAsync<GetLogsRequest, StringValue>(
                        "/v1.Backrest/GetLogs",
                        new GetLogsRequest { Ref = logRef! }, ct).ConfigureAwait(false))
                    {
                        if (!string.IsNullOrEmpty(chunk.Value)) summary.Append(chunk.Value);
                    }
                    ParseResticRestoreSummary(summary.ToString(),
                        out jsonFilesRestored, out jsonBytesRestored,
                        out jsonTotalFiles, out jsonTotalBytes,
                        out jsonErrors);
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Restore log drain via GetLogs failed; falling back to coarse classification");
            }
        }

        // Reclassify based on the JSON summary if we got one.
        //   errors == 0  → Success regardless of exit code (restic
        //                  may exit 1 on metadata-only warnings).
        //   errors > 0   AND files_restored == 0 → Error.
        //   errors > 0   AND files_restored > 0  → Warning (partial).
        if (jsonErrors == 0)
        {
            terminalStatus = RestoreTerminalStatus.Success;
            errorMessage = null;
        }
        else if (jsonErrors > 0 && jsonFilesRestored == 0)
        {
            terminalStatus = RestoreTerminalStatus.Error;
            errorMessage ??= $"restic reported {jsonErrors} error(s), 0 files restored";
        }
        else if (jsonErrors > 0)
        {
            terminalStatus = RestoreTerminalStatus.Warning;
            errorMessage ??= $"restic reported {jsonErrors} error(s); {jsonFilesRestored} file(s) restored";
        }

        // Bracket-fallback cleanup: when we substituted `[` / `]` with
        // `?` for the Windows escape gap, restic may have over-fetched
        // same-length siblings. If the caller supplied expectedLeaves
        // (the exact leaf set the user requested), delete anything
        // outside that set so the final layout matches. Without
        // expectedLeaves, the over-fetch stays — caller's choice.
        if (anyBracketRel && expectedLeavesSet is not null)
        {
            try { SweepUnrequestedFiles(targetPath, targetPath, expectedLeavesSet); }
            catch (Exception ex)
            {
                _log.Warning(ex, "Sweep of unrequested files at {Target} failed", targetPath);
            }
        }

        // Numbers in the RestoreOpResult: prefer the real ones from
        // restic's --json summary; fall back to the caller-pattern
        // approximation if the drain didn't produce a summary.
        long totalFiles = jsonTotalFiles >= 0 ? jsonTotalFiles
            : (expectedLeavesSet?.Count ?? includePatterns.Count);
        long filesRestored = jsonFilesRestored >= 0 ? jsonFilesRestored
            : (terminalStatus == RestoreTerminalStatus.Success ? totalFiles : 0);
        long totalBytes = jsonTotalBytes >= 0 ? jsonTotalBytes : 0;
        long bytesRestored = jsonBytesRestored >= 0 ? jsonBytesRestored : 0;

        var result = new RestoreOpResult(
            TotalFiles: (int)totalFiles,
            FilesRestored: (int)filesRestored,
            TotalBytes: totalBytes,
            BytesRestored: bytesRestored,
            Status: terminalStatus,
            ErrorMessage: errorMessage);
        onProgress?.Invoke(new RestoreProgressUpdate(
            (int)totalFiles, (int)filesRestored, totalBytes, bytesRestored,
            totalFiles == 0 ? 0 : (double)filesRestored / totalFiles));
        return result;
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
    /// Recursive post-restore filter for the bracket-fallback path.
    /// Removes every file under <paramref name="root"/> whose
    /// <c><paramref name="root"/>-relative path</c> is not in
    /// <paramref name="keep"/>. After the file sweep, prunes any
    /// directory that's now empty. <paramref name="keep"/> entries
    /// use forward slashes (snapshot path style).
    /// </summary>
    private static void SweepUnrequestedFiles(string root, string current, HashSet<string> keep)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(current))
        {
            if (Directory.Exists(entry))
            {
                SweepUnrequestedFiles(root, entry, keep);
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(entry).Any())
                        Directory.Delete(entry);
                }
                catch { /* best-effort */ }
            }
            else if (File.Exists(entry))
            {
                var rel = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (keep.Contains(rel)) continue;
                try { File.Delete(entry); } catch { /* best-effort */ }
            }
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
    /// (<see cref="SweepUnrequestedFiles"/>) removes them. We use
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
                "Most often this means the backup was never run against this destination — " +
                "run the backup first, then restore.");
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
    private async Task WaitForOperationTerminalAsync(
        BackrestClient client, string opIdString, CancellationToken ct,
        Action<string>? lineSink = null)
    {
        if (!long.TryParse(opIdString, out _))
            throw new InvalidOperationException($"RunCommand returned an unparseable op id: '{opIdString}'");

        // POLL-based status check via <c>GetOperations</c>. The older
        // implementation subscribed to <c>GetOperationEvents</c> and
        // waited for an event matching this op id to reach a terminal
        // status — that approach races with FAST ops (the snap:
        // subfolder restore + per-file --include shape introduced in
        // the bracket-fix refactor often completes in &lt; 200 ms),
        // because the operation's terminal event can be emitted
        // BEFORE we subscribe to the stream. GetOperations is a
        // simple unary RPC that returns the current state for any op
        // id, so this loop sees the terminal status whether it landed
        // before or after our subscribe.
        //
        // The 100 ms poll cadence matches what
        // <see cref="WaitForRepoIdleAsync"/> already uses. We cap
        // total wait at 5 minutes (300 polls × 100 ms + per-call
        // overhead): a restore of more than 5 minutes is either a
        // genuinely large user-driven restore (where the orchestrator
        // already has its own progress + cancel UI) or a stuck
        // backend (where we'd rather surface a timeout than hang
        // forever).
        var deadline = DateTime.UtcNow.AddMinutes(5);
        string lastStatus = "";
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await client.CallAsync<GetOperationsRequest, BackrestOperationList>(
                    "/v1.Backrest/GetOperations",
                    new GetOperationsRequest { Selector = new OpSelector { Ids = new[] { opIdString } } },
                    ct).ConfigureAwait(false);
                var match = resp.Operations?.FirstOrDefault(o => o.Id == opIdString);
                if (match is not null)
                {
                    if (match.Status != lastStatus)
                    {
                        lastStatus = match.Status;
                        var line = $"[backrest] op={match.Id} status={match.Status} {match.DisplayMessage}".TrimEnd();
                        RaiseLineWritten(line);
                        lineSink?.Invoke(line);
                    }
                    if (IsTerminalStatus(match.Status)) return;
                }
            }
            catch (BackrestRpcException)
            {
                // Transient — keep polling.
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException(
            $"Backrest op {opIdString} didn't reach a terminal status within 5 minutes.");
    }

    /// <summary>
    /// Fire one of Backrest's repo-scoped task RPCs (Forget / Check
    /// / Prune / Unlock) and wait until the repo is FULLY idle —
    /// no PENDING or INPROGRESS ops on it. Without this, restic's
    /// exclusive repo lock from any one of Backrest's
    /// auto-scheduled tasks (index_snapshots after every backup,
    /// the forget we just enqueued, the check we just enqueued)
    /// can still be held when the next caller hits the wire,
    /// surfacing as "repository is already locked". Implemented
    /// as a poll because the event-stream-latch approach
    /// (subscribe → match first new op id) is racy with
    /// concurrent auto-scheduled tasks on the same repo.
    /// </summary>
    private async Task RunRepoOpAndWaitAsync<TReq>(
        BackrestClient client, string endpoint, TReq request, string repoId, CancellationToken ct)
        where TReq : class
    {
        // Per-RPC deadline. HttpClient.Timeout on the sidecar's
        // client is InfiniteTimeSpan (long-lived streaming requires
        // it), so without a CancelAfter here a Backrest task queue
        // contention case (E03's two-concurrent-Forget) would block
        // this call forever. 2 minutes covers normal load plus
        // some headroom for queue contention — a 50 MB E03 cell's
        // inline maintenance is sub-second per op, and even with
        // 6 ops queued on Backrest's global runner the total is
        // ~30s. Beyond that, the maintenance is "stuck" and the
        // caller's catch logs a warning and continues; the next
        // run picks it back up. Cancellation composes with ct.
        using var deadlineCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineCts.Token);
        await client.CallVoidAsync(endpoint, request, linked.Token).ConfigureAwait(false);
        await WaitForRepoIdleAsync(client, repoId, linked.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Poll GetOperations until no in-flight (PENDING / INPROGRESS)
    /// op remains on the given repo. Used as the synchronisation
    /// point after every inline maintenance call so the next
    /// caller doesn't race the lock. Hard cap of 30 seconds keeps
    /// a stuck-forever task from wedging the whole cell.
    /// </summary>
    private async Task WaitForRepoIdleAsync(BackrestClient client, string repoId, CancellationToken ct)
    {
        // Fast path: EnsurePlanAndRepoCoreAsync seeded the guid into
        // _repoGuidCache on its way through (either via AddRepo's
        // response or the read-modify-write loop's GetConfig). The
        // guid is stable for the repo's lifetime, so we don't have
        // to pay another GetConfig RPC here. After inline
        // prune+check+unlock that's 3 GetConfig calls per cell
        // saved (more on exit-11 retries) — each fetching the
        // entire BackrestConfig just to read one field.
        string repoGuid;
        if (!_repoGuidCache.TryGetValue(repoId, out var cachedGuid) || string.IsNullOrEmpty(cachedGuid))
        {
            var cfg = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
                "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
            var repo = cfg.Repos.FirstOrDefault(r => r.Id == repoId);
            repoGuid = repo?.Guid ?? "";
            if (!string.IsNullOrEmpty(repoGuid))
                _repoGuidCache[repoId] = repoGuid;
        }
        else
        {
            repoGuid = cachedGuid;
        }
        if (string.IsNullOrEmpty(repoGuid))
            return; // can't filter without the guid; assume idle

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await client.CallAsync<GetOperationsRequest, BackrestOperationList>(
                    "/v1.Backrest/GetOperations",
                    new GetOperationsRequest
                    {
                        Selector = new OpSelector { RepoGuid = repoGuid },
                        LastN = 16,
                    },
                    ct).ConfigureAwait(false);
                var anyInFlight = resp.Operations?.Any(op => op.Status is "STATUS_PENDING" or "STATUS_INPROGRESS")
                                  ?? false;
                if (!anyInFlight) return;
            }
            catch (BackrestRpcException ex)
            {
                _log.Debug(ex, "WaitForRepoIdle: GetOperations probe failed; assuming idle");
                return;
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        _log.Warning("WaitForRepoIdleAsync: repo {RepoId} still had in-flight ops after 30s; returning anyway", repoId);
    }

    private static bool IsTerminalStatus(string status) => status is
        BackrestStatus.Success or BackrestStatus.Warning or BackrestStatus.Error or BackrestStatus.UserCancelled;

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
        await client.CallVoidAsync("/v1.Backrest/Forget",
            new ForgetRequest { RepoId = repoId, SnapshotId = snapshotId }, ct).ConfigureAwait(false);
    }

    public async Task PruneExhaustiveAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        await client.CallVoidAsync("/v1.Backrest/DoRepoTask",
            new DoRepoTaskRequest { RepoId = repoId, Task = (int)BackrestRepoTask.TaskPrune }, ct).ConfigureAwait(false);
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
        Int64Value op;
        try
        {
            op = await client.CallAsync<RunCommandRequest, Int64Value>(
                "/v1.Backrest/RunCommand",
                new RunCommandRequest
                {
                    RepoId = MakeRepoId(destination, storageName),
                    Command = "forget --keep-last 1 --prune",
                }, ct).ConfigureAwait(false);
        }
        catch (BackrestRpcException ex) { throw TranslateRpcError(ex, "Prune-to-latest"); }

        // Block on the operation's terminal event so the caller's
        // progress UI doesn't see a fire-and-forget "scheduled"
        // result and immediately mark it done. The wait helper
        // also emits LineWritten lines for each status transition,
        // which the in-app log pane tails live.
        await WaitForOperationTerminalAsync(client, op.Value, ct).ConfigureAwait(false);

        return new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            Kind = RunKind.Backup,
            StartedUtc = started,
            EndedUtc = DateTime.UtcNow,
            Status = BackupRunStatus.Success,
            Summary = $"prune-to-latest finished (op {op.Value})",
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
        if (!File.Exists(AppPaths.ResticBinary))
            return (1, $"restic binary not found at {AppPaths.ResticBinary}. Run a non-skip build " +
                       "(drop -p:SkipBackrestDownload) or place restic(.exe) next to the Backspace exe.");

        var resticEnv = ResticUriResolver.Resolve(destination, _secrets, RcloneConfigBuilder.DefaultPath);

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

            // restic key passwd needs an exclusive repo lock. The
            // sidecar's auto-scheduled index_snapshots / check / prune
            // tasks fire right after every backup and routinely hold
            // the lock for ~100ms–few-seconds. A naïve key-passwd call
            // races them and surfaces "repo already locked" (exit 11).
            // Try once; on lock failure, force-unlock and retry. The
            // forced unlock is safe here because we hold the
            // maintenance gate at the orchestrator layer — no other
            // legitimate writer is touching this repo while we run.
            var args = new[] { "key", "passwd", "--new-password-file", newPwdFile };
            var env = BuildResticPasswordChangeEnv(resticEnv, oldPassword);
            var (exit, output) = await SpawnResticAsync(args, env, ct).ConfigureAwait(false);
            if (exit == 11 && (output.Contains("already locked", StringComparison.OrdinalIgnoreCase)
                               || output.Contains("create lock", StringComparison.OrdinalIgnoreCase)))
            {
                _log.Information("ChangeStoragePasswordAsync: repo locked, force-unlocking and retrying once");
                var (unlockExit, unlockOutput) = await SpawnResticAsync(
                    new[] { "unlock", "--remove-all" }, env, ct).ConfigureAwait(false);
                if (unlockExit != 0)
                    return (exit, output + "\n[backspace] unlock returned " + unlockExit + ": " + unlockOutput);
                (exit, output) = await SpawnResticAsync(args, env, ct).ConfigureAwait(false);
            }
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
                    $"Repo {repoId} disappeared between AddRepo and SetConfig — config raced or daemon restarted. Retry the run.");
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
            var wantedResticEnv = ResticUriResolver.Resolve(destination, _secrets, RcloneConfigBuilder.DefaultPath);
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

            if (existingPlan is not null && flagsMatch && passwordMatch && uriMatch)
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
            if (!uriMatch)
            {
                _log.Information(
                    "EnsurePlanAndRepo: rewriting repo {RepoId} Uri (path/subpath rename detected) for backup {Backup} (was {Was}, now {Now})",
                    repoId, backup.Name, liveRepo.Uri, wantedResticEnv.Uri);
                liveRepo.Uri = wantedResticEnv.Uri;
                liveRepo.Env = wantedResticEnv.Env.ToList();
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
                        "This usually means the daemon's config schema differs from what Backspace expects — " +
                        "wipe Backspace.config\\backrest and let the daemon rebuild from scratch.");
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
            "Backrest config write kept hitting a modno mismatch after 5 retries — another process is mutating config concurrently.");
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
                "ids don't line up — stop Backspace, delete Backspace.config\\backrest, restart, and try again. " +
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
        if (RcloneConfigBuilder.NeedsRcloneRemote(destination))
        {
            var oauthApps = ServiceLocator.Config?.Current?.RcloneOAuthApps;
            new RcloneConfigBuilder(_secrets).Materialise(new[] { destination }, oauthApps);
        }
        var resticEnv = ResticUriResolver.Resolve(destination, _secrets, RcloneConfigBuilder.DefaultPath);

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
            Env = resticEnv.Env.ToList(),
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
