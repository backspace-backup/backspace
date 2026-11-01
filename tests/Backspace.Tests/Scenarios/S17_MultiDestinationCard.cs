using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
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
/// Backup card with local destinations and three cloud providers — exercises the
/// destination-pill wrapping logic in the BackupCard. The card
/// should grow vertically as the pill row wraps, never overflow
/// horizontally, and keep the right-side action strip aligned
/// with the card title rather than drifting down.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S17_MultiDestinationCard : ScenarioBase
{
    protected override string ScenarioName => "S17-multi-destination-card";

    [AvaloniaFact] public async Task Run_S17() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var d1 = new Destination { Name = "Local — primary",   Kind = DestinationKind.LocalFolder,      PathOrSubpath = ctx.MakeLocalDestination("p") };
        var d2 = new Destination { Name = "External — passport", Kind = DestinationKind.ExternalDrive,  PathOrSubpath = ctx.MakeLocalDestination("e") };
        var d3 = new Destination { Name = "Dropbox — work",    Kind = DestinationKind.DropboxAppScoped, PathOrSubpath = "store-1" };
        var d4 = new Destination { Name = "S3 — Cloudflare R2", Kind = DestinationKind.S3Compatible,    PathOrSubpath = "prefix",
                                   S3Endpoint = "abc.r2.cloudflarestorage.com", S3Bucket = "bucket" };
        var d5 = new Destination { Name = "Google Drive — personal", Kind = DestinationKind.GoogleDrive, PathOrSubpath = "private-app-data" };
        var d6 = new Destination { Name = "OneDrive — personal", Kind = DestinationKind.OneDrivePersonal, PathOrSubpath = "app-folder" };
        var backup = new Backup
        {
            Name = "BigFanout",
            SourcePaths = { ctx.MakeSource("docs", seed: 1, targetBytes: 200_000) },
            LastRunStatus = BackupRunStatus.Success,
            LastRunSummary = "4 cells · in 2m 14s",
            LastRunEndUtc = DateTime.UtcNow.AddMinutes(-30),
        };
        backup.Targets.Add(new BackupTarget { DestinationId = d1.Id, StorageName = "p" });
        backup.Targets.Add(new BackupTarget { DestinationId = d2.Id, StorageName = "e" });
        backup.Targets.Add(new BackupTarget { DestinationId = d3.Id, StorageName = "drop" });
        backup.Targets.Add(new BackupTarget { DestinationId = d4.Id, StorageName = "s3"  });
        backup.Targets.Add(new BackupTarget { DestinationId = d5.Id, StorageName = "google" });
        backup.Targets.Add(new BackupTarget { DestinationId = d6.Id, StorageName = "one" });

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(d1);
            cfg.Destinations.Add(d2);
            cfg.Destinations.Add(d3);
            cfg.Destinations.Add(d4);
            cfg.Destinations.Add(d5);
            cfg.Destinations.Add(d6);
            cfg.Backups.Add(backup);
        });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        var pills = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("dest-pill")).ToArray();
        Assert.Equal(6, pills.Length);
        foreach (var (destination, resource) in new[] { (d3, "Icon.BrandDropbox"), (d5, "Icon.BrandGoogleDrive"), (d6, "Icon.BrandOneDrive") })
        {
            var pill = Assert.Single(pills, p => p.CommandParameter as string == destination.Id);
            Assert.Contains(pill.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == destination.Name);
            var icon = Assert.Single(pill.GetVisualDescendants().OfType<PathIcon>());
            Assert.Same(Application.Current!.FindResource(resource), icon.Data);
        }
        ctx.Snapshot(host, "card local and cloud destinations",
            "Local destinations show paths; Dropbox, Google Drive and OneDrive keep their names and recognizable provider icons.");
        var destinationScroller = pills[0].GetVisualAncestors().OfType<ScrollViewer>().First();
        destinationScroller.Offset = new Vector(0, destinationScroller.Extent.Height);
        ctx.Driver.Pump(); host.UpdateLayout();
        var lastPill = pills.Single(p => p.CommandParameter as string == d6.Id);
        var lastPosition = lastPill.TranslatePoint(default, destinationScroller)!.Value;
        Assert.InRange(lastPosition.Y, 0, destinationScroller.Viewport.Height - lastPill.Bounds.Height + 1);
        ctx.Snapshot(host, "cloud providers after scrolling",
            "The destination list scrolls independently: all three cloud provider icons are visible and the action row stays in place.");
        host.Close();
        return Task.CompletedTask;
    }
}
