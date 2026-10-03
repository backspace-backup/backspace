using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP coverage-matrix gap "Battery / network
/// preconditions". The user-facing flags
/// (<c>SkipOnBattery</c> / <c>RequireNetwork</c> / <c>CatchUpMissed</c>)
/// each live on <see cref="BackupSchedule"/>; the orchestrator's
/// precondition check consumes them at run-time. A regression in any
/// of those gates silently breaks the user's "don't run on the train"
/// expectation.
///
/// This scenario asserts each flag's persistence + commit round-trip
/// through the editor VM and config layer. The runtime gating itself
/// lives in BackupOrchestrator and is unit-tested separately; this
/// scenario proves the user-visible knob ↔ config field linkage.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S36_BatteryNetworkPreconditions : ScenarioBase
{
    protected override string ScenarioName => "S36-battery-network-preconditions";

    [AvaloniaFact] public async Task Run_S36() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        await Task.Yield();

        var dest = new Destination
        {
            Name = "Local — preconditions",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("primary"),
        };
        var backup = new Backup
        {
            Name = "preconditions-backup",
            Enabled = true,
            Schedule = new BackupSchedule
            {
                Frequency = ScheduleFrequency.Daily,
                TimeOfDay = TimeSpan.FromHours(20),
            },
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        // -------- Toggle each flag through the editor VM --------
        using var vm = new BackupEditorViewModel(backup, isNew: false);

        // The default may be true or false depending on platform defaults
        // (Windows laptops tend to opt-in by default). Toggle through ON
        // explicitly to assert the persistence path.
        vm.SkipOnBattery = true;
        vm.RequireNetwork = true;
        vm.StopOnBattery = true;
        vm.AbortOnMeteredNetwork = true;
        vm.CatchUpMissed = true;

        var committed = vm.CommitToWorking();
        Assert.NotNull(committed);
        Assert.True(committed!.Schedule.SkipOnBattery);
        Assert.True(committed.Schedule.RequireNetwork);
        Assert.True(committed.Schedule.StopOnBattery);
        Assert.True(committed.AbortOnMeteredNetwork);
        Assert.True(committed.Schedule.CatchUpMissedRuns);

        ctx.Report.RecordNote(
            "preconditions persisted",
            $"SkipOnBattery={committed.Schedule.SkipOnBattery} " +
            $"StopOnBattery={committed.Schedule.StopOnBattery} " +
            $"RequireNetwork={committed.Schedule.RequireNetwork} " +
            $"AbortOnMetered={committed.AbortOnMeteredNetwork} " +
            $"CatchUp={committed.Schedule.CatchUpMissedRuns}");

        // -------- Round-trip through ConfigStore.Update --------
        ServiceLocator.Config.Update(cfg =>
        {
            var loaded = cfg.Backups.Find(b => b.Id == backup.Id);
            Assert.NotNull(loaded);
            loaded!.Schedule.SkipOnBattery = true;
            loaded.Schedule.StopOnBattery = true;
            loaded.Schedule.RequireNetwork = true;
            loaded.AbortOnMeteredNetwork = true;
            loaded.Schedule.CatchUpMissedRuns = true;
        });
        var afterRoundTrip = ServiceLocator.Config.Current.Backups.Find(b => b.Id == backup.Id);
        Assert.True(afterRoundTrip!.Schedule.SkipOnBattery);
        Assert.True(afterRoundTrip.Schedule.StopOnBattery);
        Assert.True(afterRoundTrip.Schedule.RequireNetwork);
        Assert.True(afterRoundTrip.AbortOnMeteredNetwork);
        Assert.True(afterRoundTrip.Schedule.CatchUpMissedRuns);
    }
}
