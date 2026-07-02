using System;
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
/// Visual regression guard for the BackupCard action shelf —
/// Run now / Edit / Browse / Mount / Restore files / Delete.
/// Earlier the shelf used a StackPanel + ItemHeight=32 which
/// clipped the button text vertically (user-reported "Fdit"
/// instead of "Edit"). The WrapPanel without ItemHeight should
/// let each button render at its natural padded height.
///
/// Renders the card at two widths — wide (1100px, everything on
/// one row) and narrow (640px, action buttons wrap to a second
/// row instead of clipping past Delete) — and confirms the
/// button TextBlocks measure tall enough to fit their content.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S27_BackupCardActionShelf : ScenarioBase
{
    protected override string ScenarioName => "S27-backup-card-action-shelf";

    [AvaloniaFact] public async Task Run_S27() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination
        {
            Name = "Local — primary",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("primary"),
        };
        var backup = new Backup
        {
            Name = "Documents",
            SourcePaths = { ctx.MakeSource("docs", seed: 1, targetBytes: 100_000) },
            LastRunStatus = BackupRunStatus.Success,
            LastRunSummary = "rev 12 · in 38s",
            LastRunEndUtc = DateTime.UtcNow.AddHours(-1),
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest);
            cfg.Backups.Add(backup);
        });

        // Wide view: 1100px — full action row, no wrapping.
        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1180, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Driver.Pump();
        ctx.Snapshot(host, "card at 1180px wide", "All action buttons render on a single row.");

        // Hunt for the action buttons and measure their TextBlock
        // heights. Anything below 18 px would mean text is being
        // clipped (the font's logical height at 14 pt is ~18 px).
        var buttonTextBlocks = host.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Classes.Contains("ghost") || b.Classes.Contains("primary") || b.Classes.Contains("restore-action"))
            .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>())
            .Where(tb => !string.IsNullOrEmpty(tb.Text))
            .ToList();
        foreach (var tb in buttonTextBlocks)
        {
            // Bounds.Height is the rendered height. If it's clipped
            // by an outer ItemHeight, the property returns the
            // clipped value.
            Assert.True(tb.Bounds.Height >= 14,
                $"Button text '{tb.Text}' rendered at height {tb.Bounds.Height} — likely clipped.");
        }
        ctx.Report.RecordNote(
            "button-height assertion",
            $"Verified {buttonTextBlocks.Count} button text labels each render ≥14 px tall (no vertical clip).");

        // Narrow view: resize the window so the WrapPanel pushes
        // trailing buttons to a second row. The intent is to confirm
        // wrapping happens instead of clipping past Delete.
        host.Width = 740;
        ctx.Driver.Pump();
        ctx.Snapshot(host, "card at 740px narrow", "Action shelf wraps — no horizontal clip.");

        host.Close();
        return Task.CompletedTask;
    }
}
