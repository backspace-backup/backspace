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
/// Settings notification state and legacy configuration compatibility:
///   • "Toggle notification gating per event" (Success/Skipped/Failure
///     × Toast/Sound/Email — 9 independent gates).
///   • The legacy restore-delay list remains ordered after a config update.
///
/// Asserts: notification values remain in the current configuration after
/// an explicit ConfigStore.Update, and the email-gate aggregate
/// (<c>AnyEmailEnabled</c>) changes with the selected values. The legacy
/// retry-delay list is written directly and checked for preserved order;
/// there is no Settings control or active engine retry behavior for it.
/// This scenario does not reload a fresh ConfigStore from disk.
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

        // Legacy configuration: pick a non-default list with a distinctive
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

        // -------- Current state after ConfigStore.Update --------
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
            "Notification values and legacy restore-delay compatibility",
            $"Current configuration retains all 9 notification values and the legacy delay order " +
            $"[{string.Join(", ", saved.Restore.RetryDelaysSeconds)}]. " +
            "The delay list has no active Settings control or engine retry behavior; this check does not reload the file.");

        return Task.CompletedTask;
    }
}
