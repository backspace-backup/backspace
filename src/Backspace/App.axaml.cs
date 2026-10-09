using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;

namespace Backspace;

public partial class App : Application
{
    /// <summary>
    /// The currently-merged accent resource dictionary, kept as a field so
    /// <see cref="ApplyAccentFromConfig"/> can swap it out without removing
    /// the other merged dictionaries (Theme, Icons).
    /// </summary>
    private IResourceProvider? _currentAccent;

    /// <summary>
    /// Last-applied values, so the Config.Changed handler can skip the
    /// (relatively expensive) resource-dictionary swap when the user
    /// touched some unrelated setting and accent / theme didn't actually
    /// move. This also avoids the visual flicker caused by removing then
    /// re-adding the SAME merged dictionary.
    /// </summary>
    private AccentColor? _lastAppliedAccent;
    private AppTheme? _lastAppliedTheme;
    private readonly TrayStatusPresenter _trayPresenter = new();
    private TrayScheduleStatus _trayStatus = new(ScheduleReadiness.Unknown, "Checking schedules…", "");
    private bool _trayRefreshRunning;
    private int _trayRefreshVersion;
    private DispatcherTimer? _trayPauseTimer;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Register the WinFSP DLL resolver BEFORE any code touches a
        // Fsp.* type. WinFSP's installer doesn't add its bin folder
        // to PATH, so the default Win32 DLL search can't find
        // winfsp-x64.dll — the first reference to Fsp.Interop.Api
        // would otherwise throw "the type initializer for
        // 'Fsp.Interop.Api' threw an exception" (which is exactly
        // what the user hit on their dist box at first-mount time).
        // No-op on macOS / Linux.
#if WINDOWS
        Backspace.Services.WinFsp.WinFspNativeLoader.EnsureRegistered();
#endif

        ServiceLocator.InitializeCore();

        // Catastrophic-exit hook: if the app dies without going
        // through Avalonia's ShutdownRequested (unhandled exception,
        // SIGTERM, parent terminal closes), still try to stop the
        // Backrest engine AND unmount any in-place snapshot mounts.
        // The job-object KILL_ON_JOB_CLOSE wired up in
        // BackrestSidecar.StartAsync is the real safety net for the
        // engine process; the unmount cleanup matters because
        // `net use` drive-letter mappings persist past process exit
        // and would otherwise leave the user with a half-dead
        // Z:\ drive until reboot.
        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            if (ForceQuitRequested) return;
            try { ServiceLocator.SnapshotMounts?.DisposeAllAsync().GetAwaiter().GetResult(); }
            catch { /* dying process — best-effort */ }
            try { ServiceLocator.BackrestSidecar?.DisposeAsync().GetAwaiter().GetResult(); }
            catch { /* dying process, log won't reach disk anyway */ }
        };

        // Sweep stale shell-integration verbs left by predecessor
        // builds (Duplimate's "DuplimateRestore" verb) and by older
        // Backspace dev builds that registered the verb without an
        // Icon value. Idempotent and best-effort; failure here can't
        // block startup.
        try { ShellIntegrationService.CleanLegacyEntries(); }
        catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Shell-integration legacy sweep threw"); }

        // Only do UI-thread work when we're actually running as a desktop
        // app with a window. Under the headless test lifetime we still load
        // App.axaml's resources (so views render), but skip the live-update
        // Config.Changed subscription — otherwise a background-thread config
        // mutation during a test would try to touch Application.Resources
        // from the wrong thread and crash.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplyAccentFromConfig();
            ApplyThemeFromConfig();
            ApplyTrayVisibility();
            // Defer via Dispatcher.UIThread.Post: Config.Changed fires
            // synchronously from the call site that mutated config, which
            // is typically a binding setter on the Settings combobox.
            // Doing the resource-dictionary surgery synchronously inside
            // that setter can leave the visual tree in an inconsistent
            // state (DynamicResource lookups for accent brushes briefly
            // return null between the Remove and Add), and any exception
            // raised by a downstream subscriber cascades back into the
            // binding system as a hard error. Posting puts the work
            // after the current binding callback completes.
            ServiceLocator.Config.Changed += (_, __) => Dispatcher.UIThread.Post(() =>
            {
                try { ApplyAccentFromConfig(); }
                catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Accent re-apply failed"); }
                try { ApplyThemeFromConfig(); }
                catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Theme re-apply failed"); }
                ApplyTrayVisibility();
                RefreshTrayStatus();
            });

            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
            desktop.MainWindow.Activated += (_, _) => RefreshTrayStatus();
            AppStatus.Changed += _ => Dispatcher.UIThread.Post(ApplyTrayStatus);
            TaskSchedulerService.Changed += () => Dispatcher.UIThread.Post(RefreshTrayStatus);
            RefreshTrayStatus();
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // Wire the WinFSP install-consent prompt: shown the first
            // time a user clicks Mount on a clean Backspace install
            // without the WinFSP runtime registered. The MSI ships
            // bundled next to Backspace.exe (FetchWinFsp.targets), so
            // a Yes here triggers msiexec + UAC; the install completes
            // in seconds and the mount proceeds. Saying No just returns
            // an "Install cancelled" toast — the user can try again.
            // Windows-only — SnapshotMounts is null on macOS/Linux,
            // and MountedSnapshotRegistry is csproj-Compile-Removed
            // off the cross-platform TFM.
