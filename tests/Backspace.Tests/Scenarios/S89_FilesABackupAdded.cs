using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U143 (5 November 2026): in a backup's activity, "Files added" shows the files
/// that backup added or changed, as a map of their folders sized by bytes
/// (like WinDirStat) and a list of the largest, from its snapshot and the one
/// before. No Verbose needed.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S89_FilesABackupAdded : ScenarioBase
{
    protected override string ScenarioName => "S89-files-a-backup-added";

    [AvaloniaFact] public async Task Run_S89() => await Run();

    private static void Write(string root, string relative, int bytes, int seed)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var source = ctx.MakeSource("profile", targetBytes: 4_000);
        Write(source, "Documents/notes.txt", 20_000, 1);
        Write(source, "Photos/2025/beach.jpg", 400_000, 2);
        Write(source, "Projects/app/README.md", 3_000, 3);
        var destination = new Destination { Name = "Google Drive - backspace", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("drive") };
        var backup = new Backup { Name = "local_c", SourcePaths = { source } };
        backup.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(destination); cfg.Backups.Add(backup); });
        var engine = ServiceLocator.Engine;
        await engine.BackupPruneCheckAsync(backup, source, destination, "default", CancellationToken.None);

        // Between the two backups: new photos, a video, a project's build
        // output and a changed note. The README is unchanged.
        for (var i = 0; i < 6; i++) Write(source, $"Photos/2026/IMG_{i:000}.jpg", 300_000 + i * 40_000, 10 + i);
        Write(source, "Videos/holiday.mp4", 2_500_000, 20);
        for (var i = 0; i < 12; i++) Write(source, $"Projects/app/bin/Debug/part{i}.dll", 60_000 + i * 5_000, 30 + i);
        Write(source, "Documents/notes.txt", 26_000, 4);
        File.SetLastWriteTimeUtc(Path.Combine(source, "Documents/notes.txt"), DateTime.UtcNow.AddMinutes(1));
        await engine.BackupPruneCheckAsync(backup, source, destination, "default", CancellationToken.None);
        var snapshots = await engine.ListSnapshotsAsync(backup, source, destination, "default", CancellationToken.None);
        var latest = snapshots[0].SnapshotId!;

        // The run as the history keeps it, with the recap its log ends with.
        var log = Path.Combine(ctx.RootDir, "cell.log");
        File.WriteAllText(log, "[progress] 100.0% · Backing up\n[recap] Backup complete: 23 files (6.0 MB) checked · 19 new, 1 changed, 3 unchanged · 5.9 MB added to the storage · took 4s · snapshot " + latest + "\n");
        var run = new RunRecord
        {
            BackupId = backup.Id, BackupName = backup.Name, Kind = RunKind.Backup, Status = BackupRunStatus.Success,
            StartedUtc = DateTime.UtcNow.AddMinutes(-2), EndedUtc = DateTime.UtcNow, Summary = "Completed in 4s",
            FilesNew = 19, FilesModified = 1, BytesUploaded = 5_900_000,
            Cells = { new CellOutcome { SourcePath = source, DestinationName = destination.Name, Status = BackupRunStatus.Success, LogPath = log } },
        };
        Assert.Equal(latest, RunChanges.SnapshotOf(run, run.Cells[0]));

        // Clicked from the backup's activity.
        var history = new BackupHistoryWindow { DataContext = new BackupHistoryViewModel(backup, new[] { run }, checks: false), Width = 800, Height = 600 };
        ctx.Driver.Show(history);
        await ctx.Driver.PumpAsync(200);
        var button = history.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Files added");
        Assert.True(button.IsEffectivelyVisible);
        ctx.Snapshot(history, "activity row", "A backup in the activity: 'Files added' beside Open log.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var window = history.OwnedWindows.OfType<RunChangesWindow>().Single();
        var vm = (RunChangesViewModel)window.DataContext!;
        window.Width = 1080; window.Height = 720;
        Assert.True(ctx.Driver.WaitFor(() => !vm.IsLoading, 20_000), "The file lists were never read: " + vm.Status);
        await ctx.Driver.PumpAsync(300);

        Assert.Equal("19 new · 1 changed · 5.7 MB of files", vm.Summary);
        Assert.Equal("holiday.mp4", vm.LargestFiles[0].Name);
        Assert.Equal("new", vm.LargestFiles[0].Change);
        Assert.Contains(vm.LargestFiles, f => f.Name == "notes.txt" && f.Change == "changed");
        // Unchanged files are not listed.
        Assert.DoesNotContain(vm.LargestFiles, f => f.Name is "README.md" or "beach.jpg");
        // The map opens where the changes branch out: the source's folders.
        Assert.Equal(new[] { "Documents", "Photos", "Projects", "Videos" }, vm.Current!.Children.Select(c => c.Name).OrderBy(n => n));
        ctx.Snapshot(window, "map of the changes",
            "What this backup added: the source's folders as boxes sized by the bytes added (Videos 2.4 MB, Photos 2.3 MB, Projects 1.0 MB, Documents 25 KB), Photos showing its 2026 folder inside; the largest files on the right, holiday.mp4 first; '19 new · 1 changed · 5.7 MB of files'.");

        // Clicking a folder opens it; Up goes back.
        vm.Open(vm.Current.Children.Single(c => c.Name == "Projects"));
        await ctx.Driver.PumpAsync(200);
        Assert.Equal(Path.Combine("profile", "Projects", "app", "bin", "Debug"), vm.Location); // through the single-folder chain
        Assert.True(vm.CanGoUp);
        vm.Hover(vm.Current!.Children.First());
        await ctx.Driver.PumpAsync(100);
        ctx.Snapshot(window, "a folder opened",
            "Projects opened: through app, bin and Debug (one folder each) straight to the 12 build files; the line below names the box under the pointer with its size and file count.");
        vm.UpCommand.Execute(null);
        Assert.False(vm.CanGoUp);
        window.Close();
        history.Close();
    }
}
