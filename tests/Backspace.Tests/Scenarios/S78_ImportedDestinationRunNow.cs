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
/// U082: an imported Duplicacy backup to Dropbox has no sign-in and no storage
/// password yet. Run now opens that destination with a note saying what to
/// finish, instead of starting a run that fails four times on restic's
/// "an empty password is not allowed".
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S78_ImportedDestinationRunNow : ScenarioBase
{
    protected override string ScenarioName => "S78-imported-destination-run-now";

    [AvaloniaFact] public async Task Run_S78() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var imported = new Destination { Name = "Dropbox - erwin_local_c", Kind = DestinationKind.DropboxAppScoped, Encrypted = true, PathOrSubpath = "erwin_local_c" };
        var backup = new Backup { Name = "local_c", SourcePaths = { ctx.MakeSource("c", targetBytes: 20_000) } };
        backup.Targets.Add(new BackupTarget { DestinationId = imported.Id, StorageName = "dropbox" });
        ServiceLocator.Config.Update(c => { c.Destinations.Add(imported); c.Backups.Add(backup); });

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1100, Height = 760, DataContext = main });
        await ctx.Driver.PumpAsync(200);
        main.Backups.TestRestoreOwner = () => host; // headless tests have no desktop main window
        var card = host.GetVisualDescendants().OfType<BackupCard>().Single();
        var runNow = card.GetVisualDescendants().OfType<Button>().Single(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Run now"));
        runNow.Command!.Execute(runNow.CommandParameter);
        Assert.True(ctx.Driver.WaitFor(() => host.OwnedWindows.OfType<DestinationEditorWindow>().Any()),
            "Run now did not open the unfinished destination.");
        var editor = host.OwnedWindows.OfType<DestinationEditorWindow>().Single();
        var vm = (DestinationEditorViewModel)editor.DataContext!;
        Assert.Equal("To back up to «Dropbox - erwin_local_c», first connect your Dropbox account and choose a storage password, then save and run the backup again.", vm.SetupNotice);
        Assert.True(editor.FindControl<TextBlock>("SetupNoticeText")!.IsEffectivelyVisible);
        Assert.False(ServiceLocator.Orchestrator.IsRunning(backup.Id));
        Assert.Empty(ServiceLocator.Logs.LoadTimelineByBackupId(backup.Id));
        ctx.Snapshot(editor, "editor opened by run now",
            "The Dropbox destination opens with a note: connect your Dropbox account and choose a storage password, then run the backup again. No run was started.");
        editor.Close();
        await ctx.Driver.PumpAsync(100);
        Assert.False(ServiceLocator.Orchestrator.IsRunning(backup.Id));
    }
}
