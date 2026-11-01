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
public sealed class S70_AutomaticBackupRemoval : ScenarioBase
{
    protected override string ScenarioName => "S70-automatic-backup-removal";
    [AvaloniaFact] public async Task Run_S70() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var backend = new ControlledSetup();
        var automatic = new BackgroundBackupViewModel(backend);
        using var settings = new SettingsViewModel(null, automatic);
        var view = new SettingsView { DataContext = settings };
        var window = ctx.Driver.Show(new Window { Content = view, Width = 1024, Height = 768, Title = "Backspace — Settings" });
        await ctx.Driver.PumpAsync(100);
        var remove = view.FindControl<Button>("RemoveAutomaticBackups")!;
        var apply = view.FindControl<Button>("ApplyAutomaticBackups")!;
        var card = view.FindControl<Border>("AutomaticBackupsCard")!;
        Assert.Equal(0, backend.RemoveCalls);
        // Access on: the section is collapsed; Remove access is inside it (U027).
        var section = view.FindControl<Expander>("AdministratorAccessSection")!;
        Assert.True(card.IsVisible);
        Assert.False(section.IsExpanded);
        section.IsExpanded = true; await ctx.Driver.PumpAsync(30);
        Assert.True(remove.IsEffectivelyVisible);
        var removing = automatic.RemoveCommand.ExecuteAsync(null); await ctx.Driver.PumpAsync(30);
        Assert.True(automatic.IsBusy);
        Assert.True(section.IsExpanded); // Progress and any problem show in Settings.
        Assert.False(automatic.RemoveCommand.CanExecute(null)); // No second removal while one runs.
        Assert.Equal(1, backend.RemoveCalls);
        backend.State = new(BackgroundBackupState.RemovalPending, "Scheduled backups are paused until this finishes. Your backups and sign-ins are kept.");
        backend.Finish.SetException(new OperationCanceledException("Windows approval was cancelled. Your saved settings and credentials have been preserved."));
        for (var n = 0; n < 50 && automatic.IsBusy; n++) await ctx.Driver.PumpAsync(20);
        await removing;
        Assert.False(automatic.IsBusy);
        Assert.Contains("cancelled", automatic.Error);
        Assert.Equal("Finish removing access", apply.Content);
        Assert.Equal("Removing access isn't finished", automatic.Title);
        Assert.False(remove.IsEffectivelyVisible);
        await ctx.Driver.PumpAsync(250);
        ctx.Snapshot(window, "removal-paused", "Cancelling the Windows prompt while removing access: says it isn't finished and that schedules are paused, and offers Finish removing access.");
        backend.Finish = new();
        Click(window, apply); await ctx.Driver.PumpAsync(30);
        Assert.Equal(1, backend.ApplyCalls);
        backend.State = new(BackgroundBackupState.Off, "Without administrator access, backups can't include files other apps have open. They run on schedule only while you're signed in.");
        backend.Finish.SetResult();
        for (var n = 0; n < 50 && automatic.IsBusy; n++) await ctx.Driver.PumpAsync(20);
        Assert.False(automatic.IsBusy);
        Assert.Empty(automatic.Error);
        Assert.Equal("Allow administrator access", apply.Content);
        Assert.Equal("Files that are open may be skipped", automatic.Title);
        Assert.False(remove.IsEffectivelyVisible);
        await ctx.Driver.PumpAsync(250);
        ctx.Snapshot(window, "removed", "After removing access: Files that are open may be skipped, and Allow administrator access is offered again.");
        ctx.Report.RecordNote("Limits", "Actual Settings controls and pointer clicks with controlled backend replies. The separate VM checks exercise real credential return, Windows registration and file permissions. This scenario is not native UAC acceptance.");
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }
    private sealed class ControlledSetup : IBackgroundBackupSetup
    {
        internal BackgroundBackupStatus State = new(BackgroundBackupState.Ready, "Backups also run on schedule when nobody is signed in.", true);
        internal TaskCompletionSource Finish = new();
        internal int ApplyCalls, RemoveCalls;
        public Task<BackgroundBackupStatus> InspectAsync() => Task.FromResult(State);
        public Task ApplyAsync() { ApplyCalls++; return Finish.Task; }
        public Task RemoveAsync() { RemoveCalls++; return Finish.Task; }
    }
}
