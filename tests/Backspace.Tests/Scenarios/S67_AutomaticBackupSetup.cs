using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S67_AutomaticBackupSetup : ScenarioBase
{
    protected override string ScenarioName => "S67-automatic-backup-setup";
    [AvaloniaFact] public async Task Run_S67() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var backend = new ControlledSetup();
        var automatic = new BackgroundBackupViewModel(backend);
        using var settings = new SettingsViewModel(null, automatic);
        var view = new SettingsView { DataContext = settings };
        var window = ctx.Driver.Show(new Window { Content = view, Width = 1024, Height = 768, Title = "Backspace — Settings" });
        await ctx.Driver.PumpAsync(100);
        Assert.Equal(0, backend.ApplyCalls); // Opening Settings must not register anything.
        var button = view.FindControl<Button>("ApplyAutomaticBackups")!;
        Assert.Equal("Allow administrator access", button.Content);
        Assert.Equal("Files that are open may be skipped", automatic.Title);
        Assert.Contains("approve this once", automatic.ActionHint);
        ctx.Snapshot(window, "off", "Default: Files that are open may be skipped, one sentence why, and one button, Allow administrator access, with one line saying Windows asks once. Opening Settings changed nothing.");
        Click(window, button); await ctx.Driver.PumpAsync(100);
        Assert.Equal(1, backend.ApplyCalls);
        Assert.True(automatic.IsBusy); Assert.False(button.IsEffectivelyEnabled);
        // A second click cannot start another elevation/credential transfer.
        Click(window, button); await ctx.Driver.PumpAsync(30); Assert.Equal(1, backend.ApplyCalls);
        backend.State = new(BackgroundBackupState.Ready, "Backups also run on schedule when nobody is signed in.", true);
        backend.Finish.SetResult();
        for (int n = 0; n < 50 && automatic.IsBusy; n++) await ctx.Driver.PumpAsync(20);
        Assert.False(automatic.IsBusy); Assert.False(automatic.HasAction);
        Assert.Equal("Files in use are included", automatic.Title);
        // Once on, the section folds away like the others; Remove access stays inside (U027).
        var section = view.FindControl<Expander>("AdministratorAccessSection")!;
        Assert.True(view.FindControl<Border>("AutomaticBackupsCard")!.IsVisible);
        Assert.False(section.IsExpanded);
        Assert.True(automatic.CanRemove);
        ctx.Snapshot(window, "enabled", "After approval Administrator access is a collapsed section; nothing needs doing.");
        // Saved changes are applied without a prompt (U043): while that
        // happens nothing needs doing, so the section stays folded.
        backend.State = new(BackgroundBackupState.ChangesPending, "Until your latest changes are applied, scheduled backups wait.", true);
        await automatic.RefreshAsync(); await ctx.Driver.PumpAsync(30);
        Assert.False(section.IsExpanded);
        Assert.False(automatic.HasAction);
        Assert.Equal("Files in use are included", automatic.Title);
        Assert.Contains("Applying your latest changes", automatic.Detail);
        // Applying fails: the real reason shows, with Try again, and no prompt.
        backend.Quiet = new(QuietApplyResult.Failed, "The backup source C:\\Missing no longer exists.");
        await automatic.ApplyChangesQuietlyAsync(); await ctx.Driver.PumpAsync(30);
        Assert.True(section.IsExpanded);
        Assert.Equal("Your latest changes aren't applied yet", automatic.Title);
        Assert.Contains("no longer exists", automatic.Detail);
        Assert.Equal("Try again", button.Content);
        Assert.Equal("", automatic.ActionHint);
        Assert.Equal(1, backend.ApplyCalls); // No Windows prompt.
        await ctx.Driver.PumpAsync(250);
        ctx.Snapshot(window, "apply-failed", "Saved changes could not be applied: the real reason, Try again, and no Windows prompt.");
        backend.Quiet = new(QuietApplyResult.Applied);
        backend.State = new(BackgroundBackupState.Ready, "Backups also run on schedule when nobody is signed in.", true);
        Click(window, button);
        for (int n = 0; n < 50 && (automatic.IsBusy || automatic.Title != "Files in use are included"); n++) await ctx.Driver.PumpAsync(20);
        Assert.Equal("Files in use are included", automatic.Title);
        Assert.Equal(2, backend.QuietCalls); Assert.Equal(1, backend.ApplyCalls);
        // A setup made by an earlier version has no settings task: one prompt adds it.
        backend.State = new(BackgroundBackupState.ChangesPending, "Until your latest changes are applied, scheduled backups wait.", true);
        backend.Quiet = new(QuietApplyResult.NeedsApproval);
        backend.Finish = new();
        var legacy = automatic.ApplyChangesQuietlyAsync();
        for (int n = 0; n < 50 && backend.ApplyCalls < 2; n++) await ctx.Driver.PumpAsync(20);
        Assert.Equal(2, backend.ApplyCalls);
        backend.Finish.SetException(new OperationCanceledException("Windows approval was cancelled. Your saved settings and credentials have been preserved."));
        await legacy;
        Assert.Contains("cancelled", automatic.Error);
        // A failed approved setup step shows its real reason, not a generic line.
        backend.State = new(BackgroundBackupState.SetupPending, "Setup stopped before it finished. Your backups and sign-ins are unchanged.", true);
        await automatic.RefreshAsync(); await ctx.Driver.PumpAsync(30);
        backend.Finish = new();
        Click(window, button); await ctx.Driver.PumpAsync(30);
        backend.Finish.SetException(new System.IO.IOException("Scheduled backups could not be set up. Your backups and settings are unchanged. Reason: Background backup settings need attention: A backup is still running. Finish it, then try again."));
        for (int n = 0; n < 50 && automatic.IsBusy; n++) await ctx.Driver.PumpAsync(20);
        Assert.Contains("Reason: ", automatic.Error);
        await ctx.Driver.PumpAsync(250);
        ctx.Snapshot(window, "failure-reason", "A failed approved step shows the actual reason in an amber box, with the action still available to try again.");
        // A folder that cannot run all the time says why before any prompt.
        backend.State = new(BackgroundBackupState.Unavailable, "Without administrator access, backups can't include files other apps have open. They run on schedule only while you're signed in.\nBackspace is on a network drive, which Windows cannot reach before you sign in. Copy the whole Backspace folder to this PC and try again.");
        automatic.Error = "";
        await automatic.RefreshAsync(); await ctx.Driver.PumpAsync(250);
        Assert.False(button.IsVisible);
        Assert.Equal("Files that are open may be skipped", automatic.Title);
        ctx.Snapshot(window, "unsuitable-folder", "A network drive: the card says plainly why administrator access can't be used from here, and offers no button that would fail.");
        ctx.Report.RecordNote("Limits", "Actual Settings controls and pointer clicks in headless Avalonia, with controlled setup responses. No Windows service registration, UAC prompt or credential transfer was performed by this UI test. The separate VM settings report exercises the real backend.");
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }

    private sealed class ControlledSetup : IBackgroundBackupSetup
    {
        internal BackgroundBackupStatus State = new(BackgroundBackupState.Off, "Without administrator access, backups can't include files other apps have open. They run on schedule only while you're signed in.");
        internal TaskCompletionSource Finish = new();
        internal int ApplyCalls, QuietCalls;
        internal QuietApplyOutcome Quiet = new(QuietApplyResult.Applied);
        public Task<BackgroundBackupStatus> InspectAsync() => Task.FromResult(State);
        public Task ApplyAsync() { ApplyCalls++; return Finish.Task; }
        public Task<QuietApplyOutcome> ApplyChangesQuietlyAsync() { QuietCalls++; return Task.FromResult(Quiet); }
    }
}
