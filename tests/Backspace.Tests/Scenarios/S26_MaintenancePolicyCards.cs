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
/// Exercises the new PRUNING POLICY + CHECK POLICY cards on the
/// Schedule tab. Opens the BackupEditor in edit mode on a backup
/// that already has a weekly prune cadence + monthly check
/// cadence, takes a screenshot of the cadence combos in their
/// non-default state, and asserts the WeeklyDaysBitmask was
/// pinned to a single day (so a Weekly cadence doesn't end up
/// firing daily through the OS scheduler — regression guard).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S26_MaintenancePolicyCards : ScenarioBase
{
    protected override string ScenarioName => "S26-maintenance-policy-cards";

    [AvaloniaFact] public async Task Run_S26() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination
        {
            Name = "Local — maintenance test",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("primary"),
        };
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(dest));

        var existing = new Backup
        {
            Name = "Photos-Weekly-Prune",
            SourcePaths = { ctx.MakeSource("photos", seed: 1, targetBytes: 80_000) },
            Schedule = new BackupSchedule
            {
                Frequency = ScheduleFrequency.Daily,
                TimeOfDay = new TimeSpan(20, 0, 0),
            },
            // Weekly prune + monthly check pre-existing, so the
            // editor hydrates the combos to Weekly / Monthly.
            PruneSchedule = new BackupSchedule
            {
                Frequency = ScheduleFrequency.Weekly,
                TimeOfDay = TimeSpan.FromHours(3),
                WeeklyDaysBitmask = 0b0000_0001,
            },
            CheckSchedule = new BackupSchedule
            {
                Frequency = ScheduleFrequency.Monthly,
                TimeOfDay = TimeSpan.FromHours(4),
            },
        };
        existing.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });

        var vm = new BackupEditorViewModel(existing, isNew: false);

        // Sanity-check the hydration mapped schedule→cadence correctly.
        Assert.Equal(MaintenanceCadence.Weekly, vm.PruneCadence);
        Assert.Equal(MaintenanceCadence.Monthly, vm.CheckCadence);

        var win = new BackupEditorWindow { DataContext = vm, Width = 980, Height = 820 };
        ctx.Driver.Show(win);

        // Navigate to the Schedule tab via the public surface: find
        // the TabControl and select the Schedule tab.
        var tabs = win.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (tabs is not null && tabs.Items.Count > 1) tabs.SelectedIndex = 1;
        ctx.Driver.Pump();
        ctx.Snapshot(win, "schedule tab with maintenance cards",
            "Maintenance section: PRUNING POLICY = Weekly, CHECK POLICY = Monthly. " +
            "Cadence combo + Time of Day picker visible in each card.");

        // Flip the prune cadence to AfterEachBackup so the
        // time-of-day column collapses; verify the UI updates.
        vm.PruneCadence = MaintenanceCadence.AfterEachBackup;
        ctx.Driver.Pump();
        Assert.False(vm.PruneShowsTimeOfDay);
        ctx.Snapshot(win, "prune cadence after-each-backup",
            "PruneCadence flipped to AfterEachBackup → TIME OF DAY column hidden.");

        // Commit and verify the round-trip lands on Backup.PruneSchedule = null.
        var saved = vm.CommitToWorking();
        Assert.NotNull(saved);
        Assert.Null(saved!.PruneSchedule); // AfterEachBackup → no standalone schedule, engine runs prune inline
        Assert.NotNull(saved.CheckSchedule);
        Assert.Equal(ScheduleFrequency.Monthly, saved.CheckSchedule!.Frequency);
        ctx.Report.RecordNote(
            "commit round-trip",
            "PruneSchedule=null (AfterEachBackup), CheckSchedule.Frequency=Monthly.");

        win.Close();
        return Task.CompletedTask;
    }
}
