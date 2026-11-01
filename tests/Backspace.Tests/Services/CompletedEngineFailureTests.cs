using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class CompletedEngineFailureTests
{
    [Theory]
    [InlineData("/v1.Backrest/AddRepo", "{\"message_type\":\"exit_error\",\"message\":\"Fatal: create key failed: repository already contains keys\"}", true)]
    [InlineData("/v1.Backrest/Backup", "{\"message_type\":\"exit_error\",\"message\":\"Fatal: create key failed: repository already contains keys\"}", false)]
    [InlineData("/v1.Backrest/AddRepo", "{\"message_type\":\"exit_error\",\"message\":\"Fatal: open repository already contains keys.txt: sharing violation\"}", false)]
    [InlineData("/v1.Backrest/AddRepo", "{\"message_type\":\"status\",\"message\":\"Example: repository already contains keys\"}", false)]
    [InlineData("/v1.Backrest/AddRepo", "{\"message_type\":\"exit_error\",\"message\":\"Fatal: save config: Access is denied\"}", false)]
    [InlineData("/v1.Backrest/AddRepo", "{broken json: repository already contains keys", false)]
    [InlineData("/v1.Backrest/AddRepo", "{\"message_type\":42,\"message\":null}", false)]
    public void Interrupted_initialization_requires_the_actual_structured_terminal_error(string endpoint, string output, bool expected)
    {
        var original = new BackrestRpcException(endpoint, 500, "unknown", "failed to init repo\nOutput:\n" + output);
        var translated = ResticBackrestEngine.TranslateRpcError(original, "AddRepo");
        Assert.Equal(expected, translated is EngineRepositoryInitializationException);
        if (expected) Assert.IsAssignableFrom<INonRetriable>(translated);
        else Assert.Same(original, translated);
    }

    [AvaloniaFact]
    public async Task Real_interrupted_initialization_fails_once_and_preserves_every_existing_file()
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        using var workspace = new TempWorkspace("interrupted-init");
        var (config, secrets, logs, backup, destination) = Setup(workspace);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(AppPaths.ResticBinary)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-r", destination.PathOrSubpath, "init", "--insecure-no-password", "--json" })
            start.ArgumentList.Add(argument);
        start.Environment["RESTIC_PASSWORD"] = "";
        using (var process = Process.Start(start)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
        }
        // Only this disposable repository is damaged: simulate interruption after
        // key creation but before publishing its config. Never repair it in the app.
        File.Delete(Path.Combine(destination.PathOrSubpath, "config"));
        var before = Directory.GetFiles(destination.PathOrSubpath, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        Assert.NotEmpty(before);
        await using var sidecar = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, workspace.Sub("backrest"));
        var engine = new ResticBackrestEngine(sidecar, secrets);
        var run = await Orchestrator(config, secrets, logs, engine).RunBackupAsync(backup, deadline.Token);
        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Equal(1, Assert.Single(run.Cells).AttemptsMade);
        Assert.Contains("unfinished repository setup", Assert.Single(run.Cells).LastError);
        Assert.Contains("repository already contains keys", Assert.Single(run.Cells).LastError);
        Assert.Equal(1, Assert.Single(Assert.Single(logs.LoadHistoryByBackupId(backup.Id)).Cells).AttemptsMade);
        var after = Directory.GetFiles(destination.PathOrSubpath, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        Assert.Equal(before.OrderBy(pair => pair.Key), after.OrderBy(pair => pair.Key));
        Assert.False(File.Exists(Path.Combine(destination.PathOrSubpath, "config")));
    }

    /// <summary>U045: a folder that already holds another backup (another
    /// Backspace copy, another password) failed four times with restic's
    /// "config file already exists". It must fail once, say so plainly and
    /// leave every file of the other backup as it was.</summary>
    [AvaloniaFact]
    public async Task A_folder_holding_another_backup_fails_once_with_a_plain_reason()
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        using var workspace = new TempWorkspace("foreign-repository");
        var (config, secrets, logs, backup, destination) = Setup(workspace);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(AppPaths.ResticBinary)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-r", destination.PathOrSubpath, "init", "--json" })
            start.ArgumentList.Add(argument);
        start.Environment["RESTIC_PASSWORD"] = "another copy's own password";
        using (var process = Process.Start(start)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
        }
        var before = Directory.GetFiles(destination.PathOrSubpath, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        await using var sidecar = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, workspace.Sub("backrest"));
        var engine = new ResticBackrestEngine(sidecar, secrets);
        var run = await Orchestrator(config, secrets, logs, engine).RunBackupAsync(backup, deadline.Token);
        Assert.Equal(BackupRunStatus.Failed, run.Status);
        var cell = Assert.Single(run.Cells);
        Assert.Equal(1, cell.AttemptsMade);
        Assert.Contains("already holds another backup", cell.LastError);
        var after = Directory.GetFiles(destination.PathOrSubpath, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        Assert.Equal(before.OrderBy(pair => pair.Key), after.OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData("oauth2: \"invalid_grant\" \"refresh token is invalid\"", BackupRunStatus.Failed, 1, false)]
    [InlineData("401 Unauthorized", BackupRunStatus.Failed, 1, true)]
    [InlineData("Fatal: repository does not exist: unable to open config file: The system cannot find the file specified.", BackupRunStatus.Failed, 1, false)]
    [InlineData("Fatal: repository does not exist: unable to open config file: The system cannot find the file specified.", BackupRunStatus.Failed, 1, true)]
    [InlineData("Fatal: repository does not exist: The network path was not found.", BackupRunStatus.Skipped, 1, false)]
    [InlineData("Could not read notes/repository does not exist.txt", BackupRunStatus.Failed, 4, true)]
    [InlineData("dial tcp 127.0.0.1:56789: connectex: No connection could be made because the target machine actively refused it.", BackupRunStatus.Skipped, 1, false)]
    [InlineData("dial tcp: connection refused", BackupRunStatus.Skipped, 1, true)]
    [InlineData("The repository returned an unfamiliar failure.", BackupRunStatus.Failed, 4, true)]
    [InlineData("Could not read source file invalid_grant.txt", BackupRunStatus.Failed, 4, false)]
    public async Task Completed_failure_keeps_its_reason_and_uses_the_normal_classification(
        string reason, BackupRunStatus expected, int attempts, bool summaryOnly)
    {
        using var workspace = new TempWorkspace();
        var (config, secrets, logs, backup, _) = Setup(workspace);
        var engine = DispatchProxy.Create<IBackupEngine, ResultEngine>();
        var results = (ResultEngine)(object)engine;
        results.Result = new RunRecord { Status = BackupRunStatus.Failed, Summary = reason };
        if (!summaryOnly) results.Result.Errors.Add(reason);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = await Orchestrator(config, secrets, logs, engine).RunBackupAsync(backup, deadline.Token);

        Assert.Equal(expected, run.Status);
        Assert.Equal(expected, Assert.Single(run.Cells).Status);
        Assert.Equal(attempts, results.Calls);
        Assert.Contains(reason, Assert.Single(run.Cells).LastError);
        var persisted = Assert.Single(logs.LoadHistoryByBackupId(backup.Id));
        Assert.Equal(expected, persisted.Status);
        Assert.Contains(reason, Assert.Single(persisted.Cells).LastError);
        Assert.Equal(expected, config.Current.Backups.Single().LastRunStatus);
        Assert.Null(config.Current.Backups.Single().LastSuccessUtc);
        if (expected == BackupRunStatus.Failed) Assert.Contains(run.Errors, error => error.Contains(reason));
    }

    [AvaloniaFact]
    public async Task Existing_repository_losing_its_config_fails_once_without_reinitializing_or_changing_files()
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        using var workspace = new TempWorkspace("missing-repository-config");
        var (config, secrets, logs, backup, destination) = Setup(workspace);
        await using var sidecar = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, workspace.Sub("backrest"));
        var engine = new ResticBackrestEngine(sidecar, secrets);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var first = await engine.BackupPruneCheckAsync(backup, backup.SourcePaths.Single(), destination, "default", deadline.Token);
        Assert.Equal(BackupRunStatus.Success, first.Status);
        // Disposable fixture only. The engine or a scanner can still hold the
        // just-written file briefly; retry only that sharing violation.
        for (var attempt = 0; ; attempt++)
        {
            try { File.Delete(Path.Combine(destination.PathOrSubpath, "config")); break; }
            catch (IOException ex) when (attempt < 20 && (ex.HResult & 0xFFFF) == 32) { await Task.Delay(100, deadline.Token); }
        }
        var before = Directory.GetFiles(destination.PathOrSubpath, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        var run = await Orchestrator(config, secrets, logs, engine).RunBackupAsync(backup, deadline.Token);
        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Equal(1, Assert.Single(run.Cells).AttemptsMade);
        Assert.Contains("repository does not exist", Assert.Single(run.Cells).LastError);
        var after = Directory.GetFiles(destination.PathOrSubpath, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        Assert.NotEmpty(before);
        Assert.Equal(before.OrderBy(pair => pair.Key), after.OrderBy(pair => pair.Key));
        Assert.False(File.Exists(Path.Combine(destination.PathOrSubpath, "config")));
    }

    [Theory]
    [InlineData(BackupRunStatus.Success)]
    [InlineData(BackupRunStatus.Warning)]
    public async Task Completed_content_is_not_reclassified_from_old_or_warning_diagnostics(BackupRunStatus status)
    {
        using var workspace = new TempWorkspace();
        var (config, secrets, logs, backup, _) = Setup(workspace);
        var engine = DispatchProxy.Create<IBackupEngine, ResultEngine>();
        var results = (ResultEngine)(object)engine;
        results.Result = new RunRecord { Status = status, Summary = "Content completed; diagnostic mentions 401 Unauthorized." };
        var run = await Orchestrator(config, secrets, logs, engine).RunBackupAsync(backup, default);
        Assert.Equal(status, run.Status);
        Assert.Equal(1, results.Calls);
    }

    [AvaloniaFact]
    public async Task Real_Backrest_wrong_password_retains_the_error_in_backup_history_and_maintenance()
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        using var workspace = new TempWorkspace("failure-details");
        var (config, secrets, logs, backup, destination) = Setup(workspace);
        destination.Encrypted = true;
        destination.StoragePasswordRef = "test-repository-password";
        secrets.Set(destination.StoragePasswordRef, "disposable-correct-repository-password");
        await using var sidecar = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, workspace.Sub("backrest"));
        var engine = new ResticBackrestEngine(sidecar, secrets);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var first = await engine.BackupPruneCheckAsync(backup, backup.SourcePaths.Single(), destination, "default", deadline.Token);
        Assert.Equal(BackupRunStatus.Success, first.Status);
        Assert.Empty(first.Errors);

        secrets.Set(destination.StoragePasswordRef, "disposable-wrong-repository-password");
        var maintenance = await engine.RunCheckOnlyAsync(backup, backup.SourcePaths.Single(), destination, "default", deadline.Token);
        Assert.Equal(BackupRunStatus.Failed, maintenance.Status);
        Assert.Contains(maintenance.Errors, error => error.Contains("wrong password or no key found"));
        var run = await Orchestrator(config, secrets, logs, engine).RunBackupAsync(backup, deadline.Token);
        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Equal(1, Assert.Single(run.Cells).AttemptsMade);
        Assert.Contains(run.Errors, error => error.Contains("wrong password or no key found"));
        Assert.Contains(Assert.Single(logs.LoadHistoryByBackupId(backup.Id)).Errors,
            error => error.Contains("wrong password or no key found"));
        Assert.Equal(BackupRunStatus.Failed, config.Current.Backups.Single().LastRunStatus);

        secrets.Set(destination.StoragePasswordRef, "disposable-correct-repository-password");
        var recovered = await engine.RunCheckOnlyAsync(backup, backup.SourcePaths.Single(), destination, "default", deadline.Token);
        Assert.Equal(BackupRunStatus.Success, recovered.Status);
        Assert.Empty(recovered.Errors);
    }

    private static (ConfigStore Config, SecretsStore Secrets, LogStore Logs, Backup Backup, Destination Destination) Setup(TempWorkspace workspace)
    {
        var config = new ConfigStore(); config.Load();
        var destination = new Destination { Name = "Disposable repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = workspace.Sub("repository"), Encrypted = false };
        var backup = new Backup { Name = "Saved failure test", AbortOnMeteredNetwork = false, UseVss = false,
            VssDisabledAcknowledged = true, CheckSchedule = new(), PruneSchedule = new(),
            SourcePaths = { workspace.Sub("source") }, Targets = { new() { DestinationId = destination.Id, StorageName = "default" } } };
        File.WriteAllText(Path.Combine(backup.SourcePaths.Single(), "original.txt"), "Keep the existing backup when the next run fails.");
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination);
            c.Notifications.OnSuccess = new(); c.Notifications.OnSkipped = new(); c.Notifications.OnFailure = new(); });
        return (config, new SecretsStore(), new LogStore(), backup, destination);
    }

    private static BackupOrchestrator Orchestrator(ConfigStore config, SecretsStore secrets, LogStore logs, IBackupEngine engine) =>
        new(config, secrets, engine, logs, new HealthcheckService(config, secrets), new MailService(config, secrets),
            new NotificationService(config), new BackupProgressService());

    public class ResultEngine : DispatchProxy
    {
        private readonly InMemoryBackupEngine _other = new();
        public RunRecord Result { get; set; } = null!;
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(IBackupEngine.BackupPruneCheckAsync)) return method.Invoke(_other, args);
            Calls++;
            return Task.FromResult(Result);
        }
    }
}
