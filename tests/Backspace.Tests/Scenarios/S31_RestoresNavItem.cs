using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
/// after explicit dismissal, and the cards view must show one card per
/// live session.
///
/// The scenario opens MainWindow, drives the registry directly (engine
/// interactions are covered in other scenarios), and asserts:
///   1. Sidebar Restores radio button is HIDDEN with no live restores.
///   2. After registering one session: button becomes visible.
///   3. ActiveRestoresView renders ONE card per registered session.
///   4. After two sessions: TWO cards.
///   5. Progress property notifications update rendered numbers.
///   6. Clicking the second card opens that session, and completed
///      results remain until explicitly dismissed.
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
        // The RadioButton subtree is re-created when IsVisible flips
        // true; under session load that materialization can lag the
        // VM flag, so wait-for it rather than asserting the first look.
        Assert.True(ctx.Driver.WaitFor(() => FindRestoresRadio(win) is not null),
            "Sidebar 'Restores' RadioButton never materialized after the session landed.");
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
        ServiceLocator.ActiveRestores.UpdateProgress(sessionA, 50, 12, 1000, 240);
        ServiceLocator.ActiveRestores.UpdateProgress(sessionB, 80, 60, 1000, 750);
        Pump(8);
        Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "12 of 50 files");
        Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "60 of 80 files");
        Assert.True(mainVm.HasRunningRestores);
        ctx.Snapshot(win, "two-cards-parallel",
            "Two sessions render different live progress: 12/50 and 60/80, proving property notifications reach the cards.");

        var secondCard = win.GetVisualDescendants().OfType<Button>()
            .Single(button => ReferenceEquals(button.DataContext, sessionB) && button.Classes.Contains("card"));
        var point = secondCard.TranslatePoint(new Avalonia.Point(secondCard.Bounds.Width / 2, secondCard.Bounds.Height / 2), win)!.Value;
        win.MouseDown(point, MouseButton.Left);
        win.MouseUp(point, MouseButton.Left);
        Pump(8);
        Assert.Same(sessionB, cardsViewModel.SelectedSession);
        Assert.NotSame(sessionA, cardsViewModel.SelectedSession);
        ctx.Snapshot(win, "second-session-opened", "Clicking BackupB opens BackupB's snapshot, target and 60/80 progress; BackupA remains independent.");

        // -------- End both — results stay until explicitly dismissed --------
        ServiceLocator.ActiveRestores.UpdateProgress(sessionA, 50, 50, 1000, 1000);
        ServiceLocator.ActiveRestores.UpdateProgress(sessionB, 80, 79, 1000, 985);
        ServiceLocator.ActiveRestores.End(sessionA, RestoreTerminalStatus.Success, errorMessage: null);
        ServiceLocator.ActiveRestores.End(sessionB, RestoreTerminalStatus.Warning, errorMessage: "1 file warn");
        Pump(2);
        // Completed sessions survive the old four-second expiry.
        Assert.False(sessionA.IsRunning);
        Assert.False(sessionB.IsRunning);
        Assert.False(mainVm.HasRunningRestores);
        await Task.Delay(4500);
        Pump(8);
        Assert.Equal(2, cardsViewModel.Sessions.Count);
        ctx.Snapshot(win, "completed-results-retained", "After the former four-second expiry, both final results remain visible with Dismiss result controls.");
        Assert.True(ctx.Driver.ClickButton(win, "Dismiss result"));
        Assert.True(ctx.Driver.ClickButton(win, "Dismiss result"));
        Pump(8);
        Assert.Empty(cardsViewModel.Sessions);
        Assert.False(mainVm.HasActiveRestores);

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
