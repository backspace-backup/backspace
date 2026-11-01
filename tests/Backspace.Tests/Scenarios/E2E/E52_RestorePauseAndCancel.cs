using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
public sealed class E52_RestorePauseAndCancel(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E52-restore-pause-and-cancel";
    [AvaloniaFact] public Task Run_E52() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var source = Path.Combine(ctx.SourcesDir, "documents"); Directory.CreateDirectory(source);
        var original = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        File.WriteAllBytes(Path.Combine(source, "photo.bin"), original);
        var dest = new Destination { Name = "Disposable archive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("archive"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e52", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Pause and cancel", UseVss = false, VssDisabledAcknowledged = true,
            SourcePaths = { source }, Targets = { new() { DestinationId = dest.Id } }, PruneSchedule = new(), CheckSchedule = new() };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(dest); });
        Assert.Equal(BackupRunStatus.Success, (await fixture.Engine.BackupPruneCheckAsync(backup, source, dest, "default", deadline.Token)).Status);
        var snapshot = (await fixture.Engine.ListSnapshotsAsync(backup, source, dest, "default", deadline.Token))[0];
        backup.BandwidthLimitKBps = 128;
        ServiceLocator.Config.Update(c => c.Backups.Single().BandwidthLimitKBps = 128);
        using var model = new MainWindowViewModel();
        var main = ctx.Driver.Show(new MainWindow { DataContext = model, Width = 1024, Height = 768 });
        try
        {
        var target = ctx.MakeRestoreTarget("recovered");
        File.WriteAllText(Path.Combine(target, "photo.bin"), "USER ORIGINAL");
        File.WriteAllText(Path.Combine(target, "unrelated.txt"), "UNRELATED");
        var run = await StartWizard(target);
        await Until(() => Directory.GetDirectories(target, ".backspace-restore-*").Any());
        Click("Pause");
        await Until(() => model.Restore.IsRestorePaused);
        Assert.True(ServiceLocator.ActiveRestores.HasRunning);
        Assert.True(ServiceLocator.Maintenance.HasReaders);
        Assert.Equal("USER ORIGINAL", File.ReadAllText(Path.Combine(target, "photo.bin")));
        var before = StagedHashes(target); await ctx.Driver.PumpAsync(350); Assert.Equal(before, StagedHashes(target));
        ctx.Snapshot(main, "ordinary-restore-paused", "The actual Pause button stopped the transfer. Resume and Cancel are separate, and the page explains that a file restore resumes while Backspace stays open.");
        // A new revision must not replace the snapshot selected before Pause.
        File.WriteAllBytes(Path.Combine(source, "photo.bin"), RandomNumberGenerator.GetBytes(original.Length));
        backup.BandwidthLimitKBps = 0;
        Assert.Equal(BackupRunStatus.Success, (await fixture.Engine.BackupPruneCheckAsync(backup, source, dest, "default", deadline.Token)).Status);
        backup.BandwidthLimitKBps = 128;
        Click("Resume"); await run.WaitAsync(deadline.Token); await ctx.Driver.PumpAsync(100);
        Assert.Equal("Restore complete", model.Restore.SummaryHeading);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(target, "photo.bin")));
        Assert.Equal("UNRELATED", File.ReadAllText(Path.Combine(target, "unrelated.txt")));
        Assert.Empty(Directory.GetDirectories(target, ".backspace-*"));
        ctx.Snapshot(main, "original-snapshot-restored", "Resume recovered exactly the original selected snapshot, even though a newer snapshot was created while paused.");

        var cancelTarget = ctx.MakeRestoreTarget("cancelled");
        File.WriteAllText(Path.Combine(cancelTarget, "photo.bin"), "KEEP THIS ORIGINAL");
        var cancelRun = await StartWizard(cancelTarget);
        await Until(() => Directory.GetDirectories(cancelTarget, ".backspace-restore-*").Any());
        Click("Pause"); await Until(() => model.Restore.IsRestorePaused);
        Click("Cancel"); await cancelRun.WaitAsync(deadline.Token); await ctx.Driver.PumpAsync(100);
        Assert.Equal("KEEP THIS ORIGINAL", File.ReadAllText(Path.Combine(cancelTarget, "photo.bin")));
        Assert.Empty(Directory.GetDirectories(cancelTarget, ".backspace-*"));
        Assert.Equal("Restore cancelled", model.Restore.SummaryHeading);
        Assert.False(ServiceLocator.Maintenance.HasReaders);
        ctx.Snapshot(main, "cancelled-restores-no-files", "Cancel from Paused ended the restore, removed its staging folder, and kept the pre-existing original file.");

        var restoresNav = main.GetVisualDescendants().OfType<RadioButton>().Single(b => b.IsEffectivelyVisible &&
            b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Restores"));
        ClickControl(restoresNav); await ctx.Driver.PumpAsync(50);
        Assert.Same(model.ActiveRestores, model.CurrentPage);
        var test = ServiceLocator.TestRestores.Start(ServiceLocator.Config.Current.Backups.Single(), new TestRestoreOptions(Full: true));
        ServiceLocator.ActiveRestores.Open(test);
        await Until(() => test.IsRunning && test.TotalFiles > 0);
        Click("Pause"); await Until(() => test.IsPaused);
        var journalDir = Path.Combine(AppPaths.ConfigRoot, "test-restores");
        var journalPath = Directory.GetFiles(journalDir, "*.json").Single(p => !Path.GetFileName(p).StartsWith("history-") &&
            JsonSerializer.Deserialize<TestRestoreJournal>(File.ReadAllText(p))!.BackupId == backup.Id);
        var pausedJournal = JsonSerializer.Deserialize<TestRestoreJournal>(File.ReadAllText(journalPath))!;
        Assert.Equal("paused", pausedJournal.State);
        ctx.Snapshot(main, "test-restore-paused", "Test restore has its own Resume and Cancel buttons. The paused choice is saved in the journal.");
        Click("Resume"); await Until(() => !test.IsPaused && test.IsRunning);
        Click("Pause"); await Until(() => test.IsPaused);
        Click("Cancel"); await test.Completion.WaitAsync(deadline.Token); await ctx.Driver.PumpAsync(100);
        Assert.Equal(RestoreTerminalStatus.Cancelled, test.Status);
        Assert.Equal("cancelled", JsonSerializer.Deserialize<TestRestoreJournal>(File.ReadAllText(journalPath))!.State);
        Assert.False(Directory.Exists(Path.Combine(AppPaths.ConfigRoot, "verify")) && Directory.GetDirectories(Path.Combine(AppPaths.ConfigRoot, "verify")).Length > 0);

        // Exercise startup recovery with the saved paused journal, after the
        // prior owner has fully stopped. This is a fresh service, not a forced
        // process crash; the separate process acceptance check covers that.
        pausedJournal.Id = Guid.NewGuid().ToString("N");
        var restartPath = Path.Combine(journalDir, pausedJournal.Id + ".json");
        TestRestoreContext.WriteAtomic(restartPath, pausedJournal);
        var recovery = new TestRestoreService(ServiceLocator.Config, ServiceLocator.Verifier);
        await recovery.ResumeInterruptedAsync(); await ctx.Driver.PumpAsync(200);
        var restoredPause = recovery.FindRunning(backup.Id)!;
        Assert.NotNull(restoredPause); Assert.True(restoredPause.IsPaused);
        Assert.Equal(0, restoredPause.BytesRestored);
        model.ActiveRestores.SelectedSession = restoredPause;
        ctx.Snapshot(main, "saved-pause-awaits-resume", "A fresh recovery service reads the saved paused journal and waits for Resume. It does not start downloading automatically.");
        Click("Cancel"); await restoredPause.Completion.WaitAsync(deadline.Token);
        Assert.Equal("cancelled", JsonSerializer.Deserialize<TestRestoreJournal>(File.ReadAllText(restartPath))!.State);
        Assert.False(ServiceLocator.ActiveRestores.HasRunning);
        ctx.Report.RecordNote("Adversarial checks", "Actual headless clicks paused/resumed/cancelled real restic transfers. A newer snapshot created while paused was not substituted. Cancellation preserved an existing original and removed staging. A fresh service loaded saved Paused state without downloading; this is not a simulated reboot or a real process crash. Publication rollback and concurrent user edits are also covered by focused filesystem tests.");
        }
        catch (Exception ex) { output.WriteLine(ex.ToString()); throw; }
        finally
        {
            ServiceLocator.ActiveRestores.CancelAll();
            var cleanupDeadline = DateTime.UtcNow.AddSeconds(30);
            while (ServiceLocator.ActiveRestores.HasRunning && DateTime.UtcNow < cleanupDeadline)
                await ctx.Driver.PumpAsync(50);
        }

        async Task<Task> StartWizard(string folder)
        {
            model.SelectedNav = NavItem.Restore;
            var vm = model.Restore; vm.RestartWizard();
            vm.SelectedBackup = backup; vm.SelectedSourcePath = source; vm.SelectedDestination = dest;
            vm.CurrentStep = RestoreViewModel.Step.PickRevision; vm.Revisions.Clear(); vm.Revisions.Add(snapshot); vm.SelectedRevision = snapshot;
            await vm.NextFromRevisionCommand.ExecuteAsync(null);
            await Until(() => vm.CurrentStep == RestoreViewModel.Step.PickFiles && vm.AllFiles.Count > 0);
            foreach (var file in vm.AllFiles) file.IsSelected = true;
            vm.NextFromFilesCommand.Execute(null); vm.TargetOriginalSource = false; vm.CustomTargetPath = folder;
            var operation = vm.NextFromTargetCommand.ExecuteAsync(null);
            await Until(() => vm.IsRunning && ServiceLocator.ActiveRestores.HasRunning);
            await ctx.Driver.PumpAsync(100); return operation;
        }
        async Task Until(Func<bool> ready)
        { while (!ready()) { deadline.Token.ThrowIfCancellationRequested(); await ctx.Driver.PumpAsync(50); } }
        void Click(string label)
        {
            main.UpdateLayout();
            var button = main.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, label));
            ClickControl(button);
        }
        void ClickControl(Control button)
        {
            Assert.True(button.IsEnabled); button.BringIntoView(); ctx.Driver.Pump(); main.UpdateLayout();
            var position = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), main)!.Value;
            main.MouseDown(position, MouseButton.Left); main.MouseUp(position, MouseButton.Left); ctx.Driver.Pump();
        }
        static string[] StagedHashes(string folder) => Directory.GetDirectories(folder, ".backspace-restore-*")
            .SelectMany(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories)).OrderBy(p => p)
            .Select(p => p + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
    }
}
