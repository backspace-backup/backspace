using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

[Collection(ScenarioCollection.Name)]
public sealed class E49_TestRestoreProgress : BackupE2EBase
{
    public E49_TestRestoreProgress(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E49-test-restore-progress";
    [AvaloniaFact] public Task Run_E49() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source = Path.Combine(ctx.SourcesDir, "Family files");
        var random = new Random(49013); var bytes = new byte[64 * 1024];
        for (var i = 0; i < 300; i++)
        {
            var path = Path.Combine(source, "folder-" + i % 10, $"file-{i:000}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); random.NextBytes(bytes); File.WriteAllBytes(path, bytes);
        }
        var destination = new Destination { Name = "Local test archive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e49", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Family files", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            CheckSchedule = new BackupSchedule(), PruneSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(destination); c.Backups.Add(backup); });
        using var vm = new MainWindowViewModel();
        var main = ctx.Driver.Show(new MainWindow { DataContext = vm, Width = 1024, Height = 768 });
        var backupsView = main.GetVisualDescendants().OfType<BackupsView>().Single();
        Assert.Equal(BackupRunStatus.Success, (await RunBackupViaUiAsync(ctx, backup, backupsView, default)).Status);
        await ctx.Driver.PumpAsync(200);
        var label = main.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "No checks recorded");
        Assert.True(label.Bounds.Width >= label.TextLayout.WidthIncludingTrailingWhitespace - 1);
        Assert.True(label.Bounds.Height >= label.TextLayout.Height - 1);
        ctx.Snapshot(main, "readable-status-at-1024", "No checks recorded is fully visible at a 1024 × 768 window size; the text can wrap instead of being cropped.");
        ServiceLocator.Config.Update(c => c.Backups.Single(b => b.Id == backup.Id).BandwidthLimitKBps = 1024);
        var client = fixture.Sidecar.Client!;
        var engineConfig = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>("/v1.Backrest/GetConfig", new(), default);
        engineConfig.Repos.Single().Flags.Add("--no-cache");
        await client.CallVoidAsync("/v1.Backrest/SetConfig", engineConfig, default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        async Task<ActiveRestoreSession> Start(bool full)
        {
            vm.SelectedNav = NavItem.Backups; await ctx.Driver.PumpAsync(200);
            var button = main.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible &&
                b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Test restore"));
            Click(main, button);
            var dialog = main.OwnedWindows.OfType<TestRestoreOptionsWindow>().Single(w => w.IsVisible);
            dialog.GetVisualDescendants().OfType<NumericUpDown>().Single(b => b.Name == "Percentage").Value = 25;
            if (full) Click(dialog, dialog.GetVisualDescendants().OfType<RadioButton>().Single(b => b.Name == "FullChoice"));
            else ctx.Snapshot(dialog, "choose-sample-or-every-file", "The actual Test restore dialog defaults to a sample, explains percentage and download budget, and offers every-file recovery.");
            Click(dialog, dialog.FindControl<Button>("StartButton")!);
            await vm.Backups.TestRestoreCommand.ExecutionTask!;
            ctx.Driver.Pump();
            return ServiceLocator.ActiveRestores.Sessions.Last(s => s.BackupId == backup.Id && s.IsTestRestore);
        }
        async Task Intermediate(ActiveRestoreSession session)
        {
            while (session.IsRunning && (session.BytesRestored == 0 || session.BytesRestored >= session.TotalBytes))
            { await Task.Delay(50, deadline.Token); ctx.Driver.Pump(); }
            Assert.True(session.IsRunning, session.ErrorMessage ?? session.Detail);
            Assert.InRange(session.PercentComplete, 0.00001, 0.99999);
        }
        TestRestoreJournal Latest() => Directory.GetFiles(Path.Combine(AppPaths.ConfigRoot, "test-restores"), "*.json")
            .Where(p => !Path.GetFileName(p).StartsWith("history-")).Select(p => JsonSerializer.Deserialize<TestRestoreJournal>(File.ReadAllText(p))!)
            .Where(j => j.BackupId == backup.Id).OrderByDescending(j => j.StartedUtc).First();

        var first = await Start(false);
        await Intermediate(first);
        Assert.Same(first, vm.ActiveRestores.SelectedSession);
        ctx.Snapshot(main, "real-sample-progress", "The orange Test restore label, real recovered-byte progress and Cancel button are visible in Restores.");
        await first.Completion.WaitAsync(deadline.Token); await ctx.Driver.PumpAsync(150);
        Assert.Equal(RestoreTerminalStatus.Success, first.Status);
        var firstJournal = Latest();
        Assert.Equal(75, firstJournal.Cells.Single().Results.Count);
        Assert.All(firstJournal.Cells.Single().Results, r => Assert.Equal(FileVerifyStatus.Recovered, r.Status));
        Assert.True(ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id).LastVerifyWasSample);
        ctx.Snapshot(main, "sample-result-with-scope", "75 of 300 files were recovered and validated by restic; comparison with today's originals was not requested. The result explicitly says this was a sample.");

        var second = await Start(false);
        await second.Completion.WaitAsync(deadline.Token); await ctx.Driver.PumpAsync(150);
        Assert.Equal(RestoreTerminalStatus.Success, second.Status);
        var secondJournal = Latest();
        Assert.Empty(firstJournal.Cells.Single().Files.Select(f => f.Path).Intersect(secondJournal.Cells.Single().Files.Select(f => f.Path)));
        Assert.All(secondJournal.Cells.Single().Results, r => Assert.Equal(FileVerifyStatus.Recovered, r.Status));
        ctx.Report.RecordNote("Rotating samples", "Two real 25% checks recovered 75 files each. The second sample contained none of the files checked by the first. Restic validated the recovered data; neither check compared it with today's originals.");

        var completedVerification = ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id).LastVerifyUtc;
        var cancel = await Start(true);
        await Intermediate(cancel);
        var stop = main.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, "Cancel"));
        Click(main, stop);
        await cancel.Completion.WaitAsync(deadline.Token); await ctx.Driver.PumpAsync(150);
        Assert.Equal(RestoreTerminalStatus.Cancelled, cancel.Status);
        Assert.Equal("cancelled", Latest().State);
        Assert.False(ServiceLocator.ActiveRestores.HasRunning);
        var afterCancel = ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id);
        Assert.True(afterCancel.LastVerifyPass);
        Assert.Equal(completedVerification, afterCancel.LastVerifyUtc);
        Assert.Equal("warn", vm.Backups.HealthClass); // The incomplete latest check needs attention; earlier proof remains saved.
        ctx.Snapshot(main, "stopped-test-is-not-success", "The actual Cancel button cancelled a live full check. The result says Cancelled, not Done, and the saved record prevents automatic resumption.");
        vm.SelectedNav = NavItem.Backups;
        vm.Backups.Refresh(); ctx.Driver.Pump(); main.UpdateLayout();
        Assert.Equal("warn", vm.Backups.HealthClass);
        Assert.Contains("incomplete", vm.Backups.Cards.Single(c => c.Id == backup.Id).VerifyLabel, StringComparison.OrdinalIgnoreCase);
        ctx.Snapshot(main, "cancelled-check-warning-survives-refresh", "After returning to Backups and refreshing the cards, the cancelled check still needs attention. The previous successful Test restore does not produce an all-clear.");
        var restart = new TestRestoreService(ServiceLocator.Config, ServiceLocator.Verifier);
        await restart.ResumeInterruptedAsync(); ctx.Driver.Pump();
        Assert.False(restart.IsRunning(backup.Id));
        Assert.False(ServiceLocator.ActiveRestores.HasRunning);
        Assert.Equal(300, Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length);
        ctx.Report.RecordNote("Negative cases", "A stopped run is not restarted by the same recovery entry point used at app startup. The original 300 files remain present. Real process-crash recovery is recorded separately; this scenario does not simulate a crash.");

        void Click(Window window, Control control)
        {
            control.BringIntoView(); ctx.Driver.Pump(); window.UpdateLayout();
            Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); ctx.Driver.Pump();
        }
    }
}
