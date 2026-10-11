using System;
using System.IO;
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
using CommunityToolkit.Mvvm.Input;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Real-user E2E: drive the easy-mode OnboardingWindow wizard from
/// empty config to "Save & Run", then verify the resulting Backup
/// actually ran and round-trips. Mirrors the exact UX a first-time
/// user follows on a clean install.
///
/// Flow:
///   1. Pre-seed a local Destination so the wizard's destination
///      picker has an option (the per-provider OAuth dance can't run
///      in a headless test; we use the manual-folder shape, which is
///      the same code path under the hood once a Destination exists).
///   2. Open the OnboardingWindow with isFirstBackup:true.
///   3. Drive the VM through step 1 (add source chip), step 2 (pick
///      destination), step 3 (use default schedule), then await
///      FinishCommand — that's the "Save & Run" button.
///   4. After the await, a Backup exists in config and ran once.
///      Assert that and capture the post-completion BackupsView.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E05_EasyModeWizard : BackupE2EBase
{
    public E05_EasyModeWizard(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E05-easy-mode-wizard";

    [AvaloniaFact] public async Task Run_E05() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var sourcePath = ctx.MakeSource("essentials", seed: 31, targetBytes: 1_500_000);
        var destPath   = ctx.MakeLocalDestination("local-easy");

        // Pre-seed: the wizard's step 2 needs at least one Destination
        // in config to pick from. A first-time user reaches step 2 by
        // clicking "Use a folder" / cloud-provider buttons that create
        // a Destination synchronously; the dialog path goes through
        // DestinationEditorWindow which we can't show in headless.
        // The end-state is the same: a row in config.Destinations the
        // wizard treats as "selectable".
        var dest = new Destination
        {
            Name = "Local — easy-flow",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew(
                "e2e-easy", $"e2e-{Guid.NewGuid():N}"),
        };
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(dest));

        var onboarding = new OnboardingWindow(seed: null, isFirstBackup: true)
        {
            Width = 900, Height = 760,
        };
        ctx.Driver.Show(onboarding);
        var vm = onboarding.DataContext as OnboardingViewModel
                 ?? throw new InvalidOperationException("Onboarding has no view model.");
        Assert.True(vm.Working.UseVss); // the user-facing safe default remains enabled
        // This headless flow validates wizard persistence and the real restic
        // round trip. It cannot approve a native UAC dialog or launch a helper
        // through testhost. Elevated VSS has a separate interactive probe.
        vm.Working.UseVss = false;
        ctx.Report.RecordNote("Test privilege scope", "The wizard defaults VSS on (asserted). This headless fixture disables it only for its own backup; the interactive VSS probe validates elevation separately.");

        // Step 1: source chip.
        vm.SourceChips.Add(new SourceChipVm(sourcePath));
        vm.CurrentStep = 2;
        ctx.Driver.Pump();
        ctx.Snapshot(onboarding, "wizard step1 source picked",
            "Source chip added; wizard advanced to destinations.");

        // Step 2: select the pre-seeded destination.
        var pickable = vm.AvailableDestinations.FirstOrDefault(d => d.Id == dest.Id)
                       ?? throw new InvalidOperationException(
                           "Pre-seeded destination didn't reach AvailableDestinations.");
        vm.SelectedDestinations.Add(pickable);
        vm.CurrentStep = 3;
        ctx.Driver.Pump();
        ctx.Snapshot(onboarding, "wizard step2 dest picked",
            "Destination selected; wizard on the schedule step.");

        // Step 3: defaults are fine (daily at 20:00). Snap before commit.
        ctx.Snapshot(onboarding, "wizard step3 schedule",
            "Default daily schedule visible; ready to Save & Run.");

        // Click "Save & Run" — that's the FinishCommand. The wizard
        // commits the Backup, registers a schedule, then kicks the
        // orchestrator. Run on a background thread so the dispatcher
        // keeps pumping; same reason as the harness's run helper.
        await Task.Run(() => ((IAsyncRelayCommand)vm.FinishCommand).ExecuteAsync(null));
        ctx.Driver.Pump();
        Assert.True(vm.SavedAndRan,
            $"Wizard didn't commit cleanly. SaveStatus='{vm.SaveStatus}', " +
            $"SavedNoRun={vm.SavedNoRun}.");
        onboarding.Close();

        // Verify the backup landed in config + actually ran.
        var saved = ServiceLocator.Config.Current.Backups
            .FirstOrDefault(b => b.SourcePaths.Contains(sourcePath))
            ?? throw new InvalidOperationException("Saved backup not found in config.");
        Assert.NotEmpty(saved.Targets);
        Assert.Equal(dest.Id, saved.Targets[0].DestinationId);

        // The wizard kicks the orchestrator on a background task; we
        // need to wait for it to land. Poll the persisted LastRun*
        // fields the orchestrator stamps when a run finishes — they
        // start NeverRun and flip to Success/Failed/Warning.
        // Real-engine wizard run can take 30–60s for a 1.5MB source
        // with the default Weekly prune + Monthly check schedules
        // BackupMaintenanceDefaults.ApplyTo seeds. Bumped from 20s to
        // 90s to absorb the real-engine path's natural variance.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var b = ServiceLocator.Config.Current.Backups
                .First(x => x.Id == saved.Id);
            if (b.LastRunStatus is BackupRunStatus.Success
                or BackupRunStatus.Warning or BackupRunStatus.Failed)
            {
                saved = b;
                break;
            }
            await Task.Delay(200);
        }
        Assert.True(saved.LastRunStatus is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Wizard-triggered run ended {saved.LastRunStatus}. Summary='{saved.LastRunSummary}'.");

        // Show the post-wizard Backups landing so SUMMARY captures the
        // shape a real user sees after closing the wizard.
        var backupsView = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = backupsView };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "after wizard completion",
            $"Backups page after wizard: '{saved.Name}' status={saved.LastRunStatus}.");
    }
}
