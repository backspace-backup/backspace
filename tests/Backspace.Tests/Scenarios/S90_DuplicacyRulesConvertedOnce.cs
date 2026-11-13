using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U151 (6 November 2026): "You were supposed to CONVERT duplicacy rules to
/// restic!" The owner's local_c, stored with its Duplicacy lines, is
/// converted when the settings load (as when the app starts) into exactly the
/// text he approved, saved, and shown as plain restic rules in the editor:
/// each rule under its # was: line, the recommended block from the top of the
/// drive, its first group the Essentials (U165). No rule needs fixing.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S90_DuplicacyRulesConvertedOnce : ScenarioBase
{
    protected override string ScenarioName => "S90-duplicacy-rules-converted-once";

    [AvaloniaFact] public async Task Run_S90() => await Run();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Services", "Fixtures", "U151", name));

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        if (!OperatingSystem.IsWindows()) ctx.Skip("Windows only: Windows exclusion rules (?:/...); Linux and macOS get their own");
        // A whole drive, as local_c backs up C:\. A drive this computer doesn't
        // have, so the editor's size estimate reads nothing: the rules for any
        // drive start with ?:/, so the text is the same as for C:\.
        var used = System.IO.DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var drive = Enumerable.Range('M', 14).Select(c => (char)c).First(c => !used.Contains(c)) + @":\";
        var stored = new Backup { Name = "local_c", SourcePaths = { drive }, FiltersText = Fixture("local_c.txt") };
        ServiceLocator.Config.Update(c => c.Backups.Add(stored));
        Assert.True(DuplicacyFilterConverter.NeedsConversion(ServiceLocator.Config.Current.Backups.Single(b => b.Id == stored.Id).FiltersText));

        // The next start of the app reads the settings again.
        ServiceLocator.Config.Load();
        var backup = ServiceLocator.Config.Current.Backups.Single(b => b.Id == stored.Id);
        // The approved text, with the Essentials as the recommended block's first group (U165).
        var approved = RecommendedFilters.UpgradeEssentials(Fixture("local_c-after.txt"), new[] { drive });
        Assert.Equal(approved, backup.FiltersText);
        ServiceLocator.Config.Load();
        Assert.Equal(approved, ServiceLocator.Config.Current.Backups.Single(b => b.Id == stored.Id).FiltersText);

        var vm = new BackupEditorViewModel(backup, isNew: false);
        Assert.Null(vm.FilterInvalidWarning);
        Assert.Null(vm.FilterExceptionWarning);
        var win = new BackupEditorWindow { DataContext = vm, Width = 1500, Height = 900 };
        ctx.Driver.Show(win);
        var outer = win.GetVisualDescendants().OfType<TabControl>().First();
        outer.SelectedIndex = 2;
        ctx.Driver.Pump();
        var inner = win.GetVisualDescendants().OfType<TabControl>().First(t => !ReferenceEquals(t, outer));
        inner.SelectedIndex = 1;
        await ctx.Driver.PumpAsync(200);
        var rules = win.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == vm.FiltersText);
        ctx.Snapshot(win, "converted-rules-top",
            "Filters > Advanced for local_c after the settings loaded: the syntax line describes plain restic rules (?:/ for the top of any drive, $$ for a $), " +
            "and the rules start with the converted Duplicacy lines, each restic rule under the # was: line it came from.");

        var lines = vm.FiltersText!.Split('\n');
        rules.CaretIndex = vm.FiltersText.IndexOf(RecommendedFilters.AutoBlockHeader, StringComparison.Ordinal);
        rules.ScrollToLine(Array.IndexOf(lines, "# Windows system files: locked, regenerable, or restore-unsafe."));
        await ctx.Driver.PumpAsync(200);
        ctx.Snapshot(win, "converted-recommended-block",
            "Further down: the last converted rules, then the recommended block, its rules now from the top of the drive (?:/pagefile.sys, ?:/$$RECYCLE.BIN/, ?:/$$WinREAgent/), " +
            "while the ones matched anywhere stay as they were (ntuser.dat*, OneDriveTemp/).");

        rules.CaretIndex = vm.FiltersText.IndexOf(RecommendedFilters.EssentialsHeading, StringComparison.Ordinal);
        rules.ScrollToLine(Array.IndexOf(lines, RecommendedFilters.EssentialsHeading));
        await ctx.Driver.PumpAsync(200);
        ctx.Snapshot(win, "essentials-group",
            "The recommended block's first group, Essentials (U165): the rules of the former essential block (?:/pagefile.sys, ?:/$$WinREAgent/, " +
            "?:/Users/*/AppData/Local/Microsoft/WindowsApps/…) with the old group's, sorted, from the top of the drive.");
        win.Close();

        ctx.Report.RecordNote("Conversion at load",
            "local_c's stored Duplicacy text was converted when the settings loaded, byte for byte into the approved text, and saved; loading again changed nothing.");
    }
}
