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
using Xunit.Abstractions;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Sentinel coverage of the BackupCard's action shelf — every
/// visible button must (a) be wired to a non-null command and
/// (b) have <c>CanExecute(card) == true</c> in the post-first-run
/// idle state. This is the "are the buttons clickable" sanity
/// check the snapshot-only S27_BackupCardActionShelf scenario can't
/// give us; here we run a real backup so the card is in the
/// post-run state a user actually interacts with.
///
/// The scenario doesn't INVOKE the destructive commands (Delete,
/// Stop) — that would tear down state other scenarios assume; their
/// happy paths are covered by E20 (erase) and E02 (stop). What we
/// pin here is: a button you can see is a button you can press.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E24_BackupCardActionShelf : BackupE2EBase
{
    public E24_BackupCardActionShelf(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E24-backup-card-action-shelf";

    [AvaloniaFact] public async Task Run_E24() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source   = ctx.MakeSource("docs", seed: 241, targetBytes: 400_000);
        var destPath = ctx.MakeLocalDestination("shelf-local");

        var dest = new Destination
        {
            Name = "Local — shelf target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e24", $"e24-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E24)",
            SourcePaths = { source },
            UseVss = false,
            // Non-null sentinels suppress inline prune/check.
            PruneSchedule = new BackupSchedule(),
            CheckSchedule = new BackupSchedule(),
            Threads = 2,
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } },
        };
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest);
            cfg.Backups.Add(backup);
        });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);

        var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning);
        ctx.Snapshot(host, "post-run card",
            "Backup card in the post-run idle state; every action-shelf button should now be reachable.");

        var vm = (BackupsViewModel)view.DataContext!;
        var card = vm.Cards.First(c => c.Snapshot.Id == backup.Id);

        // Every command users can reach from the card. Listed
        // explicitly (not reflected) so that adding a new command in
        // BackupsViewModel forces a conscious update to this sentinel.
        var commands = new (string Label, System.Windows.Input.ICommand Command, object? Param)[]
        {
            ("Run now",       vm.RunNowCommand,       card),
            ("Edit",          vm.EditCommand,         card),
            ("Restore files", vm.RestoreFilesCommand, card),
            ("Test restore",  vm.TestRestoreCommand,  card),
            ("Delete",        vm.DeleteCommand,       card),
            // CancelRun is only meaningful while IsRunning=true; the
            // shelf hides its button when idle, so its CanExecute is
            // not asserted here. The Stop path is covered by E02.
        };

        foreach (var (label, cmd, param) in commands)
        {
            Assert.NotNull(cmd);
            Assert.True(cmd!.CanExecute(param),
                $"Action-shelf '{label}' button reported CanExecute=false in the post-run idle state. " +
                $"That means the bound button would be rendered disabled — the user can SEE it but can't CLICK it.");
        }

        // The card's per-card guards line up with the shelf state.
        Assert.True(card.CanRunNow,
            "Card reported CanRunNow=false post-run; the Run now button would be disabled. " +
            $"RunBlockedReason='{card.RunBlockedReason}'.");
        Assert.True(card.TestRestoreEnabled,
            "Card reported TestRestoreEnabled=false post-run; the Test restore button would be disabled.");

        // CanExecute is just the gating predicate — it answers "would
        // the binding render the button enabled?". It does NOT prove
        // that pressing the button actually does something safe. The
        // round-10 review caught that a non-destructive command
        // throwing inside its handler (NRE on Owner-null short-
        // circuit, missing service in the locator, etc.) would still
        // pass E24 silently. Invoke the non-destructive commands
        // here and assert they don't throw. Destructive commands
        // (Delete, Stop) stay assertion-only — invoking Delete tears
        // down shared state assumptions for downstream scenarios in
        // the collection. RunNow is also skipped because it would
        // run a second full backup serially with this scenario.
        var nonDestructive = new (string Label, System.Windows.Input.ICommand Command, object? Param)[]
        {
            // Edit: opens BackupCreationWindow modally — the test
            // harness has no MainWindow so Owner() returns null and
            // the command returns early. Confirms the null-owner
            // guard short-circuits cleanly rather than NRE-ing.
            ("Edit",          vm.EditCommand,         card),
            // RestoreFiles: navigates to the Restore page + fires a
            // preselect event. Safe synchronous fire-and-forget.
            ("Restore files", vm.RestoreFilesCommand, card),
            // TestRestore: runs the verifier against the just-
            // completed backup. Real work, but the test already
            // has a healthy repo at this point and verify is
            // read-only — perfect place to confirm the verifier
            // wiring from the action shelf is healthy.
            ("Test restore",  vm.TestRestoreCommand,  card),
        };
        foreach (var (label, cmd, param) in nonDestructive)
        {
            var ex = await Record.ExceptionAsync(async () =>
            {
                if (cmd is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand a)
                    await a.ExecuteAsync(param);
                else
                    cmd.Execute(param);
            });
            Assert.True(ex is null,
                $"Action-shelf '{label}' button threw when invoked: {ex?.GetType().Name}: {ex?.Message}");
        }

        // After invoking TestRestore the verifier should have
        // populated the card's verify state. Confirms the command
        // actually did something observable rather than no-oping
        // (a missing PersistOutcome wiring, an Owner-null short-
        // circuit that swallowed the result, or a NullRef inside
        // the verifier would leave the label stuck at the initial
        // "Not yet tested" sentinel).
        Assert.NotEqual("Not yet tested", card.VerifyLabel);
    }
}
