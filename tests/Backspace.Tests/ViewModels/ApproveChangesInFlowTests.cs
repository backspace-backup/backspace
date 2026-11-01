using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>
/// With administrator access on, a saved change to a backup is applied to the
/// background service right away and without a Windows prompt (U043, 31
/// October 2026). Earlier, removing a backup left "Approve your latest
/// changes" and every schedule waiting; then each change asked Windows again.
/// </summary>
public sealed class ApproveChangesInFlowTests
{
    [AvaloniaTheory]
    [InlineData((int)BackgroundBackupState.ChangesPending, 1)]
    [InlineData((int)BackgroundBackupState.Ready, 0)]
    [InlineData((int)BackgroundBackupState.Off, 0)]
    [InlineData((int)BackgroundBackupState.SetupPending, 0)]
    [InlineData((int)BackgroundBackupState.RemovalPending, 0)]
    public async Task Applies_saved_changes_once_and_never_prompts(int stateValue, int expectedApplies)
    {
        if (!OperatingSystem.IsWindows()) return;
        var setup = new CountingSetup((BackgroundBackupState)stateValue);
        await WithBackups(setup, async vm =>
        {
            await vm.ApproveChangesInFlowAsync();
            Assert.Equal(expectedApplies, setup.Quiet);
            // Negative: no Windows prompt in any state, and access that is off
            // is never turned on here.
            Assert.Equal(0, setup.Prompts);
            Assert.False(vm.NeedsAdministratorAccess && stateValue == (int)BackgroundBackupState.ChangesPending);
        });
    }

    [AvaloniaFact]
    public async Task A_failure_to_apply_shows_its_reason_without_a_prompt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var setup = new CountingSetup(BackgroundBackupState.ChangesPending) { Outcome = new(QuietApplyResult.Failed, "The source folder is missing.") };
        await WithBackups(setup, async vm =>
        {
            await vm.ApproveChangesInFlowAsync();
            // The start-up attempt may also run under the headless dispatcher.
            Assert.True(setup.Quiet >= 1);
            Assert.Equal(0, setup.Prompts);
            Assert.True(vm.NeedsAdministratorAccess);
        });
    }

    [AvaloniaFact]
    public async Task A_running_backup_delays_it_without_an_alarm()
    {
        if (!OperatingSystem.IsWindows()) return;
        var setup = new CountingSetup(BackgroundBackupState.ChangesPending) { Outcome = new(QuietApplyResult.Busy) };
        await WithBackups(setup, async vm =>
        {
            await vm.ApproveChangesInFlowAsync();
            // The start-up attempt may also run under the headless dispatcher.
            Assert.True(setup.Quiet >= 1);
            Assert.Equal(0, setup.Prompts);
            Assert.False(vm.NeedsAdministratorAccess);
        });
    }

    [AvaloniaFact]
    public async Task A_setup_from_an_earlier_version_asks_once_to_add_the_settings_task()
    {
        if (!OperatingSystem.IsWindows()) return;
        var setup = new CountingSetup(BackgroundBackupState.ChangesPending) { Outcome = new(QuietApplyResult.NeedsApproval) };
        await WithBackups(setup, async vm =>
        {
            await vm.ApproveChangesInFlowAsync();
            Assert.Equal(1, setup.Quiet);
            Assert.Equal(1, setup.Prompts);
        });
    }

    private static async Task WithBackups(CountingSetup setup, Func<BackupsViewModel, Task> check)
    {
        BackgroundBackupViewModel.SetupOverride = () => setup;
        ServiceLocator.Config.Update(cfg => { cfg.Backups.Clear(); cfg.Backups.Add(new Backup { Name = "documents", UseVss = true }); });
        var vm = new BackupsViewModel();
        try { await check(vm); }
        finally
        {
            vm.Dispose();
            BackgroundBackupViewModel.SetupOverride = null;
            ServiceLocator.Config.Update(cfg => cfg.Backups.Clear());
        }
    }

    private sealed class CountingSetup(BackgroundBackupState state) : IBackgroundBackupSetup
    {
        public int Quiet, Prompts;
        public QuietApplyOutcome Outcome = new(QuietApplyResult.Applied);
        public Task<BackgroundBackupStatus> InspectAsync() => Task.FromResult(new BackgroundBackupStatus(state, "test", true));
        public Task ApplyAsync()
        {
            Prompts++;
            state = BackgroundBackupState.Ready;
            return Task.CompletedTask;
        }
        public Task<QuietApplyOutcome> ApplyChangesQuietlyAsync()
        {
            Quiet++;
            if (Outcome.Result == QuietApplyResult.Applied) state = BackgroundBackupState.Ready;
            return Task.FromResult(Outcome);
        }
    }
}
