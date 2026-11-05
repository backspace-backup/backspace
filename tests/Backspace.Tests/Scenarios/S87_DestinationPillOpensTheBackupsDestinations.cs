using System;
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
/// U147 (5 November 2026): clicking a backup's destination on its card opened a
/// form for that one destination, which looked like setting up a new one. It
/// now opens the backup's own destinations step, where an existing destination
/// is picked or edited.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S87_DestinationPillOpensTheBackupsDestinations : ScenarioBase
{
    protected override string ScenarioName => "S87-destination-pill-opens-the-backups-destinations";

    [AvaloniaFact] public async Task Run_S87() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dropbox = new Destination { Name = "Dropbox - erwin_local_d", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("dropbox") };
        var drive = new Destination { Name = "Google Drive - backspace", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("drive") };
        var backup = new Backup { Name = "local_d", SourcePaths = { ctx.MakeSource("d", targetBytes: 4096) }, LastEditMode = BackupEditMode.Easy };
        backup.Targets.Add(new BackupTarget { DestinationId = dropbox.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dropbox); cfg.Destinations.Add(drive); cfg.Backups.Add(backup); });

        using var mainVm = new MainWindowViewModel();
        var main = ctx.Driver.Show(new MainWindow { DataContext = mainVm, Width = 1100, Height = 800 });
        mainVm.Backups.TestRestoreOwner = () => main; // headless tests have no desktop main window
        await ctx.Driver.PumpAsync(300);
        var card = (BackupCardViewModel)main.GetVisualDescendants().OfType<BackupCard>().Single().DataContext!;
        var pill = main.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, card.EditDestinationCommand));
        pill.BringIntoView(); main.UpdateLayout();
        var at = pill.TranslatePoint(new Point(pill.Bounds.Width / 2, pill.Bounds.Height / 2), main)!.Value;
        main.MouseDown(at, MouseButton.Left); main.MouseUp(at, MouseButton.Left);
        await ctx.Driver.PumpAsync(400);

        var steps = main.OwnedWindows.OfType<OnboardingWindow>().SingleOrDefault();
        Assert.NotNull(steps);
        Assert.Empty(main.OwnedWindows.OfType<DestinationEditorWindow>());
        var vm = (OnboardingViewModel)steps!.DataContext!;
        Assert.Equal(2, vm.CurrentStep);
        ctx.Snapshot(steps, "the backups destinations step",
            "Clicking «Dropbox - erwin_local_d» on local_d's card opens local_d's own destinations step: its current destination, with the existing «Google Drive - backspace» available to pick, instead of a form for one destination.");
        steps.Close();
        await ctx.Driver.PumpAsync(200);
        // Closing without saving changes nothing.
        Assert.Equal(dropbox.Id, ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id).Targets.Single().DestinationId);
        main.Close();
    }
}
