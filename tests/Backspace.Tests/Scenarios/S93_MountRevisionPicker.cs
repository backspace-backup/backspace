using System;
using System.Collections.Generic;
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
using Backspace.Services.WebDav;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U162 (owner, 7 November 2026): "what do you think about asking user first
/// which revision he wants to mount (while allowing to mount all
/// revisions/multi select them if they really want to)?" Clicking Mount
/// opens the picker with the latest ticked; Enter mounts the selection, even
/// from a focused tick box; the mount receives exactly the ticked revisions
/// and the card says which it shows. Cancel in the picker mounts nothing.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S93_MountRevisionPicker : ScenarioBase
{
    protected override string ScenarioName => "S93-mount-revision-picker";
    [AvaloniaFact] public async Task Run_S93() => await Run();

    private static readonly DateTime Now = DateTime.Now;
    private static RevisionSummary Rev(int number, DateTime local, int files, long bytes)
        => new(number, local.ToUniversalTime(), files, bytes) { SnapshotId = "snapshot-" + number };

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var source = @"C:\Users\me\Documents";
        var revisions = new[]
        {
            Rev(6, Now.Date.AddHours(Now.Hour >= 1 ? Now.Hour - 1 : 0).AddMinutes(15), 612_345, 204_000_000_000),
            Rev(5, Now.Date.AddDays(-1).AddHours(14).AddMinutes(30), 612_101, 203_800_000_000),
            Rev(4, Now.Date.AddDays(-2).AddHours(14).AddMinutes(30), 611_870, 203_500_000_000),
            Rev(3, Now.Date.AddDays(-6).AddHours(9).AddMinutes(5), 609_002, 201_900_000_000),
            Rev(2, Now.Date.AddDays(-13).AddHours(9).AddMinutes(5), 0, 0),
            Rev(1, Now.Date.AddDays(-40).AddHours(22).AddMinutes(47), 0, 0),
        };
        var registry = new RecordingMount();
        var backup = new Backup { Name = "local_c", SourcePaths = { source }, LastRunStatus = BackupRunStatus.Success,
            LastRunEndUtc = DateTime.UtcNow.AddHours(-1), LastSuccessUtc = DateTime.UtcNow.AddHours(-1) };
        using var card = new BackupCardViewModel(backup, true, false, registry);
        var host = ctx.Driver.Show(new Window { Width = 1000, Height = 640, Content = new BackupCard { DataContext = card } });

        MountRevisionPickerWindow? picker = null;
        var listings = 0;
        card.ChooseMountRevisions = () =>
        {
            picker = new MountRevisionPickerWindow(new MountRevisionPickerViewModel(backup, (_, _) =>
            {
                listings++;
                return Task.FromResult<IReadOnlyList<RevisionSummary>>(revisions);
            }));
            return picker.ShowDialog<MountSelection?>(host);
        };

        // ---- Mount opens the picker; Cancel mounts nothing ----
        Click(host, "Mount"); ctx.Driver.Pump();
        await WaitAsync(ctx, () => picker is { IsVisible: true } && Vm(picker).ShowList);
        Assert.Equal(1, listings);
        Assert.Equal(0, registry.Mounts);
        Assert.False(card.IsMounting); // nothing is read until the person chooses
        Click(picker!, "Cancel"); ctx.Driver.Pump();
        await WaitAsync(ctx, () => !picker!.IsVisible);
        Assert.Equal(0, registry.Mounts);
        Assert.False(card.ShowMountLine);

        // ---- again: the latest is ticked ----
        Click(host, "Mount"); ctx.Driver.Pump();
        await WaitAsync(ctx, () => picker is { IsVisible: true } && Vm(picker).ShowList);
        var vm = Vm(picker!);
        Assert.Equal(new[] { true, false, false, false, false, false }, vm.Groups[0].Rows.Select(r => r.IsSelected));
        Assert.True(Visible(picker!, "Latest"));
        Assert.True(Visible(picker!, "612,345 files · 190 GB"));
        Assert.Equal("Mount", Button(picker!, "Mount").Content);
        Settle(ctx, picker!);
        ctx.Snapshot(picker!, "picker-latest-ticked",
            "Mount asks first: the backup's revisions newest first with their date, time, files and size; the latest is ticked and marked Latest. One revision: Mount.");

        // ---- tick an older one too: the label and the hint say what that costs ----
        var boxes = picker!.GetVisualDescendants().OfType<CheckBox>().Where(b => b.IsEffectivelyVisible).ToList();
        Assert.Equal(6, boxes.Count);
        ClickControl(picker!, boxes[3]); ctx.Driver.Pump();
        Assert.True(vm.Groups[0].Rows[3].IsSelected);
        Assert.True(Visible(picker!, "Mount 2 revisions"));
        Assert.Contains("each of the 2 revisions", vm.Hint);
        Settle(ctx, picker!);
        ctx.Snapshot(picker!, "picker-two-ticked",
            "The latest and the one from six days ago ticked: the button reads Mount 2 revisions and the note says each is read before the drive opens, one folder per revision.");

        // ---- Enter mounts the selection, even with a tick box focused ----
        boxes[3].Focus(); ctx.Driver.Pump();
        picker!.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); // closes the picker
        ctx.Driver.Pump();
        Assert.False(picker!.IsVisible);
        Assert.True(vm.Groups[0].Rows[3].IsSelected); // Enter did not toggle the focused box
        await WaitAsync(ctx, () => registry.Mounts == 1);
        Assert.Equal(new[] { "snapshot-6", "snapshot-3" }, registry.Last!.Sources.Single().Revisions.Select(r => r.SnapshotId));
        Assert.Equal(source, registry.Last.Sources[0].Path);
        Assert.True(card.IsMounting);

        registry.Report(backup.Id, MountedSnapshotRegistry.ReadingText(new MountReading(212_000, 2, 2, MountSelection.When(revisions[3].CreatedUtc))));
        ctx.Driver.Pump();
        Assert.True(Visible(host, $"Mounting · Reading the backup's folders, revision 2 of 2 ({MountSelection.When(revisions[3].CreatedUtc)}): 212,000 so far…"));
        ctx.Snapshot(host, "card-reading-second-revision",
            "While the chosen revisions are read, the card says which one: revision 2 of 2 with its date, and how many files and folders so far.");

        registry.Finish(backup.Id, MountResult.Ok("Z:")); ctx.Driver.Pump();
        await WaitAsync(ctx, () => card.MountedDrive == "Z:" && !card.IsMounting);
        Assert.True(Visible(host, "Mounted as Z: (2 revisions), read-only. Backspace unmounts it when you quit."));
        Settle(ctx, host);
        ctx.Snapshot(host, "card-mounted-two-revisions",
            "Mounted: the card names drive Z: and what it shows (2 revisions), with Open in Explorer; the button offers Unmount.");

        // ---- one older revision: the card names its date ----
        Click(host, "Unmount"); ctx.Driver.Pump();
        await WaitAsync(ctx, () => card.MountedDrive is null);
        Click(host, "Mount"); ctx.Driver.Pump();
        await WaitAsync(ctx, () => picker is { IsVisible: true } && Vm(picker).ShowList);
        vm = Vm(picker!);
        vm.Groups[0].Rows[0].IsSelected = false;
        vm.Groups[0].Rows[1].IsSelected = true;
        ctx.Driver.Pump();
        Click(picker!, "Mount");
        await WaitAsync(ctx, () => registry.Mounts == 2);
        registry.Finish(backup.Id, MountResult.Ok("Y:"));
        await WaitAsync(ctx, () => card.MountedDrive == "Y:");
        var when = MountSelection.When(revisions[1].CreatedUtc);
        Assert.True(Visible(host, $"Mounted as Y: ({when}), read-only. Backspace unmounts it when you quit."));
        Settle(ctx, host);
        ctx.Snapshot(host, "card-mounted-one-older-revision",
            $"Yesterday's revision mounted on its own: the card says \"Mounted as Y: ({when})\".");
        host.Close();
    }

    private static MountRevisionPickerViewModel Vm(Window window) => (MountRevisionPickerViewModel)window.DataContext!;

    private static void Settle(ScenarioContext ctx, Window window)
    {
        ctx.Driver.Pump(); Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
        ctx.Driver.Pump(); Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
    }

    private static async Task WaitAsync(ScenarioContext ctx, Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) { await Task.Delay(10); ctx.Driver.Pump(); }
        Assert.True(condition());
    }

    private static bool Visible(Window window, string text)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text)
            || window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.Content as string == text);
    }

    private static Button Button(Window window, string label)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<Button>().Where(b => b is not CheckBox).First(b => b.IsEffectivelyVisible &&
            (b.Content?.ToString() == label || b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label)));
    }

    private static void Click(Window window, string label) => ClickControl(window, Button(window, label));

    private static void ClickControl(Window window, Control control)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }

    /// <summary>Records what was asked to be mounted; finishes when told.</summary>
    private sealed class RecordingMount : ISnapshotMountRegistry
    {
        private readonly Dictionary<string, string> _progress = new();
        private TaskCompletionSource<MountResult>? _pending;
        private string? _drive;
        private string? _description;
        public int Mounts;
        public MountSelection? Last;
        public event Action<string>? Changed;
        public bool HasPendingOperations => _pending is not null;
        public void CancelPending() => _pending?.TrySetCanceled();
        public string? GetMountedDrive(string id) => _drive;
        public string? GetMountedDescription(string id) => _drive is null ? null : _description;
        public string? GetMountProgress(string id) => _progress.TryGetValue(id, out var text) ? text : null;
        public void Report(string id, string text) { _progress[id] = text; Changed?.Invoke(id); }
        public void Finish(string id, MountResult result)
        {
            if (result.Succeeded) { _drive = result.DriveLetter; _description = Last!.Describe(); }
            _pending!.TrySetResult(result);
        }
        public Task<MountResult> MountForAsync(Backup backup, CancellationToken ct = default)
            => throw new InvalidOperationException("The card must pass the picker's selection.");
        public async Task<MountResult> MountForAsync(Backup backup, MountSelection selection, CancellationToken ct = default)
        {
            Mounts++;
            Last = selection;
            _pending = new TaskCompletionSource<MountResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { return await _pending.Task; }
            finally { _pending = null; _progress.Remove(backup.Id); Changed?.Invoke(backup.Id); }
        }
        public Task UnmountAsync(string id, CancellationToken ct = default) { _drive = null; Changed?.Invoke(id); return Task.CompletedTask; }
        public Task DisposeAllAsync() => Task.CompletedTask;
    }
}
