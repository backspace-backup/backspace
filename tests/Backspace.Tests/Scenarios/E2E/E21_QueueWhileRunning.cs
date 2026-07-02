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
/// Run-now is the most-clicked action on the BackupCard; the UX
/// gate that disables it while the same backup is mid-flight is
/// the only thing between the user and a double-run race against
/// the same restic repo. This scenario asserts the gate works
/// end-to-end:
///
///   1. Start a backup via RunNowCommand (don't await).
///   2. While in-flight, confirm <c>CanRunNow</c> is false on the
///      card and <c>BackupOrchestrator.IsRunning</c> agrees.
///   3. Wait for the first run to finish.
///   4. Confirm <c>CanRunNow</c> flips back to true.
///
/// Note: we deliberately do NOT exercise the destination-conflict
/// guard here (two DIFFERENT backups sharing a destination) — that
/// path is its own scenario, covered by E03's two-disjoint-dests
/// concurrent run. This one is the same-backup re-entrancy guard.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E21_QueueWhileRunning : BackupE2EBase
{
    public E21_QueueWhileRunning(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E21-queue-while-running";

    [AvaloniaFact] public async Task Run_E21() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        // Bigger source so the first run takes long enough that we
        // can observe the in-flight state before it completes.
        var source   = ctx.MakeSource("docs", seed: 211, targetBytes: 3_000_000);
        var destPath = ctx.MakeLocalDestination("queue-local");

        var dest = new Destination
        {
            Name = "Local — queue target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "Docs (E21)",
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
        ctx.Driver.Pump();

        var vm = (BackupsViewModel)view.DataContext!;
        var card = vm.Cards.First(c => c.Snapshot.Id == backup.Id);

        // Sanity: idle baseline. The XAML-level gate (button
        // IsEnabled binds to card.CanRunNow) must allow a run when
        // nothing is happening. RunNowCommand itself has
        // AllowConcurrentExecutions=true (the orchestrator does the
        // refusal via its cross-process mutex); the UI-level
        // CanRunNow is what stops the user clicking twice.
        Assert.True(card.CanRunNow,
            "Pre-condition violated: card.CanRunNow shouldn't be false when no run is in flight.");
        Assert.False(ServiceLocator.Orchestrator.IsRunning(backup.Id));

        // Fire-and-forget the first run.
        var firstRun = Task.Run(() => vm.RunNowCommand.ExecuteAsync(card));

        // Wait briefly for the orchestrator to mark it Running.
        // 3 seconds is generous; on this rig the registry is populated
        // synchronously inside the orchestrator before any await yields.
        var settled = false;
        for (int i = 0; i < 30; i++)
        {
            if (ServiceLocator.Orchestrator.IsRunning(backup.Id)) { settled = true; break; }
            await Task.Delay(100);
        }
        Assert.True(settled,
            "Orchestrator never marked the run as Running within 3s. " +
            "Either the run failed instantly or the IsRunning registry isn't populated synchronously.");

        // The UI gate must engage. The button's IsEnabled binding to
        // card.CanRunNow disables the button while IsRunning=true;
        // that's the same affordance the orchestrator's cross-
        // process mutex backs up at the engine layer. Either signal
        // flipping false here would let two concurrent runs race
        // the same restic repo.
        Assert.False(card.CanRunNow,
            "card.CanRunNow stayed true while a run was in flight — the Run now button would still be clickable.");
        ctx.Snapshot(host, "in-flight",
            "Run in flight; UI-level gate is engaged (Run now button bound IsEnabled=false).");

        // Let the first run finish.
        await firstRun;

        // Gate must release.
        Assert.False(ServiceLocator.Orchestrator.IsRunning(backup.Id));
        Assert.True(card.CanRunNow,
            "Run finished but card.CanRunNow stayed false. The card would look stuck to the user.");
    }
}
