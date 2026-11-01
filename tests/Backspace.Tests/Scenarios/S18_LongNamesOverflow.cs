using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Stress the layout with adversarially long strings — long backup
/// name, long source path, long destination name + path. Common
/// regression source: a TextBlock that lacks TextTrimming swallows
/// the right edge of the card, or wraps onto a new line and pushes
/// the action strip off-screen. This snapshot is the layout-stress
/// regression checkpoint.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S18_LongNamesOverflow : ScenarioBase
{
    protected override string ScenarioName => "S18-long-names-overflow";

    [AvaloniaFact] public async Task Run_S18() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination
        {
            Name = "External — Toshiba Canvio Basics 4TB Portable Hard Drive (Black) — slot B",
            Kind = DestinationKind.ExternalDrive,
            PathOrSubpath = @"M:\Backups\Backspace Storage\photography-archive-2026\nested\deeply\with\many\levels",
        };
        var backup = new Backup
        {
            Name = "Photography Archive — Full RAW + Lightroom catalogues — 2014→2026",
            SourcePaths =
            {
                @"D:\Photography\2014-2026 — Lightroom Catalogues + RAW masters\Master collection (Canon R5 + Sony α7 IV)",
                @"D:\Photography\2014-2026 — Lightroom Catalogues + RAW masters\Tethered Captures — overflow folder",
                @"D:\Personal\Documents",
                @"D:\Work\Documents",
            },
            LastRunStatus = BackupRunStatus.Warning,
            LastRunSummary = "1 file skipped (locked by Lightroom) · in 12m 47s with very long summary text that should ellipsis cleanly somewhere",
            LastRunEndUtc = DateTime.UtcNow.AddMinutes(-15),
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest);
            cfg.Backups.Add(backup);
        });

        using var model = new BackupsViewModel();
        var view = new BackupsView { DataContext = model };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "card long backup name source path",
            "Long names are trimmed without hiding actions. The two Documents folders retain distinct paths.");
        using var card = new BackupCardViewModel(backup, showActions: true, showProgress: false);
        var pathLabels = view.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => ReferenceEquals(t.TextTrimming, BackupCard.SourcePathTrimming)).ToArray();
        Assert.Equal(5, pathLabels.Length);
        var destinationLabel = Assert.Single(pathLabels, t => t.Text == dest.PathOrSubpath);
        Assert.Contains(destinationLabel.TextLayout.TextLines, l => l.HasCollapsed);
        Assert.True(destinationLabel.Bounds.Width < host.Bounds.Width / 2);
        Assert.Contains(pathLabels, t => t.Text == @"D:\Personal\Documents");
        Assert.Contains(pathLabels, t => t.Text == @"D:\Work\Documents");
        Assert.All(pathLabels, t => Assert.Equal(t.Text, ToolTip.GetTip(t)));
        Assert.Contains(pathLabels, t => t.TextLayout.TextLines.Any(l => l.HasCollapsed));
        Assert.Contains(pathLabels, t => !t.TextLayout.TextLines.Any(l => l.HasCollapsed));
        backup.SourcePaths.Remove(@"D:\Work\Documents");
        card.Apply(backup);
        Assert.Contains(card.Sources, s => s.Path == @"D:\Personal\Documents");
        host.Close();
        return Task.CompletedTask;
    }
}
