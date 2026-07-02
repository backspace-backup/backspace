using System;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>
/// The Easy Mode wizard surfaces grace-period settings (skip
/// notification grace days + manual reset timestamp) and the user
/// can flip between Easy ↔ Advanced mid-edit. The fields must
/// survive a round-trip through the VM so a user who edits the
/// grace days in Easy, switches to Advanced, then switches back
/// doesn't lose their value. <see cref="OnboardingViewModel.BuildSnapshot"/>
/// is the join point that has to copy them through.
/// </summary>
public class OnboardingGraceRoundTripTests
{
    [Fact]
    public void SeededBackup_LoadsGraceFields_IntoVm()
    {
var seed = new Backup
        {
            Name = "Existing",
            SkipNotificationGraceDays = 21,
            SkipNotificationGraceResetUtc = new DateTime(2026, 06, 01, 12, 0, 0, DateTimeKind.Utc),
        };
        var vm = new OnboardingViewModel(seed);
        Assert.Equal(21, vm.SkipNotificationGraceDays);
        Assert.Equal(seed.SkipNotificationGraceResetUtc, vm.SkipNotificationGraceResetUtc);
    }

    [Fact]
    public void BuildSnapshot_WritesGraceFields_BackToWorking()
    {
var seed = new Backup
        {
            Name = "Edit me",
            SkipNotificationGraceDays = 7,
        };
        var vm = new OnboardingViewModel(seed);
        // User edits the field in the wizard.
        vm.SkipNotificationGraceDays = 30;
        var snapshot = vm.BuildSnapshot();
        Assert.Equal(30, snapshot.SkipNotificationGraceDays);
    }

    [Fact]
    public void ResetGracePeriod_StampsNow_AndUpdatesLabel()
    {
var vm = new OnboardingViewModel();
        // Pre-condition: no manual reset yet, label says "from last
        // successful backup".
        Assert.Null(vm.SkipNotificationGraceResetUtc);
        Assert.Contains("last successful backup", vm.GraceReferenceLabel);

        var before = DateTime.UtcNow;
        vm.ResetGracePeriodCommand.Execute(null);
        var after = DateTime.UtcNow;
        Assert.NotNull(vm.SkipNotificationGraceResetUtc);
        Assert.InRange(vm.SkipNotificationGraceResetUtc!.Value, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Contains("Manual reset", vm.GraceReferenceLabel);
    }

    [Fact]
    public void DefaultGraceDays_IsSeven_OnFreshWizard()
    {
        // First-time users land on a sensible default; the spec
        // chose 7 to cover the common "user travels for the
        // weekend" pattern.
var vm = new OnboardingViewModel();
        Assert.Equal(7, vm.SkipNotificationGraceDays);
    }
}
