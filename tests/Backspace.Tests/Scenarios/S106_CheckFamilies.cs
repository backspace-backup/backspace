using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U221 (owner, 12 November 2026, chose "Three families"): one question per
/// check. Structure check (is the backup complete?), Data check, of a sample
/// or all data (is the stored data intact?), Restore check (can the files
/// come back?). U222: the hovers in Schedule › Checks say what each does.
/// U220: each check's conditions read like the backup's.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S106_CheckFamilies : ScenarioBase
{
    protected override string ScenarioName => "S106-check-families";

    [AvaloniaFact] public async Task Run_S106() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Google Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var backup = new Backup { Name = "local_d", SourcePaths = { ctx.MakeSource("d", targetBytes: 2048) },
            Checks = { new BackupCheck { Kind = CheckKind.DataSample, MaximumBytes = BackupCheck.OneGiB, Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Monthly, TimeOfDay = new TimeSpan(21, 0, 0) } },
                       new BackupCheck { Kind = CheckKind.TestRestore, SamplePercent = 1, MaximumBytes = BackupCheck.OneGiB, Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Monthly, TimeOfDay = new TimeSpan(21, 0, 0) } } } };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(backup); });

        var win = new BackupEditorWindow(ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id), isNew: false) { Width = 1100, Height = 1300 };
        ctx.Driver.Show(win);
        var vm = (BackupEditorViewModel)win.DataContext!;
        win.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
        await ctx.Driver.PumpAsync(200);
        var card = win.FindControl<Border>("ChecksCard")!;
        card.BringIntoView(); await ctx.Driver.PumpAsync(150);

        // The names: three families.
        Assert.Equal(new[] { "Data check (1 GiB sample)", "Restore check (1% of files, at most 1 GiB)" }, vm.Checks.Select(c => c.Title));
        var adds = win.GetVisualDescendants().OfType<Button>().Where(b => b.Name?.StartsWith("Add") == true && b.Content is string s && s.StartsWith("+ ")).ToList();
        Assert.Equal(new[] { "+ Structure check", "+ Data check", "+ Restore check" }, adds.Select(b => (string)b.Content!));
        var visibleText = string.Join("\n", win.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
        foreach (var old in new[] { "data sample", "Data sample", "test restore", "Test restore", "Full data read" })
            Assert.DoesNotContain(old, visibleText);

        // U222: every hover in the checks says what it does, as a question answered.
        foreach (var add in adds)
        {
            var tip = ToolTip.GetTip(add) as string;
            Assert.False(string.IsNullOrWhiteSpace(tip), add.Name + " has no hover.");
            Assert.Matches(@"^(Is the backup complete\?|Is the stored data intact\?|Can your files come back\?) ", tip);
            Assert.DoesNotContain("by default", tip, StringComparison.OrdinalIgnoreCase);
        }
        ctx.Report.RecordNote("Hovers", string.Join("\n", adds.Select(a => $"{a.Content}: {ToolTip.GetTip(a)}")));

        // U226: each check follows the backup's conditions unless it has its own.
        var follows = win.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Name == "FollowsBackupConditions" && c.IsEffectivelyVisible).ToList();
        Assert.Equal(2, follows.Count);
        Assert.All(follows, c => Assert.True(c.IsChecked));
        Assert.DoesNotContain(win.GetVisualDescendants().OfType<CheckBox>(), c => c.IsEffectivelyVisible && c.DataContext is CheckEntryViewModel
            && c.Content is string s && s.StartsWith("Run only while"));
        ctx.Snapshot(win, "check families", "Schedule › Checks: \"Data check (1 GiB sample)\" with All data (instead of a sample), \"Restore check (1% of files, at most 1 GiB)\", each with Same conditions as the backup (ticked); Add: + Structure check, + Data check, + Restore check.");
        // Its own: the two conditions appear, and are saved with the check.
        var restoreEntry = vm.Checks.Single(c => c.Kind == CheckKind.TestRestore);
        restoreEntry.FollowsBackup = false;
        restoreEntry.SkipOnBattery = false;
        await ctx.Driver.PumpAsync(150);
        Assert.Equal(new[] { "Run only while on AC power", "Run only while on an unmetered connection" },
            win.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible && c.DataContext == restoreEntry && c.Content is string s && s.StartsWith("Run only while"))
                .Select(c => (string)c.Content!));
        var saved = restoreEntry.ToModel();
        Assert.True(saved.Schedule!.OwnConditions);
        Assert.False(saved.Schedule.SkipOnBattery);
        Assert.False(saved.Schedule.StopOnBattery);
        ctx.Snapshot(win, "a check with its own conditions", "The restore check with Same conditions as the backup unticked: its own Run only while on AC power (unticked) and Run only while on an unmetered connection.");
        restoreEntry.FollowsBackup = true;
        await ctx.Driver.PumpAsync(100);

        // A data check of all data: the same entry, "All data" ticked.
        var data = vm.Checks.Single(c => c.IsDataCheck);
        var allData = win.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "AllDataChoice" && c.DataContext == data);
        allData.IsChecked = true;
        await ctx.Driver.PumpAsync(150);
        Assert.Equal(CheckKind.FullDataRead, data.Kind);
        Assert.Equal("Data check (all data)", data.Title);
        Assert.Equal(CheckKind.FullDataRead, data.ToModel().Kind);
        Assert.DoesNotContain("by default", ToolTip.GetTip(allData) as string ?? "", StringComparison.OrdinalIgnoreCase);
        ctx.Snapshot(win, "data check of all data", "The data check with All data ticked is now \"Data check (all data)\"; its sample size field is gone.");
        allData.IsChecked = false;
        await ctx.Driver.PumpAsync(100);
        Assert.Equal("Data check (1 GiB sample)", data.Title);

        // The hover on + Restore check, open.
        var restoreAdd = adds.Single(b => (string)b.Content! == "+ Restore check");
        restoreAdd.BringIntoView(); await ctx.Driver.PumpAsync(100);
        ToolTip.SetIsOpen(restoreAdd, true);
        await ctx.Driver.PumpAsync(300);
        ctx.Snapshot(win, "restore check hover", "Hovering + Restore check: \"Can your files come back? Restores some files from the latest backup into a temporary folder and checks them. Your original files are never changed.\"");
        ToolTip.SetIsOpen(restoreAdd, false);
        win.Close();
    }
}
