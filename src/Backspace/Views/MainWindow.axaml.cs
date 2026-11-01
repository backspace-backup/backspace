using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;

namespace Backspace.Views;

public partial class MainWindow : Window
{
    private async void OnLimitedProtection(object? sender, RoutedEventArgs e)
    {
        try
        {
            var affected = ServiceLocator.Config.Current.Backups.Where(b => !b.UseVss).ToList();
            var names = string.Join(", ", affected.Select(b => b.Name));
            var enable = await DestructiveConfirmWindow.ShowConfirmAsync(this,
                "Some backups cannot capture locked files",
                $"Windows snapshots are off for: {names}.\n\nOpen files may be skipped, and changing files may not form a consistent copy. Enable snapshots to restore protection. Snapshots need administrator access; if it is not on yet, the sidebar offers it. If you cannot approve that, ask your administrator.\n\nNAS folders and unsupported filesystems may require snapshots to stay off. Their source applications should be closed before backing up.",
                "Enable snapshots", "Keep current settings");
            if (!enable) return;
            if (affected.Any(b => ServiceLocator.Orchestrator.IsRunning(b.Id)))
            {
                await DestructiveConfirmWindow.ShowInfoAsync(this, "Wait for the current backup",
                    "Stop the affected backups or let them finish, then enable snapshots here. Running backups were left unchanged.");
                return;
            }
            var ids = affected.Select(b => b.Id).ToHashSet();
            ServiceLocator.Config.Update(c =>
            {
                foreach (var b in c.Backups.Where(b => ids.Contains(b.Id)))
                {
                    b.UseVss = true;
                    b.VssDisabledAcknowledged = false;
                }
            });
        }
        catch (Exception ex)
        {
            await DestructiveConfirmWindow.ShowInfoAsync(this, "Could not update snapshot protection", ex.Message);
        }
    }

