using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>U162: Mount asks which revisions first. The latest of each
/// source is ticked; more, or all, can be ticked; the choice is all the
/// mount reads.</summary>
public class MountRevisionPickerViewModelTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 16, 0, 0, DateTimeKind.Local);

    private static RevisionSummary Rev(int number, DateTime local, int files = 0, long bytes = 0)
        => new(number, local.ToUniversalTime(), files, bytes) { SnapshotId = "snap" + number };

    private static readonly IReadOnlyList<RevisionSummary> Documents = new[]
    {
        Rev(3, Now.AddHours(-1.5), 612_345, 204_000_000_000),
        Rev(1, new DateTime(2025, 12, 30, 8, 5, 0, DateTimeKind.Local)),
        Rev(2, Now.AddDays(-1).Date.AddHours(9).AddMinutes(12)),
    };

    private static MountRevisionPickerViewModel Picker(Backup backup, Dictionary<string, IReadOnlyList<RevisionSummary>> revisions)
        => new(backup, (path, _) => Task.FromResult(revisions[path]), () => Now);

    private static Backup OneSource() => new() { Name = "local_c", SourcePaths = { @"C:\Users\me\Documents" } };

    [Fact]
    public async Task Opens_WithTheLatestTicked_NewestFirst_AndPlainLabels()
    {
        var vm = Picker(OneSource(), new() { [@"C:\Users\me\Documents"] = Documents });
        Assert.True(vm.IsLoading);
        Assert.False(vm.CanMount);
        await vm.LoadAsync();

        Assert.False(vm.IsLoading);
        var rows = Assert.Single(vm.Groups).Rows;
        Assert.False(vm.Groups[0].ShowHeading);
        Assert.Equal(new[] { 3, 2, 1 }, rows.Select(r => r.Revision.Number));
        Assert.Equal(new[] { true, false, false }, rows.Select(r => r.IsSelected));
        Assert.Equal(new[] { true, false, false }, rows.Select(r => r.IsLatest));
        Assert.Equal("Today, 14:30", rows[0].When);
        Assert.Equal("Yesterday, 09:12", rows[1].When);
        Assert.Equal("Tue 30 Dec 2025, 08:05", rows[2].When);
        Assert.Equal("612,345 files · 190 GB", rows[0].Details);
        Assert.False(rows[1].HasDetails);
        Assert.True(vm.CanMount);
        Assert.Equal("Mount", vm.MountLabel);
        Assert.Equal("Select all 3", vm.SelectAllLabel);
        Assert.StartsWith("Before the drive opens, Backspace loads this revision's list of files", vm.Hint);

        var selection = vm.ToSelection()!;
        var source = Assert.Single(selection.Sources);
        Assert.Equal(@"C:\Users\me\Documents", source.Path);
        Assert.Equal("snap3", Assert.Single(source.Revisions).SnapshotId);
    }

    [Fact]
    public async Task TickingAnOlderOne_OrAll_MountsExactlyThose()
    {
        var vm = Picker(OneSource(), new() { [@"C:\Users\me\Documents"] = Documents });
        await vm.LoadAsync();
        var rows = vm.Groups[0].Rows;

        // Only an older revision.
        rows[0].IsSelected = false;
        rows[1].IsSelected = true;
        Assert.Equal("Mount", vm.MountLabel);
        Assert.Equal(new[] { "snap2" }, vm.ToSelection()!.Sources[0].Revisions.Select(r => r.SnapshotId));

        // Two: the label and the hint say what that costs.
        rows[2].IsSelected = true;
        Assert.Equal("Mount 2 revisions", vm.MountLabel);
        Assert.Contains("each of the 2 revisions", vm.Hint);
        Assert.Equal(new[] { "snap2", "snap1" }, vm.ToSelection()!.Sources[0].Revisions.Select(r => r.SnapshotId));

        vm.ToggleAllCommand.Execute(null);
        Assert.True(vm.AllSelected);
        Assert.Equal("Mount 3 revisions", vm.MountLabel);
        Assert.Equal("Latest only", vm.SelectAllLabel);
        Assert.Equal(3, vm.ToSelection()!.RevisionCount);

        vm.ToggleAllCommand.Execute(null);
        Assert.Equal(new[] { true, false, false }, rows.Select(r => r.IsSelected));

        // Nothing ticked: nothing to mount.
        rows[0].IsSelected = false;
        Assert.False(vm.CanMount);
        Assert.Null(vm.ToSelection());
        Assert.Equal("Tick at least one revision to mount.", vm.Hint);
    }

    [Fact]
    public async Task SeveralSources_AreGroupedWithHeadings_TheLatestOfEachTicked()
    {
        var backup = new Backup { Name = "laptop", SourcePaths = { @"C:\Users\me\Documents", @"D:\Photos", @"E:\Empty" } };
        var vm = Picker(backup, new()
        {
            [@"C:\Users\me\Documents"] = Documents,
            [@"D:\Photos"] = new[] { Rev(7, Now.AddHours(-2)) },
            [@"E:\Empty"] = Array.Empty<RevisionSummary>(),
        });
        await vm.LoadAsync();
        Assert.Equal(new[] { "Documents", "Photos" }, vm.Groups.Select(g => g.Folder));
        Assert.All(vm.Groups, g => Assert.True(g.ShowHeading));
        Assert.Equal("Mount", vm.MountLabel); // one per source
        Assert.Equal(2, vm.ToSelection()!.RevisionCount);
        Assert.False(vm.AllSelected);
        Assert.True(vm.ShowSelectAll);

        vm.Groups[0].Rows[1].IsSelected = true;
        Assert.Equal("Mount 3 revisions", vm.MountLabel);
        var selection = vm.ToSelection()!;
        Assert.Equal(new[] { 2, 1 }, selection.Sources.Select(s => s.Revisions.Count));
    }

    [Fact]
    public async Task NoSnapshots_OrAFailedListing_SayWhy_AndCannotMount()
    {
        var none = Picker(OneSource(), new() { [@"C:\Users\me\Documents"] = Array.Empty<RevisionSummary>() });
        await none.LoadAsync();
        Assert.Equal("No snapshots yet. Run a backup first, then come back to mount it.", none.Error);
        Assert.False(none.CanMount);
        Assert.False(none.ShowList);

        var failing = new MountRevisionPickerViewModel(OneSource(),
            (_, _) => Task.FromException<IReadOnlyList<RevisionSummary>>(new InvalidOperationException("The cloud drive is offline.")), () => Now);
        await failing.LoadAsync();
        Assert.StartsWith("Couldn't read this backup's revisions.", failing.Error);
        Assert.EndsWith("The cloud drive is offline.", failing.Error);
        Assert.False(failing.CanMount);
        Assert.Null(failing.ToSelection());
    }

    [Fact]
    public async Task OneRevisionOnly_HidesSelectAll()
    {
        var vm = Picker(OneSource(), new() { [@"C:\Users\me\Documents"] = new[] { Rev(1, Now.AddHours(-3)) } });
        await vm.LoadAsync();
        Assert.False(vm.ShowSelectAll);
        Assert.True(vm.CanMount);
    }
}
