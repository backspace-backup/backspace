using System;
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

public sealed class FinalPersistenceRegressionTests
{
    [Theory]
    [InlineData(RunKind.Backup)]
    [InlineData(RunKind.Prune)]
    [InlineData(RunKind.Check)]
    public async Task Actual_unattended_entry_points_reject_corrupt_vault_before_starting_operations_and_next_launch_repeats_warning(RunKind kind)
    {
        using var workspace = new TempWorkspace("unattended-vault");
        var config = new ConfigStore();
        config.Load();
        var destination = new Destination { Name = "Local fixture", PathOrSubpath = workspace.Sub("destination"), Kind = DestinationKind.LocalFolder };
        var backup = new Backup { Name = "unattended", UseVss = false, SourcePaths = { workspace.Sub("source") },
            Targets = { new BackupTarget { DestinationId = destination.Id } } };
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var originalConfig = ServiceLocator.Config;
        var originalSecrets = ServiceLocator.Secrets;
        var oldError = Console.Error;
        using var errors = new StringWriter();
        int operationsStarted = 0;
        void Started(string _) => operationsStarted++;
        ServiceLocator.Orchestrator.RunStarted += Started;
        var fake = Assert.IsType<InMemoryBackupEngine>(ServiceLocator.Engine);
        var priorCalls = fake.Calls.Count;
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x12 };
        File.WriteAllBytes(AppPaths.SecretsFile, bytes);
        try
        {
            SetService(nameof(ServiceLocator.Config), config);
            Console.SetError(errors);
            for (int launch = 0; launch < 2; launch++)
            {
                SetService(nameof(ServiceLocator.Secrets), new SecretsStore());
                Assert.Null(ServiceLocator.Secrets.LoadErrorMessage);
                Assert.Equal(4, await InvokeUnattended(kind, backup.Name));
                Assert.Equal(bytes, File.ReadAllBytes(AppPaths.SecretsFile));
                Assert.NotNull(ServiceLocator.Secrets.LoadErrorMessage);
            }
            Assert.Equal(0, operationsStarted);
            Assert.Equal(priorCalls, fake.Calls.Count);
            Assert.Contains("saved credentials are unavailable", errors.ToString());
            Assert.Empty(Directory.GetFiles(workspace.ConfigRoot, "secrets.bin.unreadable-*"));

            // Valid, explicitly recovered credentials proceed past this gate;
            // a nonexistent backup has the normal distinct exit2 result.
            ServiceLocator.Secrets.Set("recovered", "fixture-value");
            Assert.Equal(2, await InvokeUnattended(kind, "not-a-configured-backup"));
        }
        finally
        {
            Console.SetError(oldError);
            ServiceLocator.Orchestrator.RunStarted -= Started;
            SetService(nameof(ServiceLocator.Config), originalConfig);
            SetService(nameof(ServiceLocator.Secrets), originalSecrets);
        }
    }

    [Fact]
    public async Task Actual_unattended_entry_point_rejects_temporarily_locked_vault_without_changing_bytes()
    {
        using var workspace = new TempWorkspace("unattended-locked-vault");
        var store = new SecretsStore();
        store.Set("fixture", "keep");
        var bytes = File.ReadAllBytes(AppPaths.SecretsFile);
        var original = ServiceLocator.Secrets;
        try
        {
            SetService(nameof(ServiceLocator.Secrets), new SecretsStore());
            using (var locked = new FileStream(AppPaths.SecretsFile, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Equal(4, await InvokeUnattended(RunKind.Backup, "not-a-configured-backup"));
            Assert.Equal(bytes, File.ReadAllBytes(AppPaths.SecretsFile));
            Assert.Null(ServiceLocator.Secrets.LoadErrorMessage);
            Assert.Equal("keep", ServiceLocator.Secrets.Get("fixture"));
        }
        finally { SetService(nameof(ServiceLocator.Secrets), original); }
    }

    [Fact]
    public void Same_instant_transcript_names_are_unique_for_backup_restore_and_verification()
    {
        using var workspace = new TempWorkspace("transcript-name");
        var instant = new DateTime(2026, 10, 3, 10, 20, 30, DateTimeKind.Utc);
        Directory.CreateDirectory(AppPaths.LogDirForBackup("same-backup"));
        foreach (var label in new[] { "Docs-target-default", "restore", "test-restore" })
        {
            var writes = Enumerable.Range(0, 100).AsParallel().Select(index =>
            {
                var path = LogStore.NewTranscriptPath("same-backup", label, instant);
                var content = $"BEGIN {index}\nEND {index}\n";
                File.WriteAllText(path, content);
                return (path, content);
            }).ToArray();
            Assert.Equal(100, writes.Select(w => w.path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(writes, write =>
            {
                Assert.EndsWith(".log", write.path);
                Assert.Equal(write.content, File.ReadAllText(write.path));
            });
        }
    }

    [Fact]
    public async Task Consecutive_same_named_sources_keep_distinct_complete_transcripts_and_history_links()
    {
        using var workspace = new TempWorkspace("parallel-docs-transcripts");
        var config = new ConfigStore(); config.Load();
        var secrets = new SecretsStore();
        var logs = new LogStore();
        var engine = DispatchProxy.Create<IBackupEngine, TranscriptEngine>();
        var proxy = (TranscriptEngine)(object)engine;
        var destination = new Destination { Name = "Same target", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("destination"), Encrypted = false };
        var first = workspace.Sub("A/Docs"); var second = workspace.Sub("B/Docs");
        File.WriteAllText(Path.Combine(first, "first.txt"), "first");
        File.WriteAllText(Path.Combine(second, "second.txt"), "second");
        var backup = new Backup { Name = "Parallel Docs", UseVss = false, Threads = 2, SourcePaths = { first, second },
            Targets = { new BackupTarget { DestinationId = destination.Id } } };
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var orchestrator = new BackupOrchestrator(config, secrets, engine, logs,
            new HealthcheckService(config, secrets), new MailService(config, secrets),
            new NotificationService(config), new BackupProgressService());
        var result = await orchestrator.RunBackupAsync(backup, CancellationToken.None);
        Assert.Equal(BackupRunStatus.Success, result.Status);
        Assert.Equal(2, proxy.Entered);
        Assert.Equal(2, result.Cells.Count);
        Assert.Equal(2, result.Cells.Select(c => c.LogPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var cell in result.Cells)
        {
            var text = File.ReadAllText(cell.LogPath);
            Assert.Contains("BEGIN " + cell.SourcePath, text);
            Assert.Contains("END " + cell.SourcePath, text);
            Assert.Contains("--- cell outcome ---", text);
            Assert.DoesNotContain("BEGIN " + (cell.SourcePath == first ? second : first), text);
        }
        var saved = Assert.Single(logs.LoadHistoryByBackupId(backup.Id));
        Assert.Equal(result.Cells.Select(c => c.LogPath).Order(), saved.Cells.Select(c => c.LogPath).Order());
    }

    private static Task<int> InvokeUnattended(RunKind kind, string backupName)
    {
        var method = typeof(Backspace.Program).GetMethod(kind == RunKind.Backup ? "RunUnattended" : "RunUnattendedMaintenance",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (Task<int>)method.Invoke(null, kind == RunKind.Backup ? new object?[] { backupName, true } : new object?[] { backupName, kind })!;
    }

    private static void SetService(string name, object value)
        => typeof(ServiceLocator).GetProperty(name)!.SetMethod!.Invoke(null, new[] { value });

    public class TranscriptEngine : DispatchProxy
    {
        private readonly InMemoryBackupEngine _inner = new();
        private int _entered;
        public int Entered => _entered;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IBackupEngine.BackupPruneCheckAsync))
                return BackupAsync((Backup)args![0]!, (string)args[1]!, (Action<string>?)args[5], (CancellationToken)args[4]!);
            return method.Invoke(_inner, args);
        }
        private Task<RunRecord> BackupAsync(Backup backup, string source, Action<string>? sink, CancellationToken ct)
        {
            sink?.Invoke("BEGIN " + source);
            Interlocked.Increment(ref _entered);
            sink?.Invoke("END " + source);
            return Task.FromResult(new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Status = BackupRunStatus.Success,
                StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow, Summary = "Fixture completed" });
        }
    }
}
