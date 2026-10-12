using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

[Collection(ScenarioCollection.Name)]
public sealed class E47_MaintenanceOutcomeSafety : BackupE2EBase
{
    public E47_MaintenanceOutcomeSafety(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E47-maintenance-outcome-safety";
    [AvaloniaFact] public async Task Run_E47() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var destination = new Destination
        {
            Name = "Maintenance review repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repository"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e47", Guid.NewGuid().ToString("N")),
        };
        var backup = new Backup
        {
            Name = "Two-source maintenance", UseVss = false, AbortOnMeteredNetwork = false,
            PruneSchedule = new(), CheckSchedule = new(),
            Targets = { new() { DestinationId = destination.Id } },
        };
        foreach (var name in new[] { "documents", "photos" })
        {
            var source = Path.Combine(ctx.SourcesDir, name); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "fixture.txt"), name + " contents must remain recoverable.");
            backup.SourcePaths.Add(source);
        }
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        foreach (var source in backup.SourcePaths)
            Assert.Equal(BackupRunStatus.Success,
                (await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", deadline.Token)).Status);

        var proxy = DispatchProxy.Create<IBackupEngine, CancelBetweenCalls>();
        var controlled = (CancelBetweenCalls)(object)proxy;
        controlled.Inner = fixture.Engine;
        var orchestrator = new BackupOrchestrator(ServiceLocator.Config, ServiceLocator.Secrets, proxy, ServiceLocator.Logs,
            new HealthcheckService(ServiceLocator.Config, ServiceLocator.Secrets),
            new MailService(ServiceLocator.Config, ServiceLocator.Secrets),
            new NotificationService(ServiceLocator.Config), new BackupProgressService());

        foreach (var kind in new[] { RunKind.Check, RunKind.Prune })
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            controlled.Cancellation = cancelled; controlled.Calls = 0;
            var result = kind == RunKind.Check
                ? await orchestrator.RunCheckAsync(backup, cancelled.Token)
                : await orchestrator.RunPruneAsync(backup, cancelled.Token);
            Assert.Equal(1, controlled.Calls);
            Assert.Equal(BackupRunStatus.Warning, result.Status);
            Assert.Equal(2, result.Cells.Count);
            Assert.Equal(BackupRunStatus.Success, result.Cells[0].Status);
            Assert.Equal(BackupRunStatus.Skipped, result.Cells[1].Status);
            Assert.Contains("cancelled", result.Cells[1].Summary);
            var persisted = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).Single(r => r.Id == result.Id);
            Assert.Equal(BackupRunStatus.Warning, persisted.Status);
            Assert.Equal(2, persisted.Cells.Count);
            ctx.Report.RecordNote(kind + " cancellation preserves unfinished work",
                "The real bundled engine completed the first source. Cancellation was then deliberately requested before the second source. " +
                "The production coordinator saved Warning, with one completed source and one skipped source. No engine outcome was mocked.");
        }

        var complete = await ServiceLocator.Orchestrator.RunCheckAsync(backup, deadline.Token);
        Assert.Equal(BackupRunStatus.Success, complete.Status);
        Assert.Equal(2, complete.Cells.Count);
        Assert.All(complete.Cells, c => Assert.Equal(BackupRunStatus.Success, c.Status));
        foreach (var source in backup.SourcePaths)
        {
            var target = ctx.MakeRestoreTarget(Path.GetFileName(source));
            await RestoreEverythingAsync(backup, source, destination, "default", target, deadline.Token);
            Assert.Equal(File.ReadAllText(Path.Combine(source, "fixture.txt")), File.ReadAllText(Path.Combine(target, "fixture.txt")));
        }
        ctx.Report.RecordNote("Normal retry and original contents",
            "An uninterrupted production check completed both sources successfully. Both encrypted snapshots then restored their exact original file contents.");
        ctx.Report.RecordNote("Scope of this proof",
            "Actual bundled Backrest and restic against an isolated local encrypted repository. The cancellation timing is controlled between engine calls. " +
            "This does not test Windows Task Scheduler launch, a live cloud account, or a computer losing power.");
    }

    public class CancelBetweenCalls : DispatchProxy
    {
        public IBackupEngine Inner { get; set; } = null!;
        public CancellationTokenSource Cancellation { get; set; } = null!;
        public int Calls { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(Inner, args);
            return method.Name is nameof(IBackupEngine.RunCheckOnlyAsync) or nameof(IBackupEngine.RunPruneOnlyAsync)
                ? CancelAfterActualResult((Task<RunRecord>)result!) : result;
        }
        private async Task<RunRecord> CancelAfterActualResult(Task<RunRecord> actual)
        {
            var result = await actual;
            if (++Calls == 1) Cancellation.Cancel();
            return result;
        }
    }
}
