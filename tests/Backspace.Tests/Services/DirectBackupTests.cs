using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Backups and the cleanup after them run restic directly. The command line
/// is checked option for option; the parent is the remembered snapshot once
/// restic confirms it, otherwise the plan's latest (the owner's answer to
/// question 4, 10 November 2026); exit codes keep their meaning.
/// </summary>
public sealed class DirectBackupTests(ITestOutputHelper output)
{
    /// <summary>Splits a string into a POSIX shell's words (quotes,
    /// backslash escapes), the way plan and repository options are written.</summary>
    internal static List<string> ShellSplit(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                inWord = true;
                var end = text.IndexOf('\'', i + 1);
                word.Append(text, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '"')
            {
                inWord = true;
                for (i++; text[i] != '"'; i++) word.Append(text[i] == '\\' ? text[++i] : text[i]);
            }
            else if (c == '\\') { inWord = true; word.Append(text[++i]); }
            else if (char.IsWhiteSpace(c)) { if (inWord) words.Add(word.ToString()); word.Clear(); inWord = false; }
            else { inWord = true; word.Append(c); }
        }
        if (inWord) words.Add(word.ToString());
        return words;
    }

    /// <summary>The expected backup: <c>backup --json &lt;path&gt;</c>, the
    /// repository's options split like a shell, the sftp option, then
    /// <c>--exclude-caches</c>, the plan and instance labels, <c>--parent</c>
    /// and the plan's options split like a shell.</summary>
    private static List<string> ExpectedBackupArguments(Backup backup, Destination destination, string source, string planId, string? parent)
    {
        var arguments = new List<string> { "backup", "--json", source };
        foreach (var flag in ResticEngine.BuildRepoFlagsForBackup(backup, destination)) arguments.AddRange(ShellSplit(flag));
        arguments.AddRange(["-o", "sftp.args=-oBatchMode=yes", "--exclude-caches", "--tag", "plan:" + planId, "--tag", "created-by:backspace"]);
        if (parent is not null) arguments.AddRange(["--parent", parent]);
        // The plan's options: the backup's own options, then the exclude
        // file, its path single-quoted for the shell-style split.
        foreach (var flag in ResticEngine.BuildBackupFlags(backup)) arguments.AddRange(ShellSplit(flag));
        arguments.AddRange(ShellSplit(FilterTranslator.ExcludeFileFlag + " '" + ResticEngine.ExcludeFilePath(planId) + "'"));
        return arguments;
    }

    private static IReadOnlyList<string> DirectBackupArguments(Backup backup, Destination destination, string source, string planId, string? parent) =>
        ResticRunner.Arguments(new ResticCommand("restic.exe", new ResticRepository("u", "", [], ResticEngine.BuildRepoFlagsForBackup(backup, destination)),
            ResticEngine.BackupArguments(source), ResticEngine.BackupTrailing(backup, planId, parent)));

    [Theory]
    [InlineData(true, true, 0, true, true)]
    [InlineData(false, false, 512, false, false)]
    [InlineData(true, false, 64, true, false)]
    public void The_backup_command_is_the_one_Backrest_ran(bool vss, bool cloudFiles, int limit, bool encrypted, bool parent)
    {
        var backup = new Backup { Id = "b1", UseVss = vss, ExcludeCloudFiles = cloudFiles, BandwidthLimitKBps = limit };
        var destination = new Destination { Id = "d1", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"D:\Backups",
            Encrypted = encrypted, StoragePasswordRef = encrypted ? "ref" : null };
        var planId = ResticEngine.MakePlanId(backup, @"D:\", destination, "default");
        var parentId = parent ? new string('c', 64) : null;
        var direct = DirectBackupArguments(backup, destination, @"D:\", planId, parentId);
        Assert.Equal(ExpectedBackupArguments(backup, destination, @"D:\", planId, parentId), direct);
        // Falsifiable: the options every backup gets without saying so.
        Assert.Contains("--exclude-caches", direct);
        Assert.Equal("plan:" + planId, direct[direct.ToList().IndexOf("--tag") + 1]);
        Assert.Contains("created-by:backspace", direct);
        Assert.Equal(parent, direct.Contains("--parent"));
        if (parent) Assert.Equal(parentId, direct[direct.ToList().IndexOf("--parent") + 1]);
        Assert.Equal(!encrypted, direct.Contains("--insecure-no-password"));
        Assert.Equal(ResticEngine.ExcludeFilePath(planId), direct[^1]);
    }

    /// <summary>The owner's local_d command line of 10 November (Appendix C of
    /// the plan): encrypted, no limit, VSS and online-only files on.</summary>
    [Fact]
    public void The_command_line_matches_the_owners_transcript()
    {
        PlatformTest.WindowsOnly("the owner\'s Windows command line (D:\\, VSS)");
        var backup = new Backup { Id = "b2", UseVss = true, ExcludeCloudFiles = true };
        var destination = new Destination { Id = "d2", Kind = DestinationKind.GoogleDrive, Encrypted = true, StoragePasswordRef = "ref" };
        var planId = ResticEngine.MakePlanId(backup, @"D:\", destination, "target-952aab70");
        var parent = new string('6', 64);
        var expected = $"backup --json D:\\ -o sftp.args=-oBatchMode=yes --exclude-caches --tag plan:{planId} --tag created-by:backspace --parent {parent} " +
            $"--no-scan --use-fs-snapshot -o vss.timeout=30m --exclude-cloud-files --iexclude-file {ResticEngine.ExcludeFilePath(planId)}";
        Assert.Equal(expected, string.Join(' ', DirectBackupArguments(backup, destination, @"D:\", planId, parent)));
    }

    // ---- remembered parent: restic's answer checked ----

    private const string Plan = "plan-1";
    private static readonly string Id = new('a', 64);
    private static string Answer(string? id = null, string[]? tags = null, string[]? paths = null, string host = "PC-1") =>
        JsonSerializer.Serialize(new[] { new { time = "2026-11-10T06:00:00.5-10:00", paths = paths ?? [@"D:\"], hostname = host,
            tags = tags ?? ["plan:" + Plan, "created-by:backspace"], id = id ?? Id, short_id = (id ?? Id)[..8] } });

    [Fact]
    public void A_remembered_snapshot_is_used_only_when_restic_confirms_it_is_this_plans()
    {
        PlatformTest.WindowsOnly("Windows paths (drive letters, \\\\server shares)");
        ((string Id, DateTime When)? Snapshot, string? Problem) Check(string answer, string source = @"D:\", string host = "pc-1")
        {
            using var json = JsonDocument.Parse(answer);
            return ResticEngine.CheckRemembered(json.RootElement, Id, Plan, source, host);
        }
        var ok = Check(Answer());
        Assert.Equal((Id, new DateTime(2026, 11, 10, 16, 0, 0, 500, DateTimeKind.Utc)), ok.Snapshot);
        Assert.Null(ok.Problem);
        Assert.Equal("is not in the repository any more, or could not be read", Check("[]").Problem);
        Assert.Equal("is not in the repository any more, or could not be read", Check(Answer(id: new string('b', 64))).Problem);
        Assert.Equal("belongs to another backup", Check(Answer(tags: ["plan:other", "created-by:backspace"])).Problem);
        Assert.Equal("belongs to another backup", Check(Answer(tags: ["plan:" + Plan])).Problem);
        Assert.Equal("holds other folders", Check(Answer(paths: [@"E:\"])).Problem);
        Assert.Equal("holds other folders", Check(Answer(paths: [@"D:\", @"E:\"])).Problem);
        Assert.Equal("was made on another computer", Check(Answer(host: "LAPTOP")).Problem);
        Assert.Equal("could not be read", Check("{}").Problem);
        // The same folder as restic writes it: case and a trailing separator aside.
        Assert.NotNull(Check(Answer(paths: [@"C:\Users\Me\Docs"]), source: @"c:\users\me\docs\").Snapshot);
        Assert.True(ResticEngine.SamePath(@"D:\", @"D:\"));
        Assert.False(ResticEngine.SamePath(@"D:\a", @"D:\ab"));
    }

    // ---- restic's exit codes in run summaries ----

    private static ResticResult Result(int code, bool cancelled = false, string error = "") =>
        new(code, cancelled, ResticStop.None, TimeSpan.FromSeconds(1), null, error + $" (restic exit code {code})", "", 0);

    private static readonly ResticBackupSummary Saved = new(1, 0, 3, 10, 4, 40, 1, new string('a', 64));
    private static readonly ResticBackupSummary NothingSaved = new(0, 0, 0, 0, 0, 0, 1, null);

    /// <summary>restic's exit codes become the run's status words, which run
    /// history, the card and <see cref="ResticEngine.WasStoppedOnRequest"/>
    /// read. The negative cases matter as much: no summary is no success.</summary>
    [Fact]
    public void Every_backup_exit_code_maps_to_the_status_history_reads()
    {
        Assert.Equal(("STATUS_SUCCESS", ""), ResticEngine.BackupOutcome(Result(0), Saved));
        Assert.Equal("STATUS_ERROR", ResticEngine.BackupOutcome(Result(0), null).Status);
        Assert.Equal(("STATUS_WARNING", "Partial backup, some files may not have been read completely."), ResticEngine.BackupOutcome(Result(3), Saved));
        Assert.Equal("STATUS_ERROR", ResticEngine.BackupOutcome(Result(3), null).Status);
        Assert.Equal("STATUS_ERROR", ResticEngine.BackupOutcome(Result(3), NothingSaved).Status);
        var missing = ResticEngine.BackupOutcome(Result(10, error: "Fatal: repository does not exist: unable to open config file"), null);
        Assert.Equal("STATUS_ERROR", missing.Status);
        Assert.True(EngineErrorClassifier.IsMissingRepositoryFailure(missing.Details));
        var password = ResticEngine.BackupOutcome(Result(12, error: "Fatal: wrong password or no key found"), null);
        Assert.Equal("STATUS_ERROR", password.Status);
        Assert.True(EngineErrorClassifier.IsAuthenticationFailure(password.Details));
        Assert.Equal("STATUS_ERROR", ResticEngine.BackupOutcome(Result(11, error: "unable to create lock"), null).Status);
        Assert.Equal("STATUS_ERROR", ResticEngine.BackupOutcome(Result(2, error: "panic"), null).Status);
        // Stopped: by Backspace (Stop, Quit, the service stopping), or by
        // someone else (Windows shutting down).
        var stopped = ResticEngine.BackupOutcome(Result(130, cancelled: true), null);
        Assert.Equal("STATUS_USER_CANCELLED", stopped.Status);
        Assert.True(ResticEngine.WasStoppedOnRequest(new Backspace.Models.RunRecord { Summary = $"Backup result: {stopped.Status}. {stopped.Details}" }));
        Assert.Equal("STATUS_USER_CANCELLED", ResticEngine.BackupOutcome(Result(1, cancelled: true), null).Status);
        Assert.Equal("STATUS_SYSTEM_CANCELLED", ResticEngine.BackupOutcome(Result(130), null).Status);
        Assert.Equal(Backspace.Models.BackupRunStatus.Skipped, ResticEngine.StatusFromTerminal("STATUS_SYSTEM_CANCELLED"));
        // A backup that completed as the stop arrived keeps its result.
        Assert.Equal("STATUS_SUCCESS", ResticEngine.BackupOutcome(Result(0, cancelled: true), Saved).Status);
    }

    // ---- real restic ----

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempWorkspace Workspace;
        public readonly SecretsStore Secrets;
        public readonly Destination Destination;
        public readonly Backup Backup;
        public readonly string Source;
        public readonly string Password = Guid.NewGuid().ToString("N");
        public readonly ResticEngine Engine;
        public readonly ConcurrentQueue<(ResticCommand Command, ResticResult Result)> Commands = new();
        public readonly ConcurrentQueue<string> Log = new();

        public Rig(string label, bool inline = false)
        {
            Assert.True(File.Exists(AppPaths.ResticBinary), "restic is needed: " + AppPaths.ResticBinary);
            Workspace = new TempWorkspace(label);
            Secrets = new SecretsStore();
            Source = Workspace.Sub("source");
            for (var i = 0; i < 20; i++) File.WriteAllBytes(Path.Combine(Source, $"file{i:00}.bin"), RandomNumberGenerator.GetBytes(8 * 1024));
            Destination = new Destination { Id = "dest-" + label, Name = "Local " + label, Kind = DestinationKind.LocalFolder,
                PathOrSubpath = Workspace.Sub("repo"), Encrypted = true, StoragePasswordRef = Secrets.PutNew("direct-" + label, Password) };
            Backup = new Backup { Id = "backup-" + label, Name = "Direct " + label, SourcePaths = { Source }, UseVss = false,
                VssDisabledAcknowledged = true, KeepPolicy = "2",
                PruneSchedule = inline ? null : new BackupSchedule(),
                Targets = { new BackupTarget { DestinationId = "dest-" + label, StorageName = "default" } } };
            Engine = new ResticEngine(Secrets);
            Engine.ResticCommandFinished += (command, result) => Commands.Enqueue((command, result));
        }

        public string PlanId => ResticEngine.MakePlanId(Backup, Source, Destination, "default");
        public string RepoId => ResticEngine.MakeRepoId(Destination, "default");

        public Task<RunRecord> BackUpAsync(CancellationToken ct = default) =>
            Engine.BackupPruneCheckAsync(Backup, Source, Destination, "default", ct, Log.Enqueue);

        public (ResticCommand Command, ResticResult Result)[] Ran(string first) => Commands.Where(c => c.Command.Arguments[0] == first).ToArray();

        public void Forget() { Commands.Clear(); Log.Clear(); }

        public ResticRepository Repository => Engine.BuildResticRepository(Destination, Backup);

        public async Task<ResticResult> ResticAsync(params string[] arguments)
        {
            var result = await ResticRunner.RunAsync(new ResticCommand(AppPaths.ResticBinary, Repository, arguments) { CaptureOutput = 1 << 20 },
                null, null, CancellationToken.None);
            Assert.True(result.Succeeded, string.Join(' ', arguments) + ": " + result.ErrorText);
            return result;
        }

        public ValueTask DisposeAsync()
        {
            Workspace.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static string? Parent((ResticCommand Command, ResticResult Result) backup)
    {
        var arguments = ResticRunner.Arguments(backup.Command).ToList();
        var at = arguments.IndexOf("--parent");
        return at < 0 ? null : arguments[at + 1];
    }

    private static bool IsListing((ResticCommand Command, ResticResult Result) c) =>
        c.Command.Arguments.SequenceEqual(["snapshots", "--json"]) && c.Command.Trailing?.FirstOrDefault() == "--tag";

    private static bool IsReadingOne((ResticCommand Command, ResticResult Result) c) =>
        c.Command.Arguments.SequenceEqual(["snapshots", "--json", "--no-lock"]);

    private static async Task<List<(string Id, string[] Paths, string[] Tags)>> SnapshotsAsync(Rig rig)
    {
        var listed = await rig.ResticAsync("snapshots", "--json");
        using var json = JsonDocument.Parse(listed.Output);
        return json.RootElement.EnumerateArray().Select(s => (s.GetProperty("id").GetString()!,
            s.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).ToArray(),
            s.GetProperty("tags").EnumerateArray().Select(p => p.GetString()!).ToArray())).ToList();
    }

    /// <summary>A backup with its cleanup after it (checks are entries of
    /// their own since U179, never inside a backup): restic only; the parent
    /// of the second backup is the first one's snapshot, confirmed by reading
    /// that one snapshot instead of listing them all; the records hold what
    /// the next backup needs.</summary>
    [Fact]
    public async Task Backups_with_their_cleanup_run_restic_alone_and_remember_their_snapshot()
    {
        await using var rig = new Rig("alone", inline: true);
        var first = await rig.BackUpAsync();
        Assert.True(first.Status == BackupRunStatus.Success, first.Summary);
        Assert.StartsWith("Backup result: STATUS_SUCCESS.", first.Summary);
        Assert.Contains(rig.Commands, c => c.Command.Arguments.SequenceEqual(["cat", "--no-lock", "config"]) && c.Result.ExitCode == 10);
        Assert.Single(rig.Ran("init"));
        Assert.Contains(rig.Commands, IsListing);
        Assert.DoesNotContain(rig.Commands, IsReadingOne);
        Assert.Null(Parent(Assert.Single(rig.Ran("backup"))));
        // No full prune known: the first cleanup prunes everything.
        Assert.Single(rig.Ran("prune"));
        Assert.Contains(rig.Ran("forget"), c => !c.Command.Arguments.Contains("--prune"));
        Assert.Empty(rig.Ran("check"));
        Assert.Empty(rig.Ran("unlock"));
        var snapshot1 = Assert.Single(await SnapshotsAsync(rig));
        Assert.Equal(snapshot1.Id, MaintenanceRecords.RememberedSnapshot(rig.PlanId));
        Assert.NotNull(MaintenanceRecords.LastFullPrune(rig.RepoId));
        Assert.False(MaintenanceRecords.PreviousBackupUnfinished(rig.PlanId));

        rig.Forget();
        File.WriteAllText(Path.Combine(rig.Source, "new.txt"), "second");
        var second = await rig.BackUpAsync();
        Assert.True(second.Status == BackupRunStatus.Success, second.Summary);
        Assert.Empty(rig.Ran("cat"));
        Assert.Empty(rig.Ran("init"));
        Assert.DoesNotContain(rig.Commands, IsListing);
        Assert.Single(rig.Commands, IsReadingOne);
        Assert.Equal(snapshot1.Id, Parent(Assert.Single(rig.Ran("backup"))));
        Assert.Empty(rig.Ran("prune"));
        Assert.Contains(rig.Ran("forget"), c => c.Command.Arguments.Contains("--prune"));
        Assert.Equal(2, (await SnapshotsAsync(rig)).Count);
        Assert.Contains(rig.Log, l => l.StartsWith("[setup] Previous backup to «", StringComparison.Ordinal) && l.Contains(snapshot1.Id[..8]));
        Assert.Contains(rig.Log, l => l.StartsWith(BackupLiveProgress.RecapPrefix, StringComparison.Ordinal));

        // The log shows each command (as Verbose detail, U196), never the password.
        Assert.Contains(rig.Log, l => l.Contains("\tdebug\tcommand: ", StringComparison.Ordinal) && l.Contains(" backup --json ", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Log, l => l.Contains(rig.Password, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other plan")]
    [InlineData("other folder")]
    [InlineData("unreadable")]
    [InlineData("none")]
    [InlineData("unfinished")]
    public async Task A_remembered_snapshot_that_is_wrong_falls_back_to_the_plans_latest(string wrong)
    {
        await using var rig = new Rig("parent-" + wrong.Replace(' ', '-'));
        Assert.Equal(BackupRunStatus.Success, (await rig.BackUpAsync()).Status);
        File.WriteAllText(Path.Combine(rig.Source, "two.txt"), "two");
        Assert.Equal(BackupRunStatus.Success, (await rig.BackUpAsync()).Status);
        var snapshots = await SnapshotsAsync(rig);
        Assert.Equal(2, snapshots.Count);
        var latest = MaintenanceRecords.RememberedSnapshot(rig.PlanId)!;
        var older = snapshots.Single(s => s.Id != latest).Id;
        var expectedParent = latest;
        switch (wrong)
        {
            case "missing":
                // Removed from the repository: the plan's latest is then the older one.
                await rig.ResticAsync("forget", latest);
                expectedParent = older;
                break;
            case "other plan":
            {
                // A snapshot of another backup in the same repository.
                var other = Path.Combine(rig.Workspace.Sub("other"), "x.txt");
                File.WriteAllText(other, "other");
                var made = await rig.ResticAsync("backup", "--json", Path.GetDirectoryName(other)!, "--tag", "plan:another", "--tag", "created-by:backspace");
                MaintenanceRecords.BackupFinished(rig.PlanId, DateTime.UtcNow, SnapshotId(made.Output));
                break;
            }
            case "other folder":
            {
                // This plan's labels on another folder, made before the
                // plan's latest (the list takes the latest).
                var folder = rig.Workspace.Sub("elsewhere");
                File.WriteAllText(Path.Combine(folder, "y.txt"), "elsewhere");
                var made = await rig.ResticAsync("backup", "--json", folder, "--tag", "plan:" + rig.PlanId, "--tag", "created-by:backspace",
                    "--time", DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd HH:mm:ss"));
                MaintenanceRecords.BackupFinished(rig.PlanId, DateTime.UtcNow, SnapshotId(made.Output));
                break;
            }
            case "unreadable":
            {
                // Damaged in the repository, and not in restic's own cache
                // (which restic would read instead): restic cannot read it,
                // and lists the others.
                var repositoryId = ResticEngine.RepositoryId((await rig.ResticAsync("cat", "config")).Output)!;
                PlatformTest.Overwrite(Path.Combine(rig.Destination.PathOrSubpath!, "snapshots", latest), RandomNumberGenerator.GetBytes(300));
                // restic's cache: LocalAppData on Windows, ~/Library/Caches on
                // macOS, $XDG_CACHE_HOME or ~/.cache elsewhere.
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var cacheRoot = Environment.GetEnvironmentVariable("RESTIC_CACHE_DIR") is { Length: > 0 } set ? set
                    : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "restic")
                    : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Caches", "restic")
                    : Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".cache"), "restic");
                foreach (var cached in new[] { Path.Combine(cacheRoot, repositoryId, "snapshots", latest[..2], latest), Path.Combine(cacheRoot, repositoryId, "snapshots", latest) })
                    if (File.Exists(cached)) File.Delete(cached);
                expectedParent = older;
                break;
            }
            case "none":
                File.Delete(MaintenanceRecords.OwnFile);
                break;
            case "unfinished":
                // A later backup started and did not finish: it may have saved
                // a snapshot nothing remembers, so the list decides.
                MaintenanceRecords.BackupStarted(rig.PlanId, DateTime.UtcNow);
                break;
        }
        rig.Forget();
        File.WriteAllText(Path.Combine(rig.Source, "three.txt"), "three");
        var run = await rig.BackUpAsync();
        Assert.True(run.Status == BackupRunStatus.Success, run.Summary);
        Assert.Single(rig.Commands, IsListing);
        Assert.Equal(expectedParent, Parent(Assert.Single(rig.Ran("backup"))));
        if (wrong is "none" or "unfinished") Assert.DoesNotContain(rig.Commands, IsReadingOne);
        else
        {
            Assert.Single(rig.Commands, IsReadingOne);
            Assert.Contains(rig.Log, l => l.StartsWith("[backspace] The previous backup Backspace remembered", StringComparison.Ordinal));
        }
        output.WriteLine(string.Join("\n", rig.Log.Where(l => l.StartsWith("[backspace]") || l.StartsWith("[setup]"))));
    }

    private static string SnapshotId(string backupJson) =>
        backupJson.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{'))
            .Select(l => JsonDocument.Parse(l).RootElement).Last(e => e.GetProperty("message_type").GetString() == "summary")
            .GetProperty("snapshot_id").GetString()!;

    /// <summary>Exit code 3: a file restic could not read. The backup is
    /// saved with a warning, and remembered.</summary>
    [Fact]
    public async Task A_file_restic_cannot_read_gives_a_saved_backup_with_a_warning()
    {
        PlatformTest.WindowsOnly("a file another program holds open (Linux and macOS have no such sharing locks)");
        await using var rig = new Rig("partial");
        using (new FileStream(Path.Combine(rig.Source, "file00.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var run = await rig.BackUpAsync();
            Assert.Equal(BackupRunStatus.Warning, run.Status);
            Assert.Equal("Backup result: STATUS_WARNING. Partial backup, some files may not have been read completely.", run.Summary);
            Assert.Equal(3, Assert.Single(rig.Ran("backup")).Result.ExitCode);
            Assert.Contains(rig.Log, l => l.StartsWith("[restic] {\"message_type\":\"error\"", StringComparison.Ordinal) && l.Contains("file00.bin"));
        }
        Assert.Equal((await SnapshotsAsync(rig)).Single().Id, MaintenanceRecords.RememberedSnapshot(rig.PlanId));
    }

    /// <summary>Exit code 12: a password that no longer opens the repository
    /// is final, with restic's words (the toast's "Check its backup password"),
    /// and no new repository is ever made.</summary>
    [Fact]
    public async Task A_wrong_password_stops_the_backup_with_restics_reason()
    {
        await using var rig = new Rig("password");
        Assert.Equal(BackupRunStatus.Success, (await rig.BackUpAsync()).Status);
        rig.Secrets.Set(rig.Destination.StoragePasswordRef!, "not-the-password");
        rig.Forget();
        var error = await Assert.ThrowsAsync<EngineAuthFailedException>(() => rig.BackUpAsync());
        Assert.Contains("wrong password or no key found", error.Message);
        Assert.Empty(rig.Ran("backup"));
        Assert.Empty(rig.Ran("init"));
    }

    /// <summary>Exit code 11 and the lock rule: a live lock held by another
    /// restic makes cleanup ask for one ordinary unlock (never --remove-all),
    /// which leaves the live lock, and one retry; the backup itself is saved.</summary>
    [Fact]
    public async Task A_live_lock_gets_one_unlock_and_one_retry_and_survives()
    {
        await using var rig = new Rig("lock", inline: true);
        Assert.Equal(BackupRunStatus.Success, (await rig.BackUpAsync()).Status);
        var start = ResticRunner.StartInfo(new ResticCommand(AppPaths.ResticBinary, rig.Repository,
            ["backup", "--stdin", "--stdin-filename", "held.txt", "--quiet"]));
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var held = Process.Start(start)!;
        try
        {
            var locks = Path.Combine(rig.Destination.PathOrSubpath!, "locks");
            for (var wait = Stopwatch.StartNew(); !Directory.EnumerateFiles(locks).Any(); await Task.Delay(50))
                Assert.True(wait.Elapsed < TimeSpan.FromSeconds(20), "The held restic took no lock.");
            rig.Forget();
            File.WriteAllText(Path.Combine(rig.Source, "more.txt"), "more");
            var run = await rig.BackUpAsync();
            Assert.Equal(BackupRunStatus.Warning, run.Status);
            Assert.Contains("maintenance reported STATUS_ERROR", run.Summary);
            Assert.Equal(0, Assert.Single(rig.Ran("backup")).Result.ExitCode);
            var forgets = rig.Ran("forget");
            Assert.Equal(2, forgets.Length);
            Assert.All(forgets, f => Assert.Equal(11, f.Result.ExitCode));
            Assert.Equal(new[] { "unlock" }, Assert.Single(rig.Ran("unlock")).Command.Arguments);
            Assert.DoesNotContain(rig.Commands, c => ResticRunner.Arguments(c.Command).Contains("--remove-all"));
            Assert.False(held.HasExited);
            Assert.NotEmpty(Directory.EnumerateFiles(locks));
        }
        finally
        {
            if (!held.HasExited) { held.Kill(entireProcessTree: true); await held.WaitForExitAsync(); }
        }
    }

    /// <summary>A backup stopped part way: restic stops gently (no lock left),
    /// the run says "stopped on request", and the next
    /// backup knows the previous one did not finish: it lists the snapshots
    /// rather than trust its memory, and prunes everything after its cleanup.</summary>
    [Fact]
    public async Task A_stopped_backup_is_stopped_gently_and_the_next_one_prunes_after_it()
    {
        await using var rig = new Rig("stopped", inline: true);
        Assert.Equal(BackupRunStatus.Success, (await rig.BackUpAsync()).Status);
        for (var i = 0; i < 6; i++) File.WriteAllBytes(Path.Combine(rig.Source, $"big{i}.bin"), RandomNumberGenerator.GetBytes(1 << 20));
        rig.Backup.BandwidthLimitKBps = 64;
        rig.Forget();
        using var stop = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            while (!rig.Log.Any(l => l.StartsWith(BackupLiveProgress.Prefix, StringComparison.Ordinal))) await Task.Delay(50);
            stop.Cancel();
        });
        var stopped = await rig.BackUpAsync(stop.Token).WaitAsync(TimeSpan.FromMinutes(3));
        await watch;
        Assert.Equal(BackupRunStatus.Skipped, stopped.Status);
        Assert.True(ResticEngine.WasStoppedOnRequest(stopped), stopped.Summary);
        Assert.NotEqual(ResticStop.None, Assert.Single(rig.Ran("backup")).Result.Stop);
        Assert.Contains(rig.Log, l => l.EndsWith("\tdebug\tsignal interrupt received, cleaning up", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Destination.PathOrSubpath!, "locks")));
        Assert.True(MaintenanceRecords.PreviousBackupUnfinished(rig.PlanId));

        rig.Backup.BandwidthLimitKBps = 0;
        rig.Forget();
        var next = await rig.BackUpAsync();
        Assert.True(next.Status == BackupRunStatus.Success, next.Summary);
        Assert.Single(rig.Commands, IsListing);
        Assert.Single(rig.Ran("prune"));
        Assert.False(MaintenanceRecords.PreviousBackupUnfinished(rig.PlanId));
    }

    // ---- the scheduled path ----

    /// <summary>A scheduled backup is Backspace.exe --run (Task Scheduler, and
    /// the service's worker runs the same orchestrator). Two runs, with their
    /// cleanup: every process under Backspace is watched while it
    /// lasts: restic appears (so the watch sees short-lived processes). The
    /// backup has no checks: they are entries of their own (U179).</summary>
    [Fact]
    public async Task A_scheduled_backup_runs_restic_and_keeps_its_records()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var ws = new TempWorkspace("scheduled-backup");
        var source = ws.Sub("source");
        for (var i = 0; i < 40; i++) File.WriteAllBytes(Path.Combine(source, $"file{i:00}.bin"), RandomNumberGenerator.GetBytes(64 * 1024));
        var config = new ConfigStore(); config.Load();
        var secrets = new SecretsStore();
        var password = Guid.NewGuid().ToString("N");
        var destination = new Destination { Name = "Scheduled repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ws.Sub("repository"), Encrypted = true, StoragePasswordRef = secrets.PutNew("scheduled-backup", password) };
        var backup = new Backup { Name = "Scheduled backup", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            KeepPolicy = "2", PruneSchedule = null, // cleanup right after the backup
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var exe = Path.ChangeExtension(typeof(ResticRunner).Assembly.Location, ".exe");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var run = 1; run <= 2; run++)
        {
            if (run == 2) File.WriteAllText(Path.Combine(source, "changed.txt"), "second run");
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--run"); start.ArgumentList.Add(backup.Name);
            start.Environment["BACKSPACE_CONFIG_ROOT"] = ws.ConfigRoot;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                foreach (var name in ProcessTree.NamesBelow(process.Id)) names.Add(name);
                await Task.Delay(15, TestContext.Current.CancellationToken);
            }
            Assert.True(process.HasExited, "The scheduled backup did not finish in 3 minutes.");
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            var text = await stdout + await stderr;
            output.WriteLine($"Run {run}: exit {process.ExitCode}; processes under Backspace: {string.Join(", ", names.Order())}");
            output.WriteLine(text);
            Assert.True(process.ExitCode == 0, text);
            Assert.Contains("Scheduled backup: Success - ", text);
            Assert.Contains("restic.exe", names); // the watch does see what Backspace starts
            seen.UnionWith(names);
        }

        // Both runs are in the history, with their cleanup, and the
        // repository holds both backups; the second one's parent is the first.
        var history = new LogStore().LoadHistory(backup.Name);
        Assert.Equal(2, history.Count(r => r.Status == BackupRunStatus.Success));
        var planId = ResticEngine.MakePlanId(backup, source, destination, "default");
        var remembered = MaintenanceRecords.RememberedSnapshot(planId);
        Assert.NotNull(remembered);
        Assert.NotNull(MaintenanceRecords.LastFullPrune(ResticEngine.MakeRepoId(destination, "default")));
        var listed = await ResticRunner.RunAsync(new ResticCommand(AppPaths.ResticBinary,
            new ResticRepository(destination.PathOrSubpath!, password, [], []), ["snapshots", "--json"]) { CaptureOutput = 1 << 20 },
            null, null, CancellationToken.None);
        Assert.True(listed.Succeeded, listed.ErrorText);
        using var json = JsonDocument.Parse(listed.Output);
        var snapshots = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, snapshots.Count);
        var latest = snapshots.Single(s => s.GetProperty("id").GetString() == remembered);
        Assert.Equal(snapshots.Single(s => s.GetProperty("id").GetString() != remembered).GetProperty("id").GetString(),
            latest.GetProperty("parent").GetString());
    }

    /// <summary>The processes below one process, by name, from a snapshot of
    /// every process's parent (Windows).</summary>
    private static class ProcessTree
    {
        public static List<string> NamesBelow(int root)
        {
            var all = new List<(int Id, int Parent, string Name)>();
            var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
            if (snapshot == new IntPtr(-1)) return [];
            try
            {
                var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>() };
                for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                    all.Add(((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
            }
            finally { CloseHandle(snapshot); }
            var names = new List<string>();
            var visited = new HashSet<int> { root };
            var parents = new Queue<int>([root]);
            while (parents.TryDequeue(out var parent))
                foreach (var child in all.Where(p => p.Parent == parent && visited.Add(p.Id)))
                {
                    names.Add(child.Name);
                    parents.Enqueue(child.Id);
                }
            return names;
        }

        private const uint SnapProcess = 0x2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Entry
        {
            public uint Size, Usage, ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId, Threads, ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