#if WINDOWS
            if (ServiceLocator.SnapshotMounts is Backspace.Services.WebDav.MountedSnapshotRegistry winFspRegistry)
            {
                winFspRegistry.RequestInstallConsent = () =>
                    DestructiveConfirmWindow.ShowConfirmAsync(
                        desktop.MainWindow!,
                        title: "One-time install needed",
                        subtitle:
                            "Mount needs Windows' WinFSP filesystem driver, a Microsoft-signed " +
                            "user-mode driver that lets Backspace expose your snapshot as a " +
                            "regular drive letter. The installer ships with Backspace; Windows " +
                            "will prompt you to allow it. After this, Mount works instantly " +
                            "every time.",
                        confirmLabel: "Install",
                        cancelLabel: "Not now");
            }
#endif

            // Cleanup on shutdown — both branches run regardless of
            // why we're closing (user clicked X, OS asked us to quit,
            // unhandled exception bubbled up).
            //
            // 1. Unmount any "Mount in Explorer" drive letters —
            //    otherwise the WebDAV server stops but the net-use
            //    mapping lingers as a half-dead drive until the user
            //    reboots / runs `net use /delete`. Windows-only.
            //
            // 2. Stop the Backrest engine cleanly. Best-effort — the
            //    job-object safety net in BackrestSidecar.StartAsync
            //    catches whatever this misses, but a graceful stop
            //    drains stderr / flushes the sqlite oplog properly
            //    where a Kill-via-job-object would not.
            // ShutdownRequested is cancellable. Never tear down a live
            // engine before the main window has finished its safe-quit flow.
            desktop.ShutdownRequested += (_, e) =>
            {
                if (QuitRequested) return;
                e.Cancel = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (desktop.MainWindow is MainWindow window) window.RequestQuit();
                });
            };
            desktop.Exit += (_, __) =>
            {
                if (ForceQuitRequested) return;
                try { ServiceLocator.SnapshotMounts?.DisposeAllAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Unmount-on-shutdown threw"); }
                try
                {
                    ServiceLocator.BackrestSidecar?.DisposeAsync().GetAwaiter().GetResult();
                    ServiceLocator.Maintenance?.ReleaseEngineOwnershipAfterStop();
                }
                catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Backrest engine stop-on-shutdown threw"); }
            };

            // Post-init startup: broken-install probe + deferred
            // housekeeping (stub extract, scheduler re-upsert,
            // missed-run check). Invoked HERE — not from Program.RunGui
            // — because both the install probe and the stub extract
            // resolve avares:// URIs, which need Avalonia's
            // IAssetLoader service. That service is registered by the
            // time this callback fires; calling them earlier silently
            // returned "not bundled" on debug builds where the
            // binaries are only present as embedded AvaloniaResources.
            Program.RunPostInitStartup();

            // Open the lazy vault before deciding whether to warn. A busy file
            // is reported separately from a preserved, unreadable encrypted blob.
            var startupSecrets = ServiceLocator.Secrets;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var warning = await SecretsStartupWarning.CheckAsync(startupSecrets);
                    if (warning is not null && !QuitRequested && desktop.MainWindow?.IsVisible == true)
                        await DestructiveConfirmWindow.ShowInfoAsync(
                            desktop.MainWindow!,
                            title: warning.Title, subtitle: warning.Message);
                }
                catch (Exception ex)
                {
                    AppLogger.For<App>().Warning(ex, "Failed to surface secrets-load warning dialog");
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Tray-menu item handler: bring the main window back to the
    /// foreground. Also fired by single-clicking the tray icon
    /// (see <see cref="OnTrayIconClicked"/>) — most users expect the
    /// click to re-open the window, not just the right-click menu.
    /// </summary>
    private void OnTrayShow(object? sender, EventArgs e) => RestoreMainWindow();

    /// <summary>Single-click on the tray icon → restore main window.
    /// Avalonia routes both left and right click here; the right-click
    /// also pops the context menu via the NativeMenu wiring in App.axaml,
    /// so RestoreMainWindow is harmless either way.</summary>
    internal void ApplyTrayVisibility()
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons is null) return;
        foreach (var icon in icons) icon.IsVisible = ServiceLocator.Config.Current.Preferences.ShowTrayIcon;
    }

    private void OnTrayMenuOpening(object? sender, EventArgs e) => RefreshTrayStatus();
    private void OnTrayRefresh(object? sender, EventArgs e) => RefreshTrayStatus();

    private void ApplyTrayStatus()
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons is null) return;
        foreach (var icon in icons)
            _trayPresenter.Apply(icon, _trayStatus, ServiceLocator.Config.Current.Preferences.Accent,
                AppStatus.Current == AppActivity.Syncing);
    }

    internal async void RefreshTrayStatus()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime || QuitRequested) return;
        _trayRefreshVersion++;
        if (_trayRefreshRunning) return;
        _trayRefreshRunning = true;
        try
        {
            int version;
            do
            {
                version = _trayRefreshVersion;
                _trayStatus = new(ScheduleReadiness.Unknown, "Checking schedules…", "");
                ApplyTrayStatus();
                // Freeze this configuration before querying COM off the UI thread.
                var backups = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<Backup>>(
                    System.Text.Json.JsonSerializer.Serialize(ServiceLocator.Config.Current.Backups))!;
                _trayPauseTimer?.Stop();
                // One wake-up when a snooze expires; no recurring idle polling.
                var nextResume = backups.Where(b => b.Enabled)
                    .Select(b => b.Schedule.PausedUntilUtc)
                    .Where(t => t > DateTime.UtcNow).OrderBy(t => t).FirstOrDefault();
                if (nextResume is { } resume)
                {
                    _trayPauseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(100, (resume - DateTime.UtcNow).TotalMilliseconds)) };
                    _trayPauseTimer.Tick += (_, _) => { _trayPauseTimer.Stop(); RefreshTrayStatus(); };
                    _trayPauseTimer.Start();
                }
                var scheduler = ServiceLocator.Scheduler;
                var executable = Environment.ProcessPath ?? "";
                var query = System.Threading.Tasks.Task.Run(() =>
                    TrayScheduleStatus.Inspect(backups, scheduler, executable, DateTime.UtcNow));
                try { _trayStatus = await query.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException)
                {
                    _trayStatus = new(ScheduleReadiness.Unknown, "Schedules not verified", "Windows did not answer in time.");
                    ApplyTrayStatus();
                    // Keep only one outstanding OS query, even if menus reopen.
                    try { await query; } catch { }
                    continue;
                }
                if (version == _trayRefreshVersion) ApplyTrayStatus();
            } while (version != _trayRefreshVersion && !QuitRequested);
        }
        catch (Exception ex)
        {
            AppLogger.For<App>().Warning(ex, "Tray schedule check failed");
            _trayStatus = new(ScheduleReadiness.Unknown, "Schedules not verified", "Could not read the current schedules.");
            ApplyTrayStatus();
        }
        finally { _trayRefreshRunning = false; }
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) => RestoreMainWindow();

    /// <summary>Tray Quit uses the same safe operation drain as window
    /// close. Exit and ProcessExit handle committed engine teardown.</summary>
    private void OnTrayQuit(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow window) window.RequestQuit();
            else desktop.Shutdown();
        }
    }

    /// <summary>Flag set when the user has chosen "Quit Backspace"
    /// from the tray menu (or another truly-exit path). MainWindow
    /// reads this in its Closing handler to know whether to honour
    /// the MinimizeToTrayOnClose preference or just shut down.</summary>
    public static bool QuitRequested { get; set; }
    internal static bool ForceQuitRequested { get; set; }

    private async void OnTrayForceQuit(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
            await window.RequestForceQuitAsync();
    }

    private void RestoreMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var w = desktop.MainWindow;
        if (w is null) return;
        try
        {
            if (!w.IsVisible) w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
        }
        catch (Exception ex) { AppLogger.For<App>().Warning(ex, "RestoreMainWindow threw"); }
    }

    private void ApplyThemeFromConfig()
    {
        var theme = ServiceLocator.Config.Current.Preferences.Theme;
        if (_lastAppliedTheme == theme) return;

        RequestedThemeVariant = theme switch
        {
            AppTheme.Dark   => ThemeVariant.Dark,
            AppTheme.System => ThemeVariant.Default,
            _               => ThemeVariant.Light,
        };
        _lastAppliedTheme = theme;
    }

    /// <summary>
    /// Swaps in the user's chosen accent palette. One dictionary owns
    /// the BS.Brush.Accent / AccentHover / AccentDeep / AccentTint brushes;
    /// switching replaces just that dictionary so we don't churn icons or
    /// the core theme.
    /// </summary>
    private void ApplyAccentFromConfig()
    {
        var accent = ServiceLocator.Config.Current.Preferences.Accent;
        if (_lastAppliedAccent == accent && _currentAccent is not null) return;

        var uri = accent switch
        {
            AccentColor.Emerald     => new Uri("avares://Backspace/Themes/Accent_Emerald.axaml"),
            AccentColor.Plum        => new Uri("avares://Backspace/Themes/Accent_Plum.axaml"),
            AccentColor.Graphite    => new Uri("avares://Backspace/Themes/Accent_Graphite.axaml"),
            AccentColor.Terracotta  => new Uri("avares://Backspace/Themes/Accent_Terracotta.axaml"),
            _                       => new Uri("avares://Backspace/Themes/Accent_Ocean.axaml"),
        };

        var include = new ResourceInclude(uri) { Source = uri };

        // Add-then-remove (instead of remove-then-add) so DynamicResource
        // lookups for accent brushes never see a moment where neither
        // dictionary is present — Avalonia walks dictionaries in reverse
        // order, so the new one wins immediately and the old one becomes
        // unreachable before we drop it.
        Resources.MergedDictionaries.Add(include);
        if (_currentAccent is not null)
            Resources.MergedDictionaries.Remove(_currentAccent);
        _currentAccent = include;
        _lastAppliedAccent = accent;
    }
}
