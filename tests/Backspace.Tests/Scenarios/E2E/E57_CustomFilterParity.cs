using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Simulate, the byte estimate, the real backup and restic's own dry run
/// leave out the same files. Since U151 (6 November 2026) the Filters text is
/// handed to restic unchanged as an exclude file, and Backspace reads it with
/// restic's rules on full paths; three backups check that:
///   1. character classes, exceptions and bare names (the original E57);
///   2. restic's own syntax: ?:/ for the top of the drive, $$ for a literal $,
///      $NAME from the environment, **, a trailing / that also matches a file,
///      and a plain leading / that matches nothing on Windows;
///   3. a stored Duplicacy filter, converted when the settings load (as at app
///      start), then backed up.
/// Each is compared with <c>restic backup --dry-run -vv</c> run directly with
/// the same exclude file.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E57_CustomFilterParity(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E57-custom-filter-parity";
    [AvaloniaFact] public async Task Run_E57() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var destination = new Destination { Name="Custom filters", Kind=DestinationKind.LocalFolder,
            PathOrSubpath=ctx.MakeLocalDestination("repo"), Encrypted=true,
            StoragePasswordRef=ServiceLocator.Secrets.PutNew("e57",Guid.NewGuid().ToString("N")) };
        ServiceLocator.Config.Update(c => c.Destinations.Add(destination));
        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var window = ctx.Driver.Show(new Window { Width = 1100, Height = 760, Content = view });

        // 1. The original case.
        var source = Path.Combine(ctx.SourcesDir, "classes-and-exceptions");
        var expected = new[] { "drop3.txt", "keep.log", "nested/keep.log", "c.bin", "notx.txt" };
        var omitted = new[] { "drop1.txt", "drop2.txt", "other.log", "nested/other.log", "a.bin", "b.bin", "literal[1].txt", "notA.txt", "cache/hidden.txt" };
        await Write(source, expected.Concat(omitted));
        var backup = await Check(ctx, view, destination, "Custom filter comparison", source,
            "drop[12].txt\n*.log\n!keep.log\n[a-b].bin\nliteral[[]1].txt\nnot[^x].txt\ncache", expected, omitted);
        ctx.Snapshot(window,"custom-filter-backup-complete","The actual backup and byte-for-byte recovery retain the same five files as the preview; nine deliberately excluded files are absent.");

        // 2. restic's own syntax, from the top of the drive.
        var syntax = Path.Combine(ctx.SourcesDir, "restic-syntax");
        var top = DuplicacyFilterConverter.Prefixes(new[] { syntax })[0];
        var user = Environment.UserName;
        var kept2 = new[] { "top.txt", "Chrome/Keep/ext.json", "notes/Folder.txt", "cache", "money.txt", "Deep/keep.me", "env-other/x.txt" };
        var omitted2 = new[] { "cache/x.bin", "Chrome/History", "Chrome/Cache/data", "$money/x.txt", "env-" + user + "/x.txt",
            "Deep/a/b/drop.me", "Deep/drop.me", "Folder/inside.txt", "notes/Folder", "scratch.TMP",
            "Twin/a/node_modules/x", "Twin/b/node_modules/d/y", "Twin/c/d/node_modules/z" };
        await Write(syntax, kept2.Where(k => k != "cache").Concat(omitted2));
        var rules2 = string.Join("\n",
            "# restic's own syntax (U151)",
            top + "/cache/*",                 // the folder stays, empty
            top + "/Chrome/*",
            "!" + top + "/Chrome/Keep",       // the last matching rule decides
            "*.tmp",                          // case ignored on Windows
            top + "/$$money/",                // a literal $
            top + "/env-$USERNAME/",          // an environment variable, as restic expands it
            "/top.txt",                       // a plain leading / matches nothing on Windows
            "Deep/**/drop.me",
            top + "/Twin/**/*/**/node_modules/*", // two **: official restic leaves Twin/a/node_modules/x in (issue 22131); ours doesn't
            "Folder/");                       // restic drops the trailing /: a file named Folder goes too
        await Check(ctx, view, destination, "restic syntax", syntax, rules2, kept2.Where(k => k != "cache").ToArray(), omitted2);

        // 3. A Duplicacy filter stored by an earlier version, converted when
        //    the settings load (as when the app starts), then backed up.
        var dropbox = Path.Combine(ctx.SourcesDir, "Dropbox");
        var kept3 = new[] { "Apps/Other/a.txt", "Apps/InpsydeBackWPup.txt", "Backup/Data/PC/doc.txt", "Backup/Duplicacy/repo/chunks/c1",
            "notes.txt", "Sub/Applications/Duplicacy/kept.txt" };
        var omitted3 = new[] { ".dropbox.cache/x", "Applications/Duplicacy/dup.exe", "Apps/InpsydeBackWPup/w.zip", "Apps/SMSBackupRestore/s.xml",
            "Backup/Duplicacy/repo/shadow/s1", "Backup/Data/iPad/i.bin" };
        await Write(dropbox, kept3.Concat(omitted3));
        var duplicacy = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Services", "Fixtures", "U151", "local_dropbox.txt"));
        var stored = new Backup { Name = "Dropbox (Duplicacy rules)", SourcePaths = { dropbox }, FiltersText = duplicacy,
            UseVss = false, VssDisabledAcknowledged = true, PruneSchedule = new(), CheckSchedule = new(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => c.Backups.Add(stored));
        Assert.Contains("\\ne:(?i)^Applications/Duplicacy/", File.ReadAllText(AppPaths.ConfigFile));
        ServiceLocator.Config.Load();
        var converted = ServiceLocator.Config.Current.Backups.Single(b => b.Id == stored.Id);
        Assert.False(DuplicacyFilterConverter.NeedsConversion(converted.FiltersText));
        Assert.StartsWith(DuplicacyFilterConverter.RunHeader1, converted.FiltersText);
        Assert.Contains("# was: e:(?i)^Applications/Duplicacy/", converted.FiltersText);
        Assert.Contains(top.Replace("/restic-syntax", "/Dropbox") + "/Applications/Duplicacy/*", converted.FiltersText);
        Assert.Contains(RecommendedFilters.EssentialsHeading, converted.FiltersText);
        Assert.DoesNotContain("\\ne:(?i)", File.ReadAllText(AppPaths.ConfigFile));
        ctx.Driver.Pump();
        await Check(ctx, view, destination, null, dropbox, converted.FiltersText, kept3, omitted3, converted);

        // The editor shows the converted text; Simulate lists what restic keeps.
        var vm = new BackupEditorViewModel(converted, isNew: false);
        // Wide: the test folder's path is long.
        var editor = new BackupEditorWindow { DataContext = vm, Width = 2600, Height = 900 };
        ctx.Driver.Show(editor);
        var outerTabs = editor.GetVisualDescendants().OfType<TabControl>().First();
        outerTabs.SelectedIndex = 2;
        ctx.Driver.Pump();
        var innerTabs = editor.GetVisualDescendants().OfType<TabControl>().First(t => !ReferenceEquals(t, outerTabs));
        innerTabs.SelectedIndex = 1;
        ctx.Driver.Pump();
        await vm.RunFilterSimulationCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        ctx.Driver.Pump();
        Assert.Contains("(rule: " + top.Replace("/restic-syntax", "/Dropbox") + "/Applications/Duplicacy/*)", vm.FilterSimulationLog);
        Assert.Contains("✓  " + Path.Combine(dropbox, "notes.txt"), vm.FilterSimulationLog);
        Assert.Null(vm.FilterInvalidWarning);
        ctx.Snapshot(editor, "converted-duplicacy-rules-and-simulate",
            "Filters > Advanced for a backup whose stored Duplicacy rules were converted when the settings loaded: each rule under its # was: line, " +
            "the recommended block with its Essentials group at the end, and Simulate listing ✓ kept files and ✗ excluded folders with the rule that decided.");
        // The test folder's path is long: scroll both panes to the ends of the lines.
        foreach (var box in editor.GetVisualDescendants().OfType<TextBox>().Where(t => t.Text == vm.FiltersText || t.Text == vm.FilterSimulationLog))
            if (box.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroller)
                scroller.Offset = new Avalonia.Vector(Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width), scroller.Offset.Y);
        await ctx.Driver.PumpAsync(200);
        ctx.Snapshot(editor, "converted-rules-and-simulate-line-ends",
            "The same panes scrolled to the ends of the lines: each converted rule ends with the Dropbox folder's own path (…/Dropbox/Applications/Duplicacy/*), " +
            "and each ✗ line of the simulation names the rule that left that folder out.");
        ctx.Report.RecordNote("Simulate pane for the converted Dropbox rules", vm.FilterSimulationLog);
        editor.Close();

        // The restic-syntax rules include "/top.txt": on Windows the editor
        // says such a rule matches nothing (U151, 7 November 2026).
        var syntaxVm = new BackupEditorViewModel(new Backup { Name = "restic syntax", SourcePaths = { syntax }, FiltersText = rules2,
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } }, isNew: false);
        Assert.Equal("/top.txt", syntaxVm.FilterNothingWarning);
        var syntaxEditor = new BackupEditorWindow { DataContext = syntaxVm, Width = 1500, Height = 900 };
        ctx.Driver.Show(syntaxEditor);
        var syntaxTabs = syntaxEditor.GetVisualDescendants().OfType<TabControl>().First();
        syntaxTabs.SelectedIndex = 2;
        ctx.Driver.Pump();
        syntaxEditor.GetVisualDescendants().OfType<TabControl>().First(t => !ReferenceEquals(t, syntaxTabs)).SelectedIndex = 1;
        await ctx.Driver.PumpAsync(200);
        ctx.Snapshot(syntaxEditor, "rule-starting-with-slash-warning",
            "Filters > Advanced with the restic-syntax rules: a warning lists /top.txt as matching nothing on Windows and suggests ?:/ or C:/; the rule itself stays as written.");
        syntaxEditor.Close();

        ctx.Report.RecordNote("Restic parity",
            "Three backups: character classes and exceptions; restic syntax (?:/, $$, $USERNAME, **, a trailing /, a plain leading /); " +
            "and a stored Duplicacy filter converted at load. For each, Simulate, the byte estimate, restic's own dry run with the same " +
            "exclude file, the real backup and the recovered files agree.");
    }

    private static async Task Write(string root, IEnumerable<string> files)
    {
        foreach (var name in files)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "Distinct synthetic contents for " + name);
        }
    }

    /// <summary>Simulate, the estimate, restic's dry run, the real backup and
    /// the restore keep exactly <paramref name="kept"/>.</summary>
    private async Task<Backup> Check(ScenarioContext ctx, BackupsView view, Destination destination, string? name, string source,
        string filters, string[] kept, string[] omitted, Backup? existing = null)
    {
        var backup = existing ?? new Backup { Name = name!, SourcePaths = { source }, FiltersText = filters,
            UseVss = false, VssDisabledAcknowledged = true, PruneSchedule = new(), CheckSchedule = new(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        if (existing is null) ServiceLocator.Config.Update(c => c.Backups.Add(backup));
        Assert.Empty(FilterTranslator.InvalidRules(filters));

        string Rel(string path) => Path.GetRelativePath(source, path).Replace('\\', '/');
        var preview = new List<SimulatedEntry>();
        await new FilterSimulator().SimulateAsync(backup, preview.Add, CancellationToken.None);
        var simulated = preview.Where(p => p.Included && File.Exists(p.Path)).Select(p => Rel(p.Path)).Order().ToArray();
        Assert.Equal(kept.Order(), simulated);

        var savings = await new SavingsEstimator().ComputeAsync(new[] { source }, filters, CancellationToken.None);
        Assert.Equal(kept.Sum(n => new FileInfo(Path.Combine(source, n)).Length), savings.IncludedBytes);
        Assert.Equal(omitted.Sum(n => new FileInfo(Path.Combine(source, n)).Length), savings.ExcludedBytes);

        var dryRun = ResticDryRun(ctx, source, filters).Order().ToArray();
        Assert.Equal(simulated, dryRun);

        var run = await RunBackupViaUiAsync(ctx, backup, view);
        Assert.True(run.Status is BackupRunStatus.Success or BackupRunStatus.Warning, run.Summary);
        // The engine handed restic the text itself, unchanged.
        var planFiles = Directory.GetFiles(Path.GetDirectoryName(ResticBackrestEngine.ExcludeFilePath("x"))!, "*.txt");
        Assert.Contains(planFiles, f => File.ReadAllText(f) == filters);

        var target = ctx.MakeRestoreTarget("recovered-" + Path.GetFileName(source));
        var recovered = await RestoreEverythingAsync(backup, source, destination, "default", target, CancellationToken.None);
        Assert.Equal(0, recovered.FailedCount);
        var actual = Directory.GetFiles(target, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(target, p).Replace('\\', '/')).Order().ToArray();
        Assert.Equal(kept.Order(), actual);
        foreach (var file in kept) Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(source, file)), await File.ReadAllBytesAsync(Path.Combine(target, file)));
        return backup;
    }

    /// <summary>The files restic itself would back up from
    /// <paramref name="source"/> with <paramref name="filters"/> as its exclude
    /// file: the bundled restic, run directly, <c>backup --dry-run -vv</c>.</summary>
    private static List<string> ResticDryRun(ScenarioContext ctx, string source, string filters)
    {
        var folder = Path.Combine(ctx.RootDir, "dry-run");
        Directory.CreateDirectory(folder);
        var repo = Path.Combine(folder, "repo");
        var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, filters);
        string Run(params string[] args)
        {
            var start = new ProcessStartInfo(AppPaths.ResticBinary) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) start.ArgumentList.Add(a);
            start.Environment["RESTIC_PASSWORD"] = "e57-dry-run";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, "restic " + string.Join(" ", args) + ": " + stderr);
            return stdout.Result;
        }
        if (!Directory.Exists(repo)) Run("init", "-r", repo, "-q");
        var output = Run("backup", "-r", repo, "--dry-run", "-vv", "--no-scan", FilterTranslator.ExcludeFileFlag, file, source);
        // "new       /C/Users/…/file.txt, saved in …" (folders end with "/").
        var prefix = "/" + source.Replace('\\', '/').Replace(":", "") + "/";
        return output.Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^new\s+(.+?), saved in"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value)
            .Where(p => !p.EndsWith('/') && p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[prefix.Length..]).ToList();
    }
}
