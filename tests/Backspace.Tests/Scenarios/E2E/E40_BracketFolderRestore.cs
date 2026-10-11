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

/// <summary>Real-engine proof that a collapsed bracket folder keeps exact-file safety.</summary>
[Collection(ScenarioCollection.Name)]
public sealed class E40_BracketFolderRestore : BackupE2EBase
{
    public E40_BracketFolderRestore(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E40-bracket-folder-restore";
    [AvaloniaFact] public async Task Run_E40() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source = Path.Combine(ctx.SourcesDir, "source");
        var pickedDir = Path.Combine(source, "Photos [2026]");
        var sibling = Path.Combine(source, "Photos x2026x");
        Directory.CreateDirectory(pickedDir);
        Directory.CreateDirectory(sibling);
        for (var i = 0; i < 2397; i++)
            File.WriteAllText(Path.Combine(pickedDir, $"image [{i:D4}].txt"), $"Original photo contents {i}");
        File.WriteAllText(Path.Combine(sibling, "unselected.txt"), "Must never be restored");
        var dest = new Destination { Name = "Disposable encrypted repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e40", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Bracket folder safety", SourcePaths = { source }, UseVss = false,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(backup); });
        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        var run = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.Equal(BackupRunStatus.Success, run.Status);
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(backup, source, dest, "default", CancellationToken.None);
        var revision = revisions[0].Number;
        var files = await ServiceLocator.Revisions.ListFilesAsync(backup, source, dest, "default", revision, CancellationToken.None);
        var all = files.Select(f => f.Path).ToArray();
        var selected = all.Where(p => p.StartsWith("Photos [2026]/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2397, selected.Length);
        var patterns = RestoreSelectionMinimizer.Compute(all, selected);
        Assert.Equal("/Photos [2026]", Assert.Single(patterns));
        var target = ctx.MakeRestoreTarget("whole-folder");
        File.WriteAllText(Path.Combine(target, "existing.txt"), "Keep this original");
        var result = await ServiceLocator.Restore.RunAsync(new RestoreRequest {
            Backup = backup, SourcePath = source, Destination = dest, StorageName = "default", Revision = revision,
            Files = selected, IncludePatterns = patterns, TargetPath = target }, CancellationToken.None);
        Assert.True(result.TerminalStatus == RestoreTerminalStatus.Success, result.ErrorMessage ?? result.TerminalStatus.ToString());
        Assert.Equal(2397, result.RestoredCount);
        foreach (var relative in selected)
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, relative)), File.ReadAllBytes(Path.Combine(target, relative)));
        Assert.False(Directory.Exists(Path.Combine(target, "Photos x2026x")));
        Assert.Equal("Keep this original", File.ReadAllText(Path.Combine(target, "existing.txt")));
        Assert.Equal(2398, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length);
        ctx.Report.RecordNote("Whole folder with brackets",
            "2,397 selected files became one folder pattern. All 2,397 restored files match their original bytes. " +
            "The similarly named Photos x2026x folder was excluded; an existing destination file was unchanged.");

        var partial = selected.Take(2).ToArray();
        var partialPatterns = RestoreSelectionMinimizer.Compute(all, partial);
        Assert.Equal(2, partialPatterns.Count);
        var partialTarget = ctx.MakeRestoreTarget("partial-folder");
        var partialResult = await ServiceLocator.Restore.RunAsync(new RestoreRequest {
            Backup = backup, SourcePath = source, Destination = dest, StorageName = "default", Revision = revision,
            Files = partial, IncludePatterns = partialPatterns, TargetPath = partialTarget }, CancellationToken.None);
        Assert.Equal(RestoreTerminalStatus.Success, partialResult.TerminalStatus);
        Assert.Equal(2, Directory.GetFiles(partialTarget, "*", SearchOption.AllDirectories).Length);
        foreach (var relative in partial)
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, relative)), File.ReadAllBytes(Path.Combine(partialTarget, relative)));
        Assert.False(File.Exists(Path.Combine(partialTarget, selected[2])));
        ctx.Report.RecordNote("Partial selection stays partial",
            "Selecting only two files keeps two patterns. Exactly those two files restored byte-for-byte; the third selected-folder file is absent.");
        ctx.Snapshot(host, "after real folder and partial restores",
            "The encrypted backup and both restores completed. The report records byte comparisons and the files that must remain absent.");
    }
}
