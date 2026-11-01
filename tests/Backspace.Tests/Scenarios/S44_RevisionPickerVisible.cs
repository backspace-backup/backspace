using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for the bug user reported (2026-06-12): "Bring Files
/// back doesn't even offer to pick the revision." Duplimate's
/// upstream UX shipped a revision picker; if the current RestoreView
/// is skipping the step or only surfacing one snapshot, that's a
/// user-visible feature regression vs the parent project.
///
/// This scenario drives the FULL wizard transition the user takes
/// when they click "Restore files" on a BackupCard:
///   1. Set up a backup with 2 revisions on the destination.
///   2. Fire <see cref="AppNavigator.PreselectRestoreBackup"/> just
///      like <c>BackupsViewModel.RestoreFiles</c> does.
///   3. Let the VM's <c>OnPreselectRequested</c> auto-advance.
///   4. Assert <c>CurrentStep == PickRevision</c> — proving the
///      wizard DOES land on a picker, not skip straight to PickFiles
///      or PickTarget.
///   5. Assert the Revisions collection has both snapshots so the
///      ListBox renders as a real picker (single-row would render
///      "info" not "picker", visually indistinguishable from skip).
///   6. Select the OLDER revision and advance via
///      <c>NextFromRevisionCommand</c> — proving the user can pick
///      any revision, not just the latest.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S44_RevisionPickerVisible : ScenarioBase
{
    protected override string ScenarioName => "S44-revision-picker-visible";

    [AvaloniaFact] public async Task Run_S44() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var src = ctx.MakeSource("revs-src", seed: 11, targetBytes: 6_000);
        var dest = new Destination
        {
            Name = "Local — revs",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("primary"),
        };
        var backup = new Backup { Name = "revs-backup", Enabled = true };
        backup.SourcePaths.Add(src);
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        // Run twice → two revisions.
        var first = await ServiceLocator.Orchestrator.RunBackupAsync(backup, default);
        Assert.True(first.Status is BackupRunStatus.Success or BackupRunStatus.Warning);
        var touch = System.IO.Path.Combine(src, "newfile-rev2.bin");
        System.IO.File.WriteAllBytes(touch, new byte[] { 1, 2, 3 });
        var second = await ServiceLocator.Orchestrator.RunBackupAsync(backup, default);
        Assert.True(second.Status is BackupRunStatus.Success or BackupRunStatus.Warning);

        // -------- Data-layer check: both revisions visible --------
        var listed = await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, src, dest, "default", default);
        Assert.True(listed.Count >= 2,
            $"Expected ≥ 2 revisions after two backup runs; got {listed.Count}.");

        ctx.Report.RecordNote(
            "revisions present",
            $"Found {listed.Count} revisions: {string.Join(", ", listed.Select(r => r.Number))}");

        // -------- Wizard-flow check: PickRevision is reached --------
        // Spin up a RestoreViewModel and fire the same preselect
        // BackupsViewModel.RestoreFiles fires. Then wait for the
        // OnPreselectRequested → NextFromBackupAsync → ... →
        // EnterAfterDestinationAsync chain to land on PickRevision.
        var vm = new RestoreViewModel();
        // The preselect is dispatched via Dispatcher.UIThread.InvokeAsync,
        // so we need to be on the UI thread already (the headless
        // harness puts us there) and pump the dispatcher to let the
        // continuation run.
        AppNavigator.PreselectRestoreBackup(backup);

        // Pump for up to 5 s. EnterAfterDestinationAsync flips
        // CurrentStep synchronously inside the NextFromBackupAsync
        // chain, but the LoadRevisionsAsync that populates Revisions
        // is fire-and-forget via Dispatcher.UIThread.InvokeAsync —
        // pump until the collection has the expected ≥2 entries.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (vm.CurrentStep == RestoreViewModel.Step.PickRevision && vm.Revisions.Count >= 2)
                break;
            await Task.Delay(50);
        }
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RestoreViewModel.Step.PickRevision, vm.CurrentStep);
        Assert.True(vm.IsStepPickRevision,
            "IsStepPickRevision must be true — the ListBox's IsVisible binds to !MultiSourceMode " +
            "while the wrapping StackPanel binds to IsStepPickRevision; a stale flag makes the " +
            "picker invisible and reproduces the user's 'no picker' complaint.");
        Assert.False(vm.LoadingRevisions,
            "LoadingRevisions must have flipped back to false so the picker renders; if it " +
            "stays true the user only sees the indeterminate progress bar, not the rows.");
        Assert.True(vm.HasRevisions,
            "HasRevisions must be true — its IsVisible drives the picker ListBox. If false, " +
            "the user sees the 'no revisions yet' empty-state instead of a picker even though " +
            "the data layer surfaced revisions.");
        Assert.True(vm.Revisions.Count >= 2,
            $"PickRevision step must show every revision the destination has so the user can " +
            $"actually pick one — found {vm.Revisions.Count} after pumping the dispatcher.");

        ctx.Report.RecordNote(
            "wizard reached PickRevision with revisions populated",
            $"CurrentStep={vm.CurrentStep} HasRevisions={vm.HasRevisions} Count={vm.Revisions.Count}");

        // -------- Picking a non-latest revision advances the wizard --------
        // The user is supposed to be ABLE to pick an older revision —
        // this catches a regression that silently snapped to latest.
        var older = vm.Revisions.OrderBy(r => r.Number).First();
        vm.SelectedRevision = older;
        Assert.Equal(older.Number, vm.SelectedRevision?.Number);

        // The Next button binds IsEnabled to SelectedRevision being
        // non-null; CanExecute should match.
        Assert.True(vm.NextFromRevisionCommand.CanExecute(null),
            "NextFromRevisionCommand must be enabled once the user picks any revision.");

        // -------- HasRevisions auto-raises on collection change --------
        // This is the actual VM-level bug from the user's report
        // (2026-06-12): HasRevisions is a computed getter, and the
        // ListBox.IsVisible binds to it. If a code path mutated
        // Revisions without manually calling
        // OnPropertyChanged(nameof(HasRevisions)), the picker stayed
        // hidden — the user saw a blank surface. The S14 screenshot
        // captured the bug exactly. The fix wired
        // Revisions.CollectionChanged to auto-raise the notification.
        // Guard against regression: mutate the collection directly
        // (no manual raise) and assert the PropertyChanged listener
        // fires.
        var emptyVm = new RestoreViewModel();
        var raised = false;
        emptyVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RestoreViewModel.HasRevisions)) raised = true;
        };
        emptyVm.Revisions.Add(new RevisionSummary(1, DateTime.UtcNow, 0, 0));
        Assert.True(raised,
            "Revisions.Add must trigger HasRevisions PropertyChanged through the " +
            "CollectionChanged hook — otherwise the picker stays invisible even " +
            "after rows are added, reproducing the user's 2026-06-12 report.");
        Assert.True(emptyVm.HasRevisions);
    }
}
