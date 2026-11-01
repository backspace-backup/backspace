using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

[Collection(ScenarioCollection.Name)]
public sealed class E51_TrayWindowRecreation(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E51-tray-window-recreation";
    [AvaloniaFact] public Task Run_E51() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var previousQuit = App.QuitRequested; App.QuitRequested = false;
        var source = Path.Combine(ctx.SourcesDir, "documents"); Directory.CreateDirectory(source);
        var original = RandomNumberGenerator.GetBytes(1024 * 1024);
        File.WriteAllBytes(Path.Combine(source, "photo.bin"), original);
        var destination = new Destination { Name = "Disposable archive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("archive"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e51", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Tray restore check", UseVss = false, VssDisabledAcknowledged = true,
            SourcePaths = { source }, Targets = { new() { DestinationId = destination.Id } }, PruneSchedule = new(), CheckSchedule = new() };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination);
            c.Preferences.MinimizeToTrayOnClose = true; c.Preferences.ShowTrayIcon = true; });
        Assert.Equal(BackupRunStatus.Success,
            (await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", deadline.Token)).Status);
        backup.BandwidthLimitKBps = 64; // Keeps the actual restore active across window recreation.
        MainWindow? attached = null;
        using var host = new TrayWindowHost(window => attached = window);
        try
        {
            var first = ctx.Driver.Show(host.GetOrCreate());
            var model = (MainWindowViewModel)first.DataContext!;
            model.SelectedNav = NavItem.Restore;
            var restore = model.Restore;
            restore.SelectedBackup = backup; restore.SelectedSourcePath = source; restore.SelectedDestination = destination;
            restore.CurrentStep = RestoreViewModel.Step.PickRevision;
            var revisions = await fixture.Engine.ListSnapshotsAsync(backup, source, destination, "default", deadline.Token);
            foreach (var revision in revisions) restore.Revisions.Add(revision);
            restore.SelectedRevision = revisions[0];
            await restore.NextFromRevisionCommand.ExecuteAsync(null);
            await Until(() => restore.CurrentStep == RestoreViewModel.Step.PickFiles && restore.AllFiles.Count > 0);
            foreach (var file in restore.AllFiles) file.IsSelected = true;
            restore.NextFromFilesCommand.Execute(null);
            restore.TargetOriginalSource = false;
            var target = ctx.MakeRestoreTarget("recovered"); restore.CustomTargetPath = target;
            var operation = restore.NextFromTargetCommand.ExecuteAsync(null);
            await Until(() => restore.IsRunning && ServiceLocator.ActiveRestores.HasRunning);
            var closed = false; first.Closed += (_, _) => closed = true;
            first.Close(); await ctx.Driver.PumpAsync(150);
            Assert.True(closed); Assert.True(first.WasClosedToTray);
            Assert.Null(attached); Assert.Null(host.Window); Assert.Null(first.DataContext);
            Assert.False(App.QuitRequested); Assert.True(restore.IsRunning);
            Assert.True(ServiceLocator.ActiveRestores.HasRunning);
            var second = ctx.Driver.Show(host.Show());
            Assert.NotSame(first, second); Assert.Same(model, second.DataContext);
            Assert.Same(restore, model.Restore);
            await ctx.Driver.PumpAsync(150);
            ctx.Snapshot(second, "restore-survives-new-window", "The first window was closed and detached, not hidden. A new window displays the same real restore, which continues without cancellation.");
            await operation.WaitAsync(deadline.Token);
            Assert.Equal(RestoreViewModel.Step.Summary, restore.CurrentStep);
            Assert.Equal("Restore complete", restore.SummaryHeading);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(target, "photo.bin")));
            ctx.Snapshot(second, "same-restore-completed", "The recovered 1 MiB file matches the original after closing to tray and creating a fresh window.");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var current = host.Window!; current.Close(); await ctx.Driver.PumpAsync(30);
                Assert.Null(host.Window);
                var reopened = ctx.Driver.Show(host.Show());
                Assert.NotSame(current, reopened); Assert.Same(model, reopened.DataContext);
                Assert.Equal("Restore complete", model.Restore.SummaryHeading);
            }
            ctx.Report.RecordNote("Adversarial checks", "A real throttled restore remained active after the window Closed event and finished with matching bytes. Three further close/reopen cycles retained the same result and model while creating distinct windows. Explicit Quit was never set by closing to tray. This is headless interface input with actual Backrest/restic; native tray CPU is measured separately.");
        }
        finally
        {
            App.QuitRequested = true; host.Window?.Close();
            App.QuitRequested = previousQuit;
        }
        async Task Until(Func<bool> ready)
        {
            while (!ready()) { deadline.Token.ThrowIfCancellationRequested(); await ctx.Driver.PumpAsync(50); }
        }
    }
}
