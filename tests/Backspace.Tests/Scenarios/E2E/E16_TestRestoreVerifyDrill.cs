using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
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

/// <summary>
/// Test-restore (verify drill) end-to-end against the real engine.
/// Clicks the actual Test restore control through headless pointer input.
/// Real Backrest/restic restores both sources, including an empty file,
/// then compares them with the originals. A second click must detect a
/// same-length source mismatch with its original modification time retained.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E16_TestRestoreVerifyDrill : BackupE2EBase
{
    public E16_TestRestoreVerifyDrill(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E16-test-restore-verify-drill";

    [AvaloniaFact] public async Task Run_E16() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source   = ctx.MakeSource("docs", seed: 161, targetBytes: 700_000);
        var secondSource = ctx.MakeSource("photos", seed: 162, targetBytes: 100_000);
        for (var n = 0; n < 12; n++) File.WriteAllText(Path.Combine(source, $"extra-{n}.txt"), $"Original file {n}");
        File.WriteAllBytes(Path.Combine(secondSource, "empty.txt"), Array.Empty<byte>());
        var changedPath = Path.Combine(secondSource, "compare-me.txt");
        File.WriteAllText(changedPath, "original bytes");
        var originalStamp = File.GetLastWriteTimeUtc(changedPath);
        var expectedFiles = new[] { source, secondSource }.Sum(p => Directory.GetFiles(p, "*", SearchOption.AllDirectories).Length);
        var destPath = ctx.MakeLocalDestination("verify-local");

        var dest = new Destination
        {
            Name = "Local — verify target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e16", $"e16-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E16)",
            SourcePaths = { source, secondSource },
            UseVss = false, VssDisabledAcknowledged = true,
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
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup ended {record.Status}; errors={string.Join(" | ", record.Errors)}.");

        var vm = (BackupsViewModel)view.DataContext!;
        var card = vm.Cards.Single(c => c.Id == backup.Id);
        Assert.Contains("Download and recover files", card.TestRestoreTooltip);
        Assert.DoesNotContain("10 random", card.TestRestoreTooltip);
        ctx.Snapshot(host, "before-test-restore-click", "Two source folders backed up; the Test restore button is available.");
        async Task ClickTestRestore()
        {
            ctx.Driver.Pump();
            var button = view.GetVisualDescendants().OfType<Button>().Single(b =>
                b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Test restore"));
            Assert.True(button.IsEnabled);
            button.BringIntoView();
            ctx.Driver.Pump();
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host);
            Assert.NotNull(point);
            var previous = vm.TestRestoreCommand.ExecutionTask;
            host.MouseDown(point.Value, MouseButton.Left);
            host.MouseUp(point.Value, MouseButton.Left);
            ctx.Driver.Pump();
            var task = vm.TestRestoreCommand.ExecutionTask;
            Assert.NotNull(task);
            Assert.NotSame(previous, task);
            var options = host.OwnedWindows.OfType<TestRestoreOptionsWindow>().Single();
            void Click(Control control)
            {
                control.BringIntoView(); ctx.Driver.Pump(); options.UpdateLayout();
                var p = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), options)!.Value;
                options.MouseDown(p, MouseButton.Left); options.MouseUp(p, MouseButton.Left); ctx.Driver.Pump();
            }
            Click(options.GetVisualDescendants().OfType<RadioButton>().Single(b => b.Name == "FullChoice"));
            var compare = options.GetVisualDescendants().OfType<CheckBox>().Single(b => b.Name == "CompareOriginals");
            if (compare.IsChecked != true) Click(compare);
            Click(options.FindControl<Button>("StartButton")!);
            await task.WaitAsync(TimeSpan.FromMinutes(2));
            var session = ServiceLocator.ActiveRestores.Sessions.Last(s => s.IsTestRestore && s.BackupId == backup.Id);
            await session.Completion.WaitAsync(TimeSpan.FromMinutes(2));
            await ctx.Driver.PumpAsync(200);
            ctx.Driver.Pump();
        }

        await ClickTestRestore();
        var verified = ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id);
        Assert.True(verified.LastVerifyPass, verified.LastVerifySummary);
        Assert.Contains($"{expectedFiles} files restored; {expectedFiles} matched today's originals", verified.LastVerifySummary);
        Assert.DoesNotContain("size-matched only", verified.LastVerifySummary);
        Assert.Contains(verified.LastVerifySummary!, card.VerifyDetails);
        // Leave the result's real tooltip visible in the proof so the file count can be read.
        async Task HoverResult()
        {
            var label = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == card.VerifyLabel);
            var at = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), host)!.Value;
            host.MouseMove(at);
            await Task.Delay(600);
            ctx.Driver.Pump();
        }
        await HoverResult();
        ctx.Snapshot(host, "test-restore-button-success", $"Actual Test restore click: all {expectedFiles} files from two sources, including an empty file, restored and compared using real Backrest/restic.");

        // Negative control: equal size and unchanged mtime must not conceal changed bytes.
        File.WriteAllText(changedPath, "modified bytes");
        File.SetLastWriteTimeUtc(changedPath, originalStamp);
        await ClickTestRestore();
        var failed = ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id);
        Assert.False(failed.LastVerifyPass, "Changed bytes in the second source must fail the drill.");
        Assert.Equal("fail", vm.HealthClass);
        Assert.Equal("Test restore failed for 1 backup", vm.HealthHeadline);
        Assert.Equal(backup.Id, Assert.Single(vm.AffectedBackups).Id);
        Assert.Contains("Files recovered successfully", failed.LastVerifySummary);
        Assert.Contains("differ from today's originals", failed.LastVerifySummary);
        Assert.Contains(failed.LastVerifySummary!, card.VerifyDetails);
        var runs = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).Where(r => r.Kind == RunKind.TestRestore).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, r => r.Status == BackupRunStatus.Success);
        Assert.Contains(runs, r => r.Status == BackupRunStatus.Failed);
        Assert.All(runs, r => Assert.True(File.Exists(r.LogPath)));
        await HoverResult();
        ctx.Snapshot(host, "test-restore-button-detects-mismatch", "A second click detects the deliberately changed bytes. Both the page banner and the card report the failed Test restore; the page cannot say everything looks good.");

        File.WriteAllText(changedPath, "original bytes");
        File.SetLastWriteTimeUtc(changedPath, originalStamp);
        await ClickTestRestore();
        var retested = ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id);
        Assert.True(retested.LastVerifyPass, retested.LastVerifySummary);
        Assert.Equal("ok", vm.HealthClass);
        Assert.Empty(vm.AffectedBackups);
        await HoverResult();
        ctx.Snapshot(host, "passing-retest-clears-health-warning", "After restoring the original fixture bytes, a third actual Test restore click succeeds and clears the red banner. A plain settings refresh or a later backup cannot clear it.");
    }
}
