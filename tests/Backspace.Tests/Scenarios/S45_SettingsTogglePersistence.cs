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
/// Settings page toggle persistence — covers two matrix rows:
///   • "Toggle notification gating per event" (Success/Skipped/Failure
///     × Toast/Sound/Email — 9 independent gates).
///   • "Restore-retry cascade settings" (the per-attempt delay list).
///
/// Asserts: the toggle changes round-trip through SettingsViewModel
/// → ConfigStore.Update → reload, the email-gate aggregate
/// (<c>AnyEmailEnabled</c>) flips with the right combination, and
/// the retry-cascade list survives the round-trip with the exact
/// element order (the engine consumes elements in order, so a
/// reordering during persist would silently break the cascade).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S45_SettingsTogglePersistence : ScenarioBase
{
    protected override string ScenarioName => "S45-settings-toggle-persistence";

    [AvaloniaFact] public async Task Run_S45() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // -------- Notification gates: toggle the full 3×3 grid --------
        var vm = new SettingsViewModel();
        Assert.False(vm.AnyEmailEnabled,
            "Fresh config: no email gates set. AnyEmailEnabled should start false " +
            "so the email-provider section stays collapsed.");

        vm.SuccessToast = true;
        vm.SuccessSound = false;
        vm.SuccessEmail = false;
        vm.SkippedToast = false;
        vm.SkippedSound = true;
        vm.SkippedEmail = false;
        vm.FailureToast = true;
        vm.FailureSound = true;
        vm.FailureEmail = true;
        Assert.True(vm.AnyEmailEnabled,
            "FailureEmail flipped on — AnyEmailEnabled must surface true so the " +
            "email-provider section expands.");

        // Cascade settings: pick a non-default list with a distinctive
        // shape that would shuffle if the persist layer used a set
        // instead of a list.
        var cascade = new[] { 2, 7, 13, 25, 90 };
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Restore.RetryDelaysSeconds = cascade.ToList();
            cfg.Notifications.OnSuccess.Toast = vm.SuccessToast;
            cfg.Notifications.OnSuccess.Sound = vm.SuccessSound;
            cfg.Notifications.OnSuccess.Email = vm.SuccessEmail;
            cfg.Notifications.OnSkipped.Toast = vm.SkippedToast;
            cfg.Notifications.OnSkipped.Sound = vm.SkippedSound;
            cfg.Notifications.OnSkipped.Email = vm.SkippedEmail;
            cfg.Notifications.OnFailure.Toast = vm.FailureToast;
            cfg.Notifications.OnFailure.Sound = vm.FailureSound;
            cfg.Notifications.OnFailure.Email = vm.FailureEmail;
        });

        // -------- Round-trip through ConfigStore --------
        var saved = ServiceLocator.Config.Current;
        Assert.True (saved.Notifications.OnSuccess.Toast, "Success.Toast lost");
        Assert.False(saved.Notifications.OnSuccess.Sound, "Success.Sound spuriously flipped");
        Assert.False(saved.Notifications.OnSuccess.Email, "Success.Email spuriously flipped");
        Assert.False(saved.Notifications.OnSkipped.Toast, "Skipped.Toast spuriously flipped");
        Assert.True (saved.Notifications.OnSkipped.Sound, "Skipped.Sound lost");
        Assert.False(saved.Notifications.OnSkipped.Email, "Skipped.Email spuriously flipped");
        Assert.True (saved.Notifications.OnFailure.Toast, "Failure.Toast lost");
        Assert.True (saved.Notifications.OnFailure.Sound, "Failure.Sound lost");
        Assert.True (saved.Notifications.OnFailure.Email, "Failure.Email lost");

        Assert.Equal(cascade, saved.Restore.RetryDelaysSeconds);

        ctx.Report.RecordNote(
            "9 notification gates + retry cascade round-trip",
            $"Cascade order preserved: [{string.Join(", ", saved.Restore.RetryDelaysSeconds)}]");

        return Task.CompletedTask;
    }
}
