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

namespace Backspace.Tests.Scenarios.E2E;

[Collection(ScenarioCollection.Name)]
public sealed class E41_NativeRestoreProgress : BackupE2EBase
{
    public E41_NativeRestoreProgress(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E41-native-restore-progress";
    [AvaloniaFact] public async Task Run_E41() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source = Path.Combine(ctx.SourcesDir, "source [2026]");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        var random = new Random(41026);
        var bytes = new byte[8 * 1024 * 1024];
        var selectedFiles = Enumerable.Range(0, 16).Select(i => $"nested/large[{i}].bin").ToArray();
        const string selected = "nested/large[0].bin";
        foreach (var file in selectedFiles)
        {
            random.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(source, file), bytes);
        }
        File.WriteAllText(Path.Combine(source, "small.txt"), "Second file in the complete source");
        var dest = new Destination { Name = "Disposable local repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e41", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Real restore progress", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(backup); });
        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        Assert.Equal(BackupRunStatus.Success, (await RunBackupViaUiAsync(ctx, backup, view, default)).Status);
        var revision = (await ServiceLocator.Revisions.ListRevisionsAsync(backup, source, dest, "default", default))[0].Number;

        // Throttle only this disposable engine's repository. This is real restic
        // I/O and real native RPC progress, not injected status messages.
        var client = fixture.Sidecar.Client!;
        var config = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>("/v1.Backrest/GetConfig", new(), default);
        config.Repos.Single().Flags.Add("--limit-download=4096");
        config.Repos.Single().Flags.Add("--no-cache");
        await client.CallVoidAsync("/v1.Backrest/SetConfig", config, default);
        // Backrest can retain an existing repository orchestrator after a
        // configuration update. Restart this owned test daemon so the limit
        // is active before measuring progress, not eventually applied later.
        await fixture.Sidecar.StopAsync();
        await fixture.Sidecar.StartAsync(default);
        var mainVm = new MainWindowViewModel { SelectedNav = NavItem.ActiveRestores };
        var main = new MainWindow { DataContext = mainVm };
        ctx.Driver.Show(main);

        var target = ctx.MakeRestoreTarget("complete-source");
        File.WriteAllText(Path.Combine(target, "existing.txt"), "Keep original");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var run = ServiceLocator.Restore.RunAsync(new RestoreRequest {
            Backup = backup, SourcePath = source, Destination = dest, StorageName = "default", Revision = revision,
            Files = selectedFiles.Append("small.txt").ToArray(), IncludePatterns = new[] { "/" }, TargetPath = target }, deadline.Token);
        var live = await WaitForLiveProgress(run, target, ctx, deadline.Token);
        Assert.False(live.IsProgressIndeterminate);
        ctx.Snapshot(main, "real restore in progress", $"Live progress from bundled Backrest/restic: {live.StatusLabel}. The restore has not finished.");
        ctx.Report.RecordNote("Live progress", $"Observed {live.BytesRestored:N0} of {live.TotalBytes:N0} bytes before completion. No simulated progress was supplied.");
        var result = await run;
        Assert.True(result.TerminalStatus == RestoreTerminalStatus.Success, result.ErrorMessage);
        foreach (var file in selectedFiles)
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, file)), File.ReadAllBytes(Path.Combine(target, file)));
        Assert.Equal(File.ReadAllText(Path.Combine(source, "small.txt")), File.ReadAllText(Path.Combine(target, "small.txt")));
        Assert.Equal("Keep original", File.ReadAllText(Path.Combine(target, "existing.txt")));
        Assert.Equal(18, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length);

        var cancelledTarget = ctx.MakeRestoreTarget("cancelled-file");
        Directory.CreateDirectory(Path.Combine(cancelledTarget, "nested"));
        File.WriteAllText(Path.Combine(cancelledTarget, selected), "Original must survive cancellation");
        var cancelledRun = ServiceLocator.Restore.RunAsync(new RestoreRequest {
            Backup = backup, SourcePath = source, Destination = dest, StorageName = "default", Revision = revision,
            Files = selectedFiles, IncludePatterns = new[] { "nested" }, TargetPath = cancelledTarget }, deadline.Token);
        var cancelledSession = await WaitForLiveProgress(cancelledRun, cancelledTarget, ctx, deadline.Token);
        cancelledSession.Cancel(); // Same cancellation action used by the session card.
        var cancelledResult = await cancelledRun;
        Assert.Equal(RestoreTerminalStatus.Cancelled, cancelledResult.TerminalStatus);
        Assert.Equal("Original must survive cancellation", File.ReadAllText(Path.Combine(cancelledTarget, selected)));
        Assert.Empty(Directory.GetDirectories(cancelledTarget, ".backspace-restore-*"));
        ctx.Driver.Pump();
        ctx.Snapshot(main, "completed and cancelled real restores", "One real restore completed; the second was cancelled after intermediate progress. Existing destination contents survived.");
        ctx.Report.RecordNote("Cancellation and layout", "The complete source restored 128 MiB across 16 bracket-named files plus a small file byte-for-byte, with the source directory removed from the output layout. Cancelling a nested-folder restore preserved the original destination file and removed only its temporary staging folder.");
    }

    private static async Task<ActiveRestoreSession> WaitForLiveProgress(Task<RestoreOutcome> run, string target,
        ScenarioContext ctx, CancellationToken ct)
    {
        while (!run.IsCompleted)
        {
            ctx.Driver.Pump();
            var session = ServiceLocator.ActiveRestores.Sessions.FirstOrDefault(s => s.TargetPath == target);
            if (session is { IsRunning: true, BytesRestored: > 0 } && session.BytesRestored < session.TotalBytes)
                return session;
            await Task.Delay(100, ct);
        }
        var result = await run;
        throw new Xunit.Sdk.XunitException($"No intermediate progress arrived: {result.TerminalStatus}, {result.ErrorMessage}");
    }
}
