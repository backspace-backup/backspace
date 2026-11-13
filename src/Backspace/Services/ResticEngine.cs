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
/// <see cref="IBackupEngine"/> implementation that runs the bundled restic
/// directly (U175): backups and the cleanup after them
/// (ResticEngine.Direct.cs), checks, the stand-alone cleanup and the
/// erase tools (ResticEngine.Maintenance.cs), and, here, snapshot
/// lists, folder listings, the mounted drive's reads and every restore. Each
/// command carries the destination's repository, built from Backspace's
/// destination and vault (<see cref="ResticRepository"/>); nothing is stored
/// in another program's settings.
///
/// Naming: Backspace's per-(backup, sourcePath, storageName) cell is a
/// "plan" whose id labels its snapshots (<c>plan:&lt;id&gt;</c>), and a
/// <see cref="Destination"/> with a storage name is a "repo" id. Both ids are
/// deterministic.
/// </summary>
public sealed partial class ResticEngine : IBackupEngine
{
    private static ILogger _log => AppLogger.For<ResticEngine>();

    private readonly SecretsStore _secrets;
    private readonly string? _resticOverride;

    /// <param name="resticBinary">The restic to run; this build's own
    /// (<see cref="AppPaths.ResticBinary"/>) when null.</param>
    public ResticEngine(SecretsStore secrets, string? resticBinary = null)
    {
        _secrets = secrets;
        _resticOverride = resticBinary;
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
    /// How a run ended (<c>STATUS_SUCCESS</c>…): run summaries carry them
    /// (<c>Backup result: STATUS_…</c>), and run history and
    /// <see cref="WasStoppedOnRequest"/> read them.
    /// <see cref="StatusFromTerminal"/> maps them to Backspace's.
    /// </summary>
    private static class TerminalStatus
    {
        public const string Success      = "STATUS_SUCCESS";
        public const string Warning      = "STATUS_WARNING";
        public const string Error        = "STATUS_ERROR";
        public const string UserCancelled = "STATUS_USER_CANCELLED";
        public const string SystemCancelled = "STATUS_SYSTEM_CANCELLED";
    }

    // Positive membership only: a snapshot ID identifies immutable bytes.
    // Reusing that identity avoids re-listing the whole cloud repository for
    // each folder or restore batch. The actual read must succeed: this is
    // never a cache of availability or recovered contents. A deleted snapshot
    // cannot become another version. The repository's address is part of it:
    // a destination pointed elsewhere lists again.
    private readonly record struct SnapshotIdentity(string RepoId, string PlanId, string RepoUri, string SnapshotId);
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
        if (limit is < 1) throw new ArgumentOutOfRangeException(nameof(limit), "A data check's sample is at least one byte.");
        return Math.Min(limit ?? BackupCheck.OneGiB, Math.Max(1, storedBytes / 100));
    }

