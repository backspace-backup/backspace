using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U104: both stop conditions read "Stop mid-run if …". U107: the storage
/// password text says what ON and OFF mean. U108: a destination's Options
/// button lists only its own options, so Google Drive no longer mentions an
/// S3 region.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S80_EditorWording : ScenarioBase
{
    protected override string ScenarioName => "S80-editor-wording";

    [AvaloniaFact] public async Task Run_S80() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        Options(ctx, DestinationKind.GoogleDrive, "(name)", "google drive options");
        Options(ctx, DestinationKind.S3Compatible, "(name, region, bucket prefix)", "s3 options");

        // U116: the drive name and the optional letter are both visible, with
        // a line saying what they mean together.
        var drive = new DestinationEditorViewModel(new Destination
        {
            Kind = DestinationKind.ExternalDrive, PathOrSubpath = @"M:\Backup\Backspace\local_dropbox",
            ExpectedDriveLetter = "M:", Encrypted = true,
        }, false);
        var driveWin = new DestinationEditorWindow { DataContext = drive, Width = 760, Height = 760 };
        ctx.Driver.Show(driveWin);
        ctx.Driver.Pump();
        Assert.Equal("Backs up to whatever drive is at M:. Add the drive's name so that another drive given this letter isn't used by mistake.", drive.VolumeMatchSummary);
        drive.ExpectedVolumeLabel = "My Passport";
        Assert.Equal("Backs up only when the drive at M: is named “My Passport”.", drive.VolumeMatchSummary);
        drive.ExpectedDriveLetter = "";
        ctx.Driver.Pump();
        Assert.Equal("Backs up to the drive named “My Passport”, whatever letter Windows gives it.", drive.VolumeMatchSummary);
        var summary = driveWin.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == drive.VolumeMatchSummary);
        summary.BringIntoView();
        ctx.Driver.Pump();
        ctx.Snapshot(driveWin, "drive name and letter",
            "External drive: DRIVE NAME 'My Passport' and LETTER (OPTIONAL) empty; the line below says it backs up to the drive named My Passport whatever its letter.");
        driveWin.Close();

        // U117: the question asked when a drive should hold the backup but doesn't.
        var ask = new DriveChoiceWindow("This drive doesn't have this backup",
            "«local_dropbox» has backed up to «My Passport» before, but the drive attached now has no backup at M:\\Backup\\Backspace\\local_dropbox. "
            + "It may be a different drive with the same name, or the drive was erased.\n\n"
            + "Start a new backup on this drive, or set this drive aside for this backup: Backspace then skips it for «local_dropbox» and doesn't ask again. "
            + "Other backups and other computers decide for themselves.",
            "Start a new backup here", "Don't use this drive") { Width = 580, Height = 330 };
        ctx.Driver.Show(ask);
        ctx.Driver.Pump();
        ctx.Snapshot(ask, "drive without the backup",
            "The question: 'This drive doesn't have this backup', with Not now, Don't use this drive and Start a new backup here.");
        ask.Close();

        var backup = new Backup { Name = "local_c" };
        backup.SourcePaths.Add(ctx.MakeSource("src", targetBytes: 10_000));
        var vm = new BackupEditorViewModel(backup, isNew: false);
        var win = new BackupEditorWindow { DataContext = vm, Width = 1000, Height = 980 };
        ctx.Driver.Show(win);
        Assert.True(ctx.Driver.WaitFor(() => win.GetVisualDescendants().OfType<TabControl>().Any()));
        win.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
        ctx.Driver.Pump();
        var stops = win.GetVisualDescendants().OfType<CheckBox>()
            .Where(c => c.Content is string s && s.StartsWith("Stop mid-run if ")).ToList();
        Assert.Equal(new[] { "Stop mid-run if the computer switches to battery", "Stop mid-run if the connection becomes metered" },
            stops.Select(c => (string)c.Content!));
        stops.Last().BringIntoView();
        ctx.Driver.Pump();
        ctx.Snapshot(win, "stop conditions", "Schedule: the two stopping rules read 'Stop mid-run if the computer switches to battery' and 'Stop mid-run if the connection becomes metered'.");
        win.Close();
        vm.Dispose();
        return Task.CompletedTask;
    }

    private static void Options(ScenarioContext ctx, DestinationKind kind, string hint, string shot)
    {
        var vm = new DestinationEditorViewModel(new Destination { Kind = kind, Encrypted = true }, false);
        var win = new DestinationEditorWindow { DataContext = vm, Width = 760, Height = 760 };
        ctx.Driver.Show(win);
        ctx.Driver.Pump();
        Assert.Equal(hint, vm.OptionsHint);
        var toggle = win.FindControl<Button>("OptionsToggleButton")!;
        toggle.BringIntoView();
        ctx.Driver.Pump();
        Assert.Contains(toggle.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == hint);
        ctx.Snapshot(win, shot, $"{kind}: the Options button reads '{hint}', with the storage password explanation above it.");
        win.Close();
    }
}
