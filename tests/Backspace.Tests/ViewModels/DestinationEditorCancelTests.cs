using System.Linq;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>U146 (5 November 2026): the destination of local_d, opened from the
/// backup card, was changed from Dropbox to Google Drive and the window closed
/// without saving; opening it again showed Google Drive. The card handed the
/// editor the saved settings object itself.</summary>
public sealed class DestinationEditorCancelTests
{
    [AvaloniaFact]
    public void Closing_without_saving_leaves_the_saved_destination_as_it_was()
    {
        var saved = new Destination { Name = "Dropbox - erwin_local_d", Kind = DestinationKind.DropboxAppScoped, PathOrSubpath = "local_d" };
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.RemoveAll(d => d.Id == saved.Id); cfg.Destinations.Add(saved); });
        var live = ServiceLocator.Config.Current.Destinations.Single(d => d.Id == saved.Id);

        // As the backup card does: the editor gets the object from the settings.
        var editor = new DestinationEditorViewModel(live, isNew: false);
        editor.Kind = DestinationKind.GoogleDrive;
        editor.Name = "Google Drive";
        // Closed without saving: nothing is stored. Opening it again:
        var again = new DestinationEditorViewModel(ServiceLocator.Config.Current.Destinations.Single(d => d.Id == saved.Id), isNew: false);
        Assert.Equal(DestinationKind.DropboxAppScoped, again.Kind);
        Assert.Equal("Dropbox - erwin_local_d", again.Name);
        Assert.Equal(DestinationKind.DropboxAppScoped, live.Kind);

        // Saving still stores the change, under the same identity.
        Assert.Equal(saved.Id, editor.Working.Id);
        Assert.NotSame(live, editor.Working);
    }

    [AvaloniaFact]
    public void The_copy_keeps_every_saved_field()
    {
        var d = new Destination { Name = "S3", Kind = DestinationKind.S3Compatible, S3Bucket = "b", S3Region = "r", S3Endpoint = "e",
            StoragePasswordRef = "ref", OAuthTokenRef = "tok", ExpectedDriveLetter = "E", Encrypted = true };
        var copy = DestinationEditorViewModel.CopyOf(d);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(d), System.Text.Json.JsonSerializer.Serialize(copy));
        Assert.NotSame(d, copy);
    }
}
