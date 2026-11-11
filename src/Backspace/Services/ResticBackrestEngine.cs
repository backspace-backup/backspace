using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// <see cref="IBackupEngine"/> implementation routing through a running
/// <see cref="BackrestSidecar"/>. Every <see cref="IBackupEngine"/>
/// member is wired: backup / forget / check route through Backrest's
/// plan- and repo-task RPCs. Single-path restores use the native Restore
/// RPC when it can express the request. Other restores use an owned
/// bundled restic process with bounded progress output and Backrest's
/// repository configuration. Password rotation and local snapshot-tree
/// fast paths also invoke the bundled restic executable directly.
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
public sealed partial class ResticBackrestEngine : IBackupEngine
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
        _justAnnounced = line;
        try { LineWritten?.Invoke(line); } catch { /* live-tail subscriber failure shouldn't escape */ }
    }

    /// <summary>Shows a line live and writes it to the run's log.</summary>
    private void Announce(string line, Action<string>? lineSink)
    {
        RaiseLineWritten(line); lineSink?.Invoke(line);
    }

    [ThreadStatic] private static string? _justAnnounced;

    /// <summary>True when this thread's last announced line is
    /// <paramref name="line"/>: the engine announces a line, then hands the
    /// same line to the run's log, whose writer announces what it receives
    /// for lines that come from elsewhere. Without this check the live view
    /// showed every progress line twice (4 November 2026).</summary>
    internal static bool AlreadyAnnounced(string line)
    {
        var announced = ReferenceEquals(_justAnnounced, line);
        _justAnnounced = null;
        return announced;
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

    // Positive membership only: a snapshot ID identifies immutable bytes.
    // Reusing that identity avoids re-listing the whole cloud repository for
    // each directory or restore batch. We still read the live daemon config,
    // and the actual read must succeed: this is never a cache of availability
    // or recovered contents. A deleted snapshot cannot become another version.
    private readonly record struct SnapshotIdentity(string RepoId, string PlanId, string RepoGuid,
        string RepoUri, long ConfigRevision, string SnapshotId);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SnapshotIdentity, byte> _snapshotIdentities = new();
    private readonly Dictionary<SnapshotIdentity, IReadOnlyList<RevisionTreeNode>> _snapshotTrees = new();
    private readonly object _snapshotTreeGate = new();
    private int _cachedTreeNodeCount;

    /// <summary>A check of every piece of stored data (<c>check --read-data</c>).</summary>
    internal static bool ReadsAllData(Backup backup) => backup.CheckReadDataSample && backup.DataCheckLimitBytes == long.MaxValue;

    internal static string RepositoryCheckDescription(Backup backup) =>
        ReadsAllData(backup)
            ? "Check scope: repository structure and every piece of saved data, downloaded and checked against its fingerprint."
        : backup.CheckReadDataSample
            ? $"Check scope: repository structure and a sample of saved data. The sample targets 1%, up to {BackupCheck.Size(backup.DataCheckLimitBytes ?? BackupCheck.OneGiB)}; whole packs and metadata can change actual traffic. This is not a full-data check."
            : "Check scope: repository structure. Saved file contents were not downloaded for a data-integrity check.";

    private static BackupCheckResult CheckResult(Backup backup, Destination destination, string storage,
        BackupRunStatus status, string detail) => new()
    {
        Depth = ReadsAllData(backup) ? CheckDepth.AllData
            : backup.CheckReadDataSample ? CheckDepth.SavedDataSample : CheckDepth.RepositoryStructure,
        Status = status, EndedUtc = DateTime.UtcNow, TargetKey = destination.Id + "/" + storage,
        DestinationName = destination.Name, Summary = RepositoryCheckDescription(backup) + " " + detail,
    };

    /// <summary>The bytes a data sample reads: 1% of the stored data, at least
    /// one byte, at most <paramref name="limit"/> (1 GiB when null).</summary>
    internal static long DataCheckSampleBytes(long storedBytes, long? limit = null)
    {
        if (storedBytes < 0) throw new InvalidDataException("The engine reported an invalid stored-data size.");
        if (limit is < 1) throw new ArgumentOutOfRangeException(nameof(limit), "A data sample's limit is at least one byte.");
        return Math.Min(limit ?? BackupCheck.OneGiB, Math.Max(1, storedBytes / 100));
    }

    internal static long ParseStoredDataSize(string output)
    {
        long? found = null;
        // Backrest adds a command heading around restic's one-line JSON.
        // Require exactly one valid size result rather than interpreting a
        // diagnostic line, missing result or duplicate as an unlimited check.
        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith('{')) continue;
            using var json = System.Text.Json.JsonDocument.Parse(text);
            if (!json.RootElement.TryGetProperty("total_size", out var size)) continue;
            if (found is not null || !size.TryGetInt64(out var value) || value < 0)
                throw new InvalidDataException("The engine's stored-data size was invalid or ambiguous. No data sample was started.");
            found = value;
        }
        return found ?? throw new InvalidDataException("The engine did not return a stored-data size. No data sample was started.");
    }

    /// <summary>How often a repository gets a full prune at the latest, for
    /// leftovers no record shows (a backup cut short where nothing recorded it).</summary>
    internal static readonly TimeSpan PruneAtLeastEvery = TimeSpan.FromDays(7);

    /// <summary>The forget command that applies the backup's retention
    /// policy (<see cref="KeepPolicyRules.ResticOptions"/>), or null when it
    /// keeps everything. This is the only path that deletes snapshots: every
    /// plan carries Backrest's keep-all retention, so Backrest's own
    /// after-backup forget never removes anything (U172).</summary>
    internal static string? BuildRetentionCommand(string planId, string instance, IReadOnlyList<string> keepOptions)
        => RetentionArguments(planId, instance, keepOptions) is null ? null
            : "forget --tag " + QuoteArg("plan:" + planId + ",created-by:" + instance) + " --group-by= " + string.Join(' ', keepOptions);

    /// <summary>The same forget as <see cref="BuildRetentionCommand"/>, as
    /// restic's arguments (run directly, U175), or null when the policy keeps
    /// everything.</summary>
    internal static List<string>? RetentionArguments(string planId, string instance, IReadOnlyList<string> keepOptions)
    {
        // An empty policy must never delete history: restic refuses a forget
        // without keep options, and Backspace never sends one.
        if (keepOptions.Count == 0) return null;
        // Match Backrest's own snapshot-list scope: AND the plan and instance
        // tags. Never change the shared plan temporarily while a browser may
        // also be reading it.
        if (string.IsNullOrWhiteSpace(planId) || string.IsNullOrWhiteSpace(instance) ||
            planId.Contains(',') || instance.Contains(',') || planId.Any(char.IsControl) || instance.Any(char.IsControl))
            throw new InvalidOperationException("The backup engine could not identify this backup's cleanup scope. Nothing was removed.");
        if (keepOptions.Any(o => !o.StartsWith("--keep-", StringComparison.Ordinal) || o.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"')))
            throw new InvalidOperationException("The retention policy produced an unexpected engine option. Nothing was removed.");
        var arguments = new List<string> { "forget", "--tag", "plan:" + planId + ",created-by:" + instance, "--group-by=" };
        arguments.AddRange(keepOptions);
        return arguments;
    }

    /// <summary>The size a backup of <paramref name="sourcePath"/> is
    /// expected to read: the last backup's, or the estimate counted with its
    /// rules, or 0 when neither is known (U124).</summary>
    internal static long ExpectedBytes(Backup backup, string sourcePath)
    {
        if (backup.SourceLastSizeBytes.TryGetValue(sourcePath, out var last) && last > 0) return last;
        return ServiceLocator.SizeProbe?.TryGetCached(sourcePath, SourceSizeRules.For(backup))
            is { Completed: true, Approximate: false, Bytes: > 0 } estimate ? estimate.Bytes : 0;
    }

    internal BackrestSidecar Sidecar => _sidecar;

    private long _lastUseTicks = DateTime.UtcNow.Ticks;

    /// <summary>When the engine was last asked for anything (U125).</summary>
    internal DateTime LastUseUtc => new(Interlocked.Read(ref _lastUseTicks), DateTimeKind.Utc);

    private void MarkUsed() => Interlocked.Exchange(ref _lastUseTicks, DateTime.UtcNow.Ticks);

    /// <summary>The size expected, known at once or counted in the background
    /// while the backup starts (U129, 4 November 2026: backups run by the
    /// background service had neither a measured size nor the app's estimate,
    /// so the card stayed at 0% with 84 GB done while restic was still
    /// counting). Counting C:\ with its rules takes about 4 s; restic's own
    /// count, on the shadow copy and while it backs up, takes many minutes.</summary>
    internal static Func<long> ExpectedSize(Backup backup, string sourcePath, CancellationToken ct)
    {
        var known = ExpectedBytes(backup, sourcePath);
        if (known > 0 || !Directory.Exists(sourcePath)) return () => known;
        // A count made in the last minutes (for the app) is reused rather
        // than counting the same folder again; otherwise it is counted now
        // (seconds) and kept for the app. Never an old figure (U129).
        var counted = Task.Run(() => new SizeEstimateCache(AppPaths.ConfigRoot)
            .CountOrReuse(sourcePath, SourceSizeRules.For(backup), SizeEstimateCache.FreshFor, TimeSpan.FromMinutes(3), ct)?.Bytes ?? 0, ct);
        return () => counted.IsCompletedSuccessfully ? counted.Result : 0;
    }

    /// <summary>What a backup starts from, like the opening lines of a
    /// Duplicacy log (U115): the previous backup, the exclusion rules and
    /// whether a shadow copy is used.</summary>
    internal static IEnumerable<string> SetupLines(Backup backup, string sourcePath, Destination destination, (string Id, DateTime When)? previous)
    {
        yield return "[setup] " + (previous is { } p
            ? $"Previous backup to «{destination.Name}»: {p.When.ToLocalTime():yyyy-MM-dd HH:mm}, snapshot {p.Id[..8]}. Only changes are sent."
            : $"No earlier backup of this folder in «{destination.Name}»: everything is read and sent.");
        var rules = FilterTranslator.ReadRules(backup.FiltersText).Count;
        static string N(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        yield return $"[setup] {N(rules)} exclusion rule{(rules == 1 ? "" : "s")} loaded"
            + (Services.Platform.PlatformInfo.IsWindows && backup.ExcludeCloudFiles ? "; files kept only in the cloud are skipped." : ".");
        yield return Services.Platform.PlatformInfo.IsWindows && backup.UseVss
            ? "[setup] Shadow copy on: open and locked files are read from a snapshot of the drive."
            : "[setup] Shadow copy off: files another program keeps locked may be skipped.";
    }

    /// <summary>restic run directly against a configured repository, with the
    /// same address, options, password and environment Backrest uses.</summary>
    internal static ProcessStartInfo BuildResticStartInfo(string binary, BackrestRepo repo, params string[] arguments)
    {
        var start = new ProcessStartInfo(binary);
        start.ArgumentList.Add("-r");
        start.ArgumentList.Add(repo.Uri);
        foreach (var flag in repo.Flags) start.ArgumentList.Add(flag);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["RESTIC_PASSWORD"] = repo.Password;
        foreach (var entry in repo.Env)
        {
            var equals = entry.IndexOf('=');
            if (equals > 0) start.Environment[entry[..equals]] = entry[(equals + 1)..];
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(binary))!;
        start.Environment["PATH"] = directory + Path.PathSeparator +
            (start.Environment.TryGetValue("PATH", out var oldPath) ? oldPath : "");
        return start;
    }

    // Backup and RunCommand wait for completion; maintenance RPCs acknowledge
    // scheduling immediately. Neither HTTP completion nor cancellation proves
    // the task stopped. Ownership lasts until a terminal state or a fail-closed guard.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _repoTaskGates = new();

    internal async Task<BackrestOperation> RunTrackedRepoRpcAsync<T>(BackrestClient client, string repoId,
        string endpoint, T request, Func<BackrestOperation, bool> matches,
        CancellationToken ct, Action<string>? lineSink = null, TimeSpan? uncertaintyTimeout = null,
        BackupLiveProgress? liveProgress = null) where T : class
    {
        var gate = _repoTaskGates.GetOrAdd(repoId, _ => new SemaphoreSlim(1, 1));
        // This engine runs one task per repository: a backup waiting here is
        // not running, and its card says so (U156).
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            RepositoryAccess.Waiting(RepositoryAccess.EngineRepositoryReason);
            try { await gate.WaitAsync(ct).ConfigureAwait(false); }
            finally { RepositoryAccess.NotWaiting(); }
        }
        using var transport = new CancellationTokenSource();
        Task<BackrestScheduledOperationResponse>? submission = null;
        string? ownedId = null;
        var acknowledged = false;
        // These two handlers wait for completion, so their running operation
        // must be discovered to support cancellation. Scheduled maintenance
        // returns an id immediately; do not guess ownership while that reply
        // is delayed or lost.
        var discoverWhileRunning = endpoint is "/v1.Backrest/Backup" or "/v1.Backrest/RunCommand";
        try
        {
            var repoGuid = await ResolveRepoGuidAsync(client, repoId, ct).ConfigureAwait(false);
            var before = await GetRepoOperationsAsync(client, repoGuid, ct).ConfigureAwait(false);
            var previousIds = before.Select(op => op.Id).ToHashSet(StringComparer.Ordinal);
            ct.ThrowIfCancellationRequested();
            submission = client.CallAsync<T, BackrestScheduledOperationResponse>(endpoint, request, transport.Token);
            var submitted = DateTime.UtcNow;
            DateTime? goneSince = null;
            DateTime? recoveryDeadline = null;
            string lastStatus = "";
            liveProgress ??= new BackupLiveProgress();
            var lastAnswer = DateTime.UtcNow;
            var movedAt = DateTime.UtcNow;
            BackupLiveProgress.Sample? lastSample = null;
            while (true)
            {
                // Prefer the daemon's exact id over a similar concurrently
                // recorded operation. Backup returns Empty, so its identity is
                // still recovered from new operations for this serialized repo.
                if (submission.IsCompletedSuccessfully && IsOperationId(submission.Result.OperationId))
                    ownedId = submission.Result.OperationId;
                if (submission.IsCompletedSuccessfully && ownedId is null)
                    recoveryDeadline ??= DateTime.UtcNow + (uncertaintyTimeout ?? TimeSpan.FromSeconds(30));
                if ((ct.IsCancellationRequested || submission.IsFaulted || submission.IsCanceled) && recoveryDeadline is null)
                    recoveryDeadline = DateTime.UtcNow + (uncertaintyTimeout ?? TimeSpan.FromSeconds(30));
                IReadOnlyList<BackrestOperation>? operations = null;
                try
                {
                    using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    MarkUsed();
                    operations = await GetRepoOperationsAsync(client, repoGuid, probe.Token).ConfigureAwait(false);
                    lastAnswer = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "Could not poll repository operation");
                    // Patience has a limit: an engine that answers nothing for
                    // this long is hung, not busy. The finally block stops the
                    // task, or keeps Backspace blocked if it can't confirm that.
                    if (!ct.IsCancellationRequested && DateTime.UtcNow - lastAnswer >= EngineSilenceLimit)
                        throw Stalled(lineSink, $"The backup engine stopped answering for {Minutes(EngineSilenceLimit)}, so Backspace stopped waiting for it.");
                    // A busy engine (restic scanning a whole drive) can answer
                    // slowly for minutes while the task runs and its request is
                    // still open: keep waiting. Only an ended request or a stop
                    // makes an unanswered poll an uncertainty (U111, 3 November
                    // 2026: a C: backup was cancelled after 30 s of slow polls).
                    if (ct.IsCancellationRequested || submission.IsCompleted)
                        recoveryDeadline ??= DateTime.UtcNow + (uncertaintyTimeout ?? TimeSpan.FromSeconds(30));
                }
                if (submission.IsCompletedSuccessfully && IsOperationId(submission.Result.OperationId))
                    ownedId = submission.Result.OperationId;
                var owned = operations?.FirstOrDefault(op => ownedId is not null ? op.Id == ownedId :
                    discoverWhileRunning && !previousIds.Contains(op.Id) && matches(op));
                // Backrest records a queued task's operation when scheduling it
                // and deletes it, without ever answering, when its queue is
                // reset (waking from sleep, a settings change). Only queued
                // tasks are discarded, so nothing is left running (U093).
                if (operations is not null && !ct.IsCancellationRequested
                    && Dropped(ownedId is not null && owned is null, ref goneSince, submitted,
                        neverSeen: endpoint == "/v1.Backrest/Backup" && ownedId is null && !submission.IsCompleted))
                {
                    acknowledged = true;
                    throw new EngineTaskDroppedException();
                }
                if (owned is not null)
                {
                    ownedId = owned.Id;
                    if (!ct.IsCancellationRequested && !submission.IsFaulted && !submission.IsCanceled)
                        recoveryDeadline = null;
                    if (lastStatus != owned.Status)
                    {
                        // Backrest runs one task at a time: a backup it holds
                        // back is queued, not running (U156).
                        if (owned.Status == "STATUS_PENDING")
                        {
                            liveProgress.HeldBack?.Invoke();
                            RepositoryAccess.Waiting(RepositoryAccess.EngineQueueReason);
                        }
                        else if (lastStatus == "STATUS_PENDING") RepositoryAccess.NotWaiting();
                        lastStatus = owned.Status;
                        var line = $"[backrest] op={owned.Id} status={owned.Status} {owned.DisplayMessage}".TrimEnd();
                        RaiseLineWritten(line);
                        lineSink?.Invoke(line);
                    }
                    if (owned.Status == "STATUS_INPROGRESS" && liveProgress.Next(owned.OperationBackup, DateTime.UtcNow) is { } progressLine)
                    {
                        RaiseLineWritten(progressLine);
                        lineSink?.Invoke(progressLine);
                    }
                    // A backup whose counts (files and bytes found, files and
                    // bytes done) stay frozen while it runs is stuck, for
                    // example on a network that stopped answering. Queued time
                    // doesn't count; restic reports progress every second.
                    var sample = owned.Status == "STATUS_INPROGRESS" ? BackupLiveProgress.Parse(owned.OperationBackup) : null;
                    if (owned.Status != "STATUS_INPROGRESS" || sample != lastSample)
                        (movedAt, lastSample) = (DateTime.UtcNow, sample);
                    else if (endpoint == "/v1.Backrest/Backup" && !ct.IsCancellationRequested
                        && DateTime.UtcNow - movedAt >= ProgressStallLimit)
                    {
                        var stall = Stalled(lineSink, $"The backup made no progress for {Minutes(ProgressStallLimit)}, so Backspace stopped it.");
                        await TrySendCancelAsync(client, owned.Id).ConfigureAwait(false);
                        using var acknowledgement = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        BackrestOperation stopped;
                        try { stopped = await WaitForOperationTerminalAsync(client, owned.Id, acknowledgement.Token, lineSink).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw stall; }
                        await DrainFollowUpOperationsAsync(client, repoGuid, previousIds, ct).ConfigureAwait(false);
                        acknowledged = true;
                        await CaptureOperationDiagnosticsAsync(client, stopped, lineSink).ConfigureAwait(false);
                        throw stall;
                    }
                    if (IsTerminalStatus(owned.Status))
                    {
                        await DrainFollowUpOperationsAsync(client, repoGuid, previousIds, ct).ConfigureAwait(false);
                        acknowledged = true;
                        await CaptureOperationDiagnosticsAsync(client, owned, lineSink).ConfigureAwait(false);
                        return owned;
                    }
                    if (ct.IsCancellationRequested)
                    {
                        await TrySendCancelAsync(client, owned.Id).ConfigureAwait(false);
                        using var acknowledgement = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        var terminal = await WaitForOperationTerminalAsync(client, owned.Id, acknowledgement.Token, lineSink).ConfigureAwait(false);
                        await DrainFollowUpOperationsAsync(client, repoGuid, previousIds, ct).ConfigureAwait(false);
                        acknowledged = true;
                        await CaptureOperationDiagnosticsAsync(client, terminal, lineSink).ConfigureAwait(false);
                        return terminal;
                    }
                }
                else if (operations is not null && submission.IsFaulted &&
                    submission.Exception?.GetBaseException() is BackrestRpcException)
                {
                    acknowledged = true;
                    await submission.ConfigureAwait(false);
                }
                else if (submission.IsCompletedSuccessfully)
                    recoveryDeadline ??= DateTime.UtcNow + (uncertaintyTimeout ?? TimeSpan.FromSeconds(30));
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

    /// <summary>How long an operation seen once may stay missing, and how long
    /// a Backup may go without its queued operation appearing, before the task
    /// counts as discarded by the engine (U093). Tests shorten them.</summary>
    internal static TimeSpan DroppedGrace = TimeSpan.FromSeconds(15);
    internal static TimeSpan NeverQueuedGrace = TimeSpan.FromSeconds(60);

    /// <summary>How long a running task may leave every status check
    /// unanswered, and how long a backup's counts may stay frozen, before it
    /// counts as stuck (U111). A busy engine answers within seconds to a few
    /// minutes; restic reports progress every second. Tests shorten them.</summary>
    internal static TimeSpan EngineSilenceLimit = TimeSpan.FromMinutes(10);
    internal static TimeSpan ProgressStallLimit = TimeSpan.FromMinutes(60);

    private EngineStalledException Stalled(Action<string>? lineSink, string reason)
    {
        var line = "[backspace] " + reason;
        RaiseLineWritten(line); lineSink?.Invoke(line);
        return new EngineStalledException(reason);
    }

    private static string Minutes(TimeSpan span) => span.TotalMinutes >= 1
        ? $"{span.TotalMinutes:0} minutes"
        : $"{span.TotalSeconds:0.#} seconds";

    internal static bool Dropped(bool gone, ref DateTime? goneSince, DateTime submitted, bool neverSeen)
    {
        var now = DateTime.UtcNow;
        if (gone) { goneSince ??= now; return now - goneSince.Value >= DroppedGrace; }
        goneSince = null;
        return neverSeen && now - submitted >= NeverQueuedGrace;
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
                // Drain follow-ups that have operation records. The automatic
                // index has none and is confirmed separately after Backup.
                // The per-repository gate excludes our other task RPCs;
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
            await client.CallVoidAsync("/v1.Backrest/Cancel", new CancelOperationRequest { OperationId = opId }, cancelCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.Debug(ex, "Cancel request for operation {Operation} failed; awaiting terminal state", opId); }
    }

    private static bool IsOperationId(string value)
        => long.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0;

    /// <summary>
    /// Map Backrest's terminal status enum to our internal status.
    /// Unknown / empty status maps to Failed — a backup we can't
    /// confirm succeeded is NOT a success. Earlier this defaulted
    /// to Success, which combined with the 1.5 s pump cutoff to
    /// produce silent data loss: real backups that hadn't actually
    /// finished within 1.5 s showed green in the UI.
    /// </summary>
    /// <summary>True for a backup the engine stopped because it was asked to
    /// (Stop, Quit, the service stopping), as opposed to a skip it decided.</summary>
    internal static bool WasStoppedOnRequest(RunRecord record)
        => record.Summary?.StartsWith($"Backup result: {BackrestStatus.UserCancelled}", StringComparison.Ordinal) == true;

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
                // Known from restic 0.17's snapshot summary; 0 when unknown.
                FileCount: (int)Math.Min(int.MaxValue, s.Summary?.TotalFilesProcessed ?? 0),
                TotalBytes: s.Summary?.TotalBytesProcessed ?? 0) { SnapshotId = s.Id });
        }
        return revisions;
    }

    public async Task<IReadOnlyList<RevisionFile>> ListSnapshotFilesAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, CancellationToken ct, string? expectedSnapshotId = null)
    {
        // Read the complete source tree in one owned restic process. The
        // directory-at-a-time RPC fallback starts another cloud connection
        // for every folder, even for a tiny snapshot with nested files.
        var tree = await ListSnapshotTreeAsync(backup, sourcePath, destination,
            storageName, revision, ct, expectedSnapshotId).ConfigureAwait(false);
        if (tree is not null)
            return tree.Where(node => !node.IsDirectory)
                .Select(node => new RevisionFile(node.Path, node.SizeBytes, node.ModifiedUtc))
                .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();

        await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var (snapshotId, _) = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct, expectedSnapshotId).ConfigureAwait(false);

        // Start at the selected source. Walking from the snapshot root starts
        // another restic/rclone request for every ancestor directory, which can
        // add minutes before the first file appears on a cloud destination.
        var srcRel = SourcePathToSnapshotPrefix(sourcePath);
        var collected = new List<RevisionFile>();
        await WalkSnapshotTreeAsync(client, MakeRepoId(destination, storageName), snapshotId, "/" + srcRel, collected, ct)
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
        // A drive root becomes "C/" above. Callers append their own separator
        // when bounding a subtree; retaining this one would ask for /C//file
        // and silently discard every file from a whole-volume snapshot.
        return s.TrimEnd('/');
    }

    public async Task<IReadOnlyList<RevisionDirEntry>?> ListSnapshotDirectoryAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, string dirRelativePath, CancellationToken ct, string? expectedSnapshotId = null)
    {
        await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var (snapshotId, _) = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct, expectedSnapshotId).ConfigureAwait(false);

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
                new ListSnapshotFilesRequest { RepoId = MakeRepoId(destination, storageName), SnapshotId = snapshotId, Path = snapshotDirPath },
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

    /// <summary>Raised with the snapshot ID each time a whole tree is read
    /// from the repository (not from memory), so E65 can prove a mount reads
    /// only the trees it was asked for (U162).</summary>
    internal event Action<string>? TreeListingStarted;

    public async Task<IReadOnlyList<RevisionTreeNode>?> ListSnapshotTreeAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, CancellationToken ct, string? expectedSnapshotId = null, IProgress<int>? progress = null)
    {
        if (!File.Exists(_sidecar.ResticBinaryPath)) return null;
        // Resolve the snapshot ID (via Backrest) and pull the live
        // repo stanza so we know the URI, env vars, and per-repo
        // flags Backrest would have set for any restic invocation.
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var (snapshotId, repoGuid) = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct, expectedSnapshotId).ConfigureAwait(false);
        var cfg = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
        var repo = cfg.Repos.FirstOrDefault(r =>
            r.Id == MakeRepoId(destination, storageName) && string.Equals(r.Guid, repoGuid, StringComparison.OrdinalIgnoreCase));
        if (repo is null) return null;
        var identity = new SnapshotIdentity(repo.Id, MakePlanId(backup, sourcePath, destination, storageName),
            repo.Guid, repo.Uri, cfg.Modno, snapshotId);
        lock (_snapshotTreeGate)
            if (_snapshotTrees.TryGetValue(identity, out var cached)) return cached;
        TreeListingStarted?.Invoke(snapshotId);
        await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        // The listing reads without a repository lock: keep cleanup out (U159).
        using var reading = await SnapshotReaders.HoldAsync(destination, storageName, SnapshotReaders.Kind.Restore, null, ct).ConfigureAwait(false);

        var srcPrefix = SourcePathToSnapshotPrefix(sourcePath);
        var rootBound = "/" + srcPrefix;
        ProcessStartInfo StartInfo(IEnumerable<string> paths, bool recursive)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _sidecar.ResticBinaryPath,
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
            psi.ArgumentList.Add("--no-lock"); // a listing reads only: no lock file to write and delete
            if (recursive) psi.ArgumentList.Add("--recursive");
            psi.ArgumentList.Add(snapshotId);
            foreach (var path in paths) psi.ArgumentList.Add(path);
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
            // Match direct restores: use the bundled rclone and Backrest's
            // destination-specific environment, never a user's global remote.
            var binaryDirectory = Path.GetDirectoryName(Path.GetFullPath(_sidecar.ResticBinaryPath))!;
            psi.Environment["PATH"] = binaryDirectory + Path.PathSeparator +
                (psi.Environment.TryGetValue("PATH", out var oldPath) ? oldPath : "");
            return psi;
        }

        // restic walks a tree one folder at a time in one thread: a 632,000
        // entry snapshot took 27-36 s in one process (U158). For a mount,
        // the whole listing starts at once and, beside it, a shared-out one:
        // the source's top level is listed (one short call), then its folders
        // are shared between several processes, each walking its share with
        // --recursive (measured: 9 s instead of 27 s; sharing out the second
        // level instead was slower, a process given many folders walks them
        // more slowly than one given a few). A small snapshot's whole
        // listing ends first and is used; otherwise it is stopped once the
        // shared-out listing is ready to fan out. Every part is validated
        // like a single listing; any incomplete part fails the tree.
        var listingClock = Stopwatch.StartNew();
        // Files listed so far (U166). The whole listing and the shared-out one
        // read the same files while both run: the further of the two counts.
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<SnapshotTreeListing, int>();
        SnapshotTreeListing? wholeListing = null;
        void Report()
        {
            var whole = wholeListing is { } w && counts.TryGetValue(w, out var n) ? n : 0;
            progress?.Report(Math.Max(whole, counts.Where(kv => !ReferenceEquals(kv.Key, wholeListing)).Sum(kv => kv.Value)));
        }
        async Task<IReadOnlyList<RevisionTreeNode>?> Ls(IReadOnlyList<string> paths, bool recursive, CancellationToken token)
        {
            SnapshotTreeListing? listing = null;
            var partProgress = progress is null ? null : new InlineCount(count => { counts[listing!] = count; Report(); });
            listing = new SnapshotTreeListing(rootBound, snapshotId, partProgress);
            if (recursive && paths.Count == 1 && paths[0] == rootBound) wholeListing = listing;
            try
            {
                // restic emits one JSON object per line: the first line is
                // the snapshot summary (struct_type=snapshot); every
                // subsequent line is a node (struct_type=node).
                var result = await StreamingRestoreProcess.RunAsync(StartInfo(paths, recursive), listing.Accept, token,
                    operationName: "snapshot listing", includeStandardOutput: false).ConfigureAwait(false);
                if (result.Cancelled) throw new OperationCanceledException(token);
                if (result.ExitCode != 0 || result.HadErrors || !listing.IsComplete)
                {
                    // An incomplete listing must never become a complete directory
                    // cache: missing files would otherwise disappear from Explorer.
                    _log.Warning("restic ls --json {Snap} exited {Code}: {Err}",
                        snapshotId, result.ExitCode, result.Output);
                    return null;
                }
                counts[listing] = listing.Files;
                return listing.Nodes;
            }
            catch (OperationCanceledException) { counts.TryRemove(listing, out _); throw; }
        }
        string Absolute(string relative) => rootBound == "/" ? "/" + relative : rootBound + "/" + relative;

        var fanOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<RevisionTreeNode>?> SharedOut(CancellationToken token)
        {
            var all = new List<RevisionTreeNode>();
            var seenDirectories = new HashSet<string>(StringComparer.Ordinal);
            void Add(IEnumerable<RevisionTreeNode> part)
            {
                foreach (var node in part)
                    if (!node.IsDirectory || seenDirectories.Add(node.Path)) all.Add(node);
            }
            using var running = new SemaphoreSlim(ListingProcesses);
            async Task<IReadOnlyList<RevisionTreeNode>?[]> Shared(IReadOnlyList<string> folders, bool recursive) =>
                await Task.WhenAll(ShareOut(folders, ListingProcesses).Select(async group =>
                {
                    await running.WaitAsync(token).ConfigureAwait(false);
                    try { return await Ls(group, recursive, token).ConfigureAwait(false); }
                    finally { running.Release(); }
                })).ConfigureAwait(false);
            try
            {
                var level1 = await Ls(new[] { rootBound }, recursive: false, token).ConfigureAwait(false);
                if (level1 is null) return null;
                Add(level1);
                var top = level1.Where(n => n.IsDirectory && n.Path.Length > 0).Select(n => Absolute(n.Path)).ToArray();
                if (top.Length == 0) return all;
                fanOut.TrySetResult();
                _log.Information("Snapshot {Snap}: top level listed in {Ms} ms; {Count} folders shared out", snapshotId[..8], listingClock.ElapsedMilliseconds, top.Length);
                var parts = await Shared(top, recursive: true).ConfigureAwait(false);
                if (parts.Any(p => p is null)) return null;
                foreach (var part in parts) Add(part!);
                return all;
            }
            finally { fanOut.TrySetResult(); }
        }

        IReadOnlyList<RevisionTreeNode>? listed;
        if (progress is null)
            listed = await Ls(new[] { rootBound }, recursive: true, ct).ConfigureAwait(false);
        else
        {
            using var wholeStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var sharedStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var whole = Ls(new[] { rootBound }, recursive: true, wholeStop.Token);
            var shared = SharedOut(sharedStop.Token);
            await Task.WhenAny(whole, fanOut.Task).ConfigureAwait(false);
            if (whole.IsCompletedSuccessfully && whole.Result is not null)
            {
                sharedStop.Cancel();
                try { await shared.ConfigureAwait(false); } catch (Exception) { /* stopped: the whole listing answered */ }
                listed = whole.Result;
            }
            else
            {
                wholeStop.Cancel();
                try { await whole.ConfigureAwait(false); } catch (Exception) when (!ct.IsCancellationRequested) { /* stopped or failed: the shared-out listing answers */ }
                listed = await shared.ConfigureAwait(false);
            }
        }
        if (listed is null) return null;
        _log.Information("Listed snapshot {Snap}: {Count} entries in {Ms} ms ({How})", snapshotId[..8], listed.Count, listingClock.ElapsedMilliseconds,
            progress is null ? "one listing" : fanOut.Task.IsCompleted ? "shared out" : "one listing");
        var nodes = listed.ToArray();
        if (nodes.Length <= 65536)
        {
            lock (_snapshotTreeGate)
            {
                if (!_snapshotTrees.ContainsKey(identity))
                {
                    if (_snapshotTrees.Count >= 8 || _cachedTreeNodeCount + nodes.Length > 65536)
                    { _snapshotTrees.Clear(); _cachedTreeNodeCount = 0; }
                    _snapshotTrees.Add(identity, Array.AsReadOnly(nodes));
                    _cachedTreeNodeCount += nodes.Length;
                }
            }
        }
        return nodes;
    }

    public async Task<bool> RestoreSnapshotSubtreeAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, string targetDir, CancellationToken ct, string? expectedSnapshotId = null)
    {
        // Keep consumer-cloud mounts lazy; browsing a second small file must
        // not begin downloading a whole cloud snapshot as a side effect.
        if (destination.NeedsOAuth) return false;
        // Use the same staging, process ownership and terminal-result checks as
        // an ordinary restore. Cancellation must stop the owned engine before
        // the mount's repository lease can end. The provider publishes this
        // private cache only after every requested file has been recovered.
        var result = await RestoreFilesAsync(backup, sourcePath, destination, storageName,
            revision, new[] { "" }, targetDir, onProgress: null, ct,
            expectedSnapshotId: expectedSnapshotId).ConfigureAwait(false);
        if (result.Status == RestoreTerminalStatus.Cancelled)
            throw new OperationCanceledException(ct);
        return result.Status == RestoreTerminalStatus.Success;
    }

    /// <summary>How many restic processes share out a snapshot's tree (U158).
    /// Each loads the repository index once (285 MB for a 632,000-entry
    /// fixture), so they are limited to one per free core and per 2 GB of
    /// memory, at most 12; listing is otherwise one thread per process.</summary>
    internal static int ListingProcesses => (int)Math.Clamp(Math.Min(Environment.ProcessorCount - 2,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (2L << 30)), 2, 12);

    /// <summary>Shares the folders out in turn (so neighbours, often alike in
    /// size, go to different listings) between <paramref name="processes"/>
    /// listings, or more when one command line would pass about 16,000
    /// characters (Windows allows 32,767). The caller runs at most
    /// <paramref name="processes"/> at once.</summary>
    internal static List<List<string>> ShareOut(IReadOnlyList<string> folders, int processes)
    {
        if (folders.Count == 0) return new();
        var characters = folders.Sum(f => (long)f.Length + 3);
        var count = (int)Math.Min(folders.Count, Math.Max(processes, (characters + 15_999) / 16_000));
        var groups = Enumerable.Range(0, count).Select(_ => new List<string>()).ToList();
        for (var i = 0; i < folders.Count; i++) groups[i % count].Add(folders[i]);
        return groups;
    }

    /// <summary>Reports on the calling thread.</summary>
    private sealed class InlineCount(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    /// <summary>One line from <c>restic ls --json</c>. restic emits one
    /// JSON object per line: the FIRST line is the snapshot summary
    /// (<c>struct_type=snapshot</c>) which we skip, every line after
    /// is a node (<c>struct_type=node</c>). The fields here are the
    /// subset we need — restic emits more (uid, gid, mode,
    /// permissions, atime, ctime) but they don't apply to a
    /// read-only WinFSP volume that presents an everyone-read SD.
    /// Lower-case field names match restic's JSON shape verbatim.</summary>
    internal sealed class SnapshotTreeListing(string rootBound, string snapshotId, IProgress<int>? progress = null)
    {
        private readonly List<RevisionTreeNode> _nodes = new(2048);
        private bool _sawSnapshot;
        private bool _invalid;
        private int _files;
        internal bool IsComplete => _sawSnapshot && !_invalid;
        internal IReadOnlyList<RevisionTreeNode> Nodes => _nodes;
        /// <summary>Files (not folders) listed so far: what <c>progress</c>
        /// hears, comparable with the snapshot summary's file count (U166).</summary>
        internal int Files { get { lock (_nodes) return _files; } }

        internal void Accept(string line)
        {
            // Both output streams can call this concurrently. Publish nothing
            // until the complete process result and the header are validated.
            lock (_nodes)
            {
                line = line.TrimStart();
                if (!line.StartsWith('{')) return;
                ResticLsLine? parsed;
                try { parsed = System.Text.Json.JsonSerializer.Deserialize<ResticLsLine>(line); }
                catch (System.Text.Json.JsonException) { _invalid = true; return; }
                if (parsed?.struct_type == "snapshot")
                {
                    if (_sawSnapshot || parsed.id != snapshotId) _invalid = true;
                    _sawSnapshot = true;
                    return;
                }
                if (parsed?.struct_type != "node") return;
                var path = parsed.path ?? "";
                if (!_sawSnapshot || !path.StartsWith('/') || string.IsNullOrEmpty(parsed.type) || parsed.size < 0)
                { _invalid = true; return; }
                // Snapshot paths are case-sensitive even when browsed from
                // Windows. Never include a similarly named sibling tree.
                string relative;
                if (path == rootBound) relative = "";
                else if (rootBound == "/") relative = path[1..];
                else if (path.StartsWith(rootBound + "/", StringComparison.Ordinal)) relative = path[(rootBound.Length + 1)..];
                else return;
                if (relative.Split('/').Any(segment => segment is "." or ".."))
                { _invalid = true; return; }
                // Links are left out: creating one to another computer
                // (\\server\share) makes Windows connect there and sign in as
                // whoever restores, Windows itself for a background test
                // restore (U152). restic's listing doesn't say where a link
                // leads, so none is offered for a test restore, the mounted
                // drive or a single-file restore. Restoring its folder still
                // brings it back.
                if (parsed.type == "symlink") return;
                var directory = parsed.type == "dir";
                var modified = DateTime.TryParse(parsed.mtime, out var mt) ? mt.ToUniversalTime() : DateTime.MinValue;
                _nodes.Add(new(relative, directory, directory ? 0 : parsed.size, modified));
                if (!directory) _files++;
                if (progress is not null && _nodes.Count % 5000 == 0) progress.Report(_files);
            }
        }
    }

    private sealed class ResticLsLine
    {
        public string? id { get; set; }
        public string? struct_type { get; set; }
        public string? name { get; set; }
        public string? type { get; set; }
        public string? path { get; set; }
        public long size { get; set; }
        public string? mtime { get; set; }
    }

    /// <summary>Raised with the file's snapshot path each time a file is read
    /// from the repository for the mounted drive, so tests can prove that
    /// readers share one download (U167).</summary>
    internal event Action<string>? FileDumpStarted;

    /// <summary><c>restic dump</c> of one file: its bytes reach
    /// <paramref name="output"/> as restic reads them, the first ones as soon
    /// as restic has found the file, with nothing written elsewhere (U167).
    /// The path is literal (no pattern), so brackets need no escaping.</summary>
    public async Task<bool> DumpSnapshotFileAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, string fileRelativePath, Stream output, CancellationToken ct, string? expectedSnapshotId = null)
    {
        if (!File.Exists(_sidecar.ResticBinaryPath)) return false;
        var relative = NormalizeRestoreRelativePath(fileRelativePath);
        if (relative.Length == 0) throw new InvalidOperationException("No file named.");
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var (snapshotId, repoGuid) = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct, expectedSnapshotId).ConfigureAwait(false);
        var cfg = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
        var repo = cfg.Repos.FirstOrDefault(r =>
            r.Id == MakeRepoId(destination, storageName) && string.Equals(r.Guid, repoGuid, StringComparison.OrdinalIgnoreCase));
        if (repo is null) return false;
        await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        // Reads without a repository lock: keep cleanup out meanwhile (U159).
        using var reading = await SnapshotReaders.HoldAsync(destination, storageName, SnapshotReaders.Kind.Restore, null, ct).ConfigureAwait(false);
        var srcPrefix = SourcePathToSnapshotPrefix(sourcePath);
        var path = "/" + (srcPrefix.Length > 0 ? srcPrefix + "/" : "") + relative;
        var arguments = new List<string> { "dump", "--no-lock", snapshotId, path };
        if (backup.BandwidthLimitKBps > 0)
            arguments.AddRange(["--limit-download", backup.BandwidthLimitKBps.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        FileDumpStarted?.Invoke(path);
        var result = await StreamingRestoreProcess.RunToStreamAsync(
            BuildResticStartInfo(_sidecar.ResticBinaryPath, repo, [.. arguments]), output, ct, "file download").ConfigureAwait(false);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (result.ExitCode != 0)
        {
            _log.Warning("restic dump {Snap} exited {Code}: {Err}", snapshotId[..8], result.ExitCode, result.Output);
            throw new IOException("The backup engine could not read this file from the destination. Open Activity & logs for details.");
        }
        return true;
    }

    public async Task<RestoreOpResult> RestoreFilesAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, IReadOnlyList<string> includePatterns, string targetPath,
        Action<RestoreProgressUpdate>? onProgress, CancellationToken ct,
        IReadOnlyList<string>? expectedLeaves = null, string? expectedSnapshotId = null, bool overwrite = true, bool preserveStructure = true,
        RestoreControl? control = null, Action<string?>? onStaging = null)
    {
        if (includePatterns.Count == 0)
            return new RestoreOpResult(0, 0, 0, 0, RestoreTerminalStatus.Success, null);
        ValidateRestoreTarget(targetPath);
        await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        // What this restore reads stays in place until it ends (U159).
        using var reading = await SnapshotReaders.HoldAsync(destination, storageName, SnapshotReaders.Kind.Restore, null, ct).ConfigureAwait(false);
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var (snapshotId, repoGuid) = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct, expectedSnapshotId).ConfigureAwait(false);
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
        // Native Restore accepts one path and no per-request bandwidth limit.
        // Scattered selections and per-backup limits use one owned restic
        // process, consuming progress directly without Backrest's command-log
        // limit. Backrest still provides repository credentials and snapshots.
        // Backrest's native Restore refuses an existing target. Resumable
        // transfers therefore use the owned restic path, which can verify and
        // reuse the same staged bytes after the child has actually stopped.
        var native = control is null && includes.Length == 1 && backup.BandwidthLimitKBps == 0 && srcPrefix.Length > 0;
        var selectedPath = native ? "/" + srcPrefix.TrimEnd('/') + (includes[0].Length > 0 ? "/" + includes[0] : "") : "";
        // The native handler trims the path. Preserve significant whitespace by
        // using the existing command path in that uncommon case.
        native &= selectedPath == selectedPath.Trim();
        var selectionParent = native && includes[0].Contains('/') ? includes[0][..includes[0].LastIndexOf('/')] : "";
        var outputRoot = native && includes[0].Length == 0 ? Path.Combine(staging, srcPrefix.Split('/').Last()) : staging;
        string OutputRelative(string path)
        {
            var relative = Path.GetRelativePath(outputRoot, path).Replace('\\', '/');
            return selectionParent.Length > 0 ? selectionParent + "/" + relative : relative;
        }
        // Native Restore refuses an already existing target, even an empty one.
        if (!native) Directory.CreateDirectory(staging);
        var includeFile = Path.Combine(Path.GetTempPath(), $"backspace-restore-include-{Guid.NewGuid():N}.txt");
        var safeToCleanup = true;
        var publication = control?.Output ?? new RestoreOutputTransaction();
        string? opId = null;
        var status = RestoreTerminalStatus.Error;
        string? error = null;
        int restored = 0;
        long restoredBytes = 0;
        var total = exact?.Count ?? 0;
        long totalBytes = 0;
        using var submissionCts = new CancellationTokenSource();
        Task<BackrestScheduledOperationResponse>? submission = null;
        string captured = "";
        // The progress card names the staging folder while output arrives
        // there (U154). Native Restore creates it itself, so report it once
        // it exists. A UI callback never interferes with the restore.
        var stagingReported = false;
        void ReportStaging(string? path)
        {
            if (path is not null && (stagingReported || !Directory.Exists(path))) return;
            if (path is null && !stagingReported) return;
            stagingReported = path is not null;
            try { onStaging?.Invoke(path); }
            catch (Exception ex) { _log.Debug(ex, "Restore staging observer failed"); }
        }
        try
        {
            ReportStaging(staging);
            if (!native)
                await File.WriteAllLinesAsync(includeFile,
                    includes.Select(p => "/" + EscapeBracketsForResticPattern(p)), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            BackrestOperation? terminal = null;
            if (native)
            {
                safeToCleanup = false;
                // Backrest 1.14.1 owns the native path's platform-specific
                // escaping. Keep the literal selection intact here; staged
                // output is still filtered before it reaches the destination.
                var nativePath = selectedPath;
                submission = client.CallAsync<RestoreSnapshotRequest, BackrestScheduledOperationResponse>("/v1.Backrest/Restore", new RestoreSnapshotRequest
                { RepoId = repoId, PlanId = MakePlanId(backup, sourcePath, destination, storageName),
                    SnapshotId = snapshotId, Path = nativePath, Target = staging }, submissionCts.Token);
                opId = await AwaitScheduledRestoreOperationAsync(submission,
                    () => safeToCleanup = true).ConfigureAwait(false);
            }
            else
            {
                var config = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
                    "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
                var repo = config.Repos.SingleOrDefault(r => r.Id == repoId && r.Guid == repoGuid)
                    ?? throw new InvalidOperationException("The restore repository changed. Reopen Restore and try again.");
                var start = BuildStreamingRestoreStartInfo(_sidecar.ResticBinaryPath, repo,
                    snapshotId + ":/" + srcPrefix, staging, includeFile, backup.BandwidthLimitKBps);
                // The runner only returns/throws after the child has exited.
                // The caller's repository lease and the staging folder therefore
                // remain owned during cancellation, including a failed kill.
                StreamingRestoreProcess.Result result;
                while (true)
                {
                    if (control is not null) await control.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                    using var transfer = control?.TransferCancellation(ct);
                    try
                    {
                    result = await StreamingRestoreProcess.RunAsync(start, line =>
                    {
                    if (!TryParseRestoreProgress(line, out var update)) return;
                    if (exact is not null)
                    {
                        var directories = Directory.Exists(staging) ? CountRestoreDirectories(staging) : 0;
                        update = update with { TotalFiles = exact.Count,
                            FilesRestored = Math.Clamp(update.FilesRestored - directories, 0, exact.Count) };
                    }
                        onProgress?.Invoke(update);
                    }, transfer?.Token ?? ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && control?.State is RestoreControlState.Pausing or RestoreControlState.Paused)
                    {
                        await control.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                        continue;
                    }
                    ct.ThrowIfCancellationRequested();
                    if (control?.State is RestoreControlState.Pausing or RestoreControlState.Paused)
                    {
                        // The runner has acknowledged child exit. Keep staged
                        // bytes and the immutable snapshot while paused.
                        await control.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                        continue;
                    }
                    break;
                }
                captured = result.Output;
                if (!result.Cancelled && (result.ExitCode != 0 || result.HadErrors))
                    _log.Warning("Restore process failed with exit code {ExitCode}. Last engine diagnostics: {Details}",
                        result.ExitCode, result.Output);
                terminal = new BackrestOperation {
                    Status = result.Cancelled ? BackrestStatus.UserCancelled :
                        result.ExitCode == 0 && !result.HadErrors ? BackrestStatus.Success : BackrestStatus.Error,
                    DisplayMessage = result.Cancelled ? "Restore cancelled." :
                        result.ExitCode == 0 && !result.HadErrors ? "" :
                        "The backup engine could not finish this restore. Open Activity & logs for details." };
            }
            if (native)
            {
                var nativeOperationId = opId ?? throw new InvalidOperationException("The native restore has no operation identifier.");
                try
                {
                    terminal = await WaitForOperationTerminalAsync(client, nativeOperationId, ct, observed: op =>
                    {
                        ReportStaging(staging);
                        if (op.OperationRestore?.LastStatus is { } progress)
                        {
                            var directories = Directory.Exists(outputRoot) ? CountRestoreDirectories(outputRoot) : 0;
                            var completed = (int)Math.Clamp(progress.FilesRestored - directories, 0, exact?.Count ?? int.MaxValue);
                            onProgress?.Invoke(new RestoreProgressUpdate(exact?.Count ?? (int)Math.Min(int.MaxValue, progress.TotalFiles),
                                completed, progress.TotalBytes, progress.BytesRestored, Math.Clamp(progress.PercentDone, 0, 1)));
                        }
                    }).ConfigureAwait(false);
                    safeToCleanup = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Cancellation of the caller's HTTP wait does not cancel a
                    // queued daemon operation. Use an independent cleanup token.
                    await TrySendCancelAsync(client, nativeOperationId).ConfigureAwait(false);
                    using var acknowledgement = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        terminal = await WaitForOperationTerminalAsync(client, nativeOperationId, acknowledgement.Token).ConfigureAwait(false);
                        safeToCleanup = true;
                    }
                    catch (Exception ex)
                    {
                        error = $"The backup engine did not confirm that restore stopped. Temporary output is kept at {staging}. Restart Backspace before running other operations.";
                        ServiceLocator.Maintenance.BlockUntilRestart(error);
                        _log.Warning(ex, "Restore cancellation acknowledgement failed for {Operation}", opId);
                    }
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

            ParseResticRestoreSummary(captured, out var completedFiles, out var completedBytes,
                out var reportedTotal, out totalBytes, out var errors);
            if (terminal?.OperationRestore?.LastStatus is { MessageType: "summary" } summary)
            {
                completedFiles = summary.FilesRestored;
                completedBytes = summary.BytesRestored;
                reportedTotal = summary.TotalFiles;
                totalBytes = summary.TotalBytes;
            }
            totalBytes = Math.Max(0, totalBytes);
            if (exact is null && reportedTotal >= 0) total = (int)Math.Min(int.MaxValue, reportedTotal);

            // A success summary never overrides a backend error or cancellation.
            // Metadata warnings remain warnings even when all content recovered.
            var leaves = safeToCleanup && Directory.Exists(outputRoot) ? EnumerateRestoreLeaves(outputRoot).ToArray() : Array.Empty<string>();
            // Restic summary "files" includes directories and its byte total
            // deduplicates hardlinks, so neither equals our leaf-file counts.
            // A partial recovery after ERROR requires explicit completion for
            // every produced leaf, with no error for that leaf, plus a final
            // summary. An incomplete/preallocated file never replaces originals.
            var completedLeaves = ParseCompletedRestoreLeaves(captured);
            var stagedDirectories = Directory.Exists(outputRoot) ? CountRestoreDirectories(outputRoot) : 0;
            var allOutputConfirmed = completedFiles == leaves.Length + stagedDirectories &&
                completedBytes >= 0 && completedBytes == totalBytes;
            if (status == RestoreTerminalStatus.Error && reportedTotal >= 0 && leaves.Length > 0 &&
                (allOutputConfirmed || leaves.All(path => completedLeaves.Contains(Path.GetRelativePath(staging, path).Replace('\\', '/')))))
                status = RestoreTerminalStatus.Warning;
            if (safeToCleanup && status is RestoreTerminalStatus.Success or RestoreTerminalStatus.Warning)
            {
                if (control is not null)
                {
                    do
                    {
                        await control.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                        control.BeginPublishing();
                    } while (control.State is RestoreControlState.Pausing or RestoreControlState.Paused);
                }
                var output = leaves.Where(path =>
                {
                    var relative = OutputRelative(path);
                    return exact?.Contains(relative) ?? includes.Any(p => p.Length == 0 || relative == p || relative.StartsWith(p.TrimEnd('/') + "/", StringComparison.Ordinal));
                }).ToArray();
                // Restic counts directories in its summary. The browser counts
                // files, so a confirmed whole-tree success must compare the
                // published leaves with the requested leaves, not directories.
                if (exact is null && (total == 0 || status == RestoreTerminalStatus.Success))
                    total = output.Length;
                var destinations = output.Select(path => SafeRestorePath(targetPath,
                    preserveStructure ? OutputRelative(path) : Path.GetFileName(path))).ToArray();
                var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                if (destinations.Distinct(pathComparer).Count() != destinations.Length)
                    throw new IOException("Several selected files have the same name. Keep folder structure enabled or restore them separately. No files were replaced.");
                // A whole-folder restore must also preserve ordinary empty
                // directories. Exact leaf selections must not create extra
                // unselected folders. Only confirmed success publishes them.
                var directoryDestinations = exact is null && preserveStructure && status == RestoreTerminalStatus.Success
                    ? EnumerateRestoreDirectories(outputRoot).Where(path =>
                    {
                        var relative = OutputRelative(path);
                        return includes.Any(p => p.Length == 0 || relative == p || relative.StartsWith(p.TrimEnd('/') + "/", StringComparison.Ordinal));
                    }).Select(path => SafeRestorePath(targetPath, OutputRelative(path))).ToArray()
                    : Array.Empty<string>();
                foreach (var directory in directoryDestinations)
                {
                    ValidateRestoreTarget(directory);
                    if (File.Exists(directory)) throw new IOException($"A file already occupies the folder {directory}. Restore to another folder. No files were replaced.");
                }
                foreach (var directory in directoryDestinations) { ct.ThrowIfCancellationRequested(); publication.CreateDirectory(directory); }
                var skipped = 0;
                for (var outputIndex = 0; outputIndex < output.Length; outputIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = output[outputIndex];
                    var destinationPath = destinations[outputIndex];
                    if (!overwrite && (File.Exists(destinationPath) || Directory.Exists(destinationPath)))
                    {
                        skipped++;
                        continue;
                    }
                    var attributes = File.GetAttributes(path);
                    var length = (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0 ? new FileInfo(path).Length : 0;
                    publication.Publish(path, destinationPath, targetPath, overwrite, ct);
                    restoredBytes += length;
                    restored++;
                }
                ct.ThrowIfCancellationRequested();
                if (skipped > 0)
                {
                    status = RestoreTerminalStatus.Warning;
                    error = $"Restored {restored} file(s). Left {skipped} existing item(s) unchanged because replacing existing files is turned off.";
                }
                if (status == RestoreTerminalStatus.Success && (errors > 0 || restored < total))
                {
                    status = restored == 0 ? RestoreTerminalStatus.Error : RestoreTerminalStatus.Warning;
                    error = $"Recovered {restored} of {total} requested files. {Math.Max(errors, total - restored)} item(s) could not be restored.";
                }
                var restoredEmptyFolder = exact is null && preserveStructure &&
                    (directoryDestinations.Length > 0 || (includes.Contains("") && Directory.Exists(outputRoot)));
                if (status == RestoreTerminalStatus.Success && restored == 0 && !restoredEmptyFolder)
                {
                    status = RestoreTerminalStatus.Error;
                    error = "The backup engine completed without producing any requested files.";
                }
            }
        }
        catch (OperationCanceledException) when (safeToCleanup)
        {
            status = RestoreTerminalStatus.Cancelled;
            error = "Restore cancelled. Temporary downloads and this restore's output are being removed; replaced originals are being recovered.";
        }
        catch (Exception ex)
        {
            status = RestoreTerminalStatus.Error;
            error = ex.Message;
            control?.Failed();
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
            if (control is null)
            {
                try
                {
                    if (status is RestoreTerminalStatus.Cancelled or RestoreTerminalStatus.Error || ct.IsCancellationRequested)
                    {
                        await Task.Run(publication.Rollback).ConfigureAwait(false); restored = 0; restoredBytes = 0;
                        if (status != RestoreTerminalStatus.Error && (status == RestoreTerminalStatus.Cancelled || ct.IsCancellationRequested))
                        {
                            status = RestoreTerminalStatus.Cancelled;
                            error = "Restore cancelled. Its output was removed and existing originals were preserved.";
                        }
                    }
                    else await Task.Run(publication.Commit).ConfigureAwait(false);
                }
                catch (Exception ex) { status = RestoreTerminalStatus.Error; error = ex.Message; }
            }
            // Terminal acknowledgement, or the fail-closed ownership guard,
            // controls cleanup. Cancelling this transport never stops restic.
            submissionCts.Cancel();
            if (submission is not null) try { await submission.ConfigureAwait(false); } catch { }
            if (safeToCleanup)
            {
                try { File.Delete(includeFile); } catch { }
                try { await Task.Run(() => DeleteRestoreStaging(staging)).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Restore staging cleanup failed");
                    status = RestoreTerminalStatus.Error;
                    error = $"The transfer has stopped, but temporary files could not be removed at {staging}. {ex.Message}";
                }
            }
            ReportStaging(null);
        }
        // A bracket pattern can download extra siblings into staging. Once all
        // requested files are safely promoted, those discarded bytes must not
        // leave a successful selected-file restore showing a partial progress bar.
        if (status == RestoreTerminalStatus.Success) totalBytes = restoredBytes;
        onProgress?.Invoke(new RestoreProgressUpdate(total, restored, Math.Max(totalBytes, restoredBytes), restoredBytes,
            total == 0 ? 0 : (double)restored / total));
        return new RestoreOpResult(total, restored, Math.Max(totalBytes, restoredBytes), restoredBytes, status, error);
    }

    private static async Task<string> AwaitScheduledRestoreOperationAsync(
        Task<BackrestScheduledOperationResponse> submission, Action rejectedBeforeRun)
    {
        // Restore acknowledges scheduling, not completion. Its returned id is
        // the authority for cancellation and polling; a similar log entry must
        // never substitute for a delayed or missing acknowledgement.
        var ownershipDeadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            if (submission.IsCompletedSuccessfully)
            {
                if (IsOperationId(submission.Result.OperationId)) return submission.Result.OperationId;
                throw new IOException("The backup engine did not identify the scheduled restore operation.");
            }
            if (submission.IsFaulted || submission.IsCanceled)
            {
                // A structured rejection is issued before scheduling in the
                // pinned handler. Transport failures cannot establish that.
                if (submission.Exception?.GetBaseException() is BackrestRpcException) rejectedBeforeRun();
                await submission.ConfigureAwait(false);
            }
            if (DateTime.UtcNow >= ownershipDeadline)
                throw new IOException("The backup engine could not confirm ownership of the restore operation.");
            // Caller cancellation does not cancel the HTTP request. Obtain
            // the id first, then the caller cancels that exact operation.
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    internal async Task CaptureOperationDiagnosticsAsync(BackrestClient client, BackrestOperation operation,
        Action<string>? lineSink)
    {
        void Emit(string line) { RaiseLineWritten(line); lineSink?.Invoke(line); }
        // Backrest retains the original Restic errors as structured records.
        // Keep these even when fetching the longer operation log fails.
        if (operation.OperationBackup is { ValueKind: System.Text.Json.JsonValueKind.Object } backup
            && backup.TryGetProperty("errors", out var errors)
            && errors.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray()) Emit("[restic] " + error.GetRawText());
        }
        if (string.IsNullOrWhiteSpace(operation.Logref)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var decoder = System.Text.Encoding.UTF8.GetDecoder();
            var pending = new System.Text.StringBuilder();
            long bytes = 0;
            await foreach (var chunk in client.StreamAsync<GetLogsRequest, RestoreLogChunk>(
                "/v1.Backrest/GetLogs", new GetLogsRequest { Ref = operation.Logref }, timeout.Token).ConfigureAwait(false))
            {
                bytes += chunk.Value.Length;
                if (bytes > 8 * 1024 * 1024) { Emit("[backrest] Operation log exceeds 8 MiB; the full log remains in Backrest."); break; }
                var chars = new char[System.Text.Encoding.UTF8.GetMaxCharCount(chunk.Value.Length)];
                var count = decoder.GetChars(chunk.Value, 0, chunk.Value.Length, chars, 0, false);
                pending.Append(chars, 0, count);
                var text = pending.ToString();
                var end = text.LastIndexOf('\n');
                if (end < 0) continue;
                foreach (var line in text[..(end + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries)) Emit(line.TrimEnd('\r'));
                pending.Clear(); pending.Append(text[(end + 1)..]);
            }
            if (pending.Length > 0) Emit(pending.ToString());
        }
        catch (Exception ex)
        {
            // Log availability must not turn a completed backup into a retry.
            _log.Warning(ex, "Could not read Backrest operation log {Id}", operation.Id);
            Emit("[backrest] The full operation log could not be retrieved. Structured file errors above are retained.");
        }
    }

    // GetLogs is a protobuf BytesValue: its JSON payload contains base64 bytes.
    private sealed class RestoreLogChunk
    {
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public byte[] Value { get; set; } = Array.Empty<byte>();
    }

    internal static ProcessStartInfo BuildStreamingRestoreStartInfo(string binary, BackrestRepo repo,
        string snapshot, string target, string includeFile, int bandwidthLimitKBps)
    {
        var arguments = new List<string> { "restore", snapshot, "--target", target, "--json", "--include-file", includeFile };
        if (bandwidthLimitKBps > 0)
            arguments.AddRange(["--limit-download", bandwidthLimitKBps.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        var start = BuildResticStartInfo(binary, repo, [.. arguments]);
        // Direct pipes are drained continuously; unlike Backrest's generic
        // command log they do not retain every progress message forever.
        start.Environment["RESTIC_PROGRESS_FPS"] = "2";
        return start;
    }

    internal static bool TryParseRestoreProgress(string line, out RestoreProgressUpdate progress)
    {
        progress = default!;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("message_type", out var type) ||
                type.ValueKind != System.Text.Json.JsonValueKind.String ||
                type.GetString() is not ("status" or "summary")) return false;
            long Read(string name) => root.TryGetProperty(name, out var p) &&
                p.ValueKind == System.Text.Json.JsonValueKind.Number && p.TryGetInt64(out var n) ? Math.Max(0, n) : 0;
            var total = Read("total_files");
            var restored = Read("files_restored") + Read("files_skipped");
            var bytes = Read("bytes_restored") + Read("bytes_skipped");
            var fraction = root.TryGetProperty("percent_done", out var done) &&
                done.ValueKind == System.Text.Json.JsonValueKind.Number && done.TryGetDouble(out var value) && double.IsFinite(value)
                ? value : total == 0 ? 0 : (double)restored / total;
            progress = new RestoreProgressUpdate((int)Math.Min(int.MaxValue, total), (int)Math.Min(int.MaxValue, restored),
                Read("total_bytes"), bytes, Math.Clamp(fraction, 0, 1));
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
        => EnumerateRestoreDirectories(root).Count();

    private static IEnumerable<string> EnumerateRestoreDirectories(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var directory in Directory.EnumerateDirectories(pending.Pop()))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                yield return directory;
                pending.Push(directory);
            }
        }
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
        ValidateRestoreTarget(root);
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

    internal static void ValidateRestoreTarget(string target)
    {
        // Check the target AND every existing ancestor before creating staging.
        // Checking only descendants misses a junction chosen as the target.
        for (var path = Path.GetFullPath(target); path is not null; path = Path.GetDirectoryName(path))
            if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Cannot restore through the linked folder {path}. Choose an ordinary folder as the destination.");
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

    internal static void DeleteRestoreStaging(string root, CancellationToken ct = default)
    {
        // Directory.Delete does not recurse through reparse points. The root
        // is a fresh GUID directory created by this operation, never a user target.
        // The operation owns this directory. A slow cleanup is visible and
        // cancellable, and cancellation leaves the remaining copies intact.
        try { FileAccessRetry.Run(() =>
        {
#if WINDOWS
            // The restore's folder may be one other accounts can change: a
            // subfolder swapped for a link mid-delete must not lead the
            // deletion elsewhere (U152).
            Platform.Windows.SafeTreeDelete.Delete(root);
#else
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
#endif
        }, root, FileWaitReason.Cleanup, ct, directoryCleanup: true); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"Cleanup was stopped. Remaining temporary files were kept at {root}.", ex);
        }
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
    /// Go filepath.Match accepts <c>[[]</c> for a literal opening bracket,
    /// including on Windows where backslash escaping is disabled. A closing
    /// bracket outside a character class is already literal; <c>[]]</c> would
    /// be an invalid empty class. This keeps a picked bracket name exact and
    /// avoids downloading similarly named files into the temporary folder.
    /// The final staging allowlist remains an independent protection.
    /// </summary>
    internal static string EscapeBracketsForResticPattern(string rel) => rel.Replace("[", "[[]", StringComparison.Ordinal);

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
        await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        var before = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
        // Backups run restic directly (U175), so Backrest may not know this
        // destination or plan yet (a backup made since, a new computer): it
        // learns of them when it answers that it does not, then lists again.
        var registered = false;
        var reapplied = false;
        var lockedRetries = 0;

        // ListSnapshots invokes restic directly via Backrest. Backrest's
        // auto-scheduled IndexSnapshots task, after a repository is added,
        // holds restic's exclusive repo lock for a few seconds; a
        // ListSnapshots that races it gets "exit status 11" (repo already
        // locked). Retry with WaitForRepoIdle in between so the caller
        // sees a clean answer instead of a transient failure.
        ResticSnapshotList resp;
        while (true)
        {
            try
            {
                resp = await client.CallAsync<ListSnapshotsRequest, ResticSnapshotList>(
                    "/v1.Backrest/ListSnapshots",
                    new ListSnapshotsRequest { RepoId = repoId, PlanId = planId },
                    ct).ConfigureAwait(false);
                break;
            }
            catch (BackrestRpcException ex) when (lockedRetries < 2 && IsRepoLockedExit11(ex))
            {
                lockedRetries++;
                _log.Information(
                    "ListSnapshots hit restic exit-11 (repo locked) on attempt {Attempt} — waiting for repo idle before retry",
                    lockedRetries);
                await WaitForRepoIdleAsync(client, repoId, ct).ConfigureAwait(false);
            }
            catch (BackrestRpcException ex) when (!registered && IsMissingPlanOrRepo(ex))
            {
                _log.Information("Backrest does not know plan {PlanId} yet; adding it before listing again", planId);
                await EnsurePlanAndRepoAsync(backup, sourcePath, destination, storageName, ct, checkConnection: false).ConfigureAwait(false);
                before = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
                    "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
                registered = true;
            }
            catch (BackrestRpcException ex) when (registered && !reapplied && IsMissingPlanOrRepo(ex))
            {
                // Backrest 1.14.1 can save a new plan that it has not loaded
                // (U048): writing the same settings again makes it load them.
                // The refused request started no work; retried once.
                _log.Warning("Backrest did not find plan {PlanId} it had saved; applying its settings again", planId);
                reapplied = true;
                await ReapplyConfigAsync(client, ct).ConfigureAwait(false);
            }
        }

        // Restic snapshot ids are 64-char hex; the GUI surfaces a short
        // "revision number" instead. Sort newest-first (by unix_time_ms)
        // for display only. The immutable snapshot ID carries an existing
        // selection across retention changes; positions are not stable IDs.
        var sorted = resp.Snapshots.OrderByDescending(s => s.UnixTimeMs).ToList();
        var after = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
        var repo = after.Repos.FirstOrDefault(r => r.Id == repoId);
        if (repo is not null && IsValidBackrestGuid(repo.Guid) && before.Modno == after.Modno)
        {
            // Bound memory independently of the number of snapshots in a
            // repository. Eviction only causes a fresh membership lookup.
            if (_snapshotIdentities.Count >= 4096) _snapshotIdentities.Clear();
            foreach (var key in _snapshotIdentities.Keys.Where(k => k.RepoId == repoId && k.PlanId == planId))
                _snapshotIdentities.TryRemove(key, out _);
            foreach (var snapshot in sorted.Take(128).Where(s => IsValidBackrestGuid(s.Id)))
                _snapshotIdentities.TryAdd(new(repoId, planId, repo.Guid, repo.Uri, after.Modno, snapshot.Id), 0);
            lock (_snapshotTreeGate)
                foreach (var key in _snapshotTrees.Keys.Where(k => k.RepoId == repoId && k.PlanId == planId &&
                    !_snapshotIdentities.ContainsKey(k)).ToArray())
                { _cachedTreeNodeCount -= _snapshotTrees[key].Count; _snapshotTrees.Remove(key); }
        }
        return sorted;
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
        int revision, CancellationToken ct, string? expectedSnapshotId = null)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        var planId = MakePlanId(backup, sourcePath, destination, storageName);
        var current = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
        var knownRepo = current.Repos.FirstOrDefault(r => r.Id == repoId);
        if (expectedSnapshotId is not null && knownRepo is not null &&
            _snapshotIdentities.ContainsKey(new(repoId, planId, knownRepo.Guid, knownRepo.Uri, current.Modno, expectedSnapshotId)))
            return (expectedSnapshotId, knownRepo.Guid);

        var sorted = await ListSnapshotsSortedAsync(backup, sourcePath, destination, storageName, ct)
            .ConfigureAwait(false);
        var pickedId = expectedSnapshotId is null ? PickSnapshotIdForRevision(sorted, revision) :
            sorted.Any(s => s.Id == expectedSnapshotId) ? expectedSnapshotId :
            throw new InvalidOperationException("The selected snapshot is no longer available. Reopen Restore and choose a snapshot; no other version was substituted.");

        // Operation queries still use the canonical repository guid, while
        // Backrest 1.14.1's file-listing RPC takes its configured repo id.
        // Keep the guid for restore ownership and credential identity checks.
        _log.Debug("[resolve] GetConfig before to look up guid for {RepoId}", repoId);
        var cfg = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
        _log.Debug("[resolve] GetConfig done, repos={Count}", cfg.Repos.Count);
        var repo = cfg.Repos.FirstOrDefault(r => r.Id == repoId);
        if (repo is null || string.IsNullOrEmpty(repo.Guid))
            throw new EngineConfigOutOfSyncException(
                "The backup engine could not find this destination's registration. " +
                "Quit and reopen Backspace, then try Restore again. If it still fails, save the run details. " +
                "Keep the destination and your Backspace.config folder intact.");
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
        BackrestClient client, string repoId, string snapshotId, string rootPath,
        List<RevisionFile> collected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AppLogger.For<ResticBackrestEngine>().Debug(
            "[walk] ListSnapshotFiles before repo={RepoId} snap={Snap} path={Path}",
            repoId, snapshotId, rootPath);
        var resp = await client.CallAsync<ListSnapshotFilesRequest, ListSnapshotFilesResponse>(
            "/v1.Backrest/ListSnapshotFiles",
            new ListSnapshotFilesRequest { RepoId = repoId, SnapshotId = snapshotId, Path = rootPath },
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
                await WalkSnapshotTreeAsync(client, repoId, snapshotId, entry.Path, collected, ct)
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
        if (!IsOperationId(opIdString))
            throw new InvalidOperationException($"The backup engine returned an invalid operation id: '{opIdString}'");
        // Lifetime is owned by the caller, not a five-minute wall clock. Large
        // restores may legitimately run for hours. RPC calls remain cancellable.
        string lastStatus = "";
        DateTime? goneSince = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var response = await client.CallAsync<GetOperationsRequest, BackrestOperationList>(
                "/v1.Backrest/GetOperations",
                new GetOperationsRequest { Selector = new OpSelector { Ids = new[] { opIdString } } }, ct).ConfigureAwait(false);
            var operation = response.Operations?.FirstOrDefault(o => o.Id == opIdString);
            // A queued operation deleted by an engine queue reset never ends (U093).
            if (Dropped(operation is null, ref goneSince, DateTime.UtcNow, neverSeen: false))
                throw new EngineTaskDroppedException();
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

    public async Task EnsureInitializedAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        // Updating local engine settings must not contact a cloud provider just
        // to reopen already-cached immutable metadata. Actual reads still run
        // their connection check, as does adding a new cloud repository.
        await EnsurePlanAndRepoAsync(backup, sourcePath, destination, storageName, ct,
            checkConnection: false).ConfigureAwait(false);
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
            return (1, "A required backup component is missing. Download the full Backspace release archive " +
                       "and extract its files together. Keep your existing Backspace.config folder.");

        using var maintenance = await ServiceLocator.Maintenance.AcquireAsync("Changing destination password", ct: ct).ConfigureAwait(false);
        using var change = SnapshotReaders.TryBeginChange(destination, storageName, out var inUse);
        if (change is null) return (1, SnapshotReaders.Refusal("Changing the password", inUse));
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
        BundledEngineEnvironment.IncludeRclone(psi);

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
        MarkUsed();
        await _sidecar.EnsureStartedAsync(ct).ConfigureAwait(false);
        if (_sidecar.Client is null)
            throw new InvalidOperationException("Backspace could not connect to its running backup engine. Quit and reopen Backspace, then try again.");
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
        Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct,
        bool checkConnection = true)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        var planId = MakePlanId(backup, sourcePath, destination, storageName);
        // Before every use of the plan: restic reads it when it starts.
        WriteExcludeFile(planId, backup.FiltersText, ct);

        await new RcloneConfigBuilder(_secrets).EnsureDriveIdentityAsync(destination, ct, ServiceLocator.Config).ConfigureAwait(false);
        if (checkConnection)
            await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        await _ensureConfigGate.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsurePlanAndRepoCoreAsync(backup, sourcePath, destination, storageName, client, repoId, planId, ct, checkConnection).ConfigureAwait(false); }
        finally { _ensureConfigGate.Release(); }
    }

    /// <summary>Writes Backrest's current settings back unchanged, which makes
    /// it load them again (see the "plan not found" recovery above).</summary>
    private async Task ReapplyConfigAsync(BackrestClient client, CancellationToken ct)
    {
        await _ensureConfigGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cfg = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
                "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), ct).ConfigureAwait(false);
            await client.CallAsync<BackrestConfig, BackrestConfig>("/v1.Backrest/SetConfig", cfg, ct).ConfigureAwait(false);
        }
        finally { _ensureConfigGate.Release(); }
    }

    private async Task EnsurePlanAndRepoCoreAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        BackrestClient client, string repoId, string planId, CancellationToken ct, bool connectionChecked)
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

        // Backups run restic directly (U175): Backrest can meet a repository
        // this computer's backups have used for a long time. It is added with
        // restic's own id for it (KnownRepositories), the guid AddRepo would
        // have read, without running restic: AddRepo runs cat config, then
        // init, so a password that no longer opens the repository would read
        // as another program's repository (U045) instead of a wrong password.
        // Only at the address where restic gave that id: a destination moved
        // since then is added the usual way, which reads the id there.
        BackrestRepo? knownRepo = null;
        if (existingRepo is null && !needsReAdd)
        {
            var stanza = BuildRepoFromDestination(repoId, destination, backup);
            if (KnownRepositories.ResticId(repoId, stanza.Uri, ct) is { } knownGuid)
            {
                stanza.Guid = knownGuid;
                knownRepo = stanza;
                _log.Information("EnsurePlanAndRepo: adding repo {RepoId}, which this computer's backups use, with restic's id {Guid}", repoId, knownGuid);
            }
        }
        if (existingRepo is null && knownRepo is null)
        {
            if (!connectionChecked)
                await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
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
                        "The backup engine did not confirm the destination setup. Quit and reopen Backspace, then try again. " +
                        "If it happens again, save the run details for troubleshooting. Keep your Backspace.config folder intact.");
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
            var addingKnownRepo = liveRepo is null && knownRepo is not null;
            if (addingKnownRepo) cfg.Repos.Add(liveRepo = knownRepo!);
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

            var wantedPlan = BuildPlanFromBackup(planId, repoId, backup, sourcePath);
            // Backspace schedules cleanup explicitly. Backrest otherwise runs
            // its own retention task after every backup, regardless of our
            // separate cleanup schedule, and duplicates inline cleanup.
            wantedPlan.Retention = new() { PolicyKeepAll = true };
            var planChanged = existingPlan is not null && ApplyPlanSettings(existingPlan, wantedPlan);
            if (existingPlan is not null && !addingKnownRepo && !planChanged && flagsMatch && passwordMatch && uriMatch && envMatch && !liveRepo.AutoUnlock)
                return; // nothing to do

            // Recover only after a confirmed lock failure. An unconditional
            // unlock opens the repository again before every healthy task.
            liveRepo.AutoUnlock = false;
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
                cfg.Plans.Add(wantedPlan);

            try
            {
                var written = await client.CallAsync<BackrestConfig, BackrestConfig>(
                    "/v1.Backrest/SetConfig", cfg, ct).ConfigureAwait(false);
                var persistedRepo = written.Repos.FirstOrDefault(r => r.Id == repoId);
                var persistedPlan = written.Plans.FirstOrDefault(p => p.Id == planId);
                if (persistedRepo is null || persistedPlan is null)
                {
                    throw new EngineConfigOutOfSyncException(
                        "The backup engine did not confirm the updated backup settings. " +
                        "Quit and reopen Backspace, then try again. If it happens again, save the run details. " +
                        "Keep your Backspace.config folder intact.");
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
            catch (BackrestRpcException ex) when (OperatingSystem.IsWindows() &&
                attempt + 1 < MaxModnoMismatchRetries && IsRetryableConfigWrite(ex.Message))
            {
                // Windows may briefly deny an atomic replace while another
                // process has the config open. Reread before retrying so a
                // concurrent edit is preserved. Permanent denials still fail.
                _log.Information("Backrest could not replace its config on attempt {Attempt}; rereading before retry", attempt + 1);
                await Task.Delay(100 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException(
            "Backspace could not save the backup settings after several attempts. " +
            "Another process may be changing or holding those files. Wait for it to finish, then try again.");
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

    internal static bool IsRetryableConfigWrite(string message) =>
        (message.Contains("write config file", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("backup config file", StringComparison.OrdinalIgnoreCase)) &&
        message.Contains("replace", StringComparison.OrdinalIgnoreCase) &&
        (message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("used by another process", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("sharing violation", StringComparison.OrdinalIgnoreCase));

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
        if (ex.Endpoint == "/v1.Backrest/AddRepo" && HasInterruptedInitializationError(ex.Message))
            return new EngineRepositoryInitializationException(UnfinishedSetupMessage + ex.Message, ex);
        // U045: the folder already holds a backup, for example one made by
        // another Backspace copy or another program, with a password this
        // backup does not have. Refusing is right; repeating it four times
        // with restic's wording was not.
        if (ex.Endpoint == "/v1.Backrest/AddRepo" && ex.Message.Contains("config file already exists", StringComparison.OrdinalIgnoreCase))
            return new EngineRepositoryInitializationException(AnotherBackupMessage + ex.Message, ex);
        if (IsMissingPlanOrRepo(ex))
        {
            return new EngineConfigOutOfSyncException(
                "The backup engine could not find the backup or destination that Backspace just configured. " +
                "Quit and reopen Backspace, then try again. If it happens again, save the run details for troubleshooting. " +
                "Keep your Backspace.config folder intact. Details: " + ex.Message, ex);
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

    /// <summary>An interrupted repository setup left keys without a
    /// configuration: setup stops, never repeated or replaced.</summary>
    internal const string UnfinishedSetupMessage =
        "This destination contains encryption keys from an unfinished repository setup. " +
        "Backspace has stopped instead of repeating setup or replacing those files. " +
        "Keep the existing folder for recovery and choose a new empty folder for a new backup. Details: ";

    /// <summary>U045: a repository this backup's password does not open.</summary>
    internal const string AnotherBackupMessage =
        "This folder already holds another backup, made by another copy of Backspace or another program. " +
        "Backspace leaves it untouched. Choose an empty folder for this backup, or a new subfolder of this one. Details: ";

    private static bool HasInterruptedInitializationError(string message)
    {
        // Require restic's structured terminal error, not a matching filename or
        // a Backrest log line. Unknown or malformed output retains normal retries.
        foreach (var line in message.Split('\n'))
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("message_type", out var type) && type.ValueKind == JsonValueKind.String &&
                    type.GetString() == "exit_error" &&
                    root.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String &&
                    IsInterruptedInitialization(detail.GetString()))
                    return true;
            }
            catch (JsonException) { }
        }
        return false;
    }

    /// <summary>restic's closing message for an init that found keys without
    /// a configuration (its exit_error message).</summary>
    internal static bool IsInterruptedInitialization(string? exitMessage)
        => exitMessage?.EndsWith(": repository already contains keys", StringComparison.Ordinal) == true;

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
            // An inherited positive FPS override bypasses restic's --quiet.
            // Zero keeps ordinary commands at their default rate. Native and
            // direct restores explicitly enable bounded progress reporting.
            Env = resticEnv.Env.Concat(new[] { "RESTIC_PROGRESS_FPS=0" }).ToList(),
            Flags = repoFlags,
            AutoInitialize = false,
            AutoUnlock = false, // owned tasks recover on a confirmed lock failure
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

    /// <summary>Update fields owned by Backspace without discarding newer daemon fields.</summary>
    internal static bool ApplyPlanSettings(BackrestPlan existing, BackrestPlan wanted)
    {
        var changed = existing.Repo != wanted.Repo || !RepoFlagsEqual(existing.Paths, wanted.Paths)
            || !RepoFlagsEqual(existing.Excludes, wanted.Excludes) || !RepoFlagsEqual(existing.IExcludes, wanted.IExcludes)
            || !RepoFlagsEqual(existing.BackupFlags, wanted.BackupFlags) || existing.SkipIfUnchanged != wanted.SkipIfUnchanged
            || System.Text.Json.JsonSerializer.Serialize(existing.Retention) != System.Text.Json.JsonSerializer.Serialize(wanted.Retention);
        if (!changed) return false;
        existing.Repo = wanted.Repo;
        existing.Paths = wanted.Paths;
        existing.Excludes = wanted.Excludes;
        existing.IExcludes = wanted.IExcludes;
        existing.BackupFlags = wanted.BackupFlags;
        existing.SkipIfUnchanged = wanted.SkipIfUnchanged;
        existing.Retention = wanted.Retention;
        return true;
    }

    /// <summary>The rules as written, each once. restic applies them in order
    /// and the last rule that matches a path decides (a "!" rule includes it
    /// again), so a repeated rule only counts at its last place and its earlier
    /// copies are left out. Distinct() kept the first copy instead, which could
    /// move a rule before a "!" rule and change what is backed up (U133).</summary>
    internal static List<string> WithoutRepeats(IEnumerable<string> rules)
    {
        var all = rules.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>(all.Count);
        for (var i = all.Count - 1; i >= 0; i--)
            if (seen.Add(all[i])) kept.Add(all[i]);
        kept.Reverse();
        return kept;
    }

    /// <summary>Where a plan's exclude file lives: the engine's per-computer
    /// work folder (<see cref="EngineWorkPaths"/>), beside its other files.</summary>
    internal static string ExcludeFilePath(string planId) => Path.Combine(EngineWorkPaths.Root, "filters", planId + ".txt");

    /// <summary>Writes the backup's Filters text, unchanged, as the plan's
    /// exclude file, which restic reads at the start of each run. Unchanged
    /// contents are not rewritten.</summary>
    internal static string WriteExcludeFile(string planId, string? filtersText, CancellationToken ct = default)
    {
        var path = ExcludeFilePath(planId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new System.Text.UTF8Encoding(false).GetBytes(filtersText ?? "");
        if (File.Exists(path) && FileAccessRetry.Get(() => File.ReadAllBytes(path), path, ct: ct).AsSpan().SequenceEqual(bytes)) return path;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        try { FileAccessRetry.Run(() => File.Move(temporary, path, overwrite: true), path, ct: ct); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    internal static BackrestPlan BuildPlanFromBackup(string planId, string repoId, Backup backup, string sourcePath)
    {
        var paths = new List<string> { sourcePath };
        // The Filters text is restic's exclude file, handed over unchanged
        // (U151): --iexclude-file on Windows, --exclude-file elsewhere. Not
        // as --iexclude arguments: restic expands $NAME in an exclude file
        // (a literal $ is written $$) but not in an argument, so the same
        // text would mean something else. Backrest splits each flag like a
        // shell, so the path is quoted.
        var backupFlags = BuildBackupFlags(backup);
        backupFlags.Add(FilterTranslator.ExcludeFileFlag);
        backupFlags.Add(QuoteArg(ExcludeFilePath(planId)));
        return new BackrestPlan
        {
            Id = planId,
            Repo = repoId,
            Paths = paths,
            Excludes = new(),
            IExcludes = new(),
            BackupFlags = backupFlags,
            // Backrest needs a retention policy on every plan, and runs its
            // own forget with it after each backup. Keep-all makes that a
            // no-op: Backspace applies the backup's KeepPolicy itself, with
            // the exact restic options Backrest's policy can't express
            // (keep-within-daily and friends; U172). See RunRetentionAsync.
            Retention = new BackrestRetentionPolicy { PolicyKeepAll = true },
            SkipIfUnchanged = false,
        };
    }

    /// <summary>Restic-side <c>backup</c> command flags derived from
    /// the user's per-backup toggles. Windows-only flags are guarded
    /// behind <see cref="Services.Platform.PlatformInfo.IsWindows"/>
    /// so a portable Backspace install running on macOS/Linux doesn't
    /// pass them through and trip restic's "unknown flag" error.
    /// </summary>
    internal static List<string> BuildBackupFlags(Backup backup)
    {
        // Restic's separate counting walk only feeds its totals: it looks up
        // every file and folder a second time and cost as much CPU as the
        // backup walk on C: (252 s against 110 s without it, U145, 5 November
        // 2026). Backspace lists the source itself in seconds and shows that
        // total instead (BackupLiveProgress). The snapshot is the same.
        var flags = new List<string> { "--no-scan" };
        if (Services.Platform.PlatformInfo.IsWindows)
        {
            if (backup.UseVss)
            {
                flags.Add("--use-fs-snapshot");
                // Windows makes one snapshot at a time, and restic retries
                // while another is being made, until this deadline (2 minutes
                // by default). With several backups starting together (U156)
                // a backup must wait its turn, never fall back to no snapshot.
                flags.Add("-o");
                flags.Add("vss.timeout=" + VssSnapshotGate.ResticTimeout);
            }
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
