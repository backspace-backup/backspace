using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// U226 (owner, 13 November 2026): "Proceed, but yes manual runs should never
/// be stopped if user has specifically confirmed at the start he wanted to
/// bypass the conditions". With the real engine, through the card's Run now:
/// on battery, a backup that runs only on AC power asks; "Run when conditions
/// allow" queues it on the card ("waiting for AC power since …"), and it runs
/// by itself once plugged in. Stop during the wait runs nothing.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E67_ManualRunConditions(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E67-manual-run-conditions";
    [AvaloniaFact] public Task Run_E67() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source = ctx.MakeSource("c", 6701, 400_000);
        var dest = new Destination { Name = "USB drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var backup = new Backup { Name = "local_c", UseVss = false, VssDisabledAcknowledged = true, SourcePaths = { source },
            PruneSchedule = new(), Targets = { new() { DestinationId = dest.Id } } };
        backup.Schedule.SkipOnBattery = true;
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(dest); });

        var onBattery = true;
        var asked = new System.Collections.Generic.List<ManualRunConditions.Question>();
        var answer = ManualRunConditions.Choice.WhenAllowed;
        var (battery, poll) = (ManualRunConditions.OnBattery, ManualRunConditions.Poll);
        ManualRunConditions.OnBattery = () => onBattery;
        ManualRunConditions.Poll = TimeSpan.FromMilliseconds(200);
        ManualRunConditions.AskOverride = q => { asked.Add(q); return Task.FromResult(answer); };
        try
        {
            using var mainVm = new MainWindowViewModel();
            var main = ctx.Driver.Show(new MainWindow { DataContext = mainVm, Width = 1100, Height = 760 });
            var view = main.GetVisualDescendants().OfType<BackupsView>().Single();
            await ctx.Driver.PumpAsync(200);
            var vm = (BackupsViewModel)view.DataContext!;
            var card = vm.Cards.Single(c => c.Id == backup.Id);

            // On battery: asked, then queued on the card.
            var run = vm.RunNowCommand.ExecuteAsync(card);
            Assert.True(ctx.Driver.WaitFor(() => card.IsQueued, 5000), "The run was not queued.");
            var question = Assert.Single(asked);
            Assert.Equal("«local_c» runs only on AC power", question.Title);
            Assert.Equal("Run when plugged in", question.WaitLabel);
            Assert.Equal("Run now anyway", question.AnywayLabel);
            Assert.Contains("not stopped if the computer is unplugged", question.Body);
            Assert.StartsWith("waiting for AC power since ", card.QueuedReason);
            Assert.Equal("Queued", card.LastRunLabel);
            Assert.Empty(ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id));
            // U239: the sidebar says something waits on the Backups page.
            Assert.True(mainVm.BackupsActive);
            Assert.Equal("local_c · queued", mainVm.BackupsActivityTip);
            Assert.True(main.FindControl<Avalonia.Controls.PathIcon>("BackupsActivity")!.IsEffectivelyVisible);
            ctx.Snapshot(main, "queued until plugged in",
                "local_c's card after Run now on battery and \"Run when plugged in\": Queued, its hover says \"waiting for AC power since HH:mm; it starts by itself\". Nothing has run.");

            // Plugged in: it runs by itself.
            onBattery = false;
            await run;
            var record = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).OrderByDescending(r => r.StartedUtc).FirstOrDefault();
            Assert.NotNull(record);
            Assert.Equal(BackupRunStatus.Success, record!.Status);
            await ctx.Driver.PumpAsync(200);
            Assert.False(mainVm.BackupsActive);

            // On battery again: asked, queued, then Stop: nothing runs.
            onBattery = true;
            asked.Clear();
            var second = vm.RunNowCommand.ExecuteAsync(card);
            Assert.True(ctx.Driver.WaitFor(() => card.IsQueued, 5000));
            await vm.CancelRunCommand.ExecuteAsync(card);
            await second;
            Assert.False(card.IsRunning);
            Assert.False(card.IsQueued);
            Assert.Single(ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id));

            // Not now: nothing.
            answer = ManualRunConditions.Choice.NotNow;
            await vm.RunNowCommand.ExecuteAsync(card);
            Assert.False(card.IsRunning);
            Assert.Single(ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id));

            // Plugged in: nothing is asked.
            onBattery = false;
            asked.Clear();
            answer = ManualRunConditions.Choice.WhenAllowed;
            await vm.RunNowCommand.ExecuteAsync(card);
            Assert.Empty(asked);
            Assert.Equal(2, ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).Count);

            // Started plugged in, it follows its conditions: unplugged mid-run it
            // stops, waits on the card, and runs again by itself once plugged in.
            var big = ctx.MakeSource("big", 6702, 300_000_000);
            ServiceLocator.Config.Update(c => c.Backups.Single(b => b.Id == backup.Id).SourcePaths.Add(big));
            await ctx.Driver.PumpAsync(200);
            card = vm.Cards.Single(c => c.Id == backup.Id);
            var before = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).Count;
            var third = vm.RunNowCommand.ExecuteAsync(card);
            Assert.True(ctx.Driver.WaitFor(() => ServiceLocator.Orchestrator.IsRunning(backup.Id), 10000), "The run did not start.");
            await Task.Delay(500);
            onBattery = true;
            Assert.True(ctx.Driver.WaitFor(() => card.IsQueued, 60000), "Unplugged, the run was not stopped and queued.");
            Assert.StartsWith("waiting for AC power since ", card.QueuedReason);
            var stopped = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).OrderByDescending(r => r.StartedUtc).First();
            Assert.Equal(before + 1, ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).Count);
            Assert.Equal(BackupRunStatus.Skipped, stopped.Status);
            Assert.Contains("on battery", stopped.Summary + string.Join(" ", stopped.Cells.Select(c => c.Summary)));
            ctx.Snapshot(main, "stopped on battery, queued",
                "local_c started plugged in, then unplugged: the run stopped and the card is Queued again, waiting for AC power.");
            onBattery = false;
            await third;
            var resumed = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).OrderByDescending(r => r.StartedUtc).First();
            Assert.Equal(before + 2, ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).Count);
            Assert.Equal(BackupRunStatus.Success, resumed.Status);

            // The question itself.
            var dialog = new DriveChoiceWindow(question.Title, question.Body, question.WaitLabel, question.AnywayLabel);
            ctx.Driver.Show(dialog);
            await ctx.Driver.PumpAsync(150);
            ctx.Snapshot(dialog, "the question",
                "\"«local_c» runs only on AC power\": the computer is on battery; Run when plugged in (default), Run now anyway (not stopped if unplugged), Not now.");
            dialog.Close();
        }
        finally
        {
            ManualRunConditions.OnBattery = battery;
            ManualRunConditions.Poll = poll;
            ManualRunConditions.AskOverride = null;
        }
    }
}
