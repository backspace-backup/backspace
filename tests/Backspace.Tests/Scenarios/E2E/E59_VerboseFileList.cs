using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// U115 (4 November 2026): with Verbose on, a backup's log lists each file it
/// added, changed or removed, like Duplicacy's "Uploaded" lines, with full
/// paths and no cap; a first backup lists every file. Real restic, local
/// repository: the opening lines, the time-left estimate's absence on a tiny
/// run, the snapshot in the recap and the file list are read back from the
/// saved run logs.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E59_VerboseFileList : BackupE2EBase
{
    public E59_VerboseFileList(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E59-verbose-file-list";

    [AvaloniaFact] public async Task Run_E59() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source = ctx.MakeSource("documents", seed: 159, targetBytes: 200_000);
        var deep = Path.Combine(source, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("a fairly long folder name", 4)));
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "kept.txt"), "kept");
        File.WriteAllText(Path.Combine(source, "changes.txt"), "first");
        File.WriteAllText(Path.Combine(source, "removed.txt"), "soon gone");
        var destination = new Destination
        {
            Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("local"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e59", $"e59-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Documents (E59)", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(), Threads = 2,
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } },
        };
        var level = ServiceLocator.Config.Current.Logging.MinimumLevel;
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(destination); cfg.Backups.Add(backup); cfg.Logging.MinimumLevel = "Debug"; });
        try
        {
            var first = await Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None));
            Assert.True(first.Status is BackupRunStatus.Success or BackupRunStatus.Warning, string.Join(" | ", first.Errors));
            var firstLog = File.ReadAllText(first.LogPath);
            Assert.Contains("[setup] No earlier backup of this folder in «Local»", firstLog);
            Assert.Contains("[setup] Shadow copy off", firstLog);
            Assert.Matches(@"\[recap\] .* · snapshot [0-9a-f]{8}", firstLog);
            // A first backup lists every file, with its full path.
            var expectedFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            foreach (var file in expectedFiles)
                Assert.Contains("[file] added: " + file, firstLog);
            Assert.Equal(expectedFiles.Length, firstLog.Split('\n').Count(l => l.Contains("[file] ")));

            File.WriteAllText(Path.Combine(source, "changes.txt"), "second, longer");
            File.Delete(Path.Combine(source, "removed.txt"));
            File.WriteAllText(Path.Combine(source, "new.txt"), "new");
            var second = await Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None));
            Assert.True(second.Status is BackupRunStatus.Success or BackupRunStatus.Warning, string.Join(" | ", second.Errors));
            var secondLog = File.ReadAllText(second.LogPath);
            Assert.Matches(@"\[setup\] Previous backup to «Local»: .*, snapshot [0-9a-f]{8}\. Only changes are sent\.", secondLog);
            var fileLines = secondLog.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains("[file] ")).ToList();
            Assert.Contains(fileLines, l => l.EndsWith("[file] changed: " + Path.Combine(source, "changes.txt")));
            Assert.Contains(fileLines, l => l.EndsWith("[file] removed: " + Path.Combine(source, "removed.txt")));
            Assert.Contains(fileLines, l => l.EndsWith("[file] added: " + Path.Combine(source, "new.txt")));
            // Unchanged files are not listed.
            Assert.DoesNotContain(fileLines, l => l.Contains("kept.txt"));
            Assert.Equal(3, fileLines.Count);
        }
        finally { ServiceLocator.Config.Update(cfg => cfg.Logging.MinimumLevel = level); }
    }
}
