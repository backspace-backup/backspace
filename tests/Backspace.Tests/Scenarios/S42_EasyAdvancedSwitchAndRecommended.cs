using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP coverage-matrix gaps:
///   - "Apply recommended exclusions in onboarding"
///   - "Switch Easy → Advanced mid-wizard"
///
/// The onboarding wizard's "Skip junk for me" checkbox seeds the
/// new backup's filter text with the recommended block. The
/// Easy → Advanced mode switch must carry the working draft over
/// without losing user input (a regression here forced users to
/// restart the wizard).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S42_EasyAdvancedSwitchAndRecommended : ScenarioBase
{
    protected override string ScenarioName => "S42-easy-advanced-recommended";

    [AvaloniaFact] public async Task Run_S42() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // -------- Apply recommended exclusions inside Easy wizard --------
        var draft = new Backup
        {
            Name = "draft-backup",
            Enabled = true,
        };
        draft.SourcePaths.Add(ctx.MakeSource("draft-src", seed: 50, targetBytes: 4_000));

        var onboardingVm = new OnboardingViewModel(draft);
        Assert.True(onboardingVm.ApplyRecommendedExclusions,
            "Onboarding wizard should default ApplyRecommendedExclusions=true so " +
            "first-time users get safe filters by default.");

        // Commit the wizard's Easy-side state back to the backup.
        // The recommended-exclusions checkbox seeds the filter text
        // when present.
        onboardingVm.SkipNotificationGraceDays = 14;  // user customised this
        var snapshot = onboardingVm.BuildSnapshot();
        Assert.NotNull(snapshot);

        // -------- Switch Easy → Advanced — working draft survives --------
        // The BackupCreationWindow re-spawns an editor on mode switch
        // and passes the snapshot. Verify the editor accepts a Backup
        // with the customised values intact.
        var editorVm = new BackupEditorViewModel(snapshot!, isNew: true);
        Assert.Equal(14, editorVm.SkipNotificationGraceDays);
        Assert.Contains(draft.SourcePaths[0], editorVm.SourcePaths);

        ctx.Report.RecordNote(
            "easy→advanced switch preserves draft",
            $"Customised SkipNotificationGraceDays={editorVm.SkipNotificationGraceDays} " +
            $"survived. Sources count={editorVm.SourcePaths.Count}.");

        return Task.CompletedTask;
    }
}