    internal static long ParseStoredDataSize(string output)
    {
        long? found = null;
        // restic prints one line of JSON. Require exactly one valid size
        // result rather than interpreting a diagnostic line, missing result
        // or duplicate as an unlimited check.
        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith('{')) continue;
            using var json = System.Text.Json.JsonDocument.Parse(text);
            if (!json.RootElement.TryGetProperty("total_size", out var size)) continue;
            if (found is not null || !size.TryGetInt64(out var value) || value < 0)
                throw new InvalidDataException("The engine's stored-data size was invalid or ambiguous. No data check was started.");
            found = value;
        }
        return found ?? throw new InvalidDataException("The engine did not return a stored-data size. No data check was started.");
    }

    /// <summary>How often a repository gets a full prune at the latest, for
    /// leftovers no record shows (a backup cut short where nothing recorded it).</summary>
    internal static readonly TimeSpan PruneAtLeastEvery = TimeSpan.FromDays(7);

    /// <summary>The forget that applies the backup's retention policy
    /// (<see cref="KeepPolicyRules.ResticOptions"/>, U172) to this plan's
    /// snapshots only, as restic's arguments, or null when the policy keeps
    /// everything. This is the only path that deletes snapshots.</summary>
    internal static List<string>? RetentionArguments(string planId, string instance, IReadOnlyList<string> keepOptions)
    {
        // An empty policy must never delete history: restic refuses a forget
        // without keep options, and Backspace never sends one.
        if (keepOptions.Count == 0) return null;
        // The plan's snapshots only: both its labels (plan and instance),
        // as every snapshot list reads them.
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

    // One restic task per repository in this process: a backup waiting for
    // its turn is not running, and its card says so (U156).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _repoTaskGates = new();

    /// <summary>How long a backup's counts may stay frozen before it counts
    /// as stuck (U111): restic reports its progress twice a second. Tests
    /// shorten it.</summary>
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

    /// <summary>
    /// Map a run's terminal status to our internal status.
    /// Unknown / empty status maps to Failed — a backup we can't
    /// confirm succeeded is NOT a success. Earlier this defaulted
    /// to Success, which combined with the 1.5 s pump cutoff to
    /// produce silent data loss: real backups that hadn't actually
    /// finished within 1.5 s showed green in the UI.
    /// </summary>
    /// <summary>True for a backup the engine stopped because it was asked to
    /// (Stop, Quit, the service stopping), as opposed to a skip it decided.</summary>
    internal static bool WasStoppedOnRequest(RunRecord record)
        => record.Summary?.StartsWith($"Backup result: {TerminalStatus.UserCancelled}", StringComparison.Ordinal) == true;

    internal static BackupRunStatus StatusFromTerminal(string status) => status switch
    {
        TerminalStatus.Success       => BackupRunStatus.Success,
        TerminalStatus.Warning       => BackupRunStatus.Warning,
        TerminalStatus.Error         => BackupRunStatus.Failed,
        TerminalStatus.UserCancelled => BackupRunStatus.Skipped,
        TerminalStatus.SystemCancelled => BackupRunStatus.Skipped,
        _                            => BackupRunStatus.Failed,
    };

    public async Task<RunRecord> ListRevisionsAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        var snapshots = await ListSnapshotsSortedAsync(backup, sourcePath, destination, storageName, ct).ConfigureAwait(false);
        return new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            Kind = RunKind.Backup,
            StartedUtc = DateTime.UtcNow,
            EndedUtc = DateTime.UtcNow,
            Status = BackupRunStatus.Success,
            Summary = $"{snapshots.Count} snapshot(s)",
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
            revisions.Add(new RevisionSummary(
                Number: sorted.Count - i,
                CreatedUtc: DateTimeOffset.FromUnixTimeMilliseconds(s.UnixTimeMs).UtcDateTime,
                // Known from restic 0.17's snapshot summary; 0 when unknown.
                FileCount: (int)Math.Min(int.MaxValue, s.TotalFiles),
                TotalBytes: s.TotalBytes) { SnapshotId = s.Id, Hostname = s.Hostname });
        }
        return revisions;
    }

    public async Task<IReadOnlyList<RevisionFile>> ListSnapshotFilesAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, CancellationToken ct, string? expectedSnapshotId = null)
    {
        // The complete source tree in one restic listing. Paths are relative
        // to the source ("notes/recipe.json", not "C/Users/…/notes/recipe.json"):
        // the restore wizard, a cherry-picked restore and the validators all
        // read RevisionFile.Path that way.
        var tree = await ListSnapshotTreeAsync(backup, sourcePath, destination,
            storageName, revision, ct, expectedSnapshotId).ConfigureAwait(false)
            ?? throw new IOException("The backup engine could not list this snapshot's files. Open Activity & logs for details.");
        return tree.Where(node => !node.IsDirectory)
            .Select(node => new RevisionFile(node.Path, node.SizeBytes, node.ModifiedUtc))
            .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
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
        var snapshot = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct, expectedSnapshotId).ConfigureAwait(false);
        // The folder as the snapshot stores it: "/" + the source's prefix +
        // the source-relative folder ("/C/Users/me/Documents/notes").
        var srcPrefix = SourcePathToSnapshotPrefix(sourcePath);
        var rel = (dirRelativePath ?? "").Replace('\\', '/').Trim('/');
        var folder = "/" + (srcPrefix.Length == 0 ? rel : rel.Length == 0 ? srcPrefix : srcPrefix + "/" + rel);
        // Reads without a repository lock: keep cleanup out meanwhile (U159).
        using var reading = await SnapshotReaders.HoldAsync(destination, storageName, SnapshotReaders.Kind.Restore, null, ct).ConfigureAwait(false);
        // restic ls names the folder itself, then its children.
        var listing = new SnapshotTreeListing(folder, snapshot.SnapshotId);
        var result = await StreamingRestoreProcess.RunAsync(ResticRunner.StartInfo(new ResticCommand(snapshot.Restic, snapshot.Repository,
                ["ls", "--json", "--no-lock"], ["--", snapshot.SnapshotId, folder])), listing.Accept, ct,
            operationName: "folder listing", includeStandardOutput: false).ConfigureAwait(false);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        // Not there, not a folder, or not read completely: the mounted drive
        // says "no such folder" rather than showing part of one.
        if (result.ExitCode != 0 || result.HadErrors || !listing.IsComplete
            || folder != "/" && !listing.Nodes.Any(node => node.Path.Length == 0 && node.IsDirectory))
        {
            _log.Debug("restic ls {Snap} {Folder} exited {Code}: {Err}", snapshot.SnapshotId[..8], folder, result.ExitCode, result.Output);
            return null;
        }
        var children = listing.Nodes.Where(node => node.Path.Length > 0 && !node.Path.Contains('/'))
            .Select(node => new RevisionDirEntry(node.Path, node.IsDirectory, node.SizeBytes, node.ModifiedUtc)).ToList();
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
        var snapshot = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct, expectedSnapshotId).ConfigureAwait(false);
        var snapshotId = snapshot.SnapshotId;
        var identity = new SnapshotIdentity(snapshot.RepoId, MakePlanId(backup, sourcePath, destination, storageName),
            snapshot.Repository.Uri, snapshotId);
        lock (_snapshotTreeGate)
            if (_snapshotTrees.TryGetValue(identity, out var cached)) return cached;
        TreeListingStarted?.Invoke(snapshotId);
        await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        // The listing reads without a repository lock: keep cleanup out (U159).
        using var reading = await SnapshotReaders.HoldAsync(destination, storageName, SnapshotReaders.Kind.Restore, null, ct).ConfigureAwait(false);

        var srcPrefix = SourcePathToSnapshotPrefix(sourcePath);
        var rootBound = "/" + srcPrefix;
        // restic ls --json: the snapshot's line, then one line per file or
        // folder, read as they arrive (UTF-8, for accented names). A listing
        // reads only: no lock file to write and delete.
        ProcessStartInfo StartInfo(IEnumerable<string> paths, bool recursive)
        {
            var arguments = new List<string> { "ls", "--json", "--no-lock" };
            if (recursive) arguments.Add("--recursive");
            return ResticRunner.StartInfo(new ResticCommand(snapshot.Restic, snapshot.Repository, arguments, ["--", snapshotId, .. paths]));
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

    /// <summary>Raised as each restore starts restic, with its command line
    /// and the include file's lines (null for the whole source), so tests can
    /// prove what restic was asked (U175).</summary>
    internal event Action<IReadOnlyList<string>, IReadOnlyList<string>?>? RestoreStarted;

    /// <summary><c>restic dump</c> of one file: its bytes reach
    /// <paramref name="output"/> as restic reads them, the first ones as soon
    /// as restic has found the file, with nothing written elsewhere (U167).
    /// The path is literal (no pattern), so brackets need no escaping.</summary>
    public async Task<bool> DumpSnapshotFileAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, string fileRelativePath, Stream output, CancellationToken ct, string? expectedSnapshotId = null)
    {
        var relative = NormalizeRestoreRelativePath(fileRelativePath);
        if (relative.Length == 0) throw new InvalidOperationException("No file named.");
        var snapshot = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct, expectedSnapshotId).ConfigureAwait(false);
        await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        // Reads without a repository lock: keep cleanup out meanwhile (U159).
        using var reading = await SnapshotReaders.HoldAsync(destination, storageName, SnapshotReaders.Kind.Restore, null, ct).ConfigureAwait(false);
        var srcPrefix = SourcePathToSnapshotPrefix(sourcePath);
        var path = "/" + (srcPrefix.Length > 0 ? srcPrefix + "/" : "") + relative;
        FileDumpStarted?.Invoke(path);
        // The backup's download limit is among the repository's options.
        var result = await StreamingRestoreProcess.RunToStreamAsync(ResticRunner.StartInfo(new ResticCommand(snapshot.Restic, snapshot.Repository,
            ["dump", "--no-lock"], ["--", snapshot.SnapshotId, path])), output, ct, "file download").ConfigureAwait(false);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (result.ExitCode != 0)
        {
            _log.Warning("restic dump {Snap} exited {Code}: {Err}", snapshot.SnapshotId[..8], result.ExitCode, result.Output);
            throw new IOException("The backup engine could not read this file from the destination. Open Activity & logs for details.");
        }
        return true;
    }

    public async Task<RestoreOpResult> RestoreFilesAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, IReadOnlyList<string> includePatterns, string targetPath,
        Action<RestoreProgressUpdate>? onProgress, CancellationToken ct,
        IReadOnlyList<string>? expectedLeaves = null, string? expectedSnapshotId = null, bool overwrite = true, bool preserveStructure = true,
        RestoreControl? control = null, Action<string?>? onStaging = null, bool folderPermissions = false)
    {
        if (includePatterns.Count == 0)
            return new RestoreOpResult(0, 0, 0, 0, RestoreTerminalStatus.Success, null);
        ValidateRestoreTarget(targetPath);
        await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        // What this restore reads stays in place until it ends (U159).
        using var reading = await SnapshotReaders.HoldAsync(destination, storageName, SnapshotReaders.Kind.Restore, null, ct).ConfigureAwait(false);
        var snapshot = await ResolveSnapshotIdAsync(
            backup, sourcePath, destination, storageName, revision, ct, expectedSnapshotId).ConfigureAwait(false);
        Directory.CreateDirectory(targetPath);
        var srcPrefix = SourcePathToSnapshotPrefix(sourcePath);
        var includes = includePatterns.Select(NormalizeRestoreRelativePath).ToArray();
        var exact = expectedLeaves?.Select(NormalizeRestoreRelativePath)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // Never restore wildcard overmatches into an occupied destination or
        // infer success from a file that existed before this operation. Stage
        // on the target volume, then move only requested output into place.
        // One restic restore of the source folder does it, the selection in an
        // include file (a test restore's batch names up to 1,024 files), its
        // progress read as it arrives. A paused transfer resumes into the same
        // staged bytes once restic has stopped.
        var staging = Path.Combine(targetPath, ".backspace-restore-" + Guid.NewGuid().ToString("N"));
        string OutputRelative(string path) => Path.GetRelativePath(staging, path).Replace('\\', '/');
        Directory.CreateDirectory(staging);
        // The whole source needs no selection.
        var includeFile = includes.Contains("") ? null
            : Path.Combine(Path.GetTempPath(), $"backspace-restore-include-{Guid.NewGuid():N}.txt");
        var publication = control?.Output ?? new RestoreOutputTransaction();
        var status = RestoreTerminalStatus.Error;
        string? error = null;
        int restored = 0;
        long restoredBytes = 0;
        var total = exact?.Count ?? 0;
        long totalBytes = 0;
        string captured = "";
        // The progress card names the staging folder while output arrives
        // there (U154). A UI callback never interferes with the restore.
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
            var includeLines = includeFile is null ? null : includes.Select(p => "/" + EscapeBracketsForResticPattern(p)).ToArray();
            if (includeFile is not null)
                await File.WriteAllLinesAsync(includeFile, includeLines!, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var start = BuildStreamingRestoreStartInfo(snapshot.Restic, snapshot.Repository,
                snapshot.SnapshotId + ":/" + srcPrefix, staging, includeFile, folderPermissions);
            try { RestoreStarted?.Invoke(start.ArgumentList.ToArray(), includeLines); } catch (Exception ex) { _log.Debug(ex, "Restore observer failed"); }
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
            var succeeded = !result.Cancelled && result.ExitCode == 0 && !result.HadErrors;
            if (!result.Cancelled && !succeeded)
                _log.Warning("Restore process failed with exit code {ExitCode}. Last engine diagnostics: {Details}",
                    result.ExitCode, result.Output);
            status = ct.IsCancellationRequested || result.Cancelled ? RestoreTerminalStatus.Cancelled
                : succeeded ? RestoreTerminalStatus.Success : RestoreTerminalStatus.Error;
            error = status switch
            {
                RestoreTerminalStatus.Success => null,
                RestoreTerminalStatus.Cancelled => "Restore cancelled.",
                _ => "The backup engine could not finish this restore. Open Activity & logs for details.",
            };

            ParseResticRestoreSummary(captured, out var completedFiles, out var completedBytes,
                out var reportedTotal, out totalBytes, out var errors);
            totalBytes = Math.Max(0, totalBytes);
            if (exact is null && reportedTotal >= 0) total = (int)Math.Min(int.MaxValue, reportedTotal);

            // A success summary never overrides a backend error or cancellation.
            // Metadata warnings remain warnings even when all content recovered.
            var leaves = Directory.Exists(staging) ? EnumerateRestoreLeaves(staging).ToArray() : Array.Empty<string>();
            // Restic summary "files" includes directories and its byte total
            // deduplicates hardlinks, so neither equals our leaf-file counts.
            // A partial recovery after ERROR requires explicit completion for
            // every produced leaf, with no error for that leaf, plus a final
            // summary. An incomplete/preallocated file never replaces originals.
            var completedLeaves = ParseCompletedRestoreLeaves(captured);
            var stagedDirectories = Directory.Exists(staging) ? CountRestoreDirectories(staging) : 0;
            var allOutputConfirmed = completedFiles == leaves.Length + stagedDirectories &&
                completedBytes >= 0 && completedBytes == totalBytes;
            if (status == RestoreTerminalStatus.Error && reportedTotal >= 0 && leaves.Length > 0 &&
                (allOutputConfirmed || leaves.All(path => completedLeaves.Contains(Path.GetRelativePath(staging, path).Replace('\\', '/')))))
                status = RestoreTerminalStatus.Warning;
            if (status is RestoreTerminalStatus.Success or RestoreTerminalStatus.Warning)
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
                    ? EnumerateRestoreDirectories(staging).Where(path =>
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
                    (directoryDestinations.Length > 0 || (includes.Contains("") && Directory.Exists(staging)));
                if (status == RestoreTerminalStatus.Success && restored == 0 && !restoredEmptyFolder)
                {
                    status = RestoreTerminalStatus.Error;
                    error = "The backup engine completed without producing any requested files.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = RestoreTerminalStatus.Cancelled;
            error = "Restore cancelled. Temporary downloads and this restore's output are being removed; replaced originals are being recovered.";
        }
        catch (Exception ex)
        {
            status = RestoreTerminalStatus.Error;
            error = ex.Message;
            control?.Failed();
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
            // restic has ended (the runner returns only then): its output and
            // the include file can go.
            if (includeFile is not null) try { File.Delete(includeFile); } catch { }
            try { await Task.Run(() => DeleteRestoreStaging(staging)).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.Warning(ex, "Restore staging cleanup failed");
                status = RestoreTerminalStatus.Error;
                error = $"The transfer has stopped, but temporary files could not be removed at {staging}. {ex.Message}";
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

    /// <summary>restic's restore of <paramref name="snapshot"/>
    /// (<c>&lt;id&gt;:/&lt;source prefix&gt;</c>) into <paramref name="target"/>,
    /// limited to the include file's patterns when there is one, its progress
    /// as JSON twice a second (drained as it arrives, never kept). The
    /// backup's download limit is among the repository's options.</summary>
    internal static ProcessStartInfo BuildStreamingRestoreStartInfo(string binary, ResticRepository repository,
        string snapshot, string target, string? includeFile, bool folderPermissions = false)
    {
        var arguments = new List<string> { "restore", "--json", "--target", target };
        if (includeFile is not null) arguments.AddRange(["--include-file", includeFile]);
        var start = ResticRunner.StartInfo(new ResticCommand(binary, repository, arguments, ["--", snapshot]) { ProgressFps = 2 });
        // Copies only read and deleted (a test restore) take the folder's
        // permissions, not the saved ones (U219): a saved descriptor granting
        // only SYSTEM or administrators left copies the user, Dropbox and
        // Backspace's cleanup could neither read nor delete. Patch 0004 of
        // Backspace's Windows restic reads this; elsewhere it has no effect.
        if (folderPermissions) start.Environment[SkipSecurityDescriptorsVariable] = "1";
        return start;
    }

    internal const string SkipSecurityDescriptorsVariable = "RESTIC_RESTORE_SKIP_SECURITY_DESCRIPTORS";

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
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0 && !IsSystemLink(parent))
                throw new IOException($"Cannot restore through the linked folder {parent}. Choose an ordinary folder as the destination.");
        return result;
    }

    /// <summary>macOS's own links at the top of the disk (/var, /tmp and /etc
    /// lead into /private): every temporary folder is under one, so they are
    /// not a link someone chose (U235: every restore to a temporary folder
    /// failed on macOS).</summary>
    internal static bool IsSystemLink(string path) => OperatingSystem.IsMacOS()
        && path.TrimEnd('/') is "/var" or "/tmp" or "/etc";

    internal static void ValidateRestoreTarget(string target)
    {
        // Check the target AND every existing ancestor before creating staging.
        // Checking only descendants misses a junction chosen as the target.
        for (var path = Path.GetFullPath(target); path is not null; path = Path.GetDirectoryName(path))
            if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 && !IsSystemLink(path))
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

    // ---- snapshots: listing and resolution ----

    /// <summary>The snapshot a read works on, with what reading it needs:
    /// this build's restic and the destination's repository.</summary>
    internal sealed record ResolvedSnapshot(string SnapshotId, string Restic, string RepoId, ResticRepository Repository);

    /// <summary>Every snapshot of this backup's source at this destination,
    /// newest first: the order of the "revision numbers" the window shows.
    /// Shared by <see cref="ListSnapshotsAsync"/> and
    /// <see cref="ResolveSnapshotIdAsync"/> so they agree.</summary>
    private async Task<List<PlanSnapshot>> ListSnapshotsSortedAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        var (restic, repoId, repository) = await PrepareDirectAsync(backup, destination, storageName,
            checkConnection: true, create: false, ct, null).ConfigureAwait(false);
        return await ListSnapshotsSortedAsync(restic, repoId, repository,
            MakePlanId(backup, sourcePath, destination, storageName), destination, ct).ConfigureAwait(false);
    }

    /// <summary><c>restic snapshots --json --no-lock</c> with the plan's two
    /// labels, without a repository
    /// lock: a list in the window neither waits for nor stops a cleanup
    /// elsewhere, and snapshots never change. Refreshes which snapshot ids are
    /// known to be this plan's.</summary>
    private async Task<List<PlanSnapshot>> ListSnapshotsSortedAsync(string restic, string repoId, ResticRepository repository,
        string planId, Destination destination, CancellationToken ct)
    {
        List<PlanSnapshot>? listed = null;
        var command = new ResticCommand(restic, repository, ["snapshots", "--json", "--no-lock"],
            ["--tag", $"plan:{planId},created-by:{InstanceTag}"]) { MaximumLine = 64 << 20 };
        var result = await RunReadAsync(command, line =>
        {
            if (!line.StandardError && line.Json.ValueKind == JsonValueKind.Array) listed = ParsePlanSnapshots(line.Json);
        }, ct).ConfigureAwait(false);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        // No repository at this address (exit code 10): nothing was backed up
        // here yet, so no snapshot, and looking creates nothing. A local
        // destination whose folder is missing (a drive not connected) says so
        // instead of showing an empty history.
        if (result.ExitCode == 10 && !(destination.IsLocalLike && !Directory.Exists(repository.Uri)))
            listed = new();
        else if (!result.Succeeded || listed is null)
            throw ResticFailure("The backup engine could not list this backup's snapshots.", result);
        // restic's ids are 64 hex characters; the window shows a revision
        // number instead. The immutable id carries a selection across
        // retention changes; positions are not stable ids.
        var sorted = listed.OrderByDescending(s => s.UnixTimeMs).ToList();
        // Bound memory independently of the number of snapshots in a
        // repository. Eviction only causes a fresh membership lookup.
        if (_snapshotIdentities.Count >= 4096) _snapshotIdentities.Clear();
        foreach (var key in _snapshotIdentities.Keys.Where(k => k.RepoId == repoId && k.PlanId == planId))
            _snapshotIdentities.TryRemove(key, out _);
        foreach (var snapshot in sorted.Take(128))
            _snapshotIdentities.TryAdd(new(repoId, planId, repository.Uri, snapshot.Id), 0);
        lock (_snapshotTreeGate)
            foreach (var key in _snapshotTrees.Keys.Where(k => k.RepoId == repoId && k.PlanId == planId &&
                !_snapshotIdentities.ContainsKey(k)).ToArray())
            { _cachedTreeNodeCount -= _snapshotTrees[key].Count; _snapshotTrees.Remove(key); }
        return sorted;
    }

    /// <summary>A read for the window (a list of snapshots): restic run
    /// directly, without waiting for the repository's turn (it takes no
    /// lock), its command line and errors in the app's log rather than a
    /// run's.</summary>
    private async Task<ResticResult> RunReadAsync(ResticCommand command, Action<ResticLine>? onLine, CancellationToken ct)
    {
        var result = await ResticRunner.RunAsync(command, onLine, line => _log.Debug("{Line}", line), ct).ConfigureAwait(false);
        try { ResticCommandFinished?.Invoke(command, result); } catch (Exception ex) { _log.Debug(ex, "restic command observer failed"); }
        if (!result.Succeeded && !result.Cancelled) _log.Warning("restic {Command} failed: {Error}", command.Name, result.ErrorText);
        return result;
    }

    /// <summary>Translate a Backspace "revision number" (1 = oldest,
    /// N = newest), or the snapshot id the caller expects, into the snapshot
    /// to read. An id listed for this plan in this process is used without
    /// listing again; any other is checked against a fresh list and never
    /// substituted. Throws <see cref="ArgumentOutOfRangeException"/> if the
    /// revision doesn't exist.</summary>
    internal async Task<ResolvedSnapshot> ResolveSnapshotIdAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, CancellationToken ct, string? expectedSnapshotId = null)
    {
        var (restic, repoId, repository) = await PrepareDirectAsync(backup, destination, storageName,
            checkConnection: false, create: false, ct, null).ConfigureAwait(false);
        var planId = MakePlanId(backup, sourcePath, destination, storageName);
        if (expectedSnapshotId is not null &&
            _snapshotIdentities.ContainsKey(new(repoId, planId, repository.Uri, expectedSnapshotId)))
            return new(expectedSnapshotId, restic, repoId, repository);
        await new CloudDestinationPreflight(_secrets).CheckAsync(destination, ct).ConfigureAwait(false);
        var sorted = await ListSnapshotsSortedAsync(restic, repoId, repository, planId, destination, ct).ConfigureAwait(false);
        var pickedId = expectedSnapshotId is null ? PickSnapshotIdForRevision(sorted, revision) :
            sorted.Any(s => s.Id == expectedSnapshotId) ? expectedSnapshotId :
            throw new InvalidOperationException("The selected snapshot is no longer available. Reopen Restore and choose a snapshot; no other version was substituted.");
        return new(pickedId, restic, repoId, repository);
    }

    /// <summary>Pure helper: pick the snapshot that corresponds to the
    /// caller's "revision number" given a list of snapshots already
    /// sorted newest-first. Highest revision number = newest =
    /// sorted[0]; revision 1 = oldest = sorted[count-1]. Throws when
    /// the revision is out of range so callers don't accidentally
    /// hand a stale revision id to the engine.</summary>
    internal static string PickSnapshotIdForRevision(IReadOnlyList<PlanSnapshot> newestFirstSorted, int revision)
    {
        if (revision < 1 || revision > newestFirstSorted.Count)
            throw new ArgumentOutOfRangeException(nameof(revision),
                $"Revision {revision} doesn't exist; this snapshot chain has {newestFirstSorted.Count} revision(s).");
        return newestFirstSorted[newestFirstSorted.Count - revision].Id;
    }

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

    /// <summary>Nothing to prepare: every restic command carries the
    /// destination's repository, built for it (U175), and looking at a
    /// destination never creates a repository there.</summary>
    public Task EnsureInitializedAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Rotates the master-key encryption password on the repo by
    /// spawning <c>restic.exe key passwd</c> directly (the bundled restic
    /// binary, <see cref="AppPaths.ResticBinary"/>).
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

    /// <summary>Stable repo id for a (Destination, storageName) pair: the
    /// key of Backspace's records about a repository (maintenance.json,
    /// repositories.json).</summary>
    internal static string MakeRepoId(Destination destination, string storageName)
    {
        var name = string.IsNullOrEmpty(storageName) ? "default" : storageName;
        return Slug($"{destination.Id}-{name}");
    }

    /// <summary>A restic id: exactly 64 hex characters (a snapshot's or a
    /// repository's).</summary>
    internal static bool IsValidSnapshotId(string? guid)
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

    /// <summary>restic's closing message for an init that found keys without
    /// a configuration (its exit_error message).</summary>
    internal static bool IsInterruptedInitialization(string? exitMessage)
        => exitMessage?.EndsWith(": repository already contains keys", StringComparison.Ordinal) == true;

    /// <summary>The options every restic command gets for this repository
    /// (backup, restore, check, prune, stats, forget, snapshots, init): the
    /// backup's bandwidth limits and, for a repository without a password,
    /// <c>--insecure-no-password</c>.</summary>
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
        // reaches every restic command Backspace runs (including
        // init). Tests and the UI
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
        // base-2 conversion. The repository's options carry both
        // limits for every command (BuildRepoFlagsForBackup).
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