    /// <summary>The sidebar badge's fix: close its flyout so Windows'
    /// prompt isn't hidden behind it, then ask for approval.</summary>
    private async void OnAllowAdministratorAccess(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Button>("AdministratorAccessBadge")?.Flyout?.Hide();
        if (DataContext is MainWindowViewModel vm)
            await vm.Backups.AllowAdministratorAccessCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// How many rotated frames exist per accent for the syncing animation.
    /// Must match the -SyncingFrameCount passed to tools/build-icon.ps1.
    /// </summary>
    private const int SyncingFrameCount = 4;

    /// <summary>Frame period. Dropbox uses ~150-200ms; 160ms reads as "steady".</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(160);

    private DispatcherTimer? _syncTimer;
    private int _syncFrame;

    // Cached event handler refs so OnClosed can unsubscribe by the same
    // delegate identity that subscribed. Inline lambdas would compile
    // to fresh delegate instances on each `+=` and `-=` call, so the
    // unsubscribe would silently no-op (the same pattern that bit the
    // earlier MeteredNetworkWatchdog audit). MainWindow is process-
    // lifetime today so the practical leak is small, but keeping the
    // pattern correct survives future refactors that recreate the
    // window (theme reload, settings reset).
    private EventHandler? _onConfigChanged;
    private Action<string[]>? _onSecondInstanceForwarded;

    // Decoded icons cached per asset key so the syncing animation
    // doesn't allocate a new WindowIcon + Bitmap every 160ms — that
    // pattern leaked GDI ICON handles ~6/sec until the per-process
    // quota (10k) was exhausted, after which Win32Icon.CreateIcon
    // failed with "operation completed successfully" (LastError 0
    // returned for a NULL handle). Eventually a notification window's
    // icon-copy step crashed the entire process. Cached entries are
    // reused for the lifetime of the app — at most ~5 accents × 5
    // states (idle + 4 syncing frames) = 25 entries.
    private readonly System.Collections.Generic.Dictionary<string, WindowIcon> _iconCache = new();
    private readonly System.Collections.Generic.Dictionary<string, Bitmap> _bitmapCache = new();

    internal bool ReleaseWindowOnTrayClose { get; init; }
    internal bool WasClosedToTray { get; private set; }

    public MainWindow() : this(routeForwardedArguments: true) { }

    internal MainWindow(bool routeForwardedArguments)
    {
        InitializeComponent();
        WindowPlacement.Attach(this);

        // Tear down VM event subscriptions on close so the app shuts
        // down cleanly. Without this, MainWindowViewModel and its child
        // VMs hold references to ConfigStore.Changed / Orchestrator
        // events for the lifetime of the process — fine while the
        // window is alive but a leak hazard if MainWindow is ever
        // re-created (e.g. settings reset, theme change with reload).
        Closed += OnClosed;
        Activated += OnActivated;
        // Intercept the X-button close: depending on user prefs +
        // running-op state we either hide-to-tray, ask the user to
        // confirm (cancels the in-flight ops), or proceed straight
        // to shutdown.
        Closing += OnClosingAsync;

        // Footer version line — read once from the assembly's
        // InformationalVersion (or AssemblyVersion as a fallback).
        // Avoids hardcoding "v1.0" in XAML which would silently get
        // stale across releases.
        if (FooterVersionText is not null)
        {
            try
            {
                var asm = typeof(MainWindow).Assembly;
                var ver = asm
                    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                  ?? asm.GetName().Version?.ToString(3)
                  ?? "1.0";
                // Drop the +commitsha suffix some build pipelines add.
                var plus = ver.IndexOf('+');
                if (plus > 0) ver = ver[..plus];
                FooterVersionText.Text = $"v{ver}";
            }
            catch { FooterVersionText.Text = "v1.0"; }
        }

        // Auto-update light: fire-and-forget GitHub Releases check on
        // launch. Surfaces a non-blocking toast if a newer version is
        // out. See UpdateChecker for failure isolation; the check
        // never blocks the UI thread.
        try { ServiceLocator.UpdateCheck.Begin(); } catch { /* never block startup */ }

        // Taskbar / alt-tab icon + sidebar logo: both driven by the chosen
        // accent, both animated together when AppStatus goes Syncing.
        ApplyAccentIcon();
        _onConfigChanged = (_, __) => Dispatcher.UIThread.Post(ApplyAccentIcon);
        ServiceLocator.Config.Changed += _onConfigChanged;
        AppStatus.Changed += OnActivityChanged;
        OnActivityChanged(AppStatus.Current);

        // If the app was launched via the Explorer right-click verb, the
        // path is sitting on Program.PendingRestorePath. Route the user
        // to the Restore tab on first paint so they land where they expect.
        Opened += (_, _) =>
        {
            ConsumePendingRestorePath();
            // Drain any forwarded args that arrived BEFORE we hooked
            // the SecondInstanceForwarded event below. A second
            // launch arriving in the small window between
            // Coordinator.StartServer (Program.cs) and this Opened
            // handler subscribing the event would otherwise be
            // silently dropped — the event fires once with no
            // subscribers, the args evaporate.
            if (Backspace.Program.Coordinator?.DrainPending() is string[] pending)
                HandleForwardedArgs(pending);
        };

        // When a SECOND launch forwards its args via the named pipe
        // (single-instance coordinator), the primary process gets them
        // here. Marshal to the UI thread, parse out --restore-prompt,
        // and route to the Restore tab. Also bring the window forward
        // so the user sees the result of their right-click.
        if (routeForwardedArguments && Backspace.Program.Coordinator is { } coord)
        {
            _onSecondInstanceForwarded = args =>
                Dispatcher.UIThread.Post(() => HandleForwardedArgs(args));
            coord.SecondInstanceForwarded += _onSecondInstanceForwarded;
        }
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) _ = vm.RefreshExternalStateAsync();
    }

