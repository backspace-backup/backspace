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
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U177 (10 November 2026): Filters › Advanced › Rules | Left out. The rules
/// get the full width; Left out shows nothing until Run. Then one row per
/// topmost item left out, sorted by size, each with the rule responsible,
/// a search box, a total, By item / By rule, rules that match nothing (a
/// rule starting with / on Windows says why), a click on a rule selecting it
/// on the Rules tab, Keep this adding an exception, a second run compared
/// with the saved rules (new rows marked, back in listed), and Stop.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S97_WhatTheRulesLeaveOut : ScenarioBase
{
    protected override string ScenarioName => "S97-what-the-rules-leave-out";

    [AvaloniaFact] public async Task Run_S97() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Path.Combine(ctx.RootDir, "project");
        void Write(string relative, int bytes)
        {
            var path = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }
        Write("Videos/raw/holiday.mov", 3_000_000);
        Write("Videos/raw/party.mov", 2_000_000);
        Write("Videos/edited.mp4", 500_000);
        Write("site/node_modules/lib/index.js", 400_000);
        Write("site/node_modules/package.json", 2_000);
        Write("site/index.html", 3_000);
        for (var i = 0; i < 10; i++) Write($"scratch/part{i}.bin", 10_000);
        Write("draft.tmp", 50_000);
        Write("docs/old.tmp", 20_000);
        Write("docs/report.docx", 30_000);
        Write("notes.txt", 100);

        var own = "# My rules\nVideos/raw/\nscratch/\n*.iso\n/top.txt";
        var saved = RecommendedFilters.MergeIntoFilters(own,
            RecommendedFilters.RenderBlock(false, true, Array.Empty<RecommendedFilters.CloudSyncFolder>(), new[] { source }));
        var backup = new Backup { Name = "Projects", SourcePaths = { source }, FiltersText = saved };
        ServiceLocator.Config.Update(c => c.Backups.Add(backup));
        var win = new BackupEditorWindow(ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id), isNew: false) { Width = 1100, Height = 860 };
        ctx.Driver.Show(win);
        var vm = (BackupEditorViewModel)win.DataContext!;
        win.FindControl<TabControl>("EditorTabs")!.SelectedItem = win.FindControl<TabItem>("FiltersTab");
        win.FindControl<TabControl>("FiltersSubTabs")!.SelectedItem = win.FindControl<TabItem>("AdvancedFiltersTab");
        await ctx.Driver.PumpAsync(150);

        // Rules: the editor has the whole width, no simulation pane beside it.
        var editor = win.FindControl<TextBox>("RulesEditor")!;
        Assert.True(editor.IsEffectivelyVisible);
        Assert.True(editor.Bounds.Width > win.Bounds.Width * 0.85, $"Rules editor {editor.Bounds.Width} of {win.Bounds.Width}.");
        Assert.DoesNotContain(win.GetVisualDescendants().OfType<Button>(), b => b.Content as string is "Simulate" or "Count");

        // Left out: nothing until Run.
        win.FindControl<TabControl>("AdvancedSubTabs")!.SelectedItem = win.FindControl<TabItem>("LeftOutTab");
        await ctx.Driver.PumpAsync(150);
        Assert.True(win.FindControl<Border>("LeftOutEmpty")!.IsEffectivelyVisible);
        Assert.Null(vm.LeftOutResult);
        ctx.Snapshot(win, "left-out-before-run",
            "Filters > Advanced > Left out before running: a Run button, 'What these rules leave out' and a short note; nothing is read or listed yet.");

        // Run, by a pointer click.
        ClickControl(ctx, win, win.FindControl<Button>("RunLeftOut")!);
        Assert.True(ctx.Driver.WaitFor(() => vm.HasLeftOutResult, 20_000), "Left out never finished.");
        await ctx.Driver.PumpAsync(150);
        var r = vm.LeftOutResult!;
        // Topmost rows only, sorted by size, each with its rule.
        Assert.Equal(new[] { "raw", "node_modules", "scratch", "draft.tmp", "old.tmp" }, r.Items.Select(i => Path.GetFileName(i.Path)));
        var raw = r.Items[0];
        Assert.Equal((true, 2L, 5_000_000L, "Videos/raw/"), (raw.IsFolder, raw.Files, raw.Bytes, raw.Rule));
        Assert.Equal("node_modules/", r.Items[1].Rule);
        Assert.Equal(2, r.Items[1].Files);
        Assert.DoesNotContain(r.Items, i => i.Path.Contains("index.js", StringComparison.Ordinal)); // never the contents
        Assert.Equal("5 items (16 files), 5.3 MB left out", vm.LeftOutTotal);
        // Rules that match nothing: the person's own, and the one that can't on Windows.
        var unused = vm.LeftOutRows.OfType<LeftOutUnusedRow>().ToList();
        Assert.Equal(new[] { "*.iso", "/top.txt" }, unused.Select(u => u.Rule.Rule));
        Assert.Contains("a Windows path starts with its drive", unused[1].Why);
        Assert.Contains(vm.LeftOutRows.OfType<LeftOutHeading>(), h => h.Note?.Contains("recommended rules match nothing here today") == true);
        ctx.Snapshot(win, "left-out-result",
            "After Run: '5 items (16 files), 5.3 MB left out'; Videos\\raw first (Folder · 2 files, 4.8 MB, left out by Videos/raw/), then node_modules, scratch, draft.tmp and docs\\old.tmp, each with its rule and Keep this. Below: rules that match nothing (*.iso; /top.txt with why it never can on Windows) and a count of recommended rules matching nothing today.");

        // Search narrows the rows to paths or rules containing the words.
        var search = win.FindControl<TextBox>("LeftOutSearch")!;
        search.Text = "tmp";
        await ctx.Driver.PumpAsync(100);
        Assert.Equal(new[] { "draft.tmp", "old.tmp" }, vm.LeftOutRows.OfType<LeftOutRow>().Select(x => Path.GetFileName(x.Path)));
        ctx.Snapshot(win, "left-out-search",
            "Search 'tmp': only draft.tmp and docs\\old.tmp, both left out by *.tmp; the total above still counts everything.");
        search.Text = "";
        await ctx.Driver.PumpAsync(50);

        // By rule: one row per rule with what it leaves out.
        ClickControl(ctx, win, win.FindControl<Button>("LeftOutByRuleButton")!);
        Assert.True(vm.LeftOutByRule);
        var byRule = vm.LeftOutRows.OfType<LeftOutRuleRow>().ToList();
        Assert.Equal(new[] { "Videos/raw/", "node_modules/", "scratch/", "*.tmp" }, byRule.Select(x => x.Rule.Rule));
        Assert.Equal("2 items · 2 files", byRule[3].Detail);
        ctx.Snapshot(win, "left-out-by-rule",
            "By rule: Videos/raw/ 4.8 MB, node_modules/, scratch/ (10 files), *.tmp (2 items · 2 files), largest first; each rule clickable.");
        ClickControl(ctx, win, win.FindControl<Button>("LeftOutByItemButton")!);

        // A click on a row's rule selects that rule on the Rules tab.
        var link = win.GetVisualDescendants().OfType<Button>().First(b => b.Name == "RuleLink" && b.DataContext is LeftOutRow { Rule.Rule: "Videos/raw/" });
        ClickControl(ctx, win, link);
        await ctx.Driver.PumpAsync(150);
        Assert.Equal(win.FindControl<TabItem>("RulesTab"), win.FindControl<TabControl>("AdvancedSubTabs")!.SelectedItem);
        Assert.Equal(Array.FindIndex(vm.FiltersText.Split('\n'), l => l.Trim() == "Videos/raw/"), vm.HighlightedRuleLine);
        Assert.Equal("Videos/raw/", editor.SelectedText);
        ctx.Snapshot(win, "rule-highlighted-in-editor",
            "Clicking 'Videos/raw/' on a Left out row switched to the Rules tab with that line selected in the editor.");

        // Keep this: an exception at the end of the rules, waiting for Save.
        win.FindControl<TabControl>("AdvancedSubTabs")!.SelectedItem = win.FindControl<TabItem>("LeftOutTab");
        await ctx.Driver.PumpAsync(100);
        var cacheRow = vm.LeftOutRows.OfType<LeftOutRow>().Single(x => Path.GetFileName(x.Path) == "scratch");
        var keep = win.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Keep this" && ReferenceEquals(b.DataContext, cacheRow));
        ClickControl(ctx, win, keep);
        Assert.True(cacheRow.Kept);
        Assert.EndsWith(BackupEditorViewModel.KeepRule(cacheRow.Path) + "\n", vm.FiltersText);
        Assert.True(vm.LeftOutStale);
        Assert.True(win.FindControl<Border>("LeftOutStaleNotice")!.IsEffectivelyVisible);
        Assert.True(vm.FiltersUnsaved);
        ctx.Snapshot(win, "keep-this-added-an-exception",
            "Keep this on scratch: the row says 'Kept by a new exception; Save keeps it', an amber line says the rules changed since this run, and the footer says filter changes are not saved yet.");

        // Run again with one more edit: compared with the saved rules.
        vm.FiltersText += "docs/\n";
        ClickControl(ctx, win, win.FindControl<Button>("RunLeftOut")!);
        Assert.True(ctx.Driver.WaitFor(() => vm.HasLeftOutResult && !vm.LeftOutRunning, 20_000));
        await ctx.Driver.PumpAsync(150);
        r = vm.LeftOutResult!;
        Assert.True(r.Compared);
        Assert.True(r.Items.Single(i => Path.GetFileName(i.Path) == "docs").IsNew);
        Assert.False(r.Items.Single(i => Path.GetFileName(i.Path) == "raw").IsNew);
        Assert.DoesNotContain(r.Items, i => Path.GetFileName(i.Path) == "scratch");
        var back = Assert.Single(r.BackIn);
        Assert.Equal(("scratch", 10L, 100_000L), (Path.GetFileName(back.Path), back.Files, back.Bytes));
        Assert.False(vm.LeftOutStale);
        Assert.Contains("Compared with the saved rules: 1 newly left out, 1 back in.", vm.LeftOutDetail);
        win.FindControl<ListBox>("LeftOutList")!.ScrollIntoView(vm.LeftOutRows.OfType<LeftOutHeading>().First(h => h.Title?.StartsWith("Back in") == true));
        await ctx.Driver.PumpAsync(100);
        ctx.Snapshot(win, "compared-with-saved-rules",
            "Run again after the edits: docs is marked 'New with your edits'; under 'Back in the backup with your edits', scratch (10 files, 97.7 KB) that the saved rule scratch/ leaves out.");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await ctx.Driver.PumpAsync(150);
        ctx.Snapshot(win, "compared-with-saved-rules-dark", "The same result in the dark theme.");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        await ctx.Driver.PumpAsync(50);

        // Cancel: the run is held before it reads anything, so the running
        // state can be seen, then stopped by the Stop button.
        using (var hold = new ManualResetEventSlim(false))
        {
            LeftOutAnalyzer.HoldForTests = hold;
            try
            {
                ClickControl(ctx, win, win.FindControl<Button>("RunLeftOut")!);
                Assert.True(ctx.Driver.WaitFor(() => vm.LeftOutRunning));
                await ctx.Driver.PumpAsync(350);
                Assert.True(win.FindControl<TextBlock>("LeftOutProgressLine")!.IsEffectivelyVisible);
                Assert.StartsWith("Reading this backup's folders", vm.LeftOutProgress);
                Assert.False(win.FindControl<Button>("RunLeftOut")!.IsEffectivelyVisible);
                ctx.Snapshot(win, "running-with-cancel",
                    "While it runs: Stop in place of Run (the window's own Cancel stays separate), and the progress line 'Reading this backup's folders… N folders, N files so far, … left out.'");
                ClickControl(ctx, win, win.FindControl<Button>("CancelLeftOut")!);
                Assert.True(ctx.Driver.WaitFor(() => !vm.LeftOutRunning, 10_000), "Stop didn't stop the run.");
            }
            finally { LeftOutAnalyzer.HoldForTests = null; }
        }
        await ctx.Driver.PumpAsync(100);
        Assert.Null(vm.LeftOutResult);
        Assert.Equal("Stopped before reading anything. Run it again to see what these rules leave out.", vm.LeftOutMessage);
        Assert.True(win.FindControl<Button>("RunLeftOut")!.IsEffectivelyVisible);
        ctx.Snapshot(win, "cancelled",
            "After Stop: 'Stopped before reading anything. Run it again to see what these rules leave out.' and Run is back.");
        win.Close();
        ctx.Report.RecordNote("Limits",
            "Headless Avalonia on a small synthetic folder; the matching is restic's (E57 compares it with restic's own dry run). A whole drive, unreadable folders and online-only files are covered by the unit tests, not here.");
    }

    /// <summary>A real pointer click, after checking the pointer would reach the control.</summary>
    private static void ClickControl(ScenarioContext ctx, Window window, Control control)
    {
        control.BringIntoView(); ctx.Driver.Pump(); window.UpdateLayout();
        Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        var hit = window.InputHitTest(at);
        Assert.True(ReferenceEquals(hit, control) || hit is Visual visual && visual.GetVisualAncestors().Contains(control),
            $"The pointer at {at} would hit {hit?.GetType().Name ?? "nothing"}, not {control.GetType().Name} {control.Name}.");
        Assert.True(control.IsEffectivelyEnabled);
        window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
        window.MouseMove(new Point(2, 2)); // no hover or tooltip left over the next picture
        ctx.Driver.Pump();
    }
}
