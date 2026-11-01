using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S66_MultiSourceRestoreReadiness : ScenarioBase
{
    protected override string ScenarioName => "S66-multi-source-restore-readiness";
    [AvaloniaFact] public async Task Run_S66() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var sourceA = ctx.MakeSource("Documents", 1, 1000);
        var sourceB = ctx.MakeSource("Pictures", 2, 1000);
        var destination = new Destination { Name = "Local backup", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("backup") };
        var backup = new Backup { Name = "My folders", SourcePaths = { sourceA, sourceB },
            LastRunStatus = BackupRunStatus.Success, LastRunEndUtc = DateTime.UtcNow.AddHours(-1),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var originalBrowser = ServiceLocator.Revisions;
        var engine = DispatchProxy.Create<IBackupEngine, ListingProxy>();
        var proxy = (ListingProxy)engine;
        proxy.DelayedSource = sourceB;
        typeof(ServiceLocator).GetProperty(nameof(ServiceLocator.Revisions))!
            .SetValue(null, new RevisionBrowser(engine, ServiceLocator.Secrets));
        using var vm = new RestoreViewModel { SelectedBackup = backup };
        var window = ctx.Driver.Show(new Window { Width = 1100, Height = 780,
            Content = new RestoreView { DataContext = vm } });
        try
        {
            await vm.NextFromBackupCommand.ExecuteAsync(null);
            await ctx.Driver.PumpAsync(50);
            Assert.Equal(RestoreViewModel.Step.PickSource, vm.CurrentStep);
            ClickNext(window);
            await WaitFor(ctx, () => proxy.DelayedCalls == 1);
            Assert.Equal(RestoreViewModel.Step.PickRevision, vm.CurrentStep);
            Assert.True(vm.LoadingRevisions);
            Assert.False(Next(window).IsEnabled);
            ClickNext(window);
            Assert.Equal(RestoreViewModel.Step.PickRevision, vm.CurrentStep);
            ctx.Snapshot(window, "waiting-for-second-folder",
                "Snapshots are still loading for Pictures. Next stays disabled, preventing an incomplete restore.");

            proxy.Pending.SetException(new IOException("Synthetic destination read failure"));
            await WaitFor(ctx, () => !vm.LoadingRevisions);
            Assert.False(Next(window).IsEnabled);
            Assert.Contains(vm.MultiSourceRevisions, r => r.SourcePath == sourceB && r.LoadFailed);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && t.Text == "Couldn't read snapshots");
            Assert.All(vm.MultiSourceRevisions, row =>
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    t => t.IsEffectivelyVisible && t.Text == row.SourceName));
            ctx.Snapshot(window, "failed-read-is-not-empty",
                "Pictures says Couldn't read snapshots. The explanation offers Back and retry; Next is disabled.");

            // Retry via the visible Back and Next controls, then replace a
            // still-pending request with another retry. A late old failure must
            // not replace the successful new result.
            Click(window, VisibleButton(window, "Back"));
            proxy.Pending = new();
            ClickNext(window);
            await WaitFor(ctx, () => proxy.DelayedCalls == 2);
            Click(window, VisibleButton(window, "Back"));
            proxy.Succeed = true;
            ClickNext(window);
            await WaitFor(ctx, () => !vm.LoadingRevisions);
            proxy.Pending.SetException(new IOException("Late failure from abandoned request"));
            await ctx.Driver.PumpAsync(100);
            Assert.Null(vm.LoadError);
            Assert.Equal(2, vm.MultiSourceRevisions.Count);
            Assert.All(vm.MultiSourceRevisions, r => Assert.False(r.LoadFailed));
            Assert.True(Next(window).IsEnabled);
            ctx.Snapshot(window, "both-folders-ready-after-retry",
                "Both folders now have a selected snapshot and Next is enabled. A late error from the previous attempt did not replace them.");
            ClickNext(window);
            await ctx.Driver.PumpAsync(50);
            Assert.Equal(RestoreViewModel.Step.PickTarget, vm.CurrentStep);
            ctx.Report.RecordNote("Scope", "Actual Avalonia buttons and asynchronous loading. Listing responses are controlled test data; this scenario does not prove live cloud access or file recovery.");
        }
        finally
        {
            proxy.Pending.TrySetCanceled();
            typeof(ServiceLocator).GetProperty(nameof(ServiceLocator.Revisions))!.SetValue(null, originalBrowser);
            window.Close();
        }
    }

    private static async Task WaitFor(ScenarioContext ctx, Func<bool> condition)
    {
        for (var n = 0; n < 100 && !condition(); n++) await ctx.Driver.PumpAsync(20);
        Assert.True(condition());
        await ctx.Driver.PumpAsync(30);
    }
    private static Button VisibleButton(Window window, string label) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content?.ToString() == label);
    private static Button Next(Window window) => window.GetVisualDescendants().OfType<Button>()
        .Single(b => b.IsEffectivelyVisible && b.Content?.ToString()?.StartsWith("Next", StringComparison.Ordinal) == true);
    private static void ClickNext(Window window) => Click(window, Next(window));
    private static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
        var p = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left);
    }

    public class ListingProxy : DispatchProxy
    {
        public string DelayedSource = "";
        public int DelayedCalls;
        public bool Succeed;
        public TaskCompletionSource<IReadOnlyList<RevisionSummary>> Pending = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IBackupEngine.EnsureInitializedAsync)) return Task.CompletedTask;
            if (method.Name == nameof(IBackupEngine.ListSnapshotsAsync))
            {
                if ((string)args![1]! == DelayedSource && !Succeed) { DelayedCalls++; return Pending.Task; }
                return Task.FromResult<IReadOnlyList<RevisionSummary>>(new[] {
                    new RevisionSummary(1, new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc), 3, 102400)
                    { SnapshotId = "test-snapshot" } });
            }
            throw new InvalidOperationException("Unexpected engine operation: " + method.Name);
        }
    }
}
