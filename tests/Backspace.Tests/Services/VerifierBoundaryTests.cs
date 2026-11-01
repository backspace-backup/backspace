using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class VerifierBoundaryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Offline_or_missing_second_destination_cannot_disappear_from_success_verdict(bool missing)
    {
        using var workspace = new TempWorkspace();
        var source = workspace.Sub("source");
        File.WriteAllText(Path.Combine(source, "file.txt"), "unchanged bytes");
        var online = new Destination { Name = "Online", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = workspace.Sub("online"), Encrypted = false };
        var unavailable = new Destination { Name = "Unavailable", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = workspace.Sub("offline"), Encrypted = false,
            ExpectedDriveLetter = Path.GetPathRoot(workspace.Root), ExpectedVolumeLabel = "not-a-real-volume-" + Guid.NewGuid().ToString("N") };
        var backup = new Backup { Name = "All targets " + Guid.NewGuid().ToString("N"), UseVss = false, VssDisabledAcknowledged = true,
            SourcePaths = { source }, Targets = { new BackupTarget { DestinationId = online.Id, StorageName = "online" },
                new BackupTarget { DestinationId = unavailable.Id, StorageName = "unavailable" } } };
        var config = new ConfigStore(); config.Load();
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(online); if (!missing) c.Destinations.Add(unavailable); });
        var engine = new InMemoryBackupEngine();
        await engine.BackupPruneCheckAsync(backup, source, online, "online", default);
        var secrets = new SecretsStore();
        var verifier = new BackupVerifier(config, new RestoreEngine(config, engine, secrets), new RevisionBrowser(engine, secrets), engine);
        var outcome = await verifier.VerifyAsync(backup, default);
        Assert.False(outcome.OverallPass, outcome.Summary);
        Assert.Contains("1 of 2 destinations FAILED", outcome.Summary);
        Assert.Contains("1 of 2 destinations SUCCEEDED", outcome.Summary);
        Assert.Single(outcome.Files);
        Assert.Equal(FileVerifyStatus.OK, outcome.Files[0].Status);
        Assert.NotNull(outcome.FatalError);
    }

    [Fact]
    public async Task Two_storage_targets_on_one_destination_are_both_verified_and_second_failure_is_visible()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.Sub("source");
        File.WriteAllText(Path.Combine(source, "file.txt"), "unchanged bytes");
        var destination = new Destination { Name = "Shared drive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = workspace.Sub("repository"), Encrypted = false };
        var backup = new Backup { Name = "Storage targets " + Guid.NewGuid().ToString("N"), UseVss = false, VssDisabledAcknowledged = true,
            SourcePaths = { source }, Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "first" },
                new BackupTarget { DestinationId = destination.Id, StorageName = "second" } } };
        var config = new ConfigStore(); config.Load();
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var engine = DispatchProxy.Create<IBackupEngine, TerminalOverrideProxy>();
        var proxy = (TerminalOverrideProxy)(object)engine;
        proxy.Terminal = RestoreTerminalStatus.Error;
        proxy.FailureStorage = "second";
        await engine.BackupPruneCheckAsync(backup, source, destination, "first", default);
        var secrets = new SecretsStore();
        var verifier = new BackupVerifier(config, new RestoreEngine(config, engine, secrets), new RevisionBrowser(engine, secrets), engine);
        var outcome = await verifier.VerifyAsync(backup, default);
        Assert.Equal(new[] { "first", "second" }, proxy.RestoredStorageNames);
        Assert.False(outcome.OverallPass, outcome.Summary);
        Assert.Contains("1 of 2 destinations FAILED", outcome.Summary);
        Assert.Contains("1 of 2 destinations SUCCEEDED", outcome.Summary);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("\\\\server\\share\\outside.txt")]
    [InlineData("file.txt\n/other.txt")]
    public void Unsafe_snapshot_path_cannot_reach_file_verification_or_cleanup(string relative)
    {
        using var workspace = new TempWorkspace();
        var source = workspace.Sub("source");
        var restored = workspace.Sub("restored");
        var outside = Path.Combine(workspace.Root, "outside.txt");
        File.WriteAllText(outside, "outside sentinel");
        Assert.Throws<InvalidOperationException>(() => BackupVerifier.SafeVerificationPath(restored, relative));
        Assert.Throws<InvalidOperationException>(() => BackupVerifier.VerifyOneFile(
            new RevisionFile(relative, 16, DateTime.UtcNow), source, restored, new RestoreOutcome()));
        Assert.Equal("outside sentinel", File.ReadAllText(outside));
        Assert.Equal(Path.Combine(restored, "folder", "safe.txt"),
            BackupVerifier.SafeVerificationPath(restored, "folder/safe.txt"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Linked_source_or_restore_folder_is_not_followed(bool linkInRestore)
    {
        using var workspace = new TempWorkspace();
        var source = workspace.Sub("source");
        var restored = workspace.Sub("restored");
        var outside = workspace.Sub("outside");
        var outsideFile = Path.Combine(outside, "secret.txt");
        File.WriteAllText(outsideFile, "outside sentinel");
        var ordinary = Path.Combine(linkInRestore ? source : restored, "linked");
        Directory.CreateDirectory(ordinary);
        File.WriteAllText(Path.Combine(ordinary, "secret.txt"), "outside sentinel");
        var link = Path.Combine(linkInRestore ? restored : source, "linked");
        CreateDirectoryLink(link, outside);
        try
        {
            Assert.Throws<IOException>(() => BackupVerifier.VerifyOneFile(
                new RevisionFile("linked/secret.txt", 16, DateTime.UtcNow.AddMinutes(1)),
                source, restored, new RestoreOutcome()));
            Assert.Equal("outside sentinel", File.ReadAllText(outsideFile));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void Byte_comparison_honors_cancellation_before_file_access_and_detects_equal_size_corruption()
    {
        using var workspace = new TempWorkspace();
        var first = Path.Combine(workspace.Root, "first");
        var second = Path.Combine(workspace.Root, "second");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => BackupVerifier.BytesEqual(first, second, cancelled.Token));
        File.WriteAllText(first, "same");
        File.WriteAllText(second, "same");
        Assert.True(BackupVerifier.BytesEqual(first, second));
        File.WriteAllText(second, "diff");
        Assert.False(BackupVerifier.BytesEqual(first, second));
    }

    [Theory]
    [InlineData(RestoreTerminalStatus.Error)]
    [InlineData(RestoreTerminalStatus.Warning)]
    public async Task Matching_restored_files_do_not_hide_failed_restore_terminal(RestoreTerminalStatus terminal)
    {
        using var workspace = new TempWorkspace();
        var source = workspace.Sub("source");
        File.WriteAllText(Path.Combine(source, "file.txt"), "unchanged bytes");
        var destination = new Destination { Name = "Isolated verifier", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = workspace.Sub("repository"), Encrypted = false };
        var backup = new Backup { Name = "Verifier terminal " + Guid.NewGuid().ToString("N"), UseVss = false, VssDisabledAcknowledged = true,
            SourcePaths = { source }, Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        var config = new ConfigStore();
        config.Load();
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var engine = DispatchProxy.Create<IBackupEngine, TerminalOverrideProxy>();
        var proxy = (TerminalOverrideProxy)(object)engine;
        proxy.Terminal = terminal;
        await engine.BackupPruneCheckAsync(backup, source, destination, "default", default);
        var secrets = new SecretsStore();
        var verifier = new BackupVerifier(config, new RestoreEngine(config, engine, secrets),
            new RevisionBrowser(engine, secrets), engine);
        var failed = await verifier.VerifyAsync(backup, default);
        Assert.False(failed.OverallPass, failed.Summary);
        Assert.Contains(failed.Files, f => f.Status == FileVerifyStatus.RestoreFailed);
        Assert.Contains(failed.Files, f => f.Path == "file.txt" && f.Status == FileVerifyStatus.OK);
        proxy.Terminal = RestoreTerminalStatus.Success;
        var success = await verifier.VerifyAsync(backup, default);
        Assert.True(success.OverallPass, success.Summary);
        Assert.Equal(1, success.FilesOk);
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + Quote(link) +
            " -Target " + Quote(target) + " | Out-Null");
        using var child = Process.Start(info)!;
        var errors = child.StandardError.ReadToEnd();
        Assert.True(child.WaitForExit(10000), "Isolated junction creation timed out.");
        Assert.True(child.ExitCode == 0, errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test_restore_waits_for_cleanup_or_reports_that_the_user_stopped_waiting(bool persistent)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var source = workspace.Sub("source");
        File.WriteAllText(Path.Combine(source, "file.txt"), "unchanged bytes");
        var destination = new Destination { Name = "Cleanup lock fixture", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = workspace.Sub("repository"), Encrypted = false };
        var backup = new Backup { Name = "Verifier cleanup " + Guid.NewGuid().ToString("N"), UseVss = false, VssDisabledAcknowledged = true,
            SourcePaths = { source }, Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        var config = new ConfigStore(); config.Load();
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var engine = DispatchProxy.Create<IBackupEngine, TerminalOverrideProxy>();
        var proxy = (TerminalOverrideProxy)(object)engine;
        proxy.Terminal = RestoreTerminalStatus.Success;
        await engine.BackupPruneCheckAsync(backup, source, destination, "default", default);
        var secrets = new SecretsStore();
        var verifier = new BackupVerifier(config, new RestoreEngine(config, engine, secrets), new RevisionBrowser(engine, secrets), engine);
        var held = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Every handle the test opens is closed when it ends, even if a restore
        // lands after a failed assertion; a leaked handle would keep the
        // verifier's cleanup retrying forever and hang the whole run (U041).
        var gate = new object();
        var handles = new List<FileStream>();
        var ended = false;
        FileStream? handle = null;
        string? scratch = null;
        proxy.AfterRestore = target =>
        {
            lock (gate)
            {
                if (ended) return;
                scratch = target;
                // Comparison can read the restored file, but Windows must refuse deletion.
                handle = new FileStream(Path.Combine(target, "file.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
                handles.Add(handle);
            }
            held.TrySetResult(target);
        };
        var verification = Task.Run(() => verifier.VerifyAsync(backup, default));
        // Generous limits: the notice only appears after five seconds of
        // retrying, and the full suite can slow this test several-fold.
        var limit = TimeSpan.FromSeconds(60);
        try
        {
            await held.Task.WaitAsync(limit, TestContext.Current.CancellationToken);
            await Task.Delay(250);
            Assert.False(verification.IsCompleted, "Cleanup must retry while the real Windows handle still prevents deletion.");
            if (!persistent) handle!.Dispose();
            else
            {
                var deadline = DateTime.UtcNow + limit;
                while (!FileAccessRetry.Snapshot().Any(n => n.Path == scratch) && DateTime.UtcNow < deadline)
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                Assert.Single(FileAccessRetry.Snapshot(), n => n.Path == scratch).StopCommand.Execute(null);
            }
            var outcome = await verification.WaitAsync(limit, TestContext.Current.CancellationToken);
            if (persistent)
            {
                Assert.False(outcome.OverallPass, outcome.Summary);
                Assert.Contains("temporary copies could not be removed", outcome.FatalError);
                Assert.Equal("unchanged bytes", File.ReadAllText(Path.Combine(scratch!, "file.txt")));
            }
            else
            {
                Assert.True(outcome.OverallPass, outcome.Summary);
                Assert.Equal(FileVerifyStatus.OK, Assert.Single(outcome.Files).Status);
                Assert.False(Directory.Exists(scratch));
            }
            Assert.Equal("unchanged bytes", File.ReadAllText(Path.Combine(source, "file.txt")));
        }
        finally
        {
            lock (gate) { ended = true; foreach (var h in handles) h.Dispose(); }
            if (!verification.IsCompleted)
                foreach (var notice in FileAccessRetry.Snapshot().Where(n => n.Path == scratch)) notice.StopCommand.Execute(null);
            await verification.WaitAsync(limit);
            if (scratch is not null) ResticBackrestEngine.DeleteRestoreStaging(scratch);
        }
    }

    public class TerminalOverrideProxy : DispatchProxy
    {
        private readonly InMemoryBackupEngine _inner = new();
        public RestoreTerminalStatus Terminal { get; set; }
        public string? FailureStorage { get; set; }
        public Action<string>? AfterRestore { get; set; }
        public List<string> RestoredStorageNames { get; } = new();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(_inner, args);
            if (targetMethod.Name != nameof(IBackupEngine.RestoreFilesAsync)) return result;
            var storage = (string)args![3]!;
            RestoredStorageNames.Add(storage);
            return Override((Task<RestoreOpResult>)result!, storage, (string)args[6]!);
        }
        private async Task<RestoreOpResult> Override(Task<RestoreOpResult> task, string storage, string target)
        {
            var result = await task;
            AfterRestore?.Invoke(target);
            var terminal = FailureStorage is null || FailureStorage == storage ? Terminal : RestoreTerminalStatus.Success;
            return result with { Status = terminal, ErrorMessage = terminal == RestoreTerminalStatus.Success ? null : "Injected terminal failure after bytes landed." };
        }
    }
}
