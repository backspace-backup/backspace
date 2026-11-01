using System;
using System.Threading.Tasks;

namespace Backspace.Services;

internal enum BackgroundBackupState { Off, SetupPending, ChangesPending, RemovalPending, Ready, Unavailable }
internal sealed record BackgroundBackupStatus(BackgroundBackupState State, string Detail, bool CanRemove = false);
/// <summary>Saved settings differ from what the background service runs.
/// Backspace applies them right after saving (U043), so callers need not
/// raise their own warning.</summary>
internal sealed class ChangesNeedApprovalException(string message) : System.IO.IOException(message);
/// <summary>Result of applying saved settings without a Windows prompt.</summary>
internal enum QuietApplyResult { Applied, Busy, NeedsApproval, Failed }
internal sealed record QuietApplyOutcome(QuietApplyResult Result, string? Reason = null);
internal interface IBackgroundBackupSetup
{
    Task<BackgroundBackupStatus> InspectAsync();
    Task ApplyAsync();
    /// <summary>Applies saved changes through the installed settings task, with
    /// no Windows prompt (U043). NeedsApproval when this setup predates it.</summary>
    Task<QuietApplyOutcome> ApplyChangesQuietlyAsync() => Task.FromResult(new QuietApplyOutcome(QuietApplyResult.NeedsApproval));
    Task RemoveAsync() => throw new NotSupportedException("Background service removal is not available here.");
}

internal sealed class UnavailableBackgroundBackupSetup : IBackgroundBackupSetup
{
    public Task<BackgroundBackupStatus> InspectAsync() => Task.FromResult(new BackgroundBackupStatus(
        BackgroundBackupState.Unavailable, "Background service setup is available in the portable Windows app."));
    public Task ApplyAsync() => throw new NotSupportedException("Use the portable Windows app to run schedules all the time.");
}
