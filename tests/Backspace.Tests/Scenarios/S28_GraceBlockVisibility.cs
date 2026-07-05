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
/// Regression for the Easy-mode "Quiet skipped-run alerts for…" block
/// visibility. Two halves to assert:
///
///   1. Easy mode (Onboarding wizard, edit existing): the block is
///      HIDDEN by default. Surfaces only when the backup's most
///      recent run was Skipped AND the grace window is still
///      counting down — i.e. when Reset grace period is actually
///      meaningful. New backups, healthy backups, and grace-expired
///      backups all keep it hidden.
///   2. Advanced mode (BackupEditorWindow): the block is ALWAYS
///      visible so power users can configure / reset regardless of
///      current run state.
///
/// This is the "click X, see Y" scenario for ROADMAP item P1
/// (skipped-run alerts visibility) — the matching unit tests assert
/// the model's <c>ShouldShowGraceBlock</c> predicate but not the
/// actual XAML binding. AGENT.md rule 7 calls out this gap pattern.
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

        // -------- Half 1: Easy mode, HEALTHY backup, block HIDDEN --------
        var healthyBackup = MakeBackup(dest, name: "Healthy",
            lastStatus: BackupRunStatus.Success,
            lastSuccessOffset: TimeSpan.FromHours(-2));
        ServiceLocator.Config.Update(cfg => cfg.Backups.Add(healthyBackup));

        var easyHealthy = new OnboardingViewModel(healthyBackup);
        Assert.False(easyHealthy.ShowGraceBlock,
            "Healthy backup: Easy-mode grace block must stay hidden — only the user " +
            "in active grace needs the Reset control.");

        // -------- Half 2: Easy mode, IN-GRACE backup, block VISIBLE --------
        var inGraceBackup = MakeBackup(dest, name: "InGrace",
            lastStatus: BackupRunStatus.Skipped,
            lastSuccessOffset: TimeSpan.FromHours(-1));
        ServiceLocator.Config.Update(cfg => cfg.Backups.Add(inGraceBackup));

        var easyInGrace = new OnboardingViewModel(inGraceBackup);
        Assert.True(easyInGrace.ShowGraceBlock,
            "Skipped + within grace window: Easy-mode block must surface so " +
            "the user can hit Reset grace period.");

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
        Assert.Null(advancedVm.SkipNotificationGraceResetUtc);
        advancedVm.ResetGracePeriodCommand.Execute(null);
        Assert.NotNull(advancedVm.SkipNotificationGraceResetUtc);
        var commit = advancedVm.CommitToWorking();
        Assert.NotNull(commit);
        Assert.NotNull(commit!.SkipNotificationGraceResetUtc);

        advancedWin.Close();
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
