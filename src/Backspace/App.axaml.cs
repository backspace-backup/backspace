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
    private readonly System.Threading.CancellationTokenSource _removalRetry = new();
    private static System.Threading.CancellationTokenSource? _startupTaskResume;
#if WINDOWS
    private Services.Platform.Windows.BackgroundServiceNotifications? _backgroundNotifications;
#endif

    internal static void CancelPendingStartupTaskResume()
    {
        try { _startupTaskResume?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    private TrayWindowHost? _windowHost;

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
        // The window that asks for the saved-passwords password (U153); tests
        // install their own script before this runs.
        VaultGate.Prompt ??= new Views.VaultPasswordPrompt();

        ServiceLocator.InitializeCore();

        // Catastrophic-exit hook: if the app dies without going
        // through Avalonia's ShutdownRequested (unhandled exception,
        // SIGTERM, parent terminal closes), still try to unmount any
        // in-place snapshot mounts. The job object with
        // KILL_ON_JOB_CLOSE that every restic joins is the safety net
        // for the engine processes; the unmount cleanup matters because
        // `net use` drive-letter mappings persist past process exit
        // and would otherwise leave the user with a half-dead
        // Z:\ drive until reboot.
        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            if (ForceQuitRequested) return;
            try { ServiceLocator.SnapshotMounts?.DisposeAllAsync().GetAwaiter().GetResult(); }
            catch { /* dying process — best-effort */ }
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
            // No white rectangle before a window is drawn (U240).
            NoWhiteFlash.Install();
            ApplyAccentFromConfig();
            ApplyThemeFromConfig();
#if WINDOWS
            // Folders this account can't open are measured by the running
            // background service, which backs them up (U106).
            Services.SourceSizeProbe.Elevated = async (path, rules, ct) =>
            {
                var system = Services.Platform.Windows.SystemService.ForCopy(Services.AppPaths.ExeDirectory);
                if (system is not { Activated: true }
                    || Services.Platform.Windows.SystemService.Status(system.ServiceName) != System.ServiceProcess.ServiceControllerStatus.Running)
                    return null;
                return await Services.Platform.Windows.SystemServiceClient.MeasureAsync(system, path, rules, TimeSpan.FromMinutes(4), ct);
            };
            // One copy per Windows account (U100): another copy is set up.
            if (!Program.StartedInBackground && OtherInstallation() is { } other)
            {
                QuitRequested = true;
                if (TrayIcon.GetIcons(this) is { } icons) foreach (var icon in icons) icon.IsVisible = false;
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dispatcher.UIThread.Post(() => _ = HandOverAsync(desktop, other));
                base.OnFrameworkInitializationCompleted();
                return;
            }
#endif
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

            AttachDesktopWindows(desktop);
            WarmUpEditor();
#if WINDOWS
            _backgroundNotifications = Services.Platform.Windows.BackgroundServiceNotifications.Start(ServiceLocator.Notifier);
#endif
            AppStatus.Changed += _ => Dispatcher.UIThread.Post(ApplyTrayStatus);
            TaskSchedulerService.Changed += () => Dispatcher.UIThread.Post(RefreshTrayStatus);
            RefreshTrayStatus();

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
            // 2. Release the engine's ownership: each restic run has
            //    ended by now (runs return only once restic has), and
            //    the kill-on-close job catches whatever this misses.
            // ShutdownRequested is cancellable. Never tear down a live
            // engine before the main window has finished its safe-quit flow.
            desktop.ShutdownRequested += (_, e) =>
            {
                if (QuitRequested) return;
                e.Cancel = true;
                Dispatcher.UIThread.Post(() =>
                {
                    _windowHost!.With(w => w.RequestQuit());
                });
            };
            desktop.Exit += (_, __) =>
            {
                DesktopSchedule.Stop();
#if WINDOWS
                ServiceLocator.BackgroundActivity?.Dispose();
                _backgroundNotifications?.Dispose();
#endif
                _windowHost?.Dispose();
                _removalRetry.Cancel();
                if (ForceQuitRequested) return;
                try { ServiceLocator.SnapshotMounts?.DisposeAllAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Unmount-on-shutdown threw"); }
                try { ServiceLocator.Maintenance?.ReleaseEngineOwnershipAfterStop(); }
                catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Engine release on shutdown threw"); }
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
            var taskResume = new System.Threading.CancellationTokenSource();
            var startedInBackground = Program.StartedInBackground;
            _startupTaskResume = taskResume;
            Dispatcher.UIThread.Post(async () =>
            {
                // Show the window before saving the task resume state. A
                // locked file can then be explained and cancelled in the UI.
                try { if (!startedInBackground) await System.Threading.Tasks.Task.Run(() => new SessionTaskControl().ResumeOnGuiLaunch(taskResume.Token)); }
                catch (OperationCanceledException) { /* The waiting panel explains that tasks stay paused. */ }
                catch (Exception ex)
                {
                    AppLogger.For<App>().Error(ex, "Could not resume scheduled tasks");
                    _windowHost!.Show();
                    await DestructiveConfirmWindow.ShowInfoAsync(_windowHost.GetOrCreate(), "Scheduled tasks are still paused",
                        "Backspace could not save the change that resumes them. Reopen the app to try again. " + ex.Message);
                }
                finally
                {
                    if (ReferenceEquals(_startupTaskResume, taskResume)) _startupTaskResume = null;
                    taskResume.Dispose();
                }
                if (taskResume.IsCancellationRequested || QuitRequested) return;
                // Schedules run while Backspace runs, including a start at sign-in (U043).
                DesktopSchedule.Start();
                if (!startedInBackground) Program.RunPostInitStartup();
                // Backups without their Healthchecks.io check get one (U187).
                try { await ServiceLocator.Healthcheck.EnsureChecksAsync(System.Threading.CancellationToken.None); }
                catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Could not create the Healthchecks.io checks"); }
                // A changed backup (its quiet period, its checks, its name)
                // changes its checks on Healthchecks.io too (U211); only what
                // differs is sent.
                ServiceLocator.Config.Changed += (_, _) => HealthchecksFollowSettings();
                try { await ServiceLocator.TestRestores.ResumeInterruptedAsync(); }
                catch (Exception ex) { AppLogger.For<App>().Error(ex, "Could not load interrupted test restores"); }
                // Finish deleting removed backups' copies once their drive or
                // network is reachable again (U029).
                ServiceLocator.Removals.StartRetrying(_removalRetry.Token);
            });

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
                    // Saved passwords (U153): a locked vault shows a banner,
                    // never a dialog.
                    await VaultStartup.RunAsync(startupSecrets);
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
        if (!ServiceLocator.Config.Current.Preferences.ShowTrayIcon && _windowHost?.Window is null && _windowHost is not null)
            RestoreMainWindow();
    }

    /// <summary>Runs made by the background service are in its history, not
    /// in these settings: the tray showed "Not backed up yet" after a
    /// successful background run (lab VM, 2 November 2026).</summary>
    private static void WithLatestRuns(System.Collections.Generic.List<Backup> backups)
    {
        foreach (var backup in backups)
        {
            RunRecord? latest;
            try { latest = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).FirstOrDefault(r => r.Status != BackupRunStatus.Running); }
            catch (Exception ex) { AppLogger.For<App>().Debug(ex, "History unavailable for the tray status"); continue; }
            if (latest is null || backup.LastRunStartUtc is { } known && known >= latest.StartedUtc) continue;
            backup.LastRunStartUtc = latest.StartedUtc;
            backup.LastRunStatus = latest.Status;
            backup.LastRunSummary = latest.Summary;
            if (latest.Status == BackupRunStatus.Success) backup.LastSuccessUtc = latest.EndedUtc ?? latest.StartedUtc;
        }
    }

#if WINDOWS
    private static Services.Platform.Windows.InstallationHandover.Other? OtherInstallation()
    {
        try
        {
            var root = AppContext.BaseDirectory.TrimEnd('\\');
            if (!OperatingSystem.IsWindows() || !string.Equals(Environment.ProcessPath, System.IO.Path.Combine(root, "Backspace.exe"), StringComparison.OrdinalIgnoreCase)
                || !Services.Platform.Windows.ServicePortableLayout.IsPortableConfig(root, AppPaths.ConfigRoot)) return null;
            return Services.Platform.Windows.InstallationHandover.Find(root);
        }
        catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Could not look for another copy of Backspace"); return null; }
    }

    private static async System.Threading.Tasks.Task HandOverAsync(IClassicDesktopStyleApplicationLifetime desktop,
        Services.Platform.Windows.InstallationHandover.Other other)
    {
        var thisVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        var reason = other.ThisCopyHasSettings ? "Moving isn't offered because this folder has backups of its own." : null;
        var window = new Views.InstallationChoiceWindow(other.Installation, other.Version, thisVersion, reason is null, reason);
        desktop.MainWindow = window;
        var choice = await window.AskAsync();
        try
        {
            if (choice == Views.InstallationChoiceWindow.Choice.Update)
                await System.Threading.Tasks.Task.Run(() => Services.Platform.Windows.InstallationHandover.UpdateOther(other));
            else if (choice == Views.InstallationChoiceWindow.Choice.Move)
                Services.Platform.Windows.InstallationHandover.StartMove(other);
        }
        catch (Exception ex)
        {
            AppLogger.For<App>().Warning(ex, "Handing over to the other copy did not finish");
            var info = new DestructiveConfirmWindow("Backspace could not do that", ex.Message, "");
            var closed = new System.Threading.Tasks.TaskCompletionSource();
            info.Closed += (_, _) => closed.TrySetResult();
            desktop.MainWindow = info;
            info.Show();
            await closed.Task;
        }
        desktop.Shutdown();
    }
#endif

    /// <summary>Opening a backup's editor is quick from the first time (U227,
    /// owner: "Why can't we precompute this when Backspace's main window first
    /// loads?"). A few seconds after start, while nothing else happens, each
    /// backup's rules are compiled (they are kept for the process) and one
    /// editor is built and thrown away, so its layout is ready too.</summary>
    private static void WarmUpEditor()
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                FilterTranslator.WarmUpRecommended();
                foreach (var backup in ServiceLocator.Config.Current.Backups)
                    _ = FilterTranslator.CompileRules(backup.FiltersText);
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        // No sources: nothing is measured or read.
                        var editor = new BackupEditorWindow(new Backup { Name = "warm-up" }, isNew: false);
                        editor.Measure(new Size(1100, 800));
                        editor.Arrange(new Rect(0, 0, 1100, 800));
                        (editor.DataContext as IDisposable)?.Dispose();
                    }
                    catch (Exception ex) { AppLogger.For<App>().Debug(ex, "Editor warm-up failed"); }
                }, DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex) { AppLogger.For<App>().Debug(ex, "Rule warm-up failed"); }
        });
    }

    internal TrayWindowHost AttachDesktopWindows(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_windowHost is not null) return _windowHost;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _windowHost = new TrayWindowHost(window =>
        {
            desktop.MainWindow = window;
            if (window is not null) window.Activated += (_, _) => RefreshTrayStatus();
            else if (QuitRequested) desktop.Shutdown();
#if WINDOWS
            // How often the window asks the background service (U224).
            if (window is not null)
            {
                void Shown() => Services.Platform.Windows.BackgroundServiceActivity.WindowShown =
                    window.IsVisible && window.WindowState != WindowState.Minimized;
                window.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty) Shown();
                };
                window.Closed += (_, _) => Services.Platform.Windows.BackgroundServiceActivity.WindowShown = false;
                Shown();
            }
            else Services.Platform.Windows.BackgroundServiceActivity.WindowShown = false;
