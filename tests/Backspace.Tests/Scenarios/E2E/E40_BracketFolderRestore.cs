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

/// <summary>Real-engine proof that bracket names keep exact-file and folder safety.</summary>
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
        var exactDir = Path.Combine(source, "Exact [names]");
        Directory.CreateDirectory(exactDir);
        var exactNames = new[]
        {
            (Picked: "statement [2026].txt", Deceptive: "statement x2026x.txt"),
            (Picked: "report [draft] [2026].txt", Deceptive: "report xdraftx x2026x.txt"),
            (Picked: "open [draft.txt", Deceptive: "open xdraft.txt"),
        };
        foreach (var names in exactNames)
        {
            // Each deceptive name matches the '?' substitution used by the
            // bundled engine. Its different bytes make over-restoring visible.
            Assert.Equal(names.Picked.Length, names.Deceptive.Length);
            File.WriteAllText(Path.Combine(exactDir, names.Picked), $"Selected contents: {names.Picked}");
            File.WriteAllText(Path.Combine(exactDir, names.Deceptive), "Unselected contents from the backup");
        }
        var dest = new Destination { Name = "Disposable encrypted repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e40", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Bracket folder safety", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
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
        Assert.All(exactNames, names =>
        {
            Assert.Contains("Exact [names]/" + names.Picked, all);
            Assert.Contains("Exact [names]/" + names.Deceptive, all);
        });
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
        await AssertNativeRestoreAsync(fixture, target);
        ctx.Report.RecordNote("Whole folder with brackets",
            "2,397 selected files became one folder pattern. All 2,397 restored files match their original bytes. " +
            "The similarly named Photos x2026x folder was excluded; an existing destination file was unchanged. " +
            "The daemon's saved operation confirms that this used Backrest's native Restore API.");

        // Mounted browsing and whole-folder requests may not provide a leaf
        // list. Their safety must come from literal folder matching itself.
        var folderOnlyTarget = ctx.MakeRestoreTarget("folder-without-leaf-list");
        var occupiedSibling = Path.Combine(folderOnlyTarget, "Photos x2026x", "unselected.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(occupiedSibling)!);
        File.WriteAllText(occupiedSibling, "Keep the user's existing sibling file");
        var folderOnly = await ServiceLocator.Restore.RunAsync(new RestoreRequest {
            Backup = backup, SourcePath = source, Destination = dest, StorageName = "default", Revision = revision,
            IncludePatterns = patterns, TargetPath = folderOnlyTarget }, CancellationToken.None);
        Assert.True(folderOnly.TerminalStatus == RestoreTerminalStatus.Success,
            folderOnly.ErrorMessage ?? folderOnly.TerminalStatus.ToString());
        Assert.Equal(2397, folderOnly.RestoredCount);
        foreach (var relative in selected)
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, relative)), File.ReadAllBytes(Path.Combine(folderOnlyTarget, relative)));
        Assert.Equal("Keep the user's existing sibling file", File.ReadAllText(occupiedSibling));
        Assert.Equal(2398, Directory.GetFiles(folderOnlyTarget, "*", SearchOption.AllDirectories).Length);
        Assert.False(Directory.Exists(Path.Combine(folderOnlyTarget, "Exact [names]")));
        await AssertNativeRestoreAsync(fixture, folderOnlyTarget);
        ctx.Report.RecordNote("Whole folder without an exact-file list",
            "A second native restore supplied only the Photos [2026] folder name. All 2,397 files match the originals. " +
            "The existing Photos x2026x/unselected.txt kept its original contents, even though different contents " +
            "for that same sibling file exist in the backup. No other folder was created.");

        foreach (var names in exactNames)
        {
            var pickedRelative = "Exact [names]/" + names.Picked;
            var deceptiveRelative = "Exact [names]/" + names.Deceptive;
            foreach (var occupied in new[] { false, true })
            {
                var singleTarget = ctx.MakeRestoreTarget($"single-{Array.IndexOf(exactNames, names)}-{occupied}");
                var deceptiveTarget = Path.Combine(singleTarget, deceptiveRelative);
                if (occupied)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(deceptiveTarget)!);
                    File.WriteAllText(deceptiveTarget, "Keep this existing file, even with replace enabled");
                }
                var singleResult = await ServiceLocator.Restore.RunAsync(new RestoreRequest {
                    Backup = backup, SourcePath = source, Destination = dest, StorageName = "default", Revision = revision,
                    Files = new[] { pickedRelative }, IncludePatterns = new[] { pickedRelative },
                    TargetPath = singleTarget, Overwrite = true }, CancellationToken.None);
                Assert.True(singleResult.TerminalStatus == RestoreTerminalStatus.Success,
                    singleResult.ErrorMessage ?? singleResult.TerminalStatus.ToString());
                Assert.Equal(1, singleResult.RestoredCount);
                Assert.Equal(File.ReadAllBytes(Path.Combine(source, pickedRelative)),
                    File.ReadAllBytes(Path.Combine(singleTarget, pickedRelative)));
                if (occupied)
                    Assert.Equal("Keep this existing file, even with replace enabled", File.ReadAllText(deceptiveTarget));
                else
                    Assert.False(File.Exists(deceptiveTarget));
                Assert.Equal(occupied ? 2 : 1, Directory.GetFiles(singleTarget, "*", SearchOption.AllDirectories).Length);
                await AssertNativeRestoreAsync(fixture, singleTarget);
            }
            ctx.Report.RecordNote($"Single file: {names.Picked}",
                $"Both real native restores recovered the selected file byte-for-byte. Its deceptive sibling, {names.Deceptive}, " +
                "also exists in the backup: it was absent after restoring to an empty target, and its original target contents " +
                "were unchanged after restoring with Replace existing files enabled. The ancestor folder also contains brackets.");
        }

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
        ctx.Snapshot(host, "after real bracket-name restores",
            "The encrypted backup and nine restores completed. The report records byte comparisons, native operation checks, " +
            "excluded sibling files, and existing files that stayed unchanged.");
    }

    private static async Task AssertNativeRestoreAsync(RealEngineFixture fixture, string target)
    {
        var operations = await fixture.Sidecar.Client!.CallAsync<GetOperationsRequest, BackrestOperationList>(
            "/v1.Backrest/GetOperations", new GetOperationsRequest { LastN = 256 }, CancellationToken.None);
        var operation = Assert.Single(operations.Operations!.Where(op => op.OperationRestore is { } restore &&
            string.Equals(Path.GetDirectoryName(restore.Target), target,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
        Assert.Equal("STATUS_SUCCESS", operation.Status);
    }
}
