using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Restore wizard step 7 — summary. Two snapshots: a clean success
/// (no failures, just "Open target folder" + "Restore another") and
/// a partial-failure variant. Outcomes pass through the real summary
/// projection so screenshot fixtures cannot hide misleading counts or headings.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S16_RestoreSummary : ScenarioBase
{
    protected override string ScenarioName => "S16-restore-summary";

    [AvaloniaFact] public async Task Run_S16() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // Success-only summary.
        using var ok = CreateSummary(RestoreTerminalStatus.Success, 1234, 0);
        var view1 = new RestoreView { DataContext = ok };
        var host1 = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view1 };
        ctx.Driver.Show(host1);
        ctx.Driver.Pump();
        AssertRenderedSummary(host1, ok, "Restore complete", 1234, 0);
        Assert.False(ok.AnyFailures);
        Assert.Contains("finished successfully", ok.SummaryDetails);
        ctx.Snapshot(host1, "summary success",
            "All files restored — Open target folder + Restore another buttons.");
        host1.Close();

        // The real result supplies one overall explanation. Do not fabricate
        // per-file failure rows that production cannot populate.
        using var bad = CreateSummary(RestoreTerminalStatus.Warning, 1228, 6,
            "Six files could not be restored because the connection was interrupted. Your other files were restored.");
        var view2 = new RestoreView { DataContext = bad };
        var host2 = new Avalonia.Controls.Window { Width = 1100, Height = 820, Content = view2 };
        ctx.Driver.Show(host2);
        ctx.Driver.Pump();
        AssertRenderedSummary(host2, bad, "Restore needs attention", 1228, 6);
        Assert.Contains("connection was interrupted", bad.SummaryDetails);
        Assert.Contains(bad.SummaryDetails, bad.ProgressLog);
        ctx.Snapshot(host2, "summary with failures",
            "Warning heading, total 1234, restored 1228, not restored 6, and the returned explanation are visible. No fictional per-file failure list is added.");
        // Terminal failure and cancellation must never inherit the success
        // heading, even if some files were recovered before the operation stopped.
        using var failed = CreateSummary(RestoreTerminalStatus.Error, 1228, 6);
        view2.DataContext = failed;
        ctx.Driver.Pump();
        AssertRenderedSummary(host2, failed, "Restore failed", 1228, 6);
        Assert.Contains("stopped because of an error", failed.SummaryDetails);
        using var cancelled = CreateSummary(RestoreTerminalStatus.Cancelled, 1228, 6);
        view2.DataContext = cancelled;
        ctx.Driver.Pump();
        AssertRenderedSummary(host2, cancelled, "Restore cancelled", 1228, 6);
        Assert.Contains("Files already copied remain", cancelled.SummaryDetails);
        Assert.DoesNotContain("finished successfully", cancelled.SummaryDetails);
        host2.Close();

        return Task.CompletedTask;
    }

    private static RestoreViewModel CreateSummary(RestoreTerminalStatus status, int restored, int failed, string? explanation = null)
    {
        var model = new RestoreViewModel();
        typeof(RestoreViewModel).GetField("_currentOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(model, new RestoreOutcome
            {
                TerminalStatus = status,
                TotalFiles = 1234,
                RestoredCount = restored,
                FailedCount = failed,
                TargetPath = @"C:\Users\me\Documents",
                ErrorMessage = explanation,
            });
        typeof(RestoreViewModel).GetMethod("BuildSummary", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, null);
        model.CurrentStep = RestoreViewModel.Step.Summary;
        return model;
    }

    private static void AssertRenderedSummary(Window window, RestoreViewModel model, string heading, int restored, int failed)
    {
        Assert.Equal(1234, model.TotalFiles);
        Assert.Equal(restored, model.RestoredCount);
        Assert.Equal(failed, model.FailedCount);
        Assert.Equal(heading, model.SummaryHeading);
        var visibleText = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToArray();
        Assert.Contains(heading, visibleText);
        Assert.Contains("1234", visibleText);
        Assert.Contains(restored.ToString(), visibleText);
        Assert.Contains(failed.ToString(), visibleText);
        Assert.Contains(model.SummaryDetails, visibleText);
        Assert.Contains("NOT RESTORED", visibleText);
        Assert.DoesNotContain("FAILED", visibleText);
        Assert.DoesNotContain("RETRYING", visibleText);
        Assert.DoesNotContain("Failures", visibleText);
        if (failed > 0)
            Assert.DoesNotContain("Restore complete", visibleText);
        else
            Assert.DoesNotContain("Restore needs attention", visibleText);
    }
}
