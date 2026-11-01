using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Administrator access is asked inside the backup creation flow, before
/// saving, and Windows' prompt comes only from the person's own Save (U026,
/// 29 October 2026: the VM showed a Windows prompt first and the explanation
/// afterwards). Without access, the sidebar shows one compact warning whose
/// flyout explains and offers the fix; once access is on nothing shows and
/// Settings keeps it in a collapsed section (U027, 29 October 2026).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S71_AdministratorAccessOffer : ScenarioBase
{
    protected override string ScenarioName => "S71-administrator-access-offer";
    [AvaloniaFact] public async Task Run_S71() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        if (!OperatingSystem.IsWindows()) return;
        var backend = new ControlledSetup();
        BackgroundBackupViewModel.SetupOverride = () => backend;
        try
        {
            var dest = new Destination { Name = "Backup drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("drive") };
            ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(dest));

            // 1. The wizard's schedule step offers the choice, ticked.
            var wizard = new OnboardingWindow(seed: null, isFirstBackup: true) { Width = 900, Height = 900 };
            ctx.Driver.Show(wizard);
            var easy = (OnboardingViewModel)wizard.DataContext!;
            easy.InstallShellExtension = false; // Never register an Explorer verb for the test host.
            easy.SourceChips.Add(new SourceChipVm(ctx.MakeSource("documents", seed: 71, targetBytes: 50_000)));
            easy.SelectedDestinations.Add(easy.AvailableDestinations.Single(d => d.Id == dest.Id));
            easy.CurrentStep = 3;
            for (var n = 0; n < 50 && !easy.AdministratorAccess.Offered; n++) await ctx.Driver.PumpAsync(20);
            var choice = wizard.FindControl<AdministratorAccessChoiceView>("AdministratorAccessChoice")!;
            Assert.True(choice.IsEffectivelyVisible);
            Assert.True(easy.AdministratorAccess.Allow);
            Assert.Equal(0, backend.ApplyCalls); // Showing the step asks nothing of Windows.
            ctx.Snapshot(wizard, "wizard-choice", "Schedule step of a new backup: Include files that are in use, with Allow administrator access ticked and a note that Windows asks once when you save.");

            // 2. Save with it ticked: the backup is saved first, then Windows' prompt.
            backend.AfterApply = new(BackgroundBackupState.Ready, "Backups also run on schedule when nobody is signed in.", true);
            backend.OnApply = () => backend.SavedBeforeApply = ServiceLocator.Config.Current.Backups.Any(b => b.Id == easy.Working.Id);
            await easy.SaveOnlyCommand.ExecuteAsync(null);
            Assert.True(easy.SavedNoRun);
            Assert.Equal(1, backend.ApplyCalls);
            Assert.True(backend.SavedBeforeApply);
            Assert.Null(easy.AdministratorAccessError);
            wizard.Close();
            ctx.Report.RecordNote("Order", "Save with the box ticked saved the backup, then asked Windows once (controlled backend), so the approval covers the new backup before its first run.");

            // 3. Unticking it means no Windows prompt at all.
            backend.State = backend.Off; backend.AfterApply = null; backend.OnApply = null;
            var second = new OnboardingViewModel { InstallShellExtension = false };
            second.SourceChips.Add(new SourceChipVm(ctx.MakeSource("pictures", seed: 72, targetBytes: 50_000)));
            second.SelectedDestinations.Add(second.AvailableDestinations.Single(d => d.Id == dest.Id));
            second.CurrentStep = 3;
            for (var n = 0; n < 50 && !second.AdministratorAccess.Offered; n++) await ctx.Driver.PumpAsync(20);
            second.AdministratorAccess.Allow = false;
            Assert.Contains("may be skipped", second.AdministratorAccess.Hint);
            await second.SaveOnlyCommand.ExecuteAsync(null);
            Assert.True(second.SavedNoRun);
            Assert.Equal(1, backend.ApplyCalls);

            // 4. Editing an existing backup never offers it.
            var edit = new OnboardingViewModel(ServiceLocator.Config.Current.Backups.First());
            await ctx.Driver.PumpAsync(200);
            Assert.False(edit.AdministratorAccess.Offered);

            // 5. Without access, a compact warning sits in the sidebar instead of
            // a banner over the list; its flyout explains and offers the fix (U027).
            var mainVm = new MainWindowViewModel();
            var main = ctx.Driver.Show(new MainWindow { DataContext = mainVm, Width = 1180, Height = 760 });
            try
            {
                var vm = mainVm.Backups;
                for (var n = 0; n < 50 && !vm.NeedsAdministratorAccess; n++) await ctx.Driver.PumpAsync(20);
                Assert.True(vm.NeedsAdministratorAccess);
                var badge = main.FindControl<Button>("AdministratorAccessBadge")!;
                Assert.True(badge.IsEffectivelyVisible);
                Assert.Equal("Files that are open may be skipped", main.FindControl<TextBlock>("AdministratorAccessBadgeText")!.Text);
                var backupsView = main.GetVisualDescendants().OfType<BackupsView>().Single();
                Assert.Null(backupsView.FindControl<Border>("AdministratorAccessBanner"));
                ctx.Snapshot(main, "sidebar-warning", "Access off: an orange Files that are open may be skipped badge at the bottom of the sidebar; no banner above the backups list.");

                badge.Flyout!.ShowAt(badge);
                await ctx.Driver.PumpAsync(150);
                var allow = main.FindControl<Button>("AllowAccessFromBadge")!;
                Assert.True(allow.IsEffectivelyVisible);
                Assert.Equal("Allow administrator access", allow.Content);
                Assert.Equal(1, backend.ApplyCalls); // Opening the explanation asks nothing of Windows.
                ctx.Snapshot(main, "sidebar-warning-open", "Badge clicked: the flyout explains what is skipped and offers Allow administrator access, with a note that Windows asks once.");

                backend.AfterApply = new(BackgroundBackupState.Ready, "Backups also run on schedule when nobody is signed in.", true);
                allow.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                for (var n = 0; n < 100 && vm.NeedsAdministratorAccess; n++) await ctx.Driver.PumpAsync(20);
                Assert.Equal(2, backend.ApplyCalls);
                Assert.False(vm.NeedsAdministratorAccess);
                Assert.False(badge.IsVisible);
                Assert.True(mainVm.AdministratorAccess.FilesInUseIncluded);
                ctx.Snapshot(main, "allowed", "After approval nothing shows in the sidebar: there is nothing to do.");

                // Removing access elsewhere (Settings shares this state) brings the
                // badge back without any backup changing.
                backend.State = backend.Off;
                await mainVm.AdministratorAccess.RefreshAsync();
                for (var n = 0; n < 50 && !vm.NeedsAdministratorAccess; n++) await ctx.Driver.PumpAsync(20);
                Assert.True(badge.IsVisible);
                backend.State = new(BackgroundBackupState.Ready, "Backups also run on schedule when nobody is signed in.", true);
            }
            finally { main.Close(); mainVm.Dispose(); }

            // 6. Settings keeps access in a collapsed section once it is on, so
            // Remove access stays reachable without a card announcing it.
            var settings = new SettingsViewModel();
            await settings.BackgroundBackups.RefreshAsync();
            Assert.Equal("Files in use are included", settings.BackgroundBackups.Title);
            Assert.False(settings.BackgroundBackups.NeedsAttention);
            var settingsView = new SettingsView { DataContext = settings };
            var settingsWindow = ctx.Driver.Show(new Window { Width = 1100, Height = 620, Content = settingsView });
            await ctx.Driver.PumpAsync(100);
            var section = settingsView.FindControl<Expander>("AdministratorAccessSection")!;
            Assert.True(settingsView.FindControl<Border>("AutomaticBackupsCard")!.IsVisible);
            Assert.False(section.IsExpanded);
            ctx.Snapshot(settingsWindow, "settings-access-on", "Settings with access on: Administrator access is a collapsed section like Appearance and the others.");
            section.IsExpanded = true; await ctx.Driver.PumpAsync(50);
            Assert.True(settingsView.FindControl<Button>("RemoveAutomaticBackups")!.IsEffectivelyVisible);

            // Negative: with access off the section opens by itself.
            backend.State = backend.Off;
            section.IsExpanded = false;
            await settings.BackgroundBackups.RefreshAsync(); await ctx.Driver.PumpAsync(50);
            Assert.True(settings.BackgroundBackups.NeedsAttention);
            Assert.True(section.IsExpanded);
            settingsWindow.Close();
            settings.Dispose();

            // Negative: without VSS there is no warning.
            ServiceLocator.Config.Update(cfg => cfg.Backups.ForEach(b => b.UseVss = false));
            using (var noVss = new BackupsViewModel())
            {
                await ctx.Driver.PumpAsync(300);
                Assert.False(noVss.NeedsAdministratorAccess);
            }
            ctx.Report.RecordNote("Limits", "Headless Avalonia with a controlled setup backend; no UAC prompt or Windows service is involved. The VM check exercises the real approval.");
        }
        finally { BackgroundBackupViewModel.SetupOverride = null; }
    }

    private sealed class ControlledSetup : IBackgroundBackupSetup
    {
        internal readonly BackgroundBackupStatus Off = new(BackgroundBackupState.Off,
            "Without administrator access, backups can't include files other apps have open. They run on schedule only while you're signed in.");
        internal BackgroundBackupStatus State;
        internal BackgroundBackupStatus? AfterApply;
        internal Action? OnApply;
        internal bool SavedBeforeApply;
        internal int ApplyCalls;
        internal ControlledSetup() => State = Off;
        public Task<BackgroundBackupStatus> InspectAsync() => Task.FromResult(State);
        public Task ApplyAsync() { ApplyCalls++; OnApply?.Invoke(); if (AfterApply is { } next) State = next; return Task.CompletedTask; }
    }
}
