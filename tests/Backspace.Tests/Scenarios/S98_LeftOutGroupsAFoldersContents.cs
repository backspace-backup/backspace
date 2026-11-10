using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U181 (10 November 2026): on the owner's local_c, Left out listed every
/// subfolder of Dropbox as its own row, because <c>?:/Users/*/Dropbox/*</c>
/// leaves out the folder's contents while keeping the folder. Items of one
/// folder left out by the same rule are now one row ("3 folders and 1 file
/// inside, the folder itself kept"), opened with Show to list them, each
/// with Keep this. A rule whose matches sit in different folders (here
/// <c>*.tmp</c>) keeps one row per item.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S98_LeftOutGroupsAFoldersContents : ScenarioBase
{
    protected override string ScenarioName => "S98-left-out-groups-a-folders-contents";

    [AvaloniaFact] public async Task Run_S98() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Path.Combine(ctx.RootDir, "home");
        void Write(string relative, int bytes)
        {
            var path = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }
        Write("Dropbox/Projects/app/main.cs", 300_000);
        Write("Dropbox/Projects/app/readme.md", 1_000);
        Write("Dropbox/Photos/2026/a.jpg", 2_000_000);
        Write("Dropbox/Music/song.mp3", 900_000);
        Write("Dropbox/notes.txt", 500);
        Write("draft.tmp", 40_000);
        Write("docs/old.tmp", 20_000);
        Write("docs/report.docx", 30_000);
        // Like ?:/Program Files*/*/*: each program's files left out, the program folders kept.
        Write("Programs/AppA/a.dll", 10_000);
        Write("Programs/AppA/sub/b.dll", 5_000);
        Write("Programs/AppB/c.dll", 8_000);
        Write("Programs/AppB/d.dll", 2_000);
        Write("Programs/AppC/e.dll", 1_000);

        var backup = new Backup { Name = "Home", SourcePaths = { source }, FiltersText = "Dropbox/*\n*.tmp\nPrograms/*/*" };
        ServiceLocator.Config.Update(c => c.Backups.Add(backup));
        var win = new BackupEditorWindow(ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id), isNew: false) { Width = 1100, Height = 760 };
        ctx.Driver.Show(win);
        var vm = (BackupEditorViewModel)win.DataContext!;
        win.FindControl<TabControl>("EditorTabs")!.SelectedItem = win.FindControl<TabItem>("FiltersTab");
        win.FindControl<TabControl>("FiltersSubTabs")!.SelectedItem = win.FindControl<TabItem>("AdvancedFiltersTab");
        win.FindControl<TabControl>("AdvancedSubTabs")!.SelectedItem = win.FindControl<TabItem>("LeftOutTab");
        await ctx.Driver.PumpAsync(150);
        Click(ctx, win, win.FindControl<Button>("RunLeftOut")!);
        Assert.True(ctx.Driver.WaitFor(() => vm.HasLeftOutResult, 20_000), "Left out never finished.");
        await ctx.Driver.PumpAsync(150);

        // The analyzer still finds each topmost item...
        Assert.Equal(11, vm.LeftOutResult!.Items.Count);
        // ...but Dropbox's contents are one row, first by size; the two *.tmp files stay apart;
        // Programs is one row gathering its program folders.
        var top = vm.LeftOutRows.Where(r => r is LeftOutRow or LeftOutGroupRow).ToList();
        Assert.Equal(4, top.Count);
        var group = Assert.IsType<LeftOutGroupRow>(top[0]);
        Assert.Equal(4, group.Children.Count);
        Assert.Equal((5L, 3_201_500L, "Dropbox/*"), (group.Files, group.Bytes, group.Rule.Rule));
        Assert.Equal("3 folders and 1 file inside, the folder itself kept · 5 files in all", group.Kind);
        Assert.EndsWith(Path.Combine("home", "Dropbox") + Path.DirectorySeparatorChar + "…", group.Shown);
        Assert.Equal(new[] { "draft.tmp", "old.tmp" }, top.OfType<LeftOutRow>().Select(r => Path.GetFileName(r.Path)));
        var programs = Assert.IsType<LeftOutGroupRow>(top[2]);
        Assert.Equal((5L, 26_000L, "Programs/*/*", 3), (programs.Files, programs.Bytes, programs.Rule.Rule, programs.Children.Count));
        Assert.Equal("What 3 folders inside hold, those folders kept · 5 files in all", programs.Kind);
        Assert.EndsWith(Path.Combine("home", "Programs") + Path.DirectorySeparatorChar + "…", programs.Shown);
        Assert.Equal("4 items (12 files), 3.1 MB left out", vm.LeftOutTotal);
        ctx.Snapshot(win, "dropbox-contents-one-row",
            "Left out: one row 'home\\Dropbox\\…' (3 folders and 1 file inside, the folder itself kept · 5 files in all, 3.1 MB, Dropbox/*), draft.tmp, one row 'home\\Programs\\…' (What 3 folders inside hold, those folders kept · 5 files in all, Programs/*/*), and docs\\old.tmp; each group with Show.");

        Button Toggle(LeftOutGroupRow g) => win.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "GroupToggle" && ReferenceEquals(b.DataContext, g));

        // Show lists the members under the row, indented, each with Keep this.
        Click(ctx, win, Toggle(group));
        await ctx.Driver.PumpAsync(100);
        Assert.True(group.Expanded);
        var members = vm.LeftOutRows.Skip(1).Take(4).Cast<LeftOutRow>().ToList();
        Assert.All(members, m => Assert.Equal(1, m.Depth));
        Assert.Equal(new[] { "Photos", "Music", "Projects", "notes.txt" }, members.Select(m => Path.GetFileName(m.Path)));
        Assert.Equal("Hide", group.ToggleText);

        // Programs opens level by level: its program folders, then one program's files.
        Click(ctx, win, Toggle(programs));
        await ctx.Driver.PumpAsync(100);
        var appA = programs.Children.OfType<LeftOutGroupRow>().Single(g => Path.GetFileName(g.Parent) == "AppA");
        Assert.Equal(1, appA.Depth);
        Click(ctx, win, Toggle(appA));
        await ctx.Driver.PumpAsync(100);
        var aRows = appA.Children.Cast<LeftOutRow>().ToList();
        Assert.All(aRows, m => Assert.Equal(2, m.Depth));
        Assert.All(aRows, m => Assert.Contains(m, vm.LeftOutRows));
        ctx.Snapshot(win, "groups-opened",
            "Dropbox opened (Photos, Music, Projects, notes.txt indented once) and Programs opened: AppA and AppB rows indented once, AppC's single file too; AppA opened again shows a.dll and sub indented twice, each with Keep this.");

        // Keep this on a member works as on any row.
        var music = members.Single(m => Path.GetFileName(m.Path) == "Music");
        var keep = win.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Keep this" && ReferenceEquals(b.DataContext, music));
        Click(ctx, win, keep);
        Assert.True(music.Kept);
        Assert.EndsWith(BackupEditorViewModel.KeepRule(music.Path) + "\n", vm.FiltersText);

        // Search finds a member inside a closed group.
        Click(ctx, win, Toggle(group));
        var search = win.FindControl<TextBox>("LeftOutSearch")!;
        search.Text = "Photos";
        await ctx.Driver.PumpAsync(100);
        Assert.False(group.Expanded);
        Assert.Equal(new object[] { group, members[0] }, vm.LeftOutRows.Take(2).ToArray());
        Assert.Equal(2, vm.LeftOutRows.Count(r => r is LeftOutRow or LeftOutGroupRow));
    }

    private static void Click(ScenarioContext ctx, Window window, Control control)
    {
        control.BringIntoView(); ctx.Driver.Pump(); window.UpdateLayout();
        Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        var hit = window.InputHitTest(at);
        Assert.True(ReferenceEquals(hit, control) || hit is Visual visual && visual.GetVisualAncestors().Contains(control),
            $"The pointer at {at} would hit {hit?.GetType().Name ?? "nothing"}, not {control.GetType().Name} {control.Name}.");
        window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
        window.MouseMove(new Point(2, 2));
        ctx.Driver.Pump();
    }
}
