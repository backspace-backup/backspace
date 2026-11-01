using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

[Collection(ScenarioCollection.Name)]
public sealed class E56_ConcurrentLiveAndSavedLogs(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E56-concurrent-live-and-saved-logs";
    [AvaloniaFact] public Task Run_E56() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var backups = new Backup[2];
        for (var n = 0; n < 2; n++)
        {
            var name = n == 0 ? "A_Documents" : "B_Photos";
            var source = Path.Combine(ctx.SourcesDir, name); Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, name + ".bin"), RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
            var dest = new Destination { Name = name + " archive", Kind = DestinationKind.LocalFolder,
                PathOrSubpath = ctx.MakeLocalDestination(name), Encrypted = true,
                StoragePasswordRef = ServiceLocator.Secrets.PutNew("e56", Guid.NewGuid().ToString("N")) };
            var backup = new Backup { Name = name, SourcePaths = { source }, Targets = { new() { DestinationId = dest.Id } },
                UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false,
                BandwidthLimitKBps = 256, PruneSchedule = new(), CheckSchedule = new() };
            backups[n] = backup;
            ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(dest); });
            await fixture.Engine.EnsureInitializedAsync(backup, source, dest, "default", deadline.Token);
        }
        using var vm = new LogsViewModel();
        var view = new LogsView { DataContext = vm };
        var window = ctx.Driver.Show(new Window { Content = view, Width = 1180, Height = 840 });
        static string[] OperationIds(string text) => Regex.Matches(text, @"\[backrest\] op=(\d+)")
            .Select(m => m.Groups[1].Value).Distinct().ToArray();
        var runs = backups.Select(b => Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(b, deadline.Token))).ToArray();
        string live;
        try
        {
            while (!backups.All(b => ServiceLocator.Orchestrator.IsRunning(b.Id)) || OperationIds(vm.LiveTail).Length < 2)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Assert.False(runs.All(t => t.IsCompleted), "Both real backups ended before concurrent live output was observed.");
                await ctx.Driver.PumpAsync(25);
            }
            vm.SelectedRun = null;
            await ctx.Driver.PumpAsync(100);
            Assert.True(vm.IsShowingLive);
            Assert.Equal("All current activity", view.FindControl<TextBlock>("ConsoleContext")!.Text);
            Assert.True(view.FindControl<TextBlock>("CombinedOutputHint")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Border>("LiveBadge")!.IsEffectivelyVisible);
            live = vm.DisplayText;
            ctx.Snapshot(window, "two-real-backups-live", "Two real encrypted backups are running. LIVE appears once and the console says All current activity; no log lines were injected.");
            await Task.WhenAll(runs);
            await ctx.Driver.PumpAsync(200);
            foreach (var t in runs) Assert.Equal(BackupRunStatus.Success, t.Result.Status);
            var logs = runs.Select(t => File.ReadAllText(t.Result.LogPath!)).ToArray();
            var ids = logs.Select(OperationIds).ToArray();
            Assert.All(ids, x => Assert.NotEmpty(x));
            Assert.Empty(ids[0].Intersect(ids[1]));
            Assert.All(ids, x => Assert.NotEmpty(x.Intersect(OperationIds(live))));

            var picker = view.FindControl<ComboBox>("BackupPicker")!;
            // Reproduce the non-first selection regardless of which real run
            // happened to finish last. Home on a closed ComboBox does not
            // reliably change its selection; use its actual arrow navigation.
            picker.SelectedItem = backups[1].Name;
            await ctx.Driver.PumpAsync(50);
            Assert.Equal(backups[1].Name, vm.SelectedBackup);
            Assert.True(picker.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            await ctx.Driver.PumpAsync(250);
            Assert.Equal(backups[0].Name, vm.SelectedBackup);
            Assert.Equal(runs[0].Result.Id, vm.SelectedRun?.Id);
            Assert.False(vm.IsShowingLive);
            Assert.Contains(backups[0].Name, vm.DisplayText);
            Assert.DoesNotContain(backups[1].Name, vm.DisplayText);
            Assert.All(ids[1], id => Assert.DoesNotContain(id, OperationIds(vm.DisplayText)));
            Assert.False(view.FindControl<TextBlock>("CombinedOutputHint")!.IsEffectivelyVisible);
            ctx.Snapshot(window, "completed-run-is-isolated", "After selecting Documents with the actual picker, LIVE disappears and the saved output contains only that run’s operation IDs. Photos remains in its own saved log.");
            ctx.Report.RecordNote("Real concurrent engines", "Both production backup operations overlapped. Their actual operation IDs appeared in the live display, but the two saved logs had no operation IDs in common. Selecting a completed run excluded the other run’s output.");
            ctx.Report.RecordNote("Limits", "Real local Backrest/restic, encrypted repositories and production log routing. The view and keyboard input use headless Avalonia; native Windows pointer input and display scaling remain unverified. Bandwidth was deliberately limited to keep both operations observable; this is not a speed test.");
        }
        finally
        {
            deadline.Cancel();
            try { await Task.WhenAll(runs); } catch (OperationCanceledException) { }
            window.Close();
        }
    }
}
