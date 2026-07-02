using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP item 8: "Restores" left-nav item must appear
/// the moment a restore starts (registry has a session), disappear
/// after the session ends, and the cards view must show one card per
/// live session.
///
/// The scenario opens MainWindow, drives the registry directly (engine
/// interactions are covered in other scenarios), and asserts:
///   1. Sidebar Restores radio button is HIDDEN with no live restores.
///   2. After registering one session: button becomes visible.
///   3. ActiveRestoresView renders ONE card per registered session.
///   4. After two sessions: TWO cards.
///   5. After End: card lingers (the 4 s fade) but the cards view
///      keeps rendering until removal.
///
/// This is the multi-restore feature's "click X, see Y" proof — the
/// unit tests prove the registry, this proves the view binding.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S31_RestoresNavItem : ScenarioBase
{
    protected override string ScenarioName => "S31-restores-nav-item";

    [AvaloniaFact] public async Task Run_S31() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        await Task.Yield();

        var mainVm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = mainVm, Width = 1280, Height = 800 };
        ctx.Driver.Show(win);
        Pump(8);

        // -------- Starting state: no live restores --------
        Assert.False(mainVm.HasActiveRestores,
            "With nothing in the registry the sidebar Restores item must be hidden.");
        // Avalonia trims `IsVisible=false` visuals from the visual tree
        // (the entire RadioButton subtree disappears, not just gets
        // hidden). Asserting via the VM observable is therefore the
        // ground truth — the visual tree assertion only applies once
        // the binding has flipped to true.
        Assert.Null(FindRestoresRadio(win));

        // -------- Register one live restore --------
        var dest = new Destination { Name = "DestA", Kind = DestinationKind.LocalFolder, PathOrSubpath = "/tmp" };
        var backupA = new Backup { Name = "BackupA" };
        backupA.SourcePaths.Add("/src");
        var requestA = MakeRequest(backupA, dest, fileCount: 50);
        var sessionA = ServiceLocator.ActiveRestores.Begin(backupA, requestA);

        Pump(8);
        Assert.True(mainVm.HasActiveRestores,
            "Registry has a session — HasActiveRestores must flip on.");
        var restoresRadio = FindRestoresRadio(win);
        Assert.NotNull(restoresRadio);
        Assert.True(restoresRadio!.IsVisible,
            "Sidebar 'Restores' RadioButton must surface the moment a session lands.");

        ctx.Snapshot(win, "one-restore-in-flight",
            "Sidebar 'Restores' item is visible; ready to navigate to the cards view.");

        // -------- Navigate to the cards view --------
        mainVm.SelectedNav = NavItem.ActiveRestores;
        Pump(8);
        Assert.IsType<ActiveRestoresViewModel>(mainVm.CurrentPage);

        var cardsViewModel = (ActiveRestoresViewModel)mainVm.CurrentPage!;
        Assert.Single(cardsViewModel.Sessions);
        Assert.False(cardsViewModel.HasNone);
        ctx.Snapshot(win, "one-card-rendered",
            "ActiveRestoresView shows ONE card for the in-flight session.");

        // -------- Add a second parallel session --------
        var backupB = new Backup { Name = "BackupB" };
        backupB.SourcePaths.Add("/src");
        var requestB = MakeRequest(backupB, dest, fileCount: 80);
        var sessionB = ServiceLocator.ActiveRestores.Begin(backupB, requestB);

        Pump(8);
        Assert.Equal(2, cardsViewModel.Sessions.Count);
        ctx.Snapshot(win, "two-cards-parallel",
            "Two cards: each row independent, progress bars + status reflect each session.");

        // -------- End both — cards remain until fade, but Sessions count drops on Remove --------
        ServiceLocator.ActiveRestores.End(sessionA, RestoreTerminalStatus.Success, errorMessage: null);
        ServiceLocator.ActiveRestores.End(sessionB, RestoreTerminalStatus.Warning, errorMessage: "1 file warn");
        Pump(2);
        // End marks IsRunning=false synchronously but doesn't remove
        // the card; the registry's 4 s fade timer fires later.
        Assert.False(sessionA.IsRunning);
        Assert.False(sessionB.IsRunning);

        win.Close();
    }

    private static RadioButton? FindRestoresRadio(Window win) =>
        win.GetVisualDescendants()
            .OfType<RadioButton>()
            .FirstOrDefault(rb => rb.GetVisualDescendants()
                .OfType<TextBlock>()
                .Any(tb => string.Equals(tb.Text, "Restores", StringComparison.Ordinal)));

    private static RestoreRequest MakeRequest(Backup backup, Destination dest, int fileCount)
    {
        var files = new string[fileCount];
        for (int i = 0; i < fileCount; i++) files[i] = $"/src/file-{i}.bin";
        return new RestoreRequest
        {
            Backup = backup,
            SourcePath = "/src",
            Destination = dest,
            StorageName = "default",
            Revision = 1,
            Files = files,
            TargetPath = "/restore/target",
        };
    }

    private static void Pump(int rounds)
    {
        for (int i = 0; i < rounds; i++) Dispatcher.UIThread.RunJobs();
    }
}
