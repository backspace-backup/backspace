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
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U178 (10 November 2026): in What this backup added, "Exclude this folder"
/// beside the opened folder's path. The confirmation shows the exact rule,
/// what the folder holds now and that earlier backups keep it; confirming
/// adds the rule to the backup's own rules in the saved settings, and Undo
/// takes exactly that rule out. A folder an existing rule already leaves out,
/// and one an exception keeps in, are shown as such.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S95_ExcludeFolderFromChanges : ScenarioBase
{
    protected override string ScenarioName => "S95-exclude-folder-from-changes";

    [AvaloniaFact] public async Task Run_S95() => await Run();

    private static void Write(string root, string relative, int bytes, int seed)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    private static void Click(Window host, Control target)
    {
        Assert.True(target.IsEffectivelyVisible && target.IsEffectivelyEnabled, $"{target.Name} can't be clicked");
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), host)!.Value;
        host.MouseDown(at, MouseButton.Left); host.MouseUp(at, MouseButton.Left);
    }

    private static string SavedFilters(Backup backup) => ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id).FiltersText;

    // The settings file itself, which the background service reads.
    private static string FiltersOnDisk(Backup backup)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AppPaths.ConfigFile));
        return doc.RootElement.GetProperty("backups").EnumerateArray()
            .Single(b => b.GetProperty("id").GetString() == backup.Id).GetProperty("filtersText").GetString()!;
    }

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var source = ctx.MakeSource("profile", targetBytes: 4_000);
        Write(source, "Documents/notes.txt", 20_000, 1);
        Write(source, "Photos/2025/beach.jpg", 400_000, 2);
        var destination = new Destination { Name = "Backup drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("drive") };
        var recommended = RecommendedFilters.RenderBlock(false, false, Array.Empty<RecommendedFilters.CloudSyncFolder>());
        var original = RecommendedFilters.MergeIntoFilters(RecommendedFilters.CustomBlockHeader + "\n*.bak\n" + RecommendedFilters.CustomBlockFooter, recommended);
        var backup = new Backup { Name = "local_c", SourcePaths = { source }, FiltersText = original };
        backup.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(destination); cfg.Backups.Add(backup); });
        var engine = ServiceLocator.Engine;
        await engine.BackupPruneCheckAsync(backup, source, destination, "default", CancellationToken.None);

        // Between the two backups: a project's build output, new photos, a video.
        for (var i = 0; i < 12; i++) Write(source, $"Projects/app [beta]/bin/Debug/part{i}.dll", 60_000 + i * 5_000, 30 + i);
        for (var i = 0; i < 4; i++) Write(source, $"Photos/2026/IMG_{i:000}.jpg", 300_000 + i * 40_000, 10 + i);
        Write(source, "Videos/holiday.mp4", 900_000, 20);
        await engine.BackupPruneCheckAsync(backup, source, destination, "default", CancellationToken.None);
        var snapshots = await engine.ListSnapshotsAsync(backup, source, destination, "default", CancellationToken.None);
        var latest = snapshots[0].SnapshotId!;
        var log = Path.Combine(ctx.RootDir, "cell.log");
        File.WriteAllText(log, "[recap] Backup complete: 19 files checked · 17 new, 0 changed, 2 unchanged · 2.6 MB added to the storage · took 3s · snapshot " + latest + "\n");
        var run = new RunRecord
        {
            BackupId = backup.Id, BackupName = backup.Name, Kind = RunKind.Backup, Status = BackupRunStatus.Success,
            StartedUtc = DateTime.UtcNow.AddMinutes(-2), EndedUtc = DateTime.UtcNow, Summary = "Completed in 3s",
            FilesNew = 17, BytesUploaded = 2_600_000,
            Cells = { new CellOutcome { SourcePath = source, DestinationName = destination.Name, Status = BackupRunStatus.Success, LogPath = log } },
        };

        // Opened from the backup's activity, as the person does.
        var history = new BackupHistoryWindow { DataContext = new BackupHistoryViewModel(backup, new[] { run }, checks: false), Width = 800, Height = 600 };
        ctx.Driver.Show(history);
        await ctx.Driver.PumpAsync(200);
        history.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Files added").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var window = history.OwnedWindows.OfType<RunChangesWindow>().Single();
        var vm = (RunChangesViewModel)window.DataContext!;
        window.Width = 1080; window.Height = 720;
        Assert.True(ctx.Driver.WaitFor(() => !vm.IsLoading, 20_000), "The file lists were never read: " + vm.Status);
        await ctx.Driver.PumpAsync(300);
        var exclude = (Button)window.FindDescendantByName("ExcludeFolder")!;

        // At the top of the source: nothing to exclude (that is the source itself).
        Assert.Equal("profile", vm.Location);
        Assert.False(exclude.IsEffectivelyVisible);

        // The build output: opened through app [beta] and bin to Debug.
        vm.Open(vm.Current!.Children.Single(c => c.Name == "Projects"));
        await ctx.Driver.PumpAsync(200);
        Assert.Equal(Path.Combine("profile", "Projects", "app [beta]", "bin", "Debug"), vm.Location);
        var debug = Path.Combine(source, "Projects", "app [beta]", "bin", "Debug");
        var rule = (OperatingSystem.IsWindows() ? "?:" + source[2..].Replace('\\', '/') : source).Replace("[", "[[]").Replace("$", "$$") + "/Projects/app [[]beta]/bin/Debug/";
        Assert.Equal(rule, vm.ExcludeRule);
        Assert.True(exclude.IsEffectivelyVisible);
        // The button sits right after the path, not at the far edge.
        var location = (TextBlock)window.FindDescendantByName("MapLocation")!;
        var gap = exclude.TranslatePoint(new Point(0, 0), window)!.Value.X - location.TranslatePoint(new Point(location.Bounds.Width, 0), window)!.Value.X;
        Assert.InRange(gap, 0, 40);
        ctx.Snapshot(window, "exclude button beside the path",
            "Projects opened down to profile\\Projects\\app [beta]\\bin\\Debug: 'Exclude this folder' right after the path, the build files in the map.");

        // At the smallest width the path is shortened, never the button.
        window.Width = 640;
        await ctx.Driver.PumpAsync(200);
        var summary = (Control)window.FindDescendantByName("ChangeSummary")!;
        var buttonRight = exclude.TranslatePoint(new Point(exclude.Bounds.Width, 0), window)!.Value.X;
        Assert.True(exclude.Bounds.Width >= exclude.DesiredSize.Width - 1, "The button is squeezed");
        Assert.True(buttonRight <= summary.TranslatePoint(new Point(0, 0), window)!.Value.X, "The button runs into the summary");
        ctx.Snapshot(window, "narrow window", "At 640 pixels wide: the path ends in '…', 'Exclude this folder' still whole beside it, the summary on the right.");
        window.Width = 1080;
        await ctx.Driver.PumpAsync(200);

        // Exclude: the confirmation, with the rule and what Debug holds now.
        Click(window, exclude);
        Assert.True(ctx.Driver.WaitFor(() => vm.IsConfirmingExclude && !vm.ExcludeOnDisk.StartsWith("Counting", StringComparison.Ordinal), 10_000));
        await ctx.Driver.PumpAsync(200);
        Assert.Equal(original, SavedFilters(backup)); // nothing saved yet
        Assert.Equal("Leave Debug out of the next backups?", vm.ExcludeQuestion);
        Assert.Equal("It holds about 12 files, " + BackupLiveProgress.Bytes(Enumerable.Range(0, 12).Sum(i => 60_000L + i * 5_000)) + " on disk now.", vm.ExcludeOnDisk);
        Assert.False(vm.HasExcludeWarning);
        var confirmation = (Control)window.FindDescendantByName("ExcludeConfirmation")!;
        Assert.True(confirmation.IsEffectivelyVisible);
        Assert.Equal(rule, ((SelectableTextBlock)window.FindDescendantByName("ExcludeRuleText")!).Text);
        ctx.Snapshot(window, "confirmation with the rule",
            "Above the map: 'Leave Debug out of the next backups?', the rule added to local_c's own rules (?:/…/Projects/app [[]beta]/bin/Debug/, the bracket written literally), 'about 12 files, 1.0 MB on disk now', earlier backups keep it; Cancel and 'Exclude from next backups'.");

        // Cancel saves nothing; Exclude again and confirm.
        ctx.Driver.ClickButton(confirmation, "Cancel");
        await ctx.Driver.PumpAsync(100);
        Assert.False(vm.IsConfirmingExclude);
        Assert.Equal(original, SavedFilters(backup));
        Click(window, exclude);
        Assert.True(ctx.Driver.WaitFor(() => vm.IsConfirmingExclude && !vm.ExcludeOnDisk.StartsWith("Counting", StringComparison.Ordinal), 10_000));
        await ctx.Driver.PumpAsync(100);
        Click(window, (Control)window.FindDescendantByName("ConfirmExclude")!);
        await ctx.Driver.PumpAsync(200);

        // Saved in the custom block, after *.bak and before its end; the rest unchanged.
        var saved = SavedFilters(backup);
        Assert.Equal(original.Length + rule.Length + 1 + (original.Contains("*.bak\r\n", StringComparison.Ordinal) ? 1 : 0), saved.Length);
        var lines = saved.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(lines.IndexOf("*.bak") + 1, lines.IndexOf(rule));
        Assert.Equal(lines.IndexOf(rule) + 1, lines.IndexOf(RecommendedFilters.CustomBlockFooter));
        Assert.Equal(1, lines.Count(l => l == rule));
        // The next backup leaves Debug out; its parent and the other folders stay.
        Assert.Equal(rule, FolderExclusion.ExcludedBy(saved, debug));
        Assert.Null(FolderExclusion.ExcludedBy(saved, Path.GetDirectoryName(debug)!));
        Assert.Null(FolderExclusion.ExcludedBy(saved, Path.Combine(source, "Photos")));
        // The settings file on disk has it too (the background service reads that file).
        var onDisk = FiltersOnDisk(backup);
        Assert.Equal(saved, onDisk);
        Assert.True(vm.IsExcluded);
        Assert.False(exclude.IsEffectivelyVisible);
        Assert.True(window.FindDescendantByName("ExcludedNote")!.IsEffectivelyVisible);
        Assert.True(window.FindDescendantByName("UndoExclude")!.IsEffectivelyVisible);
        ctx.Snapshot(window, "row after excluding",
            "After 'Exclude from next backups': beside the path, 'Excluded from the next backup' in green and Undo; the confirmation is gone and the map is unchanged (earlier backups keep Debug).");

        // Up and back: the state is kept. Up at the source hides it.
        vm.UpCommand.Execute(null);
        await ctx.Driver.PumpAsync(100);
        Assert.False(vm.IsExcluded);
        Assert.False(exclude.IsEffectivelyVisible);
        vm.Open(vm.Current!.Children.Single(c => c.Name == "Projects"));
        await ctx.Driver.PumpAsync(100);
        Assert.True(vm.IsExcluded);

        // Undo takes out exactly that rule.
        Click(window, (Control)window.FindDescendantByName("UndoExclude")!);
        await ctx.Driver.PumpAsync(200);
        Assert.Equal(original, SavedFilters(backup));
        Assert.Equal(original, FiltersOnDisk(backup));
        Assert.False(vm.IsExcluded);
        Assert.True(exclude.IsEffectivelyVisible);
        ctx.Snapshot(window, "after undo", "After Undo: 'Exclude this folder' is back beside the path; the saved rules are as before.");

        // A folder an existing rule already leaves out: Videos, by a rule added in the editor.
        var videosRule = FolderExclusion.Rule(Path.Combine(source, "Videos"))!;
        ServiceLocator.Config.Update(cfg => cfg.Backups.Single(b => b.Id == backup.Id).FiltersText = FolderExclusion.Add(original, videosRule));
        vm.UpCommand.Execute(null);
        vm.Open(vm.Current!.Children.Single(c => c.Name == "Videos"));
        await ctx.Driver.PumpAsync(200);
        Assert.True(vm.IsAlreadyExcluded);
        Assert.Equal(videosRule, vm.AlreadyExcludedBy);
        Assert.False(exclude.IsEffectivelyVisible);
        Assert.True(window.FindDescendantByName("AlreadyExcludedNote")!.IsEffectivelyVisible);
        ctx.Snapshot(window, "already excluded",
            "Videos opened while a rule already leaves it out: 'Already excluded from the next backup' in grey instead of the button.");

        // An exception further down that keeps Photos in: said, and Exclude can't be confirmed.
        var photos = Path.Combine(source, "Photos");
        var keeper = "!" + FolderExclusion.Rule(photos)!;
        ServiceLocator.Config.Update(cfg => cfg.Backups.Single(b => b.Id == backup.Id).FiltersText = original + "\n" + keeper + "\n");
        var withException = SavedFilters(backup);
        vm.UpCommand.Execute(null);
        vm.Open(vm.Current!.Children.Single(c => c.Name == "Photos"));
        await ctx.Driver.PumpAsync(200);
        Assert.Equal(Path.Combine("profile", "Photos", "2026"), vm.Location);
        Click(window, exclude);
        Assert.True(ctx.Driver.WaitFor(() => vm.IsConfirmingExclude && !vm.ExcludeOnDisk.StartsWith("Counting", StringComparison.Ordinal), 10_000));
        await ctx.Driver.PumpAsync(200);
        Assert.Contains(keeper, vm.ExcludeWarning);
        Assert.False(((Control)window.FindDescendantByName("ConfirmExclude")!).IsEffectivelyEnabled);
        ctx.Snapshot(window, "an exception keeps it",
            "Photos\\2026 with an exception further down (!?:/…/Photos/): the confirmation says the rule would change nothing and names the exception; 'Exclude from next backups' is disabled.");
        vm.ConfirmExcludeCommand.Execute(null);
        Assert.Equal(withException, SavedFilters(backup));

        window.Close();
        history.Close();
    }
}
