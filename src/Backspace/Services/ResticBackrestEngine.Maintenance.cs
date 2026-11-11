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
/// The stand-alone cleanup and checks (their schedules, a check entry run
/// after each backup, the command line's <c>--prune</c> and <c>--check</c>)
/// and the erase tools ("free up space", removing a backup's snapshots,
/// erasing a destination's data) run restic directly through
/// <see cref="ResticRunner"/> (U175, step 2), with the commands Backrest ran
/// for them: <c>forget</c> with the keep rules, <c>prune</c>, <c>check</c>
/// (or <c>stats</c> then <c>check --read-data-subset</c>, or
/// <c>check --read-data</c>), <c>forget &lt;id&gt;</c> and
/// <c>prune --max-unused 25%</c>.
/// </summary>
public sealed partial class ResticBackrestEngine
{
    public Task<RunRecord> RunPruneOnlyAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct, Action<string>? lineSink = null) =>
        RunDirectMaintenanceAsync(backup, sourcePath, destination, storageName, RunKind.Prune, ct, lineSink);

    public Task<RunRecord> RunCheckOnlyAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct, Action<string>? lineSink = null) =>
        RunDirectMaintenanceAsync(backup, sourcePath, destination, storageName, RunKind.Check, ct, lineSink);

    /// <summary>The engine reads a check entry's data limit (U179): a data
    /// sample reads 1% of the stored data up to <see cref="Backup.DataCheckLimitBytes"/>
    /// (1 GiB when unset), and <see cref="long.MaxValue"/> reads all of it.</summary>
    public bool HonoursDataCheckLimit => true;

    /// <summary>What every direct operation on a destination starts with: this
    /// build's restic, the destination's drive identity and connection, its
    /// repository description, and the repository itself when
    /// <paramref name="create"/> (opened, or created when restic says there is
    /// none, as Backrest's AddRepo did).</summary>
    private async Task<(string Restic, string RepoId, ResticRepository Repository)> PrepareDirectAsync(
        Backup? backup, Destination destination, string storageName, bool checkConnection, bool create,
        CancellationToken ct, Action<string>? lineSink)
    {
        var restic = await ResticBinaryAsync(ct).ConfigureAwait(false);
        var repoId = MakeRepoId(destination, storageName);
        await new RcloneConfigBuilder(_secrets).EnsureDriveIdentityAsync(destination, ct, ServiceLocator.Config).ConfigureAwait(false);
        if (checkConnection) await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        var repository = BuildResticRepository(destination, backup);
        if (create) await EnsureRepositoryAsync(restic, repoId, repository, destination, ct, lineSink).ConfigureAwait(false);
        return (restic, repoId, repository);
    }

    private async Task<RunRecord> RunDirectMaintenanceAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        RunKind kind, CancellationToken ct, Action<string>? lineSink)
    {
        var (restic, repoId, repository) = await PrepareDirectAsync(backup, destination, storageName,
            checkConnection: true, create: true, ct, lineSink).ConfigureAwait(false);
        var started = DateTime.UtcNow;
        DirectOutcome terminal;
        CleanupResult cleanup = default;
        // Where this check or cleanup starts in the log (U176).
        Announce(LogSection.Line(backup.Name, kind == RunKind.Prune ? "cleanup" : "check", destination.Name), lineSink);
        if (kind == RunKind.Prune)
        {
            // Never while a snapshot here is mounted or restored (U159).
            using var change = SnapshotReaders.TryBeginChange(destination, storageName, out var inUse);
            if (change is null)
            {
                var skipped = SnapshotReaders.CleanupSkipped(inUse);
                Announce("[backspace] " + skipped, lineSink);
                return new RunRecord
                {
                    BackupId = backup.Id, BackupName = backup.Name, Kind = kind,
                    StartedUtc = started, EndedUtc = DateTime.UtcNow,
                    Status = BackupRunStatus.Skipped, Summary = skipped,
                };
            }
            var planId = MakePlanId(backup, sourcePath, destination, storageName);
            // The cleanup schedule always ends with a full prune.
            (terminal, cleanup) = await RunDirectRetentionAsync(restic, repoId, repository, planId, backup, ct, lineSink, prune: true).ConfigureAwait(false);
            if (terminal.Status is BackrestStatus.Success || cleanup != default) Announce(cleanup.Recap(), lineSink);
        }
        else terminal = await RunDirectCheckAsync(restic, repoId, repository, backup, ct, lineSink).ConfigureAwait(false);
        var status = StatusFromBackrestTerminal(terminal.Status);
        return new RunRecord
        {
            BackupId = backup.Id, BackupName = backup.Name, Kind = kind,
            StartedUtc = started, EndedUtc = DateTime.UtcNow,
            SnapshotsRemoved = cleanup.SnapshotsRemoved, BytesFreed = cleanup.BytesFreed,
            Status = status,
            Checks = kind == RunKind.Check
                ? new() { CheckResult(backup, destination, storageName, status, terminal.DisplayMessage) }
                : new(),
            Summary = $"{(kind == RunKind.Prune ? "Cleanup" : "Backup check")} result: {terminal.Status}. {terminal.DisplayMessage} "
                + (kind == RunKind.Check ? RepositoryCheckDescription(backup) : ""),
            Errors = status == BackupRunStatus.Failed
                ? new() { string.IsNullOrWhiteSpace(terminal.DisplayMessage) ? $"Backup operation result: {terminal.Status}." : terminal.DisplayMessage }
                : new(),
        };
    }

    /// <summary>A check: <c>restic check</c>; with a data sample, <c>stats</c>
    /// for the stored size then <c>check --read-data-subset=&lt;bytes&gt;B</c>
    /// for 1% of it up to the entry's limit; a full data read
    /// <c>check --read-data</c> (U179).</summary>
    private async Task<DirectOutcome> RunDirectCheckAsync(string restic, string repoId, ResticRepository repository, Backup backup,
        CancellationToken ct, Action<string>? lineSink)
    {
        if (!backup.CheckReadDataSample)
            return Outcome(await RunResticAsync(repoId, new ResticCommand(restic, repository, ["check"]) { OutputToTranscript = true },
                lineSink, ct).ConfigureAwait(false));
        if (ReadsAllData(backup))
        {
            Announce("[backspace] Data check reads all stored data; this downloads the whole repository.", lineSink);
            return Outcome(await RunResticAsync(repoId, new ResticCommand(restic, repository, DataCheckArguments(backup, null)) { OutputToTranscript = true },
                lineSink, ct).ConfigureAwait(false));
        }
        const int sizeLimit = 64 * 1024;
        var stats = await RunResticAsync(repoId, new ResticCommand(restic, repository, ["stats", "--mode", "raw-data", "--json"]) { CaptureOutput = sizeLimit + 1 },
            lineSink, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!stats.Succeeded)
            throw new IOException("Could not determine the data-check sample size. No data sample was started. " + stats.ErrorText);
        if (stats.Output.Length > sizeLimit)
            throw new IOException("The engine's size result was unexpectedly large. No data sample was started.");
        // The entry's own limit (U179), 1 GiB when it has none.
        var sampleBytes = DataCheckSampleBytes(ParseStoredDataSize(stats.Output), backup.DataCheckLimitBytes);
        Announce($"[backspace] Data check targets {sampleBytes:N0} bytes per repository; whole-pack sampling, metadata and retries can change actual traffic.", lineSink);
        return Outcome(await RunResticAsync(repoId, new ResticCommand(restic, repository, DataCheckArguments(backup, sampleBytes)) { OutputToTranscript = true },
            lineSink, ct).ConfigureAwait(false));
    }

    /// <summary>restic's check for a data read: all of it, or the sample's
    /// size (the B suffix: an unadorned integer would be read as an n/t
    /// bucket selection), with the backup's download limit.</summary>
    internal static List<string> DataCheckArguments(Backup backup, long? sampleBytes)
    {
        var arguments = new List<string>
        {
            "check",
            sampleBytes is { } bytes ? "--read-data-subset=" + bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "B" : "--read-data",
        };
        if (backup.BandwidthLimitKBps > 0)
            arguments.Add("--limit-download=" + backup.BandwidthLimitKBps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return arguments;
    }

    internal static string DataCheckCommand(Backup backup, long? sampleBytes) => string.Join(' ', DataCheckArguments(backup, sampleBytes));

    // ---- the erase tools ----

    /// <summary>One snapshot of this backup's source, removed with
    /// <c>restic forget &lt;id&gt;</c> once restic confirms it is this plan's.
    /// The data it alone used stays until <see cref="PruneExhaustiveAsync"/>.</summary>
    public async Task PruneSnapshotIdAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        string snapshotId, CancellationToken ct)
    {
        if (!IsValidBackrestGuid(snapshotId)) throw new InvalidOperationException("Invalid snapshot identity; nothing was removed.");
        var (restic, repoId, repository) = await PrepareDirectAsync(backup, destination, storageName,
            checkConnection: true, create: false, ct, null).ConfigureAwait(false);
        var owned = await PlanSnapshotsAsync(restic, repoId, repository, MakePlanId(backup, sourcePath, destination, storageName), ct, null).ConfigureAwait(false);
        if (!owned.Any(s => s.Id == snapshotId))
            throw new InvalidOperationException("The snapshot no longer belongs to this backup. Nothing was removed.");
        using var change = SnapshotReaders.TryBeginChange(destination, storageName, out var inUse)
            ?? throw new InvalidOperationException(SnapshotReaders.Refusal("Removing a snapshot", inUse));
        var result = await RunResticAsync(repoId, new ResticCommand(restic, repository, ["forget", snapshotId]) { OutputToTranscript = true },
            null, ct).ConfigureAwait(false);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (!result.Succeeded)
            throw new IOException($"Snapshot removal reported {Outcome(result).Status}: {result.ErrorText}");
    }

    /// <summary>The stored data no snapshot uses any more, removed with
    /// <c>restic prune --max-unused 25%</c> (Backrest's own prune, kept as
    /// it was for the erase tools).</summary>
    public async Task PruneExhaustiveAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        var (restic, repoId, repository) = await PrepareDirectAsync(backup, destination, storageName,
            checkConnection: true, create: false, ct, null).ConfigureAwait(false);
        using var change = SnapshotReaders.TryBeginChange(destination, storageName, out var inUse)
            ?? throw new InvalidOperationException(SnapshotReaders.Refusal("Cleaning up this backup's storage", inUse));
        var result = await RunResticAsync(repoId, new ResticCommand(restic, repository, ErasePruneArguments) { OutputToTranscript = true },
            null, ct).ConfigureAwait(false);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (!result.Succeeded)
            throw new IOException($"Repository cleanup reported {Outcome(result).Status}: {result.ErrorText}");
        MaintenanceRecords.FullPruneDone(repoId, DateTime.UtcNow, ct);
    }

    internal static readonly IReadOnlyList<string> ErasePruneArguments = ["prune", "--max-unused", "25%"];

    /// <summary>The full prune after a backup's cleanup: restic's own default
    /// for unused space (the erase tools keep 25%, as Backrest did).</summary>
    internal const string PruneCommand = "prune";

    /// <summary>
    /// Forgets older snapshots belonging to this backup/source plan only,
    /// then prunes unreferenced chunks. Explicit IDs preserve other plans
    /// sharing the repository and leave configured retention unchanged.
    /// </summary>
    public async Task<RunRecord> PruneToLatestAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        var (restic, repoId, repository) = await PrepareDirectAsync(backup, destination, storageName,
            checkConnection: true, create: true, ct, null).ConfigureAwait(false);
        var started = DateTime.UtcNow;
        RaiseLineWritten($"[backspace] prune-to-latest started for {destination.Name}/{storageName}");
        // Never run an unfiltered repository-wide retention command here.
        // Several backups, sources, or external tools can share this repository.
        var snapshots = (await PlanSnapshotsAsync(restic, repoId, repository, MakePlanId(backup, sourcePath, destination, storageName), ct, null)
            .ConfigureAwait(false)).OrderByDescending(s => s.UnixTimeMs).ToList();
        foreach (var snapshot in snapshots.Skip(1))
            await PruneSnapshotIdAsync(backup, sourcePath, destination, storageName, snapshot.Id, ct).ConfigureAwait(false);
        if (snapshots.Count > 1)
            await PruneExhaustiveAsync(backup, sourcePath, destination, storageName, ct).ConfigureAwait(false);
        return new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            Kind = RunKind.Prune,
            StartedUtc = started,
            EndedUtc = DateTime.UtcNow,
            Status = BackupRunStatus.Success,
            Summary = $"Removed {Math.Max(0, snapshots.Count - 1)} older snapshot(s) from this backup source. Other backup histories were preserved.",
        };
    }

    // ---- snapshot lists ----

    /// <summary>A snapshot as restic lists it: its id, time and, for restic
    /// 0.17 and later, the files and bytes its backup processed.</summary>
    internal sealed record PlanSnapshot(string Id, long UnixTimeMs, long TotalFiles, long TotalBytes, IReadOnlyList<string> Paths);

    /// <summary>Every snapshot of a backup's plan, as Backrest listed them:
    /// <c>restic snapshots --json --tag plan:&lt;id&gt;,created-by:backspace</c>,
    /// with a repository lock. A list that cannot be read is an error.</summary>
    internal async Task<List<PlanSnapshot>> PlanSnapshotsAsync(string restic, string repoId, ResticRepository repository,
        string planId, CancellationToken ct, Action<string>? lineSink)
    {
        List<PlanSnapshot>? listed = null;
        var command = new ResticCommand(restic, repository, ["snapshots", "--json"], ["--tag", $"plan:{planId},created-by:{InstanceTag}"])
            { MaximumLine = 64 << 20 };
        var result = await RunResticAsync(repoId, command, lineSink, ct, onLine: line =>
        {
            if (!line.StandardError && line.Json.ValueKind == JsonValueKind.Array) listed = ParsePlanSnapshots(line.Json);
        }, gated: false).ConfigureAwait(false);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (!result.Succeeded || listed is null)
            throw ResticFailure("The backup engine could not list this backup's snapshots.", result);
        return listed;
    }

    /// <summary>The snapshots of <c>restic snapshots --json</c>, those with
    /// a valid id and time.</summary>
    internal static List<PlanSnapshot> ParsePlanSnapshots(JsonElement list)
    {
        var snapshots = new List<PlanSnapshot>();
        foreach (var snapshot in list.EnumerateArray())
        {
            if (snapshot.ValueKind != JsonValueKind.Object || Text(snapshot, "id") is not { } id || !IsValidBackrestGuid(id)) continue;
            if (!DateTimeOffset.TryParse(Text(snapshot, "time"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var time)) continue;
            long files = 0, bytes = 0;
            if (snapshot.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
            {
                if (summary.TryGetProperty("total_files_processed", out var f) && f.TryGetInt64(out var fv)) files = fv;
                if (summary.TryGetProperty("total_bytes_processed", out var b) && b.TryGetInt64(out var bv)) bytes = bv;
            }
            snapshots.Add(new PlanSnapshot(id, time.ToUnixTimeMilliseconds(), files, bytes, Strings(snapshot, "paths")));
        }
        return snapshots;
    }
}
