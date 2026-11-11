using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Backups and what follows them (the cleanup after a backup, the list of
/// changed files) run restic directly through <see cref="ResticRunner"/>
/// (U175, step 1): a backup never starts Backrest, in the app or in the
/// background service. Checks are entries of their own since U179, run after
/// the backup or on their schedule through <see cref="RunCheckOnlyAsync"/>;
/// they, restore, browsing, mounting, the stand-alone cleanup and the erase
/// tools still go through Backrest until the later steps.
///
/// <para>Everything Backrest added to a backup is kept, so repositories,
/// snapshots, their labels and the run history carry on unchanged:
/// <c>--exclude-caches</c>, the labels <c>plan:&lt;id&gt;</c> and
/// <c>created-by:backspace</c>, <c>--parent</c>, the repository's options
/// (bandwidth limits, no password) and <c>-o sftp.args=-oBatchMode=yes</c>, in
/// Backrest's order. Run summaries keep Backrest's words
/// (<c>Backup result: STATUS_…</c>), which history and
/// <see cref="WasStoppedOnRequest"/> read.</para>
/// </summary>
public sealed partial class ResticBackrestEngine
{
    /// <summary>The instance Backrest was set up with (BackrestSidecar's seed
    /// configuration): every snapshot Backspace made carries
    /// <c>created-by:backspace</c>.</summary>
    internal const string InstanceTag = "backspace";

    /// <summary>Raised after each restic command run directly, with what it
    /// returned (tests count and inspect them).</summary>
    internal event Action<ResticCommand, ResticResult>? ResticCommandFinished;

    public async Task<RunRecord> BackupPruneCheckAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct, Action<string>? lineSink = null)
    {
        var restic = await ResticBinaryAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        var planId = MakePlanId(backup, sourcePath, destination, storageName);
        // Before every use of the plan: restic reads it when it starts.
        WriteExcludeFile(planId, backup.FiltersText, ct);
        await new RcloneConfigBuilder(_secrets).EnsureDriveIdentityAsync(destination, ct, ServiceLocator.Config).ConfigureAwait(false);
        await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        var repository = BuildResticRepository(destination, backup);
        await EnsureRepositoryAsync(restic, repoId, repository, destination, ct, lineSink).ConfigureAwait(false);
        // The repository exists now: another backup to it may prepare (U156).
        RepositoryAccess.SetupDone();
        var started = DateTime.UtcNow;
        var previousUnfinished = MaintenanceRecords.PreviousBackupUnfinished(planId, ct);
        var previous = await ChooseParentAsync(restic, repoId, repository, planId, sourcePath, previousUnfinished, ct, lineSink).ConfigureAwait(false);
        foreach (var setup in SetupLines(backup, sourcePath, destination, previous)) Announce(setup, lineSink);
        // What this backup will read, compared with the last one's listing (U144).
        var changes = ChangePlanner.Start(ChangePlanStore.ForOwner(), planId, sourcePath, SourceSizeRules.For(backup), previous?.Id, ct);
        var resticProcess = 0;
        var liveProgress = new BackupLiveProgress(ExpectedSize(backup, sourcePath, ct),
            SentBytesMeter(destination, () => Volatile.Read(ref resticProcess)), changes);
        // One Windows snapshot made at a time across Backspace (U156): held
        // until restic reads files, or this backup waits for its turn here.
        using var snapshotTurn = backup.UseVss && Platform.PlatformInfo.IsWindows
            ? await VssSnapshotGate.AcquireAsync(backup.Name, reason =>
                { if (reason.Length > 0) RepositoryAccess.Waiting(reason); else RepositoryAccess.NotWaiting(); }, ct).ConfigureAwait(false)
            : null;
        liveProgress.Reading = () => snapshotTurn?.Dispose();
        liveProgress.HeldBack = () => snapshotTurn?.Dispose();
        var (terminalStatus, details, summary) = await RunDirectBackupAsync(restic, repoId, repository, backup, sourcePath, planId,
            previous?.Id, liveProgress, pid => Volatile.Write(ref resticProcess, pid), ct, lineSink).ConfigureAwait(false);
        snapshotTurn?.Dispose();
        if (summary is not null) Announce(BackupLiveProgress.RecapOf(summary), lineSink);
        var snapshotId = summary?.SnapshotId;
        var status = StatusFromBackrestTerminal(terminalStatus);
        var saved = status is BackupRunStatus.Success or BackupRunStatus.Warning && IsValidBackrestGuid(snapshotId);
        // The next backup's parent, and proof this one finished (U175). A
        // completed backup that made no snapshot left nothing behind either;
        // the next one then lists the plan's snapshots for its parent.
        if (saved || summary is not null && status is BackupRunStatus.Success or BackupRunStatus.Warning)
            MaintenanceRecords.BackupFinished(planId, DateTime.UtcNow, saved ? snapshotId : null, ct);
        // Backspace's restic skips online-only files quietly and does not
        // count them (U168): this run's own listing of the source does. ARM64
        // builds run official restic, which reports Dropbox's as errors.
        long cloudOnlySkipped = 0;
        if (backup.ExcludeCloudFiles && Platform.PlatformInfo.IsWindows
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.Arm64
            && terminalStatus is BackrestStatus.Success or BackrestStatus.Warning
            && await changes.CloudOnlySkippedAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false) is { } skippedCount
            && CloudFileBehavior.SkippedPhrase(skippedCount) is { } skippedPhrase)
        {
            cloudOnlySkipped = skippedCount;
            Announce($"[backspace] {skippedPhrase}: kept only in the cloud, not downloaded and not in this backup", lineSink);
        }
        // Before retention can remove the previous snapshot.
        if (saved && VerboseLogFilter.IsVerboseOn() && !ct.IsCancellationRequested)
            await WriteChangedFilesAsync(restic, repository, previous?.Id, snapshotId!, ct, lineSink).ConfigureAwait(false);
        if (saved) await changes.SaveAsync(snapshotId!, liveProgress.MeasuredPace).ConfigureAwait(false);
        CleanupResult cleanup = default;
        if (status is BackupRunStatus.Success or BackupRunStatus.Warning && !ct.IsCancellationRequested)
        {
            // Restic lets backups share a repository but needs it alone for
            // forget and prune: wait here for the other backups on this
            // destination ("Queued: … before cleanup and checks", U156).
            if (backup.PruneSchedule is null)
                await RepositoryAccess.ExclusiveAsync(ct).ConfigureAwait(false);
            async Task RunCleanup()
            {
                // Never while a snapshot here is mounted or restored (U159):
                // the backup itself only added a snapshot; cleanup catches up
                // after a later backup.
                using var change = SnapshotReaders.TryBeginChange(destination, storageName, out var inUse);
                if (change is null)
                {
                    Announce("[backspace] " + SnapshotReaders.CleanupSkipped(inUse), lineSink);
                    details += " " + SnapshotReaders.CleanupSkipped(inUse);
                    return;
                }
                try
                {
                    Announce(LogSection.Line(backup.Name, "cleanup", destination.Name), lineSink);
                    var prune = NeedsPrune(previousUnfinished, MaintenanceRecords.LastFullPrune(repoId, ct), DateTime.UtcNow);
                    (var result, cleanup) = await RunDirectRetentionAsync(restic, repoId, repository, planId, backup, ct, lineSink, prune).ConfigureAwait(false);
                    if (result.Status is BackrestStatus.Success || cleanup != default) Announce(cleanup.Recap(), lineSink);
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
            // Checks after each backup are entries of their own (U179): the
            // orchestrator runs them once the backup has ended.
            if (backup.PruneSchedule is null)
                await RunCleanup().ConfigureAwait(false);
        }
        var stats = summary is null ? ((int New, int Changed, long Added)?)null : BackupLiveProgress.StatsOf(summary);
        return new RunRecord
        {
            BackupId = backup.Id, BackupName = backup.Name, Kind = RunKind.Backup,
            StartedUtc = started, EndedUtc = DateTime.UtcNow, Status = status,
            FilesNew = stats?.New ?? 0, FilesModified = stats?.Changed ?? 0, BytesUploaded = stats?.Added ?? 0,
            SnapshotsRemoved = cleanup.SnapshotsRemoved, BytesFreed = cleanup.BytesFreed,
            CloudOnlySkipped = cloudOnlySkipped,
            Summary = $"Backup result: {terminalStatus}. {details}".Trim(),
            Errors = status == BackupRunStatus.Failed
                ? new() { string.IsNullOrWhiteSpace(details) ? $"Backup result: {terminalStatus}." : details }
                : new(),
        };
    }

    /// <summary>restic's backup itself, with its live progress, and what it
    /// came to in Backrest's words (status, display message).</summary>
    private async Task<(string Status, string Details, ResticBackupSummary? Summary)> RunDirectBackupAsync(string restic, string repoId,
        ResticRepository repository, Backup backup, string sourcePath, string planId, string? parentId, BackupLiveProgress liveProgress,
        Action<int> onStarted, CancellationToken ct, Action<string>? lineSink)
    {
        var command = new ResticCommand(restic, repository, BackupArguments(sourcePath), BackupTrailing(backup, planId, parentId)) { ProgressFps = 2 };
        ResticBackupSummary? summary = null;
        BackupLiveProgress.Sample? lastSample = null;
        // A backup whose counts (files and bytes found, files and bytes done)
        // stay frozen while restic runs is stuck, for example on a network that
        // stopped answering (U111). Time waiting for its turn doesn't count;
        // restic reports its progress twice a second.
        var movedAt = DateTime.UtcNow.Ticks;
        var running = 0;
        EngineStalledException? stall = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watchEnd = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            var step = TimeSpan.FromMilliseconds(Math.Clamp(ProgressStallLimit.TotalMilliseconds / 4, 25, 5000));
            while (true)
            {
                try { await Task.Delay(step, watchEnd.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (ct.IsCancellationRequested || Volatile.Read(ref running) == 0) continue;
                if (DateTime.UtcNow - new DateTime(Interlocked.Read(ref movedAt), DateTimeKind.Utc) < ProgressStallLimit) continue;
                stall = Stalled(lineSink, $"The backup made no progress for {Minutes(ProgressStallLimit)}, so Backspace stopped it.");
                stop.Cancel();
                return;
            }
        });
        ct.ThrowIfCancellationRequested();
        MaintenanceRecords.BackupStarted(planId, DateTime.UtcNow, ct);
        ResticResult result;
        try
        {
            result = await RunResticAsync(repoId, command, lineSink, stop.Token, onLine: line =>
            {
                if (line.StandardError) return;
                if (line.MessageType == "status")
                {
                    var sample = BackupLiveProgress.FromResticStatus(line.Json);
                    if (sample != lastSample) { lastSample = sample; Interlocked.Exchange(ref movedAt, DateTime.UtcNow.Ticks); }
                    if (liveProgress.NextStatus(sample, DateTime.UtcNow) is { } progressLine) Announce(progressLine, lineSink);
                }
                else if (line.MessageType == "summary") summary = BackupLiveProgress.FromResticSummary(line.Json);
            }, onStarted: pid =>
            {
                Interlocked.Exchange(ref movedAt, DateTime.UtcNow.Ticks);
                Volatile.Write(ref running, 1);
                onStarted(pid);
            }, heldBack: () => liveProgress.HeldBack?.Invoke()).ConfigureAwait(false);
        }
        finally
        {
            watchEnd.Cancel();
            await watch.ConfigureAwait(false);
        }
        if (stall is not null && !ct.IsCancellationRequested) throw stall;
        var (status, details) = BackupOutcome(result, summary);
        return (status, details, summary);
    }

    /// <summary>What a backup's restic run comes to, in Backrest's words: exit
    /// code 0 with a summary is a success, 3 with a snapshot a backup with
    /// warnings (some files could not be read), a stop Backspace asked for is
    /// STATUS_USER_CANCELLED (<see cref="WasStoppedOnRequest"/>), anything else
    /// a failure carrying restic's own reason.</summary>
    internal static (string Status, string Details) BackupOutcome(ResticResult result, ResticBackupSummary? summary)
    {
        // A backup that completed as the stop arrived keeps its result.
        var completed = summary?.SnapshotId is not null && result.ExitCode is 0 or 3;
        if (result.Cancelled && !completed) return (BackrestStatus.UserCancelled, "Stopped on request.");
        return result.ExitCode switch
        {
            0 when summary is not null => (BackrestStatus.Success,
                summary.SnapshotId is null ? "No snapshot added, possibly due to no changes in the source data." : ""),
            3 when summary?.SnapshotId is not null => (BackrestStatus.Warning, "Partial backup, some files may not have been read completely."),
            0 or 3 => (BackrestStatus.Error, "restic ended without reporting a completed backup, so no snapshot is confirmed. " + result.ErrorText),
            130 => (BackrestStatus.SystemCancelled, "restic was stopped before it finished, by Windows or another program. " + result.ErrorText),
            _ => (BackrestStatus.Error, result.ErrorText),
        };
    }

    internal static IReadOnlyList<string> BackupArguments(string sourcePath) => ["backup", "--json", sourcePath];

    /// <summary>The options after the repository's: Backrest's labels and
    /// parent, then the backup's own options and its exclude file (U151).</summary>
    internal static List<string> BackupTrailing(Backup backup, string planId, string? parentId)
    {
        var trailing = new List<string> { "--exclude-caches", "--tag", "plan:" + planId, "--tag", "created-by:" + InstanceTag };
        if (parentId is not null) { trailing.Add("--parent"); trailing.Add(parentId); }
        trailing.AddRange(BuildBackupFlags(backup));
        trailing.Add(FilterTranslator.ExcludeFileFlag);
        trailing.Add(ExcludeFilePath(planId));
        return trailing;
    }

    /// <summary>The repository description restic gets: built from the
    /// destination and the vault for each operation, as Backrest's repository
    /// settings were (the address, its environment, the password, and the
    /// options for every command).</summary>
    internal ResticRepository BuildResticRepository(Destination destination, Backup? backup)
    {
        var password = destination.StoragePasswordRef is null ? "" : _secrets.Get(destination.StoragePasswordRef);
        var rclonePath = RcloneConfigBuilder.DefaultPath;
        if (RcloneConfigBuilder.NeedsRcloneRemote(destination))
            rclonePath = new RcloneConfigBuilder(_secrets).MaterialiseDestination(destination, ServiceLocator.Config?.Current?.RcloneOAuthApps);
        var resticEnv = ResticUriResolver.Resolve(destination, _secrets, rclonePath);
        return new ResticRepository(resticEnv.Uri, password, resticEnv.Env.ToList(), BuildRepoFlagsForBackup(backup, destination));
    }

    /// <summary>The restic to run, after making sure this build's engines are
    /// here (releases fetch them on first use, and again on a new computer).</summary>
    private async Task<string> ResticBinaryAsync(CancellationToken ct)
    {
        await EngineInstallation.EnsureAsync(null, ct).ConfigureAwait(false);
        var binary = _sidecar.ResticBinaryPath;
        if (!File.Exists(binary))
            throw new EngineBinaryMissingException("A required backup component is missing. Restart Backspace while online to download it again. " +
                "Your Backspace.config folder is not affected.", binary);
        return binary;
    }

    /// <summary>Runs a restic command for this repository. One that could not
    /// lock the repository (exit code 11) gets one <c>restic unlock</c>, which
    /// removes stale locks only (never <c>--remove-all</c>), and one more try;
    /// a live lock survives and the second refusal stands.</summary>
    private async Task<ResticResult> RunResticAsync(string repoId, ResticCommand command, Action<string>? lineSink, CancellationToken ct,
        Action<ResticLine>? onLine = null, Action<int>? onStarted = null, bool gated = true, bool lockRecovery = true, Action? heldBack = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await RunResticOnceAsync(repoId, command, lineSink, ct, onLine, onStarted, gated, heldBack).ConfigureAwait(false);
            if (!lockRecovery || attempt != 0 || result.Cancelled || result.ExitCode != 11) return result;
            var unlock = await RunResticOnceAsync(repoId, new ResticCommand(command.Binary, command.Repository, ["unlock"]) { OutputToTranscript = true },
                lineSink, ct, null, null, gated, heldBack).ConfigureAwait(false);
            if (!unlock.Succeeded) return unlock;
        }
    }

    // This process runs one task per repository (with Backrest's tasks: the
    // same gates): a backup waiting here is not running, and its card says
    // so (U156). Backups to the same destination stay queued in one process,
    // as before (owner's answer to U175's question 6).
    private async Task<ResticResult> RunResticOnceAsync(string repoId, ResticCommand command, Action<string>? lineSink, CancellationToken ct,
        Action<ResticLine>? onLine, Action<int>? onStarted, bool gated, Action? heldBack)
    {
        var gate = gated ? _repoTaskGates.GetOrAdd(repoId, _ => new SemaphoreSlim(1, 1)) : null;
        if (gate is not null && !await gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            heldBack?.Invoke();
            RepositoryAccess.Waiting(RepositoryAccess.EngineRepositoryReason);
            try { await gate.WaitAsync(ct).ConfigureAwait(false); }
            finally { RepositoryAccess.NotWaiting(); }
        }
        try
        {
            var result = await ResticRunner.RunAsync(command, onLine, line => Announce(line, lineSink), ct, onStarted).ConfigureAwait(false);
            try { ResticCommandFinished?.Invoke(command, result); } catch (Exception ex) { _log.Debug(ex, "restic command observer failed"); }
            return result;
        }
        finally { gate?.Release(); }
    }

    /// <summary>The error a restic failure before the backup becomes: a wrong
    /// password is final (no retry with the same password); restic's own words
    /// stay in the message, so a missing repository or an offline network is
    /// classified as before.</summary>
    private static Exception ResticFailure(string what, ResticResult result) => result.ExitCode == 12
        ? new EngineAuthFailedException(what + " " + result.ErrorText)
        : new IOException(what + " " + result.ErrorText);

    // ---- the repository ----

    /// <summary>Makes sure the repository exists, as Backrest's AddRepo did
    /// for a destination it did not know: for a repository this computer has
    /// not used yet, <c>restic cat config</c> (it exists), and only when restic
    /// says there is no repository (exit code 10), <c>restic init</c>. A
    /// repository this computer knows is not asked again (one restic start and
    /// several round trips on a cloud destination); if it is gone, restic's
    /// backup says so, and nothing creates it again in its place.</summary>
    private async Task EnsureRepositoryAsync(string restic, string repoId, ResticRepository repository, Destination destination,
        CancellationToken ct, Action<string>? lineSink)
    {
        if (KnownRepositories.Knows(repoId, ct)) return;
        var config = await RunResticAsync(repoId, new ResticCommand(restic, repository, ["cat", "config"]) { CaptureOutput = 64 * 1024 },
            lineSink, ct, gated: false, lockRecovery: false).ConfigureAwait(false);
        if (config.Cancelled) throw new OperationCanceledException(ct);
        if (config.Succeeded) { KnownRepositories.Remember(repoId, RepositoryId(config.Output), repository.Uri, ct); return; }
        // U045: a repository this backup's password does not open.
        if (config.ExitCode == 12) throw new EngineRepositoryInitializationException(AnotherBackupMessage + config.ErrorText, new IOException(config.ErrorText));
        if (config.ExitCode != 10) throw ResticFailure("The backup engine could not open this destination's repository.", config);
        // One backup at a time prepares a repository (RepositoryAccess's setup
        // gate): two creating one at once each wrote their own key (E64).
        var init = await RunResticAsync(repoId, new ResticCommand(restic, repository, ["init", "--json"]) { CaptureOutput = 64 * 1024 },
            lineSink, ct, gated: false, lockRecovery: false).ConfigureAwait(false);
        if (init.Cancelled) throw new OperationCanceledException(ct);
        if (init.Succeeded)
        {
            KnownRepositories.Remember(repoId, RepositoryId(init.Output), repository.Uri, ct);
            Announce($"[setup] New repository created in «{destination.Name}».", lineSink);
            return;
        }
        if (IsInterruptedInitialization(init.ExitMessage))
            throw new EngineRepositoryInitializationException(UnfinishedSetupMessage + init.ErrorText, new IOException(init.ErrorText));
        if (init.ErrorText.Contains("config file already exists", StringComparison.OrdinalIgnoreCase)
            || init.ErrorText.Contains("already initialized", StringComparison.OrdinalIgnoreCase))
        {
            // Created meanwhile by another program: use it if it opens.
            var again = await RunResticAsync(repoId, new ResticCommand(restic, repository, ["cat", "config"]) { CaptureOutput = 64 * 1024 },
                lineSink, ct, gated: false, lockRecovery: false).ConfigureAwait(false);
            if (again.Cancelled) throw new OperationCanceledException(ct);
            if (again.Succeeded) { KnownRepositories.Remember(repoId, RepositoryId(again.Output), repository.Uri, ct); return; }
            throw new EngineRepositoryInitializationException(AnotherBackupMessage + again.ErrorText, new IOException(again.ErrorText));
        }
        throw ResticFailure("The backup engine could not create a repository at this destination.", init);
    }

    /// <summary>restic's id for a repository, from <c>cat config</c> or
    /// <c>init --json</c>, or null.</summary>
    internal static string? RepositoryId(string output)
    {
        try
        {
            var text = output.Trim();
            if (!text.StartsWith('{')) return null;
            using var json = JsonDocument.Parse(text);
            return json.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // ---- the parent ----

    /// <summary>
    /// The previous snapshot of this plan: restic's <c>--parent</c>, the log's
    /// "Previous backup" line, the change plan and the changed-files list.
    /// The owner's answer to U175's question 4 (10 November 2026): the
    /// snapshot this plan's last finished backup made is remembered
    /// (<see cref="MaintenanceRecords.RememberedSnapshot"/>). restic reads that
    /// one snapshot (no lock, no list of the other snapshots) and it is used
    /// when it is still there, carries this plan's two labels, and holds these
    /// folders from this computer. Otherwise (none remembered, the previous
    /// backup did not finish, restic could not read it, or it is gone or
    /// another's) the plan's snapshots are listed and the latest is taken, as
    /// Backrest did.
    /// </summary>
    internal async Task<(string Id, DateTime When)?> ChooseParentAsync(string restic, string repoId, ResticRepository repository,
        string planId, string sourcePath, bool previousUnfinished, CancellationToken ct, Action<string>? lineSink)
    {
        var remembered = previousUnfinished ? null : MaintenanceRecords.RememberedSnapshot(planId, ct);
        if (remembered is not null)
        {
            var (snapshot, problem) = await ReadRememberedAsync(restic, repoId, repository, planId, sourcePath, remembered, ct, lineSink).ConfigureAwait(false);
            if (snapshot is not null) return snapshot;
            Announce($"[backspace] The previous backup Backspace remembered (snapshot {remembered[..8]}) {problem}: reading the list of this backup's snapshots instead.", lineSink);
        }
        return await LatestPlanSnapshotAsync(restic, repoId, repository, planId, sourcePath, ct, lineSink).ConfigureAwait(false);
    }

    private async Task<((string Id, DateTime When)? Snapshot, string? Problem)> ReadRememberedAsync(string restic, string repoId,
        ResticRepository repository, string planId, string sourcePath, string remembered, CancellationToken ct, Action<string>? lineSink)
    {
        (string Id, DateTime When)? found = null;
        string? problem = null;
        var answered = false;
        var command = new ResticCommand(restic, repository, ["snapshots", "--json", "--no-lock"], [remembered]) { MaximumLine = 4 << 20 };
        var result = await RunResticAsync(repoId, command, lineSink, ct, onLine: line =>
        {
            if (line.StandardError || line.Json.ValueKind != JsonValueKind.Array) return;
            answered = true;
            (found, problem) = CheckRemembered(line.Json, remembered, planId, sourcePath, ThisHost());
        }, gated: false, lockRecovery: false).ConfigureAwait(false);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        // The repository itself refused (wrong password, gone): listing would
        // fail the same way.
        if (result.ExitCode is 10 or 12) throw ResticFailure("The backup engine could not read this destination's repository.", result);
        if (!result.Succeeded) return (null, $"could not be read (restic exit code {result.ExitCode})");
        if (!answered) return (null, "could not be read");
        return (found, problem);
    }

    /// <summary>The host name restic writes into a snapshot.</summary>
    internal static string ThisHost()
    {
        if (Environment.GetEnvironmentVariable("RESTIC_HOST") is { Length: > 0 } host) return host;
        try { return System.Net.Dns.GetHostName(); }
        catch (System.Net.Sockets.SocketException) { return Environment.MachineName; }
    }

    /// <summary>Whether <c>restic snapshots --json &lt;id&gt;</c>'s answer
    /// confirms the remembered snapshot: there, both of this plan's labels,
    /// exactly this source folder, this computer. Gives the snapshot, or why
    /// not. restic answers an id it cannot load (removed, or damaged) with an
    /// empty list and exit code 0, saying why on its error output.</summary>
    internal static ((string Id, DateTime When)? Snapshot, string? Problem) CheckRemembered(JsonElement answer, string id, string planId,
        string sourcePath, string host)
    {
        if (answer.ValueKind != JsonValueKind.Array) return (null, "could not be read");
        foreach (var snapshot in answer.EnumerateArray())
        {
            if (snapshot.ValueKind != JsonValueKind.Object || Text(snapshot, "id") != id) continue;
            var tags = Strings(snapshot, "tags");
            if (!tags.Contains("plan:" + planId, StringComparer.Ordinal) || !tags.Contains("created-by:" + InstanceTag, StringComparer.Ordinal))
                return (null, "belongs to another backup");
            var paths = Strings(snapshot, "paths");
            if (paths.Count != 1 || !SamePath(paths[0], sourcePath)) return (null, "holds other folders");
            if (!string.Equals(Text(snapshot, "hostname"), host, StringComparison.OrdinalIgnoreCase)) return (null, "was made on another computer");
            if (!DateTimeOffset.TryParse(Text(snapshot, "time"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var time)) return (null, "has no readable time");
            return ((id, time.UtcDateTime), null);
        }
        return (null, "is not in the repository any more, or could not be read");
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : new List<string>();

    /// <summary>The same folder as restic stores it (absolute, without a
    /// trailing separator but for a drive's root).</summary>
    internal static bool SamePath(string stored, string sourcePath)
    {
        static string Normal(string path)
        {
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        }
        return string.Equals(Normal(stored), Normal(sourcePath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>The plan's latest snapshot, as Backrest chose a backup's
    /// parent: <c>restic snapshots --json --tag plan:&lt;id&gt;,created-by:backspace</c>,
    /// sorted by time, the last one of these folders (every snapshot Backspace
    /// made with this plan's label is; a hand-made one may not be). Null for a
    /// first backup. A list that cannot be read stops the backup, as it did
    /// Backrest's.</summary>
    private async Task<(string Id, DateTime When)?> LatestPlanSnapshotAsync(string restic, string repoId, ResticRepository repository,
        string planId, string sourcePath, CancellationToken ct, Action<string>? lineSink)
    {
        List<(string Id, long UnixMs)>? listed = null;
        var command = new ResticCommand(restic, repository, ["snapshots", "--json"], ["--tag", $"plan:{planId},created-by:{InstanceTag}"])
            { MaximumLine = 64 << 20 };
        var result = await RunResticAsync(repoId, command, lineSink, ct, onLine: line =>
        {
            if (!line.StandardError && line.Json.ValueKind == JsonValueKind.Array) listed = SnapshotTimes(line.Json, sourcePath);
        }, gated: false).ConfigureAwait(false);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (!result.Succeeded || listed is null)
            throw ResticFailure("The backup engine could not list this backup's earlier snapshots, so the backup was not started.", result);
        return LatestOf(listed);
    }

    /// <summary>The listed snapshots of <paramref name="sourcePath"/> (all, when null), with their times.</summary>
    internal static List<(string Id, long UnixMs)> SnapshotTimes(JsonElement list, string? sourcePath = null)
    {
        var times = new List<(string, long)>();
        foreach (var snapshot in list.EnumerateArray())
        {
            if (snapshot.ValueKind != JsonValueKind.Object || Text(snapshot, "id") is not { } id) continue;
            if (!DateTimeOffset.TryParse(Text(snapshot, "time"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var time)) continue;
            if (sourcePath is not null && Strings(snapshot, "paths") is var paths && (paths.Count != 1 || !SamePath(paths[0], sourcePath))) continue;
            times.Add((id, time.ToUnixTimeMilliseconds()));
        }
        return times;
    }

    /// <summary>The newest valid snapshot, or null: sorted by time keeping
    /// restic's order for equal times, the last one (Backrest's choice).</summary>
    internal static (string Id, DateTime When)? LatestOf(IEnumerable<(string Id, long UnixMs)> snapshots)
    {
        var latest = snapshots.Where(s => IsValidBackrestGuid(s.Id)).OrderBy(s => s.UnixMs).LastOrDefault();
        return latest.Id is null ? null : (latest.Id, DateTimeOffset.FromUnixTimeMilliseconds(latest.UnixMs).UtcDateTime);
    }

    // ---- after the backup ----

    /// <summary>Whether a full prune follows this backup's cleanup: the
    /// plan's previous backup did not finish (it may have left data behind),
    /// or no full prune has run here for <see cref="PruneAtLeastEvery"/>. Both
    /// come from Backspace's own records (<see cref="MaintenanceRecords"/>).</summary>
    internal static bool NeedsPrune(bool previousUnfinished, DateTime? lastFullPruneUtc, DateTime nowUtc)
        => previousUnfinished || lastFullPruneUtc is not { } at || nowUtc - at >= PruneAtLeastEvery;

    private sealed record DirectOutcome(string Status, string DisplayMessage);

    /// <summary>A maintenance command's result in Backrest's words.</summary>
    private static DirectOutcome Outcome(ResticResult result) =>
        result.Cancelled ? new(BackrestStatus.UserCancelled, "Stopped on request.")
        : result.ExitCode == 0 ? new(BackrestStatus.Success, "")
        : result.ExitCode == 3 ? new(BackrestStatus.Warning, result.ErrorText)
        : new(BackrestStatus.Error, result.ErrorText);

    /// <summary>The retention policy applied with restic's forget (U172) and,
    /// after a backup that may have left data behind or once a week, a full
    /// prune (U131); see <see cref="RunRetentionAsync"/>, its Backrest twin
    /// for the stand-alone schedule.</summary>
    private async Task<(DirectOutcome Result, CleanupResult Cleanup)> RunDirectRetentionAsync(string restic, string repoId,
        ResticRepository repository, string planId, Backup backup, CancellationToken ct, Action<string>? lineSink, bool prune)
    {
        var policy = KeepPolicyRules.Parse(backup.KeepPolicy);
        if (!policy.Valid)
            Announce("[backspace] The retention policy could not be read, so no backup was removed: " + policy.Error, lineSink);
        var forget = RetentionArguments(planId, InstanceTag, KeepPolicyRules.ResticOptions(policy));
        DirectOutcome? result = null;
        CleanupResult cleanup = default;
        if (forget is not null)
        {
            if (!prune) forget.Add("--prune");
            var figures = new CleanupLines();
            result = Outcome(await RunResticAsync(repoId, new ResticCommand(restic, repository, forget) { OutputToTranscript = true },
                lineSink, ct, onLine: figures.Accept).ConfigureAwait(false));
            cleanup = figures.Result();
            if (result.Status is not BackrestStatus.Success) return (result, cleanup);
        }
        if (prune)
        {
            Announce("[backspace] Removing stored data no backup uses any more", lineSink);
            var figures = new CleanupLines();
            var run = await RunResticAsync(repoId, new ResticCommand(restic, repository, [PruneCommand]) { OutputToTranscript = true },
                lineSink, ct, onLine: figures.Accept).ConfigureAwait(false);
            if (run.Succeeded) MaintenanceRecords.FullPruneDone(repoId, DateTime.UtcNow, ct);
            return (Outcome(run), cleanup.Plus(figures.Result()));
        }
        return (result ?? new(BackrestStatus.Success, "Retention keeps every snapshot; no cleanup was needed."), cleanup);
    }

    /// <summary>The lines of forget's and prune's output that say what they
    /// removed (U142), kept to read the figures at the end.</summary>
    private sealed class CleanupLines
    {
        private readonly List<string> _lines = new();
        internal void Accept(ResticLine line)
        {
            if (line.StandardError || line.IsJson) return;
            var text = line.Text.TrimStart();
            if (!text.StartsWith("remove ", StringComparison.Ordinal) && !text.StartsWith("total prune:", StringComparison.Ordinal)
                && !text.Contains(" snapshots have been removed", StringComparison.Ordinal)) return;
            lock (_lines) if (_lines.Count < 10_000) _lines.Add(text);
        }
        internal CleanupResult Result() { lock (_lines) return CleanupResult.Parse(string.Join('\n', _lines)); }
    }

    /// <summary>With Verbose on, lists the files this backup added, changed
    /// or removed (U115), like Duplicacy's "Uploaded" lines: restic diff
    /// against the previous snapshot, or every file of a first backup. Both
    /// read the snapshots' file lists (cached locally), not file data, without
    /// a repository lock (snapshots never change). The list is streamed and
    /// not capped. A failure only leaves the list out.</summary>
    private async Task WriteChangedFilesAsync(string restic, ResticRepository repository, string? parentId, string snapshotId,
        CancellationToken ct, Action<string>? lineSink)
    {
        void Write(string line) => Announce(line, lineSink);
        if (parentId == snapshotId) return;
        try
        {
            var arguments = parentId is null
                ? new List<string> { "ls", "--json", "--no-lock", snapshotId }
                : new List<string> { "diff", "--no-lock", parentId, snapshotId };
            var result = await ResticRunner.RunAsync(new ResticCommand(restic, repository, arguments), line =>
            {
                if (line.StandardError) return;
                if ((parentId is null ? BackupLiveProgress.ListedFileLine(line.Text) : BackupLiveProgress.FileLine(line.Text)) is { } file) Write(file);
            }, Write, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.ExitCode != 0)
                Write("[backspace] The list of changed files is incomplete: restic stopped with code " + result.ExitCode + ".");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (ServiceLocator.Maintenance.IsBlocked) throw;
            _log.Debug(ex, "Could not list the changed files");
            Write("[backspace] The list of changed files couldn't be made: " + ex.Message);
        }
    }

    /// <summary>What the engine has stored so far, when Windows can tell
    /// (U124): what this backup's own restic wrote. Two backups at once each
    /// read their own restic now.</summary>
    private static Func<long?>? SentBytesMeter(Destination destination, Func<int> resticProcess)
    {
#if WINDOWS
        return () => resticProcess() is var pid && pid > 0 && Platform.Windows.EngineTransfer.Written(pid) is { } written
            ? Platform.Windows.EngineTransfer.Stored(written, direct: destination.Kind == DestinationKind.S3Compatible) : null;
#else
        return null;
#endif
    }
}
