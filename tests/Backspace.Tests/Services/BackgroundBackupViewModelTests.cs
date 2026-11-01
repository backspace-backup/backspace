using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class BackgroundBackupViewModelTests
{
    [AvaloniaFact]
    public async Task A_late_old_inspection_cannot_replace_a_newer_pending_state()
    {
        var backend = new DelayedSetup(); var vm = new BackgroundBackupViewModel(backend);
        var first = vm.RefreshAsync(); var second = vm.RefreshAsync();
        backend.Second.SetResult(new(BackgroundBackupState.ChangesPending, "New settings need approval"));
        await second;
        backend.First.SetResult(new(BackgroundBackupState.Ready, "Earlier state"));
        await first;
        // Pending changes are applied without a prompt (U043): the newer state wins.
        Assert.True(vm.ChangesPending);
        Assert.Contains("Applying your latest changes", vm.Detail);
        Assert.False(vm.CanApply);
    }
    private sealed class DelayedSetup : IBackgroundBackupSetup
    {
        internal TaskCompletionSource<BackgroundBackupStatus> First = new(), Second = new();
        private int _calls;
        public Task<BackgroundBackupStatus> InspectAsync() => ++_calls == 1 ? First.Task : Second.Task;
        public Task ApplyAsync() => throw new System.NotImplementedException();
    }
}
