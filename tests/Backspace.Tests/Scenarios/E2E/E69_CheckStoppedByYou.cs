using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// U242 (owner, 15 November 2026: "Yes please complete everything so it works
/// consistently"). With the real engine: a backup and its structure check
/// complete, then a data check (all data) started as Verify does and stopped
/// with the card's Stop. The check's record says it was stopped by you; the
/// card's Checks line says "Data check (all data) stopped by you · time" in
/// neutral grey with the last completed check under it, and the overall
/// status stays healthy.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E69_CheckStoppedByYou(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E69-check-stopped-by-you";
    [AvaloniaFact] public Task Run_E69() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var source = ctx.MakeSource("docs", 6901, 250_000_000);
        var dest = new Destination { Name = "USB drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var backup = new Backup { Name = "documents", UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false,
            SourcePaths = { source }, PruneSchedule = new(), Targets = { new() { DestinationId = dest.Id } },
            Checks = { new BackupCheck { Kind = CheckKind.Structure } } };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(dest); });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        var vm = (BackupsViewModel)view.DataContext!;
        var card = vm.Cards.Single(c => c.Id == backup.Id);

        // A completed backup; its structure check runs after it.
        await vm.RunNowCommand.ExecuteAsync(card);
        Assert.True(ctx.Driver.WaitFor(() => !card.IsRunning, 180_000), "The backup did not finish.");
        Assert.True(ctx.Driver.WaitFor(() => ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id)
            .Any(r => r.Kind == RunKind.Check && r.Status == BackupRunStatus.Success), 120_000), "The structure check did not pass.");
        Assert.True(ctx.Driver.WaitFor(() => !card.IsRunning && card.VerifyClass == "ok", 30_000), "Checks: " + card.VerifyLabel);

        // A data check of all data, once, as Verify starts it; Stop while it reads.
        var saved = ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id);
        vm.StartChecks(card, saved, [new BackupCheck { Kind = CheckKind.FullDataRead }], ManualRunConditions.Decision.None);
        Assert.True(ctx.Driver.WaitFor(() => card.IsChecking, 10_000), "The check did not start.");
        await ctx.Driver.PumpAsync(200);
        await vm.CancelRunCommand.ExecuteAsync(card);
        Assert.True(ctx.Driver.WaitFor(() => !card.IsRunning, 60_000), "Stop did not end the check.");
        await ctx.Driver.PumpAsync(500);

        var stopped = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).Where(r => r.Kind == RunKind.Check).MaxBy(r => r.StartedUtc)!;
        Assert.Equal(CheckKind.FullDataRead, stopped.CheckKind);
        Assert.True(stopped.StoppedByYou, stopped.Summary);
        Assert.StartsWith("Stopped by you", stopped.Summary);

        Assert.True(ctx.Driver.WaitFor(() => card.VerifyLabel.Contains("stopped by you"), 10_000), "Checks: " + card.VerifyLabel);
        // Green (U242, 15 November 2026): the stop sign on the first line, the
        // completed check with its check mark under it.
        Assert.Equal("Data check (all data) stopped by you · just now", card.VerifyLabel);
        Assert.Equal("ok", card.VerifyClass);
        Assert.Equal("stopped", card.VerifyIconClass);
        Assert.StartsWith("Structure check passed · ", card.CheckCompletedLabel);
        Assert.Equal("ok", card.CheckCompletedClass);
        Assert.Equal("ok", vm.HealthClass);
        Assert.Equal(BackupRunStatus.Success, card.DisplayedRunStatus);
        ctx.Snapshot(host, "check stopped by you",
            "The card after pressing Stop on a running data check. Backup health stays green: the stop sign with \"Data check (all data) stopped by you · just now\", and under it the check mark with \"Structure check passed · just now\". The line at the top stays healthy.");
    }
}