    internal void HandleForwardedArgs(string[] args)
    {
        BringToFront();
        var path = ExtractRestorePath(args);
        if (!string.IsNullOrEmpty(path))
        {
            Backspace.Program.PendingRestorePath = path;
            ConsumePendingRestorePath();
        }
    }

    private void ConsumePendingRestorePath()
    {
        var path = Backspace.Program.PendingRestorePath;
        if (string.IsNullOrEmpty(path)) return;
        Backspace.Program.PendingRestorePath = null;
        try
        {
            // Stash the path on the singleton RestoreViewModel so it
            // can auto-select the matching backup + load that file's
            // version history. Then navigate.
            if (DataContext is ViewModels.MainWindowViewModel mvm)
                mvm.Restore.HandleShellLaunchPath(path);
            AppNavigator.Go(NavItem.Restore);
        }
        catch (Exception ex)
        {
            AppLogger.For<MainWindow>().Warning(ex, "Failed to route to Restore for shell-launch path {Path}", path);
        }
    }

    private static string? ExtractRestorePath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--restore-prompt") return args[i + 1];
        return null;
    }

    private void BringToFront()
    {
        try
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }
        catch { /* best-effort focus grab */ }
    }

    // ---- icon state machine ---------------------------------------------

    private void OnActivityChanged(AppActivity activity)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // A window hidden to the tray has no visible title-bar icon
            // to animate — don't burn a 160ms timer per hidden window
            // for the whole duration of every backup/restore/verify.
            // (The tray icon's own state is handled by App, not here.)
            if (!IsVisible)
            {
                StopSyncingIconAnimation();
                return;
            }
            if (activity == AppActivity.Syncing)
                StartSyncingIconAnimation();
            else
            {
                StopSyncingIconAnimation();
                ApplyAccentIcon();
            }
        });
    }

    private void StartSyncingIconAnimation()
    {
        if (_syncTimer is not null) return;
        _syncFrame = 0;
        _syncTimer = new DispatcherTimer { Interval = FrameInterval };
        _syncTimer.Tick += (_, __) =>
        {
            _syncFrame = (_syncFrame + 1) % SyncingFrameCount;
            SetIconFromAccent(isSyncing: true, frameIndex: _syncFrame);
        };
        _syncTimer.Start();
        SetIconFromAccent(isSyncing: true, frameIndex: 0);
    }

    private void StopSyncingIconAnimation()
    {
        _syncTimer?.Stop();
        _syncTimer = null;
    }

    private void ApplyAccentIcon() => SetIconFromAccent(isSyncing: false, frameIndex: 0);

    // Open-on-demand is cheap (embedded resource read) and matches how
    // Avalonia itself resolves an <Window Icon="avares://..."> attribute —
    // which is the proven pattern, so accent swaps stay reliable.
    // Decoded results are cached per resource name (see _iconCache /
    // _bitmapCache) so the syncing animation doesn't reallocate every
    // tick.
    private void SetIconFromAccent(bool isSyncing, int frameIndex)
    {
        var accent = ServiceLocator.Config.Current.Preferences.Accent;
        var resName = isSyncing
            ? $"Backspace-{accent}-Syncing-{frameIndex}.ico"
            : $"Backspace-{accent}.ico";
        var uri = new Uri($"avares://Backspace/Assets/{resName}");

        // Window icon: cached. Setting Icon to the SAME WindowIcon
        // instance the property already holds is a fast no-op in
        // Avalonia's PlatformImpl, so even ticking the same animation
        // frame repeatedly has zero GDI cost.
        try
        {
            if (!_iconCache.TryGetValue(resName, out var icon))
            {
                using var s = AssetLoader.Open(uri);
                icon = new WindowIcon(s);
                _iconCache[resName] = icon;
            }
            if (!ReferenceEquals(Icon, icon)) Icon = icon;
        }
        catch (Exception ex)
        {
            AppLogger.For<MainWindow>().Warning(ex, "Icon load failed for {Res}", resName);
        }

        // Sidebar Image: same caching strategy. Without this, every
        // animation tick allocated a fresh Bitmap whose GDI surface
        // bled through the same exhaustion path as Window.Icon.
        if (SidebarLogo is not null)
        {
            try
            {
                if (!_bitmapCache.TryGetValue(resName, out var bmp))
                {
                    using var s = AssetLoader.Open(uri);
                    bmp = new Bitmap(s);
                    _bitmapCache[resName] = bmp;
                }
                if (!ReferenceEquals(SidebarLogo.Source, bmp)) SidebarLogo.Source = bmp;
            }
            catch (Exception ex)
            {
                AppLogger.For<MainWindow>().Warning(ex, "Sidebar image decode failed for {Res}", resName);
            }
        }
    }

    // ---- window-chrome handlers ----------------------------------------

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnRestoresNavClick(object? sender, RoutedEventArgs e) => AppNavigator.Go(NavItem.ActiveRestores);

    private bool _explicitQuit;
    private bool _quitInProgress;
    private bool _forceQuitInProgress;
    private bool _savingQuitPause;
    private string? _sessionPauseGeneration;
    internal SessionTaskControl SessionTasks { get; set; } = new();
    private static string QuitScheduleExplanation => "Automatic backups stop until Backspace starts again.";

    /// <summary>Tray Quit and window close share the same operation checks.</summary>
    public void RequestQuit()
    {
        _explicitQuit = true;
        Show();
        Activate();
        Close();
    }

    private IDisposable? _shutdownAdmission;

    /// <summary>Updates never cancel active work. Seal admission, recheck the
    /// actual operation leases, and keep the seal until the process exits.</summary>
    internal bool TryQuitForUpdate(Action prepare)
    {
        if (_quitInProgress || _forceQuitInProgress || App.QuitRequested
            || HasAnyRunningOperation() || ServiceLocator.Maintenance.IsBlocked) return false;
        if (!ServiceLocator.Maintenance.TryBeginShutdown(out _shutdownAdmission)) return false;
        try
        {
            if (HasAnyRunningOperation()) { _shutdownAdmission?.Dispose(); _shutdownAdmission = null; return false; }
            prepare();
            App.QuitRequested = true;
            _explicitQuit = true;
            Close();
            return true;
        }
        catch
        {
            WindowsUpdateRuntime.Pending = null;
            App.QuitRequested = false;
            _shutdownAdmission?.Dispose(); _shutdownAdmission = null;
            throw;
        }
    }

    internal async System.Threading.Tasks.Task RequestForceQuitAsync()
    {
        // Force quit must remain available while an ordinary quit is
        // waiting for an operation that has not stopped.
        if (_forceQuitInProgress || App.QuitRequested || _savingQuitPause) return;
        Show(); Activate();
        _forceQuitInProgress = true;
        try
        {
            if (!await DestructiveConfirmWindow.ShowConfirmAsync(this,
                "Force quit Backspace?",
                "Active backups, restores and maintenance will be interrupted immediately. " +
                "An unfinished restore may contain incomplete files. Mounted drives may need reopening. " +
                QuitScheduleExplanation,
                "Force quit now", "Keep running")) return;
            App.CancelPendingStartupTaskResume();
            try { _sessionPauseGeneration = await System.Threading.Tasks.Task.Run(() => SessionTasks.PauseForCurrentSession(force: true)); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLogger.For<MainWindow>().Warning(ex, "Could not save the task pause before forced quit");
                await DestructiveConfirmWindow.ShowInfoAsync(this, "Could not pause scheduled tasks",
                    "Backspace is staying open because Windows could not save or verify the pause. Try Quit again. " + ex.Message);
                return;
            }
            if (_shutdownAdmission is null)
                ServiceLocator.Maintenance.TryBeginShutdown(out _shutdownAdmission);
            CancelEverything();
            // Stop only this instance's engine. A stalled engine cannot hold
            // the UI open indefinitely; Windows also owns its process job.
            if (ServiceLocator.BackrestSidecar is { } sidecar)
            {
                try { await sidecar.StopAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) { AppLogger.For<MainWindow>().Warning(ex, "Forced engine shutdown will fall back to process-exit cleanup"); }
            }
            App.ForceQuitRequested = true;
            App.QuitRequested = true;
            Close();
        }
        finally { _forceQuitInProgress = false; }
    }

    private async void OnClosingAsync(object? sender, WindowClosingEventArgs e)
    {
        WasClosedToTray = false;
        if (App.QuitRequested) return;
        if (_quitInProgress || _forceQuitInProgress) { e.Cancel = true; return; }
        var hadWork = HasAnyRunningOperation();
        var prefs = ServiceLocator.Config.Current.Preferences;
        if (!_explicitQuit && prefs.MinimizeToTrayOnClose && prefs.ShowTrayIcon)
        {
            if (ReleaseWindowOnTrayClose)
            {
                // The app host owns the model across native windows. Disposing
                // it here would cancel the user's active restores and backups.
                WasClosedToTray = true;
                return;
            }
            e.Cancel = true;
            Hide();
            return;
        }

        e.Cancel = true;
        _quitInProgress = true;
        var committed = false;
        try
        {
            if (hadWork && !await ConfirmStopAsync()) return;
            if (!ServiceLocator.Maintenance.TryBeginShutdown(out _shutdownAdmission)) return;
            // A reader can enter between the first idle check and closing
            // admission. Recheck after the gate before committing the exit.
            if (!hadWork && HasAnyRunningOperation() && !await ConfirmStopAsync()) return;
            App.CancelPendingStartupTaskResume();
            _savingQuitPause = true;
            try { _sessionPauseGeneration = await System.Threading.Tasks.Task.Run(() => SessionTasks.PauseForCurrentSession()); }
            finally { _savingQuitPause = false; }
            CancelEverything();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (HasAnyRunningOperation() && DateTime.UtcNow < deadline)
                await System.Threading.Tasks.Task.Delay(100);
            if (HasAnyRunningOperation())
            {
                await DestructiveConfirmWindow.ShowInfoAsync(this,
                    "Still stopping safely",
                    "An operation has not finished stopping, or storage maintenance is still running. " +
                    "Backspace will stay open to protect your files. Quitting has been cancelled; try Quit again after it finishes.");
                return;
            }
            // Keep admission closed through desktop.Exit and daemon cleanup.
            committed = true;
            App.QuitRequested = true;
            Dispatcher.UIThread.Post(Close);
        }
        catch (OperationCanceledException) { /* Cancel quit leaves the window and previous task state intact. */ }
        catch (Exception ex)
        {
            AppLogger.For<MainWindow>().Warning(ex, "Could not finish safe shutdown");
            await DestructiveConfirmWindow.ShowInfoAsync(this, "Backspace could not finish quitting",
                "The app is staying open because it could not safely pause tasks or stop active work. " + ex.Message);
        }
        finally
        {
            if (!committed && !App.ForceQuitRequested)
            {
                try { await System.Threading.Tasks.Task.Run(() => SessionTasks.CancelQuit(_sessionPauseGeneration)); }
                catch (Exception ex) { AppLogger.For<MainWindow>().Warning(ex, "Could not undo the cancelled quit pause; reopening Backspace will retry"); }
                _sessionPauseGeneration = null;
                _shutdownAdmission?.Dispose();
                _shutdownAdmission = null;
                _explicitQuit = false;
            }
            _quitInProgress = false;
        }
    }

    private System.Threading.Tasks.Task<bool> ConfirmStopAsync() =>
        DestructiveConfirmWindow.ShowConfirmAsync(this,
            title: "Work is still running.",
            subtitle: "Quitting stops active backups, restores and test restores. " +
                "Backspace will wait for them to stop safely. Storage maintenance must finish first. " +
                QuitScheduleExplanation,
            confirmLabel: "Stop work and quit", cancelLabel: "Keep running");
    internal static bool HasAnyRunningOperation() =>
        ServiceLocator.Orchestrator.SnapshotRunningBackupIds().Count > 0
        || ServiceLocator.ActiveRestores.HasRunning
        || ServiceLocator.SnapshotMounts?.HasPendingOperations == true
        || ServiceLocator.Maintenance.HasReaders
        || ServiceLocator.Maintenance.IsActive
        || new SessionTaskControl().HasRunningTasks;

    internal static void CancelEverything()
    {
        foreach (var id in ServiceLocator.Orchestrator.SnapshotRunningBackupIds())
            ServiceLocator.Orchestrator.CancelRun(id, "User quit Backspace");
        ServiceLocator.ActiveRestores.CancelAll();
        ServiceLocator.SnapshotMounts?.CancelPending();
        ServiceLocator.Maintenance.CancelReaders();
    }

    /// <summary>
    /// Sidebar footer GitHub link. Launches the user's default browser
    /// at the project repo.
    /// </summary>
    private void OnOpenGitHub(object? sender, RoutedEventArgs e) =>
        OpenExternal("https://github.com/backspace-backup/backspace", "GitHub");

    /// <summary>
    /// Opens the Backrest GitHub repo — matches the "Powered by
    /// Backrest" attribution copy in the sidebar footer. Backrest is
    /// the upstream Go daemon Backspace embeds as a sidecar to drive
    /// restic, so the upstream repo is the right place to send
    /// curious users.
    /// </summary>
    private void OnOpenBackrest(object? sender, RoutedEventArgs e) =>
        OpenExternal("https://github.com/garethgeorge/backrest", "Backrest");

    private void OnClosed(object? sender, EventArgs e)
    {
        try { if (!WasClosedToTray) (DataContext as MainWindowViewModel)?.Dispose(); }
        catch (Exception ex) { AppLogger.For<MainWindow>().Warning(ex, "VM dispose threw"); }
        // Break the closed view's bindings to the retained model. Reopening
        // builds a new visual tree, with fresh view event subscriptions.
        DataContext = null;
        StopSyncingIconAnimation();
        // Unsubscribe by the cached delegate refs so the same identity
        // that subscribed is the one detached. Without the cached refs
        // a fresh `(_, __) => …` lambda would compile to a different
        // delegate than the one ServiceLocator.Config.Changed is
        // holding, leaving the subscription live for the lifetime of
        // the process.
        if (_onConfigChanged is not null)
        {
            ServiceLocator.Config.Changed -= _onConfigChanged;
            _onConfigChanged = null;
        }
        AppStatus.Changed -= OnActivityChanged;
        if (_onSecondInstanceForwarded is not null
            && Backspace.Program.Coordinator is { } coord)
        {
            coord.SecondInstanceForwarded -= _onSecondInstanceForwarded;
            _onSecondInstanceForwarded = null;
        }
        // Drop cached icons + bitmaps. Bitmaps wrap native GDI surfaces
        // and are IDisposable; WindowIcon doesn't expose Dispose in
        // current Avalonia, so we just release the reference and let
        // finalisation reclaim the kernel handle. Closing to the tray
        // also comes through here: the next open gets a new native
        // window, so none of these resources should retain the old view.
        foreach (var b in _bitmapCache.Values)
        {
            try { b.Dispose(); } catch { /* best-effort */ }
        }
        _bitmapCache.Clear();
        _iconCache.Clear();
        Closed -= OnClosed;
        Activated -= OnActivated;
    }

    private static void OpenExternal(string url, string label)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.For<MainWindow>().Warning(ex, "Couldn't open {Label} link", label);
        }
    }
}
