using System;
using System.IO;
using System.Linq;
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
public sealed class E42_StreamingRestoreProgress : BackupE2EBase
{
    public E42_StreamingRestoreProgress(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E42-streaming-restore-progress";
    [AvaloniaFact] public async Task Run_E42() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source = Path.Combine(ctx.SourcesDir, "source [2026]");
        var selected = new[] { "left/picked[1].bin", "right/picked[2].bin" };
        var allFiles = selected.Concat(new[] { "left/pickedX1Y.bin", "right/unselected.bin" }).ToArray();
        var random = new Random(42026);
        var bytes = new byte[16 * 1024 * 1024];
        foreach (var file in allFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, file))!);
            random.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(source, file), bytes);
        }
        var dest = new Destination { Name = "Disposable local repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e42", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Selected files · live progress", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(backup); });
        var view = new BackupsView { DataContext = new BackupsViewModel() };
        ctx.Driver.Show(new Window { Width = 1100, Height = 760, Content = view });
        Assert.Equal(BackupRunStatus.Success, (await RunBackupViaUiAsync(ctx, backup, view, default)).Status);
        var revision = (await ServiceLocator.Revisions.ListRevisionsAsync(backup, source, dest, "default", default))[0].Number;
        var client = fixture.Sidecar.Client!;
        var config = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>("/v1.Backrest/GetConfig", new(), default);
        config.Repos.Single().Flags.Add("--no-cache");
        await client.CallVoidAsync("/v1.Backrest/SetConfig", config, default);
        backup.BandwidthLimitKBps = 1024; // The production per-backup limit, not a synthetic progress source.
        var main = ctx.Driver.Show(new MainWindow { DataContext = new MainWindowViewModel { SelectedNav = NavItem.ActiveRestores } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        RestoreRequest Request(string target) => new() { Backup = backup, SourcePath = source, Destination = dest,
            StorageName = "default", Revision = revision, Files = selected, IncludePatterns = selected, TargetPath = target };
        var target = ctx.MakeRestoreTarget("selected-files");
        File.WriteAllText(Path.Combine(target, "existing.txt"), "Keep the original");
        var run = ServiceLocator.Restore.RunAsync(Request(target), deadline.Token);
        var live = await WaitForProgress(run, target);
        Assert.False(live.IsProgressIndeterminate);
        var restoreIcon = main.GetVisualDescendants().OfType<PathIcon>()
            .Single(icon => icon.Classes.Contains("active-restore-icon"));
        var offsets = new System.Collections.Generic.List<double>();
        for (var frame = 0; frame < 40; frame++)
        {
            await Task.Delay(25, deadline.Token);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            ctx.Driver.Pump();
            offsets.Add(restoreIcon.RenderTransform?.Value.M32 ?? 0);
        }
        Assert.True(offsets.Max() - offsets.Min() > 1, "The real restore must animate the sidebar arrow.");
        ctx.Report.RecordNote("Actual restore icon motion", "A real, bandwidth-limited restore produced rendered vertical offsets from " +
            offsets.Min().ToString("F2") + " to " + offsets.Max().ToString("F2") + " pixels across 40 frames.");
        ctx.Snapshot(main, "selected files restore in progress", "Two scattered files restored with the production 1 MiB/s limit. The displayed byte progress comes from real restic output.");
        ctx.Report.RecordNote("Real intermediate progress", $"Observed {live.BytesRestored:N0} of {live.TotalBytes:N0} bytes before completion.");
        var result = await run;
        Assert.True(result.TerminalStatus == RestoreTerminalStatus.Success, result.ErrorMessage);
        foreach (var file in selected)
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, file)), File.ReadAllBytes(Path.Combine(target, file)));
        Assert.False(File.Exists(Path.Combine(target, "left/pickedX1Y.bin")));
        Assert.False(File.Exists(Path.Combine(target, "right/unselected.bin")));
        Assert.Equal("Keep the original", File.ReadAllText(Path.Combine(target, "existing.txt")));
        Assert.Equal(3, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(32L * 1024 * 1024, result.TotalBytes);
        Assert.Equal(result.TotalBytes, result.BytesRestored);
        ctx.Driver.Pump();
        Assert.Equal(1, live.PercentComplete);

        var cancelledTarget = ctx.MakeRestoreTarget("cancelled-selected-files");
        Directory.CreateDirectory(Path.Combine(cancelledTarget, "left"));
        var original = Path.Combine(cancelledTarget, selected[0]);
        File.WriteAllText(original, "Existing content survives Stop restore");
        var cancelledRun = ServiceLocator.Restore.RunAsync(Request(cancelledTarget), deadline.Token);
        var cancelling = await WaitForProgress(cancelledRun, cancelledTarget);
        var stop = main.GetVisualDescendants().OfType<Button>().Single(b =>
            Equals(b.Content, "Stop restore") && ReferenceEquals(b.CommandParameter, cancelling));
        stop.BringIntoView();
        ctx.Driver.Pump();
        var point = stop.TranslatePoint(new Point(stop.Bounds.Width / 2, stop.Bounds.Height / 2), main)!.Value;
        main.MouseMove(point);
        main.MouseDown(point, MouseButton.Left);
        main.MouseUp(point, MouseButton.Left);
        ctx.Driver.Pump();
        var cancelled = await cancelledRun;
        Assert.Equal(RestoreTerminalStatus.Cancelled, cancelled.TerminalStatus);
        Assert.Equal("Existing content survives Stop restore", File.ReadAllText(original));
        Assert.Empty(Directory.GetDirectories(cancelledTarget, ".backspace-restore-*"));
        Assert.False(ServiceLocator.ActiveRestores.HasRunning);
        Assert.False(ServiceLocator.Maintenance.HasReaders);
        await Task.Delay(500, deadline.Token);
        ctx.Driver.Pump();
        ctx.Snapshot(main, "completed and stopped selected restores", "The first restore recovered exactly the selected contents. Clicking Stop restore cancelled the second and preserved the existing file.");
        Assert.DoesNotContain("active-restore-icon", restoreIcon.Classes);
        for (var frame = 0; frame < 8; frame++)
        {
            await Task.Delay(25, deadline.Token);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            ctx.Driver.Pump();
            Assert.InRange(Math.Abs(restoreIcon.RenderTransform?.Value.M32 ?? 0), 0, 0.01);
        }
        var mainVm = (MainWindowViewModel)main.DataContext!;
        var secondCard = main.GetVisualDescendants().OfType<Button>().Single(button =>
            ReferenceEquals(button.DataContext, cancelling) && button.Classes.Contains("card"));
        secondCard.BringIntoView();ctx.Driver.Pump();
        var cardPoint = secondCard.TranslatePoint(new Point(secondCard.Bounds.Width / 2, secondCard.Bounds.Height / 2), main)!.Value;
        main.MouseDown(cardPoint, MouseButton.Left);main.MouseUp(cardPoint, MouseButton.Left);ctx.Driver.Pump();
        Assert.Same(cancelling, mainVm.ActiveRestores.SelectedSession);
        Assert.NotSame(live, mainVm.ActiveRestores.SelectedSession);
        Assert.DoesNotContain(main.GetVisualDescendants().OfType<Button>(), button =>
            button.IsEffectivelyVisible && button.Command == mainVm.ActiveRestores.OpenSessionCommand);
        ctx.Snapshot(main, "cancelled card opens its own result", "Clicking the second actual restore opens its cancelled result alone. The successful first restore is not selected or duplicated above it.");
        var backToList = main.GetVisualDescendants().OfType<Button>().Single(button =>
            button.IsEffectivelyVisible && Equals(button.Content, "← All restores"));
        backToList.BringIntoView();ctx.Driver.Pump();
        var backPoint = backToList.TranslatePoint(new Point(backToList.Bounds.Width / 2, backToList.Bounds.Height / 2), main)!.Value;
        main.MouseDown(backPoint, MouseButton.Left);main.MouseUp(backPoint, MouseButton.Left);
        ctx.Driver.Pump();Assert.Null(mainVm.ActiveRestores.SelectedSession);
        Assert.Equal(2, mainVm.ActiveRestores.Sessions.Count);

        // A real restic error must not turn temporary/preallocated output into
        // recovered data. Corrupt only large content packs in this disposable
        // test repository; tree metadata remains available for browsing.
        var dataRoot = Path.Combine(config.Repos.Single().Uri, "data");
        var packs = Directory.GetFiles(dataRoot, "*", SearchOption.AllDirectories)
            .Where(p => new FileInfo(p).Length > 1024 * 1024).ToArray();
        Assert.NotEmpty(packs);
        foreach (var pack in packs)
        {
            Assert.StartsWith(Path.GetFullPath(dest.PathOrSubpath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                Path.GetFullPath(pack), StringComparison.OrdinalIgnoreCase);
            File.WriteAllBytes(pack, new byte[] { 1, 2, 3 });
        }
        var failedTarget = ctx.MakeRestoreTarget("failed-selected-files");
        foreach (var file in selected)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(failedTarget, file))!);
            File.WriteAllText(Path.Combine(failedTarget, file), "Original survives a damaged repository");
        }
        var failed = await ServiceLocator.Restore.RunAsync(Request(failedTarget), deadline.Token);
        Assert.Equal(RestoreTerminalStatus.Error, failed.TerminalStatus);
        Assert.Equal(0, failed.RestoredCount);
        foreach (var file in selected)
            Assert.Equal("Original survives a damaged repository", File.ReadAllText(Path.Combine(failedTarget, file)));
        Assert.Empty(Directory.GetDirectories(failedTarget, ".backspace-restore-*"));
        Assert.False(ServiceLocator.Maintenance.HasReaders);
        ctx.Driver.Pump();
        ctx.Report.RecordNote("Real failure control", "After the successful/cancelled cases, the fixture deliberately damaged the large content packs in its own disposable repository. Restic reported an error, zero files were promoted, both existing target files stayed unchanged, and the repository read lease was released only after the process stopped.");
        ctx.Report.RecordNote("Scope", "Real bundled Backrest created the encrypted snapshot and supplied its configuration. An owned bundled restic process restored the scattered selection with a per-backup limit. Every selected file matched; an overmatching bracket sibling and another unselected file were excluded. A real headless pointer click on Stop restore cancelled the second run. No live cloud account was used.");

        async Task<ActiveRestoreSession> WaitForProgress(Task<RestoreOutcome> task, string output)
        {
            while (!task.IsCompleted)
            {
                ctx.Driver.Pump();
                var session = ServiceLocator.ActiveRestores.Sessions.FirstOrDefault(s => s.TargetPath == output);
                if (session is { IsRunning: true, BytesRestored: > 0 } && session.BytesRestored < session.TotalBytes) return session;
                await Task.Delay(100, deadline.Token);
            }
            var outcome = await task;
            throw new Xunit.Sdk.XunitException($"No intermediate progress arrived: {outcome.TerminalStatus}, {outcome.ErrorMessage}");
        }
    }
}
