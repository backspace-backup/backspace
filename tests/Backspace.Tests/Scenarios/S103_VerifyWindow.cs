using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U213 (owner, 12 November 2026): the card's Test restore button becomes
/// Verify. Its window lists this backup's own checks, ticked, with their
/// latest result, then other checks to run once (a test restore with its own
/// sample size among them). Run runs the ticked ones one after another; a
/// once-only check runs, not skipped as removed, and is not added to the backup.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S103_VerifyWindow : ScenarioBase
{
    protected override string ScenarioName => "S103-verify-window";

    [AvaloniaFact] public async Task Run_S103() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Google Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var source = ctx.MakeSource("d", targetBytes: 4096);
        var backup = new Backup { Name = "local_d", SourcePaths = { source }, LastRunStatus = BackupRunStatus.Success,
            LastRunStartUtc = DateTime.UtcNow.AddHours(-2), LastRunEndUtc = DateTime.UtcNow.AddHours(-2), LastSuccessUtc = DateTime.UtcNow.AddHours(-2),
            Checks = { new BackupCheck { Kind = CheckKind.DataSample, MaximumBytes = BackupCheck.OneGiB },
                       new BackupCheck { Kind = CheckKind.TestRestore, SamplePercent = 1, MaximumBytes = BackupCheck.OneGiB,
                                         Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Monthly } } } };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });
        var now = DateTime.UtcNow;
        var entries = new List<BackupCheckEntry> { new("r1", now.AddDays(-2), BackupRunStatus.Success, CheckDepth.SavedDataSample, "No errors were found.") };

        // The card says Verify.
        using (var main = new MainWindowViewModel())
        {
            var host = ctx.Driver.Show(new MainWindow { Width = 1100, Height = 700, DataContext = main });
            await ctx.Driver.PumpAsync(200);
            var cardView = host.GetVisualDescendants().OfType<BackupCard>().Single();
            var button = cardView.FindControl<Button>("TestRestoreButton")!;
            Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Verify");
            Assert.True(button.IsEnabled);
            ctx.Snapshot(host, "card with verify", "The card's buttons: Run now, Mount, Restore files and Verify (formerly Test restore); More on the right.");
            host.Close();
        }

        // The window: own checks ticked with their latest result, others once.
        var model = new VerifyViewModel(backup, entries, now);
        Assert.Equal(new[] { "Structure check + data sample (1 GiB)", "Sample test restore (1%, at most 1 GiB)" }, model.Own.Select(c => c.Title));
        Assert.All(model.Own, c => Assert.True(c.IsSelected));
        Assert.Equal("Passed 2d ago", model.Own[0].Detail);
        Assert.Equal("Not run yet", model.Own[1].Detail);
        Assert.Equal(new[] { "Structure check", "Test restore", "Full data read" }, model.Other.Select(c => c.Title));
        Assert.Equal("Run 2 checks", model.RunLabel);
        Assert.False(model.ShowsRestoreOptions);
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var window = new VerifyWindow(model) { RequestedThemeVariant = theme };
            ctx.Driver.Show(window);
            await ctx.Driver.PumpAsync(150);
            Assert.Equal("Verify “local_d”", window.FindControl<TextBlock>("Heading")!.Text);
            ctx.Snapshot(window, $"verify window {theme}",
                $"{theme}: \"Verify “local_d”\": the backup's two checks ticked (\"Passed 2d ago\", \"Not run yet\"), then Structure check, Test restore and Full data read to run once; \"Run 2 checks\".");
            window.Close();
        }

        // A once-only test restore with its own size, ticked with the structure check.
        model.Own[1].IsSelected = false;
        model.Other.Single(c => c.Title == "Test restore").IsSelected = true;
        model.Other.Single(c => c.Title == "Structure check").IsSelected = true;
        Assert.True(model.ShowsRestoreOptions);
        model.RestoreOptions.Percentage = 5;
        Assert.Equal("Run 3 checks", model.RunLabel);
        var chosen = model.Selected();
        Assert.Equal(new[] { CheckKind.DataSample, CheckKind.Structure }, chosen.Select(c => c.Kind));
        Assert.Same(backup.Checks[0], chosen[0]);
        // The test restore runs last, as one you start yourself (followed in Restores).
        Assert.Equal(5, model.OnceRestore()!.SamplePercent);
        Assert.All(chosen.Skip(1), c => { Assert.DoesNotContain(backup.Checks, s => s.Id == c.Id); Assert.Null(c.Schedule); });
        var tickedAgain = new VerifyWindow(model);
        ctx.Driver.Show(tickedAgain);
        await ctx.Driver.PumpAsync(150);
        ctx.Snapshot(tickedAgain, "verify window, test restore once",
            "The backup's data sample ticked, its monthly test restore unticked; Structure check and a once-only Test restore ticked, with its sample size (5%) shown below it; \"Run 3 checks\".");
        tickedAgain.Close();

        // Running them: the once-only structure check runs (not skipped as
        // removed) and the backup keeps exactly its own checks.
        var structureOnce = chosen[1];
        var record = await ServiceLocator.Orchestrator.RunChecksAsync(backup, [structureOnce], default);
        Assert.NotEqual(BackupRunStatus.Skipped, record.Status);
        Assert.DoesNotContain("removed", record.Summary ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id).Checks.Count);
        ctx.Report.RecordNote("Run", $"A once-only structure check ran through the orchestrator: {record.Status}, \"{record.Summary}\".");
    }
}
