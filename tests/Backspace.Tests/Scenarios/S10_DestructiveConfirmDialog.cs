using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Type-to-confirm dialog used by every destructive action in the
/// app: delete backup, delete destination, restore-overwriting-
/// originals, etc. The blocking pattern is "type the literal name"
/// rather than a single yes/no so muscle-memory clicks can't
/// accidentally trigger irrecoverable actions. Snapshot two
/// example invocations so the dialog's copy + accent-coloured
/// confirm button stay visually in line with the rest of the app.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S10_DestructiveConfirmDialog : ScenarioBase
{
    protected override string ScenarioName => "S10-destructive-confirm-dialog";

    [AvaloniaFact] public async Task Run_S10() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // Remove-a-backup wording, from the production builder: removing a
        // backup also deletes what it stored (U029, 29 October 2026).
        var dest = new Destination { Name = "USB drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("usb") };
        var backup = new Backup { Name = "WorkingDocs", Targets = { new BackupTarget { DestinationId = dest.Id } } };
        ServiceLocator.Config.Update(c => c.Destinations.Add(dest));
        var dlg1 = BackupsViewModel.RemovalConfirmation(backup);
        dlg1.Width = 560; dlg1.Height = 440;
        ctx.Driver.Show(dlg1);
        ctx.Snapshot(dlg1, "confirm remove backup",
            "Remove «WorkingDocs»?: every copy it stored at «USB drive» is deleted, other backups are not affected, an unconnected destination is cleaned up later; type OK.");
        var (title, detail) = BackupsViewModel.DeferredRemovalNotice("WorkingDocs", new[] { "USB drive" });
        Assert.Equal("«WorkingDocs» was removed", title);
        Assert.Contains("«USB drive» couldn't be reached yet", detail);
        dlg1.Close();

        // Restore-overwrite wording.
        var dlg2 = new DestructiveConfirmWindow(
            title: "Restore will overwrite the source folder",
            subtitle:
                "Files at C:\\Users\\me\\Documents will be replaced with their state " +
                "at revision 12. There's no undo.",
            confirmString: "OK",
            caseInsensitive: true)
        { Width = 560, Height = 420 };
        ctx.Driver.Show(dlg2);
        ctx.Snapshot(dlg2, "confirm restore overwrite",
            "Restore-to-original-source guardrail — universal OK token.");
        dlg2.Close();

        return Task.CompletedTask;
    }
}
