using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Both editors offer grace controls before an absence. Exercises real
/// Easy navigation, number binding, Reset, Save code-behind and reopen;
/// checks never-run, healthy, skipped, expired and disabled-grace cases.
/// Only the external OS scheduler is replaced, so saving cannot create
/// scheduled tasks on the test machine.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S28_GraceBlockVisibility : ScenarioBase
{
    protected override string ScenarioName => "S28-grace-block-visibility";

    [AvaloniaFact] public async Task Run_S28() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        await Task.Yield();

        var dest = new Destination
        {
            Name = "Local — e36",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("primary"),
        };
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(dest));

        // A healthy backup must be configurable before a trip begins.
        var healthyBackup = MakeBackup(dest, name: "Healthy",
            lastStatus: BackupRunStatus.Success,
            lastSuccessOffset: TimeSpan.FromHours(-2));
        ServiceLocator.Config.Update(cfg => cfg.Backups.Add(healthyBackup));

        var easyWin = OpenSchedule(healthyBackup);
        var easyVm = (OnboardingViewModel)easyWin.DataContext!;
        var days = easyWin.FindControl<NumericUpDown>("GraceDays")!;
        days.Value = 14;
        ctx.Driver.Pump();
        Assert.Equal(14, easyVm.SkipNotificationGraceDays);
        Assert.Null(easyVm.SkipNotificationGraceResetUtc);
        Assert.True(ctx.Driver.ClickButton(easyWin, "Reset grace period"));
        Assert.NotNull(easyVm.SkipNotificationGraceResetUtc);
        Assert.True(easyVm.SkipNotificationGraceResetUtc >= DateTime.UtcNow.AddMinutes(-1));
        ctx.Snapshot(easyWin, "easy healthy backup grace ready before holiday",
            "A healthy Easy-mode backup exposes 14 days and Reset grace period before any skipped run. Reset has started a fresh window.");

        // Invoke the actual Save button handler, not just a snapshot helper.
        var schedulerProperty = typeof(ServiceLocator).GetProperty(nameof(ServiceLocator.Scheduler))!;
        var previousScheduler = ServiceLocator.Scheduler;
        try
        {
            schedulerProperty.SetValue(null, new NoOpScheduler());
            easyVm.InstallShellExtension = false;
            easyVm.ApplyRecommendedExclusions = false;
            var save = easyWin.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Content as string == "Save");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(ctx.Driver.WaitFor(() => easyVm.SavedNoRun));
            Assert.False(easyWin.IsVisible);
        }
        finally { schedulerProperty.SetValue(null, previousScheduler); }

        var saved = ServiceLocator.Config.Current.Backups.Single(b => b.Id == healthyBackup.Id);
        Assert.Equal(14, saved.SkipNotificationGraceDays);
        Assert.Equal(easyVm.SkipNotificationGraceResetUtc, saved.SkipNotificationGraceResetUtc);
        Assert.Equal(BackupRunStatus.Success, saved.LastRunStatus);
        Assert.Equal(healthyBackup.LastSuccessUtc, saved.LastSuccessUtc); // Reset never fakes success.
        var reopened = OpenSchedule(saved);
        var reopenedVm = (OnboardingViewModel)reopened.DataContext!;
        Assert.Equal(14, reopenedVm.SkipNotificationGraceDays);
        Assert.Equal(easyVm.SkipNotificationGraceResetUtc, reopenedVm.SkipNotificationGraceResetUtc);
        ctx.Snapshot(reopened, "easy grace saved and reopened",
            "The saved 14-day setting and manual reset remain present after reopening the real Easy editor.");
        reopened.Close();

        foreach (var (name, status, offset, graceDays) in new[]
        {
            ("Never run", BackupRunStatus.NeverRun, TimeSpan.FromDays(-10), 7),
            ("Skipped in grace", BackupRunStatus.Skipped, TimeSpan.FromHours(-1), 7),
            ("Skipped after expiry", BackupRunStatus.Skipped, TimeSpan.FromDays(-9), 7),
            ("Grace disabled", BackupRunStatus.Skipped, TimeSpan.FromHours(-1), 0),
        })
        {
            var backup = MakeBackup(dest, name, status, offset);
            backup.SkipNotificationGraceDays = graceDays;
            var window = OpenSchedule(backup);
            var vm = (OnboardingViewModel)window.DataContext!;
            Assert.True(ctx.Driver.ClickButton(window, "Reset grace period"));
            Assert.NotNull(vm.SkipNotificationGraceResetUtc);
            window.Close();
        }

        // -------- Half 3: Advanced mode, ALWAYS visible --------
        // BackupEditorWindow hosts the grace controls inline in the
        // Schedule tab; the inputs are unconditional. We assert by
        // finding the "Reset grace period" button in the visual tree.
        var advancedVm = new BackupEditorViewModel(healthyBackup, isNew: false);
        var advancedWin = new BackupEditorWindow { DataContext = advancedVm, Width = 980, Height = 820 };
        ctx.Driver.Show(advancedWin);

        // Schedule tab is index 1 (Overview=0, Schedule=1, Filters=2…).
        // Wait for the TabControl to materialize — template application
        // can lag Show() when the shared headless session is busy.
        Assert.True(ctx.Driver.WaitFor(() =>
                advancedWin.GetVisualDescendants().OfType<TabControl>().Any()),
            "BackupEditorWindow never populated its TabControl.");
        var tabs = advancedWin.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (tabs is not null && tabs.Items.Count > 1) tabs.SelectedIndex = 1;
        ctx.Driver.Pump();

        Assert.True(ctx.Driver.WaitFor(() =>
                advancedWin.GetVisualDescendants().OfType<Button>()
                    .Any(b => b.Content as string == "Reset grace period")),
            "Reset grace period button never appeared on the Schedule tab.");
        var resetButton = advancedWin.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Content as string == "Reset grace period");
        Assert.NotNull(resetButton);
        Assert.True(resetButton!.IsVisible,
            "Advanced mode: Reset grace period button must be visible regardless of " +
            "run state. Healthy backups still need access to the configuration.");

        ctx.Snapshot(advancedWin, "advanced grace block always visible",
            "Advanced mode hosts the grace-period block unconditionally — power users " +
            "configure / reset regardless of current run state.");

        // -------- Half 4: ResetGracePeriod command actually mutates state --------
        advancedVm.SkipNotificationGraceResetUtc = null;
        advancedVm.ResetGracePeriodCommand.Execute(null);
        Assert.NotNull(advancedVm.SkipNotificationGraceResetUtc);
        var commit = advancedVm.CommitToWorking();
        Assert.NotNull(commit);
        Assert.NotNull(commit!.SkipNotificationGraceResetUtc);

        advancedWin.Close();

        OnboardingWindow OpenSchedule(Backup backup)
        {
            // Use the window's own VM so code-behind and XAML bind to the
            // same object, matching the real Edit flow.
            var window = ctx.Driver.Show(new OnboardingWindow(backup, isFirstBackup: false, isEdit: true));
            Assert.True(ctx.Driver.ClickButton(window, "Continue"));
            Assert.True(ctx.Driver.ClickButton(window, "Continue"));
            var block = window.FindControl<Border>("GraceSettings");
            Assert.NotNull(block);
            Assert.True(block!.IsEffectivelyVisible, $"Easy grace block hidden for {backup.Name}.");
            var reset = window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Content as string == "Reset grace period");
            Assert.True(reset.IsEffectivelyVisible);
            Assert.True(reset.IsEnabled);
            return window;
        }
    }

    private static Backup MakeBackup(Destination dest, string name,
        BackupRunStatus lastStatus, TimeSpan lastSuccessOffset)
    {
        var b = new Backup
        {
            Name = name,
            LastRunStatus = lastStatus,
            LastSuccessUtc = DateTime.UtcNow + lastSuccessOffset,
            SkipNotificationGraceDays = 7,
            CreatedUtc = DateTime.UtcNow.AddDays(-10),  // explicit so SkipGraceReferenceUtc isn't NOW
        };
        b.SourcePaths.Add("/tmp/fake-source");  // never read; the editor doesn't enumerate it for this test
        b.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        return b;
    }
}
