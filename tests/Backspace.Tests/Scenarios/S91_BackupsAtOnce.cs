using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U156 (7 November 2026): the owner started local_c and local_d, both to
/// Google Drive. The background service ran them one after the other while
/// local_d's card said "Running…" the whole time. A backup waiting its turn
/// now says "Queued" and why, in the window and from the background service,
/// and its time running stands still meanwhile. "Backups at once per
/// destination" (1) and "in total" (2) are in Settings.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S91_BackupsAtOnce : ScenarioBase
{
    protected override string ScenarioName => "S91-backups-at-once";

    [AvaloniaFact] public async Task Run_S91() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var drive = new Destination { Name = "Google Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("gdrive"), Encrypted = false };
        var nas = new Destination { Name = "NAS", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("nas"), Encrypted = false };
        Backup Make(string name, Destination destination, int seed) => new()
        {
            Name = name, SourcePaths = { ctx.MakeSource(name, seed: seed, targetBytes: 50_000) },
            UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false, Threads = 1,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } },
        };
        var localC = Make("local_c", drive, 1); var localD = Make("local_d", drive, 2); var photos = Make("photos", nas, 3);
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(drive); cfg.Destinations.Add(nas);
            cfg.Backups.Add(localC); cfg.Backups.Add(localD); cfg.Backups.Add(photos);
            cfg.Limits = new BackupLimits(); // the defaults: 1 per destination, 2 in total
        });

        var engine = (InMemoryBackupEngine)ServiceLocator.Engine;
        var release = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource>();
        TaskCompletionSource Gate(string name) => release.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        engine.HoldBackup = (b, ct) => Gate(b.Name).Task.WaitAsync(ct);
        using var listVm = new BackupsViewModel();
        var host = new Window { Width = 1100, Height = 900, Content = new BackupsView { DataContext = listVm } };
        ctx.Driver.Show(host);
        try
        {
            BackupCardViewModel Card(Backup b) => listVm.Cards.Single(c => c.Id == b.Id);
            async Task Until(Func<bool> condition, string what)
            {
                for (var i = 0; i < 200 && !condition(); i++) await ctx.Driver.PumpAsync(50);
                Assert.True(condition(), what);
            }

            // In the window: Run now on local_c, then local_d (same destination),
            // then photos (another destination).
            var runC = listVm.RunNowCommand.ExecuteAsync(Card(localC));
            await Until(() => engine.Calls.Any(c => c.Contains("(local_c,")), "local_c started");
            var runD = listVm.RunNowCommand.ExecuteAsync(Card(localD));
            await Until(() => Card(localD).IsQueued, "local_d is queued");
            Assert.Equal("Queued", Card(localD).LastRunLabel);
            Assert.StartsWith("Queued: waiting for local_c (same destination).", Card(localD).LastRunDetail);
            var runP = listVm.RunNowCommand.ExecuteAsync(Card(photos));
            await Until(() => engine.Calls.Any(c => c.Contains("(photos,")), "photos runs alongside local_c");
            Assert.False(Card(photos).IsQueued);
            Assert.True(Card(localC).IsActuallyRunning);
            var summary = host.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "BackupSummary")
                .Single(b => b.DataContext == Card(localD));
            Assert.False(summary.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RunningText").IsEffectivelyVisible);
            Assert.Contains(summary.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible
                && t.Text == "Queued");
            ctx.Snapshot(host, "window queued same destination",
                "Run now on local_c, local_d (both to Google Drive) and photos (NAS), from this window with the defaults. local_c and photos run; local_d says “Queued: waiting for local_c (same destination)” instead of Running, without a time running. The engine is simulated; the queueing is the real orchestrator and its cross-process gates.");

            Gate("local_c").SetResult();
            await Until(() => Card(localD).IsActuallyRunning, "local_d runs once local_c is done");
            Assert.Equal("Running…", Card(localD).LastRunLabel);
            Gate("local_d").SetResult(); Gate("photos").SetResult();
            await Task.WhenAll(runC, runD, runP).WaitAsync(TimeSpan.FromSeconds(30));

            // From the background service: its activity lists both runs, and
            // local_d's worker reports that it waits for local_c.
            var started = DateTime.UtcNow.AddMinutes(-16);
            var c = new ServiceActivity.Snapshot(1, Guid.NewGuid().ToString("N"), "running", localC.Id, "backup", started,
                new(localC.Id, 41, "", [new(localC.SourcePaths[0], 41, true, "Backing up · 12,400 of 30,000 files checked", "Backing up")], [new("Google Drive", 41)]));
            var dQueued = new ServiceActivity.Snapshot(1, Guid.NewGuid().ToString("N"), "running", localD.Id, "backup", started.AddSeconds(2),
                new(localD.Id, 0, "waiting for local_c (same destination)", [], []));
            var activity = ServiceActivity.Parse(ServiceActivity.Encode(dQueued with { Runs = [c, dQueued] }));
            var clock = DateTime.UtcNow;
            foreach (var card in new[] { Card(localC), Card(localD) })
            {
                card.UtcNow = () => clock;
                card.ApplyBackgroundActivity(ServiceActivity.ForBackup(activity, card.Id), null);
            }
            ctx.Driver.Pump();
            Assert.True(Card(localC).IsActuallyRunning);
            Assert.Equal("Running · 16 min", Card(localC).RunningLabel);
            Assert.True(Card(localD).IsQueued);
            Assert.Equal("Queued", Card(localD).LastRunLabel);
            Assert.StartsWith("Queued: waiting for local_c (same destination).", Card(localD).LastRunDetail);
            ctx.Snapshot(host, "background service queued same destination",
                "The background service's activity (simulated) with both runs: local_c is backing up, 16 min in; local_d's worker waits for it and its card says “Queued: waiting for local_c (same destination)”, not Running. Before U156 the service reported only local_c, and local_d's card said Running…");

            // The time running stands still while queued (U155's clock).
            clock = clock.AddMinutes(14);
            foreach (var card in new[] { Card(localC), Card(localD) }) card.ApplyBackgroundActivity(ServiceActivity.ForBackup(activity, card.Id), null);
            Assert.Equal("Running · 30 min", Card(localC).RunningLabel);
            // The worker reports the time it waited (the orchestrator's QueuedTime).
            var waited = (clock - dQueued.StartedUtc!.Value).TotalSeconds;
            var dRunning = dQueued with { Progress = dQueued.Progress! with { Queued = "", Percent = 3, QueuedSeconds = waited } };
            var after = activity with { Runs = [c with { State = "completed", Progress = c.Progress! with { Result = Finished(localC, started) } }, dRunning] };
            Card(localD).ApplyBackgroundActivity(ServiceActivity.ForBackup(after, localD.Id), null);
            Assert.False(Card(localD).IsQueued);
            Assert.Equal("Running · 0 s", Card(localD).RunningLabel);
            clock = clock.AddMinutes(3);
            Card(localD).ApplyBackgroundActivity(ServiceActivity.ForBackup(after, localD.Id), null);
            Assert.Equal("Running · 3 min", Card(localD).RunningLabel);
        }
        finally
        {
            engine.HoldBackup = null;
            foreach (var gate in release.Values) gate.TrySetResult();
            host.Close();
        }

        // Settings: the two limits, persisted for the window and the service.
        var settings = new SettingsViewModel();
        var window = new Window { Width = 1100, Height = 820, Content = new SettingsView { DataContext = settings } };
        ctx.Driver.Show(window);
        try
        {
            Assert.Equal(1, settings.BackupsPerDestination);
            Assert.Equal(2, settings.BackupsInTotal);
            var section = window.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "BackupsAtOnceSection");
            section.IsExpanded = true;
            await ctx.Driver.PumpAsync(200);
            section.BringIntoView();
            await ctx.Driver.PumpAsync(200);
            var perDestination = window.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "BackupsPerDestinationField");
            Assert.Equal(1m, perDestination.Value);
            settings.BackupsPerDestination = 2;
            settings.BackupsInTotal = 0; // below the minimum: saved as 1
            ctx.Driver.Pump();
            Assert.Equal(2, ServiceLocator.Config.Current.Limits.PerDestination);
            Assert.Equal(1, ServiceLocator.Config.Current.Limits.Total);
            settings.BackupsPerDestination = 1; settings.BackupsInTotal = 2;
            ctx.Driver.Pump();
            Assert.Equal(1, ServiceLocator.Config.Current.Limits.PerDestination);
            Assert.Equal(2, ServiceLocator.Config.Current.Limits.Total);
            Assert.Equal(1m, perDestination.Value);
            ctx.Snapshot(window, "settings backups at once",
                "Settings › Backups at once: per destination 1 and in total 2 (the defaults), with what each means; cleanup and checks always run alone on their destination. Changing them saves at once (checked: 2 and 1 were saved, then the defaults again).");
        }
        finally { settings.Dispose(); window.Close(); }
    }

    private static RunRecord Finished(Backup backup, DateTime started) => new()
    {
        Id = Guid.NewGuid().ToString("N"), BackupId = backup.Id, BackupName = backup.Name, StartedUtc = started,
        EndedUtc = started.AddMinutes(30), Status = BackupRunStatus.Success, Summary = "Backed up.",
    };
}
