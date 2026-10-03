using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Microsoft.Win32.TaskScheduler;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Backspace.Tests.Services;

public sealed class RuntimeGuardCoverageTests
{
    [Theory]
    [InlineData(RunKind.Backup, true)]
    [InlineData(RunKind.Prune, true)]
    [InlineData(RunKind.Check, true)]
    [InlineData(RunKind.Backup, false)]
    [InlineData(RunKind.Prune, false)]
    [InlineData(RunKind.Check, false)]
    public async Task Metered_signal_stops_actual_orchestration_and_opt_out_runs_remaining_cells(RunKind kind, bool enabled)
    {
        using var workspace = new TempWorkspace();
        var config = new ConfigStore(); config.Load();
        var secrets = new SecretsStore();
        var logs = new LogStore();
        var first = new Destination { Name = "first", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("first") };
        var second = new Destination { Name = "second", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("second") };
        var backup = new Backup { Name = "guard-proof", UseVss = false, AbortOnMeteredNetwork = enabled,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(), SourcePaths = { workspace.Sub("source") },
            Targets = { new() { DestinationId = first.Id }, new() { DestinationId = second.Id } } };
        File.WriteAllText(Path.Combine(backup.SourcePaths[0], "fixture.txt"), "isolated guard proof");
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(first); c.Destinations.Add(second);
            c.Notifications.OnSuccess = new(); c.Notifications.OnFailure = new(); c.Notifications.OnSkipped = new(); });
        var engine = DispatchProxy.Create<IBackupEngine, WaitingEngine>();
        var controlled = (WaitingEngine)(object)engine;
        var becameMetered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitorCreated = 0;
        var orchestrator = new BackupOrchestrator(config, secrets, engine, logs,
            new HealthcheckService(config, secrets), new MailService(config, secrets), new NotificationService(config), new BackupProgressService())
        {
            MeteredWatchdogFactory = () =>
            {
                Interlocked.Increment(ref monitorCreated);
                return new MeteredNetworkWatchdog(async (host, ct) =>
                {
                    try
                    {
                        await becameMetered.Task.WaitAsync(ct);
                        host.RaiseMeteredDetected("isolated unmetered-to-metered signal");
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    finally { probeStopped.TrySetResult(); }
                });
            }
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = kind switch {
            RunKind.Prune => orchestrator.RunPruneAsync(backup, deadline.Token),
            RunKind.Check => orchestrator.RunCheckAsync(backup, deadline.Token),
            _ => orchestrator.RunBackupAsync(backup, deadline.Token) };
        try
        {
            await controlled.Started.Task.WaitAsync(deadline.Token);
            Assert.False(controlled.ActiveToken.IsCancellationRequested);
            Assert.False(run.IsCompleted);
            becameMetered.TrySetResult();
            if (!enabled) controlled.Finish.TrySetResult();
            var result = await run.WaitAsync(deadline.Token);
            Assert.Equal(kind, result.Kind);
            Assert.Equal(enabled ? BackupRunStatus.Skipped : BackupRunStatus.Success, result.Status);
            Assert.Equal(enabled ? 1 : 2, controlled.Calls);
            Assert.Equal(enabled ? 1 : 0, monitorCreated);
            if (enabled)
            {
                Assert.True(controlled.ActiveToken.IsCancellationRequested);
                Assert.Contains("metered", result.Summary, StringComparison.OrdinalIgnoreCase);
                await probeStopped.Task.WaitAsync(deadline.Token);
            }
            else Assert.DoesNotContain("metered", result.Summary, StringComparison.OrdinalIgnoreCase);
            var persisted = Assert.Single(logs.LoadHistoryByBackupId(backup.Id));
            Assert.Equal(result.Status, persisted.Status);
            Assert.Equal(result.Kind, persisted.Kind);
        }
        finally
        {
            deadline.Cancel(); controlled.Finish.TrySetResult();
            try { await run.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    [Theory]
    [InlineData(ScheduledTaskKind.Backup, true)]
    [InlineData(ScheduledTaskKind.Prune, true)]
    [InlineData(ScheduledTaskKind.Check, true)]
    [InlineData(ScheduledTaskKind.Backup, false)]
    [InlineData(ScheduledTaskKind.Prune, false)]
    [InlineData(ScheduledTaskKind.Check, false)]
    public void Unregistered_Windows_definition_applies_power_network_and_per_kind_cadence(ScheduledTaskKind kind, bool enabled)
    {
        using var service = new TaskService(); // NewTask only: never register/delete a task.
        var backup = new Backup { Name = "unregistered-policy-proof", Enabled = enabled };
        var schedule = new BackupSchedule { SkipOnBattery = enabled, StopOnBattery = enabled,
            RequireNetwork = enabled, CatchUpMissedRuns = enabled, TimeOfDay = TimeSpan.FromHours(3),
            Frequency = kind == ScheduledTaskKind.Prune ? ScheduleFrequency.Weekly : kind == ScheduledTaskKind.Check ? ScheduleFrequency.Monthly : ScheduleFrequency.Daily,
            WeeklyDaysBitmask = 1 << 1 };
        using var definition = WindowsTaskScheduler.BuildTaskDefinition(service, backup, @"C:\isolated-fixture\Backspace.exe", kind, schedule);
        Assert.Equal(enabled, definition.Settings.DisallowStartIfOnBatteries);
        Assert.Equal(enabled, definition.Settings.StopIfGoingOnBatteries);
        Assert.Equal(enabled, definition.Settings.RunOnlyIfNetworkAvailable);
        Assert.Equal(enabled, definition.Settings.StartWhenAvailable);
        Assert.Equal(enabled, definition.Settings.Enabled);
        var trigger = Assert.Single(definition.Triggers);
        Assert.Equal(TimeSpan.FromHours(3), trigger.StartBoundary.TimeOfDay);
        if (kind == ScheduledTaskKind.Prune)
        {
            var weekly = Assert.IsType<WeeklyTrigger>(trigger);
            Assert.Equal(DaysOfTheWeek.Monday, weekly.DaysOfWeek);
            Assert.Equal(1, weekly.WeeksInterval);
        }
        else if (kind == ScheduledTaskKind.Check)
        {
            var monthly = Assert.IsType<MonthlyTrigger>(trigger);
            Assert.Equal(new[] { 1 }, monthly.DaysOfMonth);
            Assert.Equal(MonthsOfTheYear.AllMonths, monthly.MonthsOfYear);
        }
        else Assert.Equal(1, Assert.IsType<DailyTrigger>(trigger).DaysInterval);
        var action = Assert.IsType<ExecAction>(Assert.Single(definition.Actions));
        var expectedFlag = kind == ScheduledTaskKind.Prune ? "--prune" : kind == ScheduledTaskKind.Check ? "--check" : "--run";
        var script = TaskSchedulerCmdWrapperTests.DecodeScript(action.Arguments);
        Assert.Contains(expectedFlag + " ", script);
        foreach (var other in new[] { "--prune", "--check", "--run" }.Where(x => x != expectedFlag))
            Assert.DoesNotContain(other + " ", script);
    }

    public class WaitingEngine : DispatchProxy
    {
        private readonly InMemoryBackupEngine _other = new();
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ActiveToken { get; private set; }
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name is nameof(IBackupEngine.BackupPruneCheckAsync) or nameof(IBackupEngine.RunPruneOnlyAsync) or nameof(IBackupEngine.RunCheckOnlyAsync))
                return WaitForSignal((Backup)args![0]!, (CancellationToken)args[4]!);
            return method.Invoke(_other, args);
        }
        private async System.Threading.Tasks.Task<RunRecord> WaitForSignal(Backup backup, CancellationToken token)
        {
            Calls++; ActiveToken = token; Started.TrySetResult();
            await Finish.Task.WaitAsync(token);
            return new RunRecord { BackupId = backup.Id, BackupName = backup.Name, StartedUtc = DateTime.UtcNow,
                EndedUtc = DateTime.UtcNow, Status = BackupRunStatus.Success, Summary = "isolated cell completed" };
        }
    }
}
