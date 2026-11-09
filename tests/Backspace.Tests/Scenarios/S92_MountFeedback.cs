using System;
using System.Collections.Concurrent;
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
using Backspace.Services.WebDav;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U158 (owner, 7 November 2026): "Nothing happened for like 10s, I thought
/// it was not working". From the click, the card says it is mounting and
/// what it is doing; then it names the drive with Open in Explorer; a failure
/// stays on the card. Unmount stays possible while a backup runs.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S92_MountFeedback : ScenarioBase
{
    protected override string ScenarioName => "S92-mount-feedback";
    [AvaloniaFact] public async Task Run_S92() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var registry = new SlowMount();
        var backup = new Backup { Name = "local_c", LastRunStatus = BackupRunStatus.Success,
            LastRunEndUtc = DateTime.UtcNow.AddHours(-1), LastSuccessUtc = DateTime.UtcNow.AddHours(-1) };
        using var card = new BackupCardViewModel(backup, true, false, registry);
        var host = ctx.Driver.Show(new Window { Width = 1000, Height = 520, Content = new BackupCard { DataContext = card } });

        // The click itself: before the registry has said anything.
        Click(host, "Mount"); ctx.Driver.Pump();
        Assert.Equal("Mounting…", card.MountActionLabel);
        Assert.False(card.CanMountOrUnmount);
        Assert.True(Visible(host, "Mounting · Starting…"));
        Assert.False(Button(host, "Mounting…").IsEnabled);
        ctx.Snapshot(host, "mounting-from-the-click", "Right after the click: the button reads Mounting… (disabled) and the card shows \"Mounting · Starting…\" with a moving bar and Cancel.");

        // A snapshot without a summary (older snapshots): no percentage, the bar keeps moving.
        registry.Report(backup.Id, new MountReading(312_000)); ctx.Driver.Pump();
        Assert.True(Visible(host, "Mounting · Reading the backup's folders: 312,000 files so far…"));
        Assert.True(Bar(host).IsIndeterminate);
        Assert.False(card.HasMountPercent);

        // U166: the summary's 415,256 files make it a percentage.
        registry.Report(backup.Id, new MountReading(170_000, Percent: 40)); ctx.Driver.Pump();
        registry.Report(backup.Id, new MountReading(312_000, Percent: 75)); ctx.Driver.Pump();
        Assert.True(Visible(host, "Mounting · Reading the backup's folders: 312,000 files so far…"));
        Assert.True(Visible(host, "75%"));
        Assert.False(Bar(host).IsIndeterminate);
        Assert.Equal(75, Bar(host).Value);
        host.MouseMove(new Point(990, 510)); // away from Mounting…, whose tooltip would cover the line
        Settle(ctx, host);
        ctx.Snapshot(host, "mounting-reading-folders", "While the snapshot's folder list is read, the bar fills to 75% with \"75%\" beside it, and the text says 312,000 files so far (of the 415,256 the backup's summary counts).");

        registry.Finish(backup.Id, MountResult.Ok("Z:")); ctx.Driver.Pump();
        await WaitAsync(ctx, () => card.MountedDrive == "Z:" && !card.IsMounting);
        Assert.Equal("Unmount", card.MountActionLabel);
        Assert.True(Visible(host, "Mounted as Z:, read-only. Backspace unmounts it when you quit."));
        Assert.False(card.HasMountPercent);
        Settle(ctx, host);
        Click(host, "Open in Explorer"); ctx.Driver.Pump();
        await WaitAsync(ctx, () => registry.ExplorerOpens == 1);
        ctx.Snapshot(host, "mounted-with-open-in-explorer", "Mounted: the card names drive Z: with Open in Explorer, and the button offers Unmount.");

        // A backup starting meanwhile adds a snapshot; the drive stays and can still be unmounted.
        card.IsRunning = true; ctx.Driver.Pump();
        Assert.True(card.CanMountOrUnmount);
        Assert.True(Button(host, "Unmount").IsEnabled);
        card.IsRunning = false; ctx.Driver.Pump();

        Click(host, "Unmount"); ctx.Driver.Pump();
        await WaitAsync(ctx, () => card.MountedDrive is null);
        Assert.False(card.ShowMountLine);

        Click(host, "Mount"); ctx.Driver.Pump();
        registry.Finish(backup.Id, MountResult.Failed("Couldn't read the snapshot's folders from the destination. Check the destination is reachable and try again."));
        await WaitAsync(ctx, () => card.MountError is not null);
        Assert.Equal("Mount", card.MountActionLabel);
        Assert.True(Visible(host, "Couldn't mount: Couldn't read the snapshot's folders from the destination. Check the destination is reachable and try again."));
        ctx.Snapshot(host, "mount-failure-inline", "A failed mount says why on the card, in red, with Dismiss; Mount is available again.");
        Click(host, "Dismiss"); ctx.Driver.Pump();
        Assert.False(card.ShowMountLine);

        // Cancel during a mount stops it and clears the line.
        Click(host, "Mount"); ctx.Driver.Pump();
        Click(host, "Cancel"); ctx.Driver.Pump();
        await WaitAsync(ctx, () => !card.IsMounting);
        Assert.Null(card.MountError);
        host.Close();
    }

    private static void Settle(ScenarioContext ctx, Window window)
    {
        ctx.Driver.Pump(); Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
        ctx.Driver.Pump(); Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
    }

    private static ProgressBar Bar(Window window)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<ProgressBar>().Single(b => b.Name == "MountProgressBar");
    }

    private static async Task WaitAsync(ScenarioContext ctx, Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) { await Task.Delay(10); ctx.Driver.Pump(); }
        Assert.True(condition());
    }

    private static bool Visible(Window window, string text)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);
    }

    private static Button Button(Window window, string label)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible &&
            (b.Content?.ToString() == label || b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label)));
    }

    private static void Click(Window window, string label)
    {
        var button = Button(window, label);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }

    /// <summary>A mount that takes as long as the test wants.</summary>
    private sealed class SlowMount : ISnapshotMountRegistry
    {
        private readonly ConcurrentDictionary<string, string> _progress = new();
        private readonly ConcurrentDictionary<string, int> _percent = new();
        private TaskCompletionSource<MountResult>? _pending;
        private string? _drive;
        public int ExplorerOpens;
        public event Action<string>? Changed;
        public bool HasPendingOperations => _pending is not null;
        public void CancelPending() => _pending?.TrySetCanceled();
        public string? GetMountedDrive(string id) => _drive;
        public string? GetMountProgress(string id) => _progress.TryGetValue(id, out var text) ? text : null;
        public void OpenInExplorer(string id) => ExplorerOpens++;
        public int? GetMountPercent(string id) => _percent.TryGetValue(id, out var p) ? p : null;
        public void Report(string id, MountReading reading)
        {
            if (reading.Percent is { } p) _percent[id] = p; else _percent.TryRemove(id, out _);
            _progress[id] = MountedSnapshotRegistry.ReadingText(reading);
            Changed?.Invoke(id);
        }
        public void Finish(string id, MountResult result)
        {
            if (result.Succeeded) _drive = result.DriveLetter;
            _pending!.TrySetResult(result);
        }
        public async Task<MountResult> MountForAsync(Backup backup, CancellationToken ct = default)
        {
            _pending = new TaskCompletionSource<MountResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { return await _pending.Task; }
            finally { _pending = null; _progress.TryRemove(backup.Id, out _); _percent.TryRemove(backup.Id, out _); Changed?.Invoke(backup.Id); }
        }
        public Task UnmountAsync(string id, CancellationToken ct = default) { _drive = null; Changed?.Invoke(id); return Task.CompletedTask; }
        public Task DisposeAllAsync() => Task.CompletedTask;
    }
}
