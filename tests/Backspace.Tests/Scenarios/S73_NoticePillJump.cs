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
/// Clicking a backup's pill on a notice scrolls its card to the top of the
/// list and outlines it for a moment, even when the card is below the fold
/// and not yet realised by the virtualising list (U036, 29 October 2026: the
/// pill did nothing). The skipped card's Last backup band is tinted orange,
/// not grey with orange text (U035).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S73_NoticePillJump : ScenarioBase
{
    protected override string ScenarioName => "S73-notice-pill-jump";
    [AvaloniaFact] public async Task Run_S73() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var source = ctx.MakeSource("files", seed: 72, targetBytes: 10_000);
        ServiceLocator.Config.Update(cfg =>
        {
            foreach (var name in new[] { "alpha", "bravo", "charlie", "delta", "echo" })
                cfg.Backups.Add(new Backup { Name = name, SourcePaths = { source }, LastRunStatus = BackupRunStatus.Success, LastRunEndUtc = DateTime.UtcNow.AddHours(-2) });
            cfg.Backups.Add(new Backup { Name = "zulu-music", SourcePaths = { source }, LastRunStatus = BackupRunStatus.Skipped, LastRunEndUtc = DateTime.UtcNow.AddHours(-1) });
        });

        using var vm = new BackupsViewModel();
        var view = new BackupsView { DataContext = vm };
        var window = ctx.Driver.Show(new Window { Width = 1100, Height = 640, Content = view, Title = "Backspace — Your backups" });
        await ctx.Driver.PumpAsync(300);

        var target = vm.Cards.Single(c => c.Name == "zulu-music");
        var pill = Assert.Single(vm.AffectedBackups);
        Assert.Equal(target.Id, pill.Id);
        var scroller = view.FindControl<ScrollViewer>("CardsScroller")!;
        Assert.Equal(0, scroller.Offset.Y);
        // Below the fold: the list has not built this card yet.
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<BackupCard>(), c => (c.DataContext as BackupCardViewModel)?.Id == target.Id && c.IsEffectivelyVisible && IsInViewport(c, scroller));
        ctx.Snapshot(window, "before", "Six backups; the notice says one was skipped and shows a zulu-music pill. The skipped card is below the fold.");

        pill.Jump.Execute(pill.Id);
        BackupCard? card = null;
        for (var n = 0; n < 50 && card is null; n++)
        {
            await ctx.Driver.PumpAsync(20);
            card = view.GetVisualDescendants().OfType<BackupCard>().FirstOrDefault(c => (c.DataContext as BackupCardViewModel)?.Id == target.Id && c.IsHighlighted);
        }
        Assert.NotNull(card);
        Assert.True(scroller.Offset.Y > 0);
        Assert.True(IsInViewport(card!, scroller));
        // The outline actually paints (a value set on the element would hide it).
        Assert.Equal(new Thickness(2), card!.FindControl<Border>("CardSurface")!.BorderThickness);
        var band = card!.FindControl<Border>("BackupSummary")!;
        Assert.Contains("skipped", band.Classes);
        ctx.Snapshot(window, "after", "After clicking the pill: the zulu-music card is scrolled to the top and outlined in the accent colour; its Last backup band is tinted orange.");

        // The outline is temporary.
        await ctx.Driver.PumpAsync(1800);
        Assert.False(card.IsHighlighted);
        Assert.Equal(new Thickness(1), card.FindControl<Border>("CardSurface")!.BorderThickness);
    }

    private static bool IsInViewport(Control control, ScrollViewer scroller)
    {
        var top = control.TranslatePoint(new Avalonia.Point(0, 0), scroller);
        return top is { } p && p.Y >= -1 && p.Y < scroller.Viewport.Height;
    }
}
