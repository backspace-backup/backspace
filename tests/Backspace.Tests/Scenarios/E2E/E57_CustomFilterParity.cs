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
/// Left out (U177), the byte estimate, the real backup and restic's own dry run
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
        // *.tmp ignores case only on Windows: elsewhere scratch.TMP stays, as restic does.
        if (!OperatingSystem.IsWindows()) { kept2 = [.. kept2, "scratch.TMP"]; omitted2 = omitted2.Where(o => o != "scratch.TMP").ToArray(); }
        // Two ** in one rule: only Backspace's Windows restic carries the fix (restic issue 22131);
        // elsewhere official restic, and the estimate with it, keep Twin/a/node_modules/x.
        if (!OperatingSystem.IsWindows()) { kept2 = [.. kept2, "Twin/a/node_modules/x"]; omitted2 = omitted2.Where(o => o != "Twin/a/node_modules/x").ToArray(); }
        await Write(syntax, kept2.Where(k => k != "cache").Concat(omitted2));
        var rules2 = string.Join("\n",
            "# restic's own syntax (U151)",
            top + "/cache/*",                 // the folder stays, empty
            top + "/Chrome/*",
            "!" + top + "/Chrome/Keep",       // the last matching rule decides
            "*.tmp",                          // case ignored on Windows
            top + "/$$money/",                // a literal $
            top + (OperatingSystem.IsWindows() ? "/env-$USERNAME/" : "/env-$USER/"), // an environment variable, as restic expands it
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
        // The recommended block comes with a conversion on Windows, where Duplicacy setups come from.
        if (OperatingSystem.IsWindows()) Assert.Contains(RecommendedFilters.EssentialsHeading, converted.FiltersText);
        Assert.DoesNotContain("\\ne:(?i)", File.ReadAllText(AppPaths.ConfigFile));
        ctx.Driver.Pump();
        await Check(ctx, view, destination, null, dropbox, converted.FiltersText, kept3, omitted3, converted);

        // The editor shows the converted text; Left out lists what restic leaves out (U177).
        var vm = new BackupEditorViewModel(converted, isNew: false);
        // Wide: the test folder's path is long.
        var editor = new BackupEditorWindow { DataContext = vm, Width = 1600, Height = 900 };
        ctx.Driver.Show(editor);
        editor.FindControl<TabControl>("EditorTabs")!.SelectedItem = editor.FindControl<TabItem>("FiltersTab");
        editor.FindControl<TabControl>("FiltersSubTabs")!.SelectedItem = editor.FindControl<TabItem>("AdvancedFiltersTab");
        editor.FindControl<TabControl>("AdvancedSubTabs")!.SelectedItem = editor.FindControl<TabItem>("LeftOutTab");
        ctx.Driver.Pump();
        await vm.RunLeftOutCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await ctx.Driver.PumpAsync(200);
        Assert.Contains(vm.LeftOutResult!.Items, i => i.Rule == top.Replace("/restic-syntax", "/Dropbox") + "/Applications/Duplicacy/*");
        Assert.DoesNotContain(vm.LeftOutResult.Items, i => i.Path == Path.Combine(dropbox, "notes.txt"));
        Assert.Null(vm.FilterInvalidWarning);
        ctx.Snapshot(editor, "converted-duplicacy-rules-left-out",
            "Filters > Advanced > Left out for a backup whose stored Duplicacy rules were converted when the settings loaded: each folder the converted rules leave out, " +
            "once, with its size and the rule responsible (…/Dropbox/Applications/Duplicacy/*).");
        ctx.Report.RecordNote("Left out for the converted Dropbox rules",
            string.Join("\n", vm.LeftOutResult.Items.Select(i => $"{i.Path}  {i.Files} files  {i.Bytes} B  ({i.Rule})")));
        editor.Close();

        // The restic-syntax rules include "/top.txt": on Windows Left out says
        // such a rule matches nothing, and why (U151, moved here by U177).
        var syntaxVm = new BackupEditorViewModel(new Backup { Name = "restic syntax", SourcePaths = { syntax }, FiltersText = rules2,
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } }, isNew: false);
        var syntaxEditor = new BackupEditorWindow { DataContext = syntaxVm, Width = 1500, Height = 900 };
        ctx.Driver.Show(syntaxEditor);
        syntaxEditor.FindControl<TabControl>("EditorTabs")!.SelectedItem = syntaxEditor.FindControl<TabItem>("FiltersTab");
        syntaxEditor.FindControl<TabControl>("FiltersSubTabs")!.SelectedItem = syntaxEditor.FindControl<TabItem>("AdvancedFiltersTab");
        syntaxEditor.FindControl<TabControl>("AdvancedSubTabs")!.SelectedItem = syntaxEditor.FindControl<TabItem>("LeftOutTab");
        await syntaxVm.RunLeftOutCommand.ExecuteAsync(null);
        await ctx.Driver.PumpAsync(200);
        var rooted = Assert.Single(syntaxVm.LeftOutResult!.MatchNothing, u => u.Rule == "/top.txt");
        Assert.True(rooted.Rooted);
        Assert.Contains(syntaxVm.LeftOutRows.OfType<LeftOutUnusedRow>(), r => r.Rule.Rule == "/top.txt" && r.Why.Contains("?:/", StringComparison.Ordinal));
        ctx.Snapshot(syntaxEditor, "rule-starting-with-slash-matches-nothing",
            "Filters > Advanced > Left out with the restic-syntax rules: under 'Rules that match nothing here', /top.txt with why (a Windows path starts with its drive) and the fix (?:/ or C:/); the rule itself stays as written.");
        syntaxEditor.Close();

        ctx.Report.RecordNote("Restic parity",
            "Three backups: character classes and exceptions; restic syntax (?:/, $$, $USERNAME, **, a trailing /, a plain leading /); " +
            "and a stored Duplicacy filter converted at load. For each, Left out, the byte estimate, restic's own dry run with the same " +
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

    /// <summary>Left out, the estimate, restic's dry run, the real backup and
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
        // What these rules leave out (U177): every file not under a row is kept.
        var leftOut = LeftOutAnalyzer.Analyze(new LeftOutRequest(new[] { source }, filters, null, backup.ExcludeCloudFiles),
            new LeftOutCounters(), CancellationToken.None);
        bool Out(string file) => leftOut.Items.Any(i => string.Equals(i.Path, file, StringComparison.OrdinalIgnoreCase)
            || file.StartsWith(i.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        var all = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        var simulated = all.Where(f => !Out(f)).Select(Rel).Order().ToArray();
        Assert.Equal(kept.Order(), simulated);
        Assert.Equal(all.Length - kept.Length, leftOut.TotalFiles);

        var savings = await new SavingsEstimator().ComputeAsync(new[] { source }, filters, CancellationToken.None);
        Assert.Equal(kept.Sum(n => new FileInfo(Path.Combine(source, n)).Length), savings.IncludedBytes);
        Assert.Equal(omitted.Sum(n => new FileInfo(Path.Combine(source, n)).Length), savings.ExcludedBytes);
        Assert.Equal(savings.ExcludedBytes, leftOut.TotalBytes);

        var dryRun = ResticDryRun(ctx, source, filters).Order().ToArray();
        Assert.Equal(simulated, dryRun);

        var run = await RunBackupViaUiAsync(ctx, backup, view);
        Assert.True(run.Status is BackupRunStatus.Success or BackupRunStatus.Warning, run.Summary);
        // The engine handed restic the text itself, unchanged.
        var planFiles = Directory.GetFiles(Path.GetDirectoryName(ResticEngine.ExcludeFilePath("x"))!, "*.txt");
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
        // On Linux and macOS, the path as it is.
        var prefix = (OperatingSystem.IsWindows() ? "/" + source.Replace('\\', '/').Replace(":", "") : source) + "/";
        return output.Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^new\s+(.+?), saved in"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value)
            .Where(p => !p.EndsWith('/') && p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[prefix.Length..]).ToList();
    }
}