#endif

        });
        if (!Program.StartedInBackground) _windowHost.GetOrCreate();
        if (Program.Coordinator is { } coordinator)
            coordinator.SecondInstanceForwarded += args => Dispatcher.UIThread.Post(() =>
            {
                if (QuitRequested) return;
                // Another copy is taking over (InstallationHandover, U100).
                if (args is [SingleInstanceCoordinator.QuitArgument]) { _windowHost.With(w => w.RequestQuit()); return; }
                Program.OpenFromBackground(); _windowHost.Show(w => w.HandleForwardedArgs(args));
            });
        return _windowHost;
    }

    private int _healthchecksPending;

    /// <summary>Once settings stop changing for two seconds, Backspace's
    /// Healthchecks.io checks follow them (U211).</summary>
    private void HealthchecksFollowSettings()
    {
        // A key that doesn't work waits for Settings (saving the key tries again).
        if (!ServiceLocator.Config.Current.Monitoring.HealthchecksEnabled || ServiceLocator.Healthcheck.LastProblem is not null) return;
        if (System.Threading.Interlocked.Exchange(ref _healthchecksPending, 1) == 1) return;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2));
            System.Threading.Volatile.Write(ref _healthchecksPending, 0);
            try { await ServiceLocator.Healthcheck.EnsureChecksAsync(System.Threading.CancellationToken.None); }
            catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Could not update the Healthchecks.io checks"); }
        });
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
                {
                    WithLatestRuns(backups);
                    return TrayScheduleStatus.Inspect(backups, scheduler, executable, DateTime.UtcNow);
                });
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
            if (_windowHost is not null) _windowHost.With(w => w.RequestQuit());
            else if (desktop.MainWindow is MainWindow window) window.RequestQuit();
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
        if (_windowHost is not null) await _windowHost.GetOrCreate().RequestForceQuitAsync();
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
            await window.RequestForceQuitAsync();
    }

    internal void RestoreMainWindow()
    {
        if (!QuitRequested) Program.OpenFromBackground();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (_windowHost is not null)
        {
            if (!QuitRequested) _windowHost.Show();
            return;
        }
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
    internal void ApplyAccentFromConfig()
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
        ApplyAccentToStandardControls(include);
    }

    /// <summary>Check boxes, the selected tab's underline, radio buttons and
    /// other standard controls take Backspace's accent, not Windows' (owner,
    /// 12 November 2026: "Since when do we use orange as the checkbox colors
    /// and underline for Schedule?" — their Windows accent is orange-red;
    /// those controls had always followed it, unlike the rest of the app).</summary>
    private ResourceDictionary? _standardAccent;

    private void ApplyAccentToStandardControls(ResourceInclude include)
    {
        try
        {
            // The app's own resources are looked up before the Fluent theme's,
            // so these replace Windows' accent for every standard control.
            var colors = new ResourceDictionary();
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                if (!include.TryGetResource("BS.Brush.Accent", variant, out var value) || value is not Avalonia.Media.ISolidColorBrush brush) continue;
                var accent = brush.Color;
                var shades = new ResourceDictionary
                {
                    ["SystemAccentColor"] = accent,
                    // Light: fills use Dark1; dark: fills use Light2. Both are the accent itself.
                    ["SystemAccentColorDark1"] = variant == ThemeVariant.Light ? accent : Shade(accent, -0.15),
                    ["SystemAccentColorDark2"] = Shade(accent, variant == ThemeVariant.Light ? -0.12 : -0.30),
                    ["SystemAccentColorDark3"] = Shade(accent, variant == ThemeVariant.Light ? -0.25 : -0.45),
                    ["SystemAccentColorLight1"] = variant == ThemeVariant.Dark ? accent : Shade(accent, 0.15),
                    ["SystemAccentColorLight2"] = variant == ThemeVariant.Dark ? accent : Shade(accent, 0.30),
                    ["SystemAccentColorLight3"] = Shade(accent, variant == ThemeVariant.Dark ? 0.15 : 0.45),
                };
                colors.ThemeDictionaries[variant] = shades;
            }
            Resources.MergedDictionaries.Add(colors);
            if (_standardAccent is not null) Resources.MergedDictionaries.Remove(_standardAccent);
            _standardAccent = colors;
        }
        catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Could not apply the accent to standard controls"); }
    }

    /// <summary>The colour mixed towards white (positive) or black (negative).</summary>
    internal static Avalonia.Media.Color Shade(Avalonia.Media.Color c, double amount)
    {
        byte Mix(byte v) => (byte)Math.Clamp(Math.Round(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount)), 0, 255);
        return Avalonia.Media.Color.FromArgb(c.A, Mix(c.R), Mix(c.G), Mix(c.B));
    }
}
