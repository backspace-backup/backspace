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
using Xunit;
using Xunit.Abstractions;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Real-user E2E: drive the Advanced-mode BackupEditorViewModel
/// rather than the easy-mode wizard. Same end goal — Backup ends
/// up in config + ran once + round-trips — but exercises the
/// advanced surface (VSS toggle, threads override, prune-after,
/// custom KeepPolicy) that the wizard hides.
///
/// Closely mirrors what a user does when they pick "Advanced"
/// from BackupsView's Add-a-backup flow: they get the full
/// BackupEditorWindow, fill it in, click Save. We poke the VM
/// directly because the BackupEditorWindow uses a Dialog
/// ShowDialog flow that requires an owner window in headless.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E06_AdvancedModeEditor : BackupE2EBase
{
    public E06_AdvancedModeEditor(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E06-advanced-mode-editor";

    [AvaloniaFact] public async Task Run_E06() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var sourcePath = ctx.MakeSource("workspace", seed: 53, targetBytes: 1_500_000);
        var destPath   = ctx.MakeLocalDestination("local-advanced");
        var restoreDir = ctx.MakeRestoreTarget("restored");

        var dest = new Destination
        {
            Name = "Local — advanced-flow",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew(
                "e2e-advanced", $"e2e-{Guid.NewGuid():N}"),
        };
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(dest));

        // Build the editor over a fresh, empty Backup — same as
        // BackupsView's "Add a backup → Advanced" hands to the editor.
        var working = new Backup { Name = "" }; // editor auto-names if blank
        var vm = new BackupEditorViewModel(working, isNew: true);

        // Populate every Advanced-mode field a real user would touch.
        vm.SourcePaths.Add(sourcePath);
        vm.Name = "Workspace (E06)";
        vm.Threads = 3;                // 0 = auto; pick something visible
        vm.UseVss = false;             // VSS needs admin; off for the test
        vm.ExcludeCloudFiles = true;   // exercise the default-on advanced flag
        // Prune-after-backup + check-after-backup are no longer simple
        // bool toggles in the editor — they're folded into the
        // MaintenanceCadence policy combo. AfterEachBackup → inline
        // pass; Daily/Weekly/Monthly → standalone scheduled run.
        // Pick Weekly for both so this scenario exercises the
        // "schedule != null" code path on commit.
        vm.PruneCadence = Backspace.Models.MaintenanceCadence.Weekly;
        vm.CheckCadence = Backspace.Models.MaintenanceCadence.Weekly;
        vm.KeepPolicy = "0:365 30:90 7:30 1:7";

        // Add the local destination as a target row. The editor's
        // public surface for targets is the observable collection;
        // mirroring what the "Add destination" button does behind the
        // scenes.
        vm.Targets.Add(new TargetRow(dest, storageName: "default", threads: null));

        var saved = await Task.Run(() => vm.SaveAsync(CancellationToken.None));
        Assert.NotNull(saved);

        // Emulate the upsert BackupsViewModel.AddNew runs once the
        // editor dialog closes (the editor's SaveAsync only fills in
        // Working — the parent owns config write-through).
        ServiceLocator.Config.Update(cfg =>
        {
            var idx = cfg.Backups.FindIndex(b => b.Id == saved!.Id);
            if (idx >= 0) cfg.Backups[idx] = saved!;
            else cfg.Backups.Add(saved!);
        });
        Assert.Contains(ServiceLocator.Config.Current.Backups, b => b.Id == saved!.Id);

        // No auto-run in the Advanced flow (Save vs Save & Run), so
        // explicitly kick the orchestrator the same way the BackupsView
        // "Run now" button does. Use the harness helper for the same
        // gating semantics + UI-thread marshaling.
        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "after advanced save",
            $"Saved via Advanced editor — Name='{saved!.Name}', Threads={saved.Threads}, " +
            $"ExcludeCloudFiles={saved.ExcludeCloudFiles}.");

        var record = await RunBackupViaUiAsync(ctx, saved, view);
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Advanced-saved backup run ended {record.Status}; errors={string.Join(" | ", record.Errors)}.");
        ctx.Snapshot(host, "advanced run complete",
            $"Advanced backup completed: {record.Summary}.");

        // Round-trip restore + byte equality, same as E01.
        var outcome = await RestoreEverythingAsync(
            saved, sourcePath, dest, "default", restoreDir, CancellationToken.None);
        Assert.Equal(0, outcome.FailedCount);
        AssertBytesEqual(sourcePath, restoreDir, ctx);
    }
}
