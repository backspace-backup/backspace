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
/// Regression for ROADMAP P1 "Status text next to 'Apply recommended
/// exclusions' is cropped". The long zero-savings message ("Nothing in
/// your sources matched the recommended rules...") used to sit beside
/// the button inside a horizontal StackPanel, which measures children
/// at infinite width — so its TextWrapping never engaged and the line
/// was hard-clipped at the card edge. The row is now a Grid with a
/// star column, so the message wraps.
///
/// Asserts the two observable outcomes: the TextBlock is tall enough
/// to hold at least two wrapped lines, and its right edge stays inside
/// the window. Screenshot captures the wrapped row for eyes-on
/// validation.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S50_RecommendedRowTextWraps : ScenarioBase
{
    protected override string ScenarioName => "S50-recommended-row-text-wraps";

    [AvaloniaFact] public async Task Run_S50() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // isNew:false so the constructor doesn't kick off a live
        // savings estimate that would race our hand-set label state.
        var backup = new Backup { Name = "wrap-proof" };
        backup.SourcePaths.Add(System.IO.Path.Combine(ctx.RootDir, "narrow-src"));
        var vm = new BackupEditorViewModel(backup, isNew: false)
        {
            // Zero savings over a non-empty scan → the long friendly
            // message, the exact string the user reported as cropped.
            RecEstimatedSavingsBytes = 0,
            RecScannedBytes = 123_456,
            RecEstimatedTruncated = false,
        };
        Assert.StartsWith("Nothing in your sources matched", vm.RecEstimatedSavingsLabel);

        var win = new BackupEditorWindow { DataContext = vm, Width = 980, Height = 820 };
        ctx.Driver.Show(win);

        // Overview=0, Schedule=1, Filters=2. Recommended sub-tab is
        // selected by default inside Filters. Template application can
        // lag Show() when the session is busy, so wait-for rather than
        // asserting on the first lookup.
        Assert.True(ctx.Driver.WaitFor(() =>
                win.GetVisualDescendants().OfType<TabControl>().Any()),
            "BackupEditorWindow never populated its TabControl.");
        var tabs = win.GetVisualDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 2;
        ctx.Driver.Pump();

        Assert.True(ctx.Driver.WaitFor(() =>
                win.GetVisualDescendants().OfType<TextBlock>().Any(
                    t => t.Text?.StartsWith("Nothing in your sources matched") == true)),
            "Savings status label never appeared on the Filters tab.");
        var label = win.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text?.StartsWith("Nothing in your sources matched") == true);
        Assert.NotNull(label);
        Assert.True(label!.IsVisible, "Savings status label should be visible.");

        // Wrapped: at 12px font a single line is ~16-18px tall; the
        // ~150-char message beside the button must occupy >= 2 lines.
        Assert.True(label.Bounds.Height >= 28,
            $"Label should wrap to multiple lines; height was {label.Bounds.Height:F0}px " +
            "(single-line height means the horizontal-StackPanel crop regressed).");

        // Not clipped: the label was measured narrower than the window
        // (pre-fix, the horizontal StackPanel measured it at its full
        // ~1300px single-line width and the excess was clipped).
        Assert.True(label.Bounds.Width < win.Bounds.Width,
            $"Label measured wider than the window ({label.Bounds.Width:F0}px vs " +
            $"{win.Bounds.Width:F0}px): the infinite-width measure is back.");

        ctx.Snapshot(win, "savings message wraps beside button",
            "Filters > Recommended: the 'Nothing in your sources matched…' message wraps " +
            "onto two+ lines next to 'Apply recommended exclusions' instead of running off " +
            "the right edge of the card.");

        win.Close();
        return Task.CompletedTask;
    }
}
