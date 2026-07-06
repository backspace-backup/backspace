using System;
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

    public MainWindow()
    {
        InitializeComponent();

        // Tear down VM event subscriptions on close so the app shuts
        // down cleanly. Without this, MainWindowViewModel and its child
        // VMs hold references to ConfigStore.Changed / Orchestrator
        // events for the lifetime of the process — fine while the
        // window is alive but a leak hazard if MainWindow is ever
        // re-created (e.g. settings reset, theme change with reload).
        Closed += OnClosed;
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
        if (Backspace.Program.Coordinator is { } coord)
        {
            _onSecondInstanceForwarded = args =>
                Dispatcher.UIThread.Post(() => HandleForwardedArgs(args));
            coord.SecondInstanceForwarded += _onSecondInstanceForwarded;
        }
    }

    private void HandleForwardedArgs(string[] args)
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

    /// <summary>
    /// Closing-event interceptor. Three branches:
    ///   1. The user explicitly chose "Quit" (tray menu, etc.) →
    ///      <see cref="App.QuitRequested"/> is true → fall through
    ///      to normal shutdown.
    ///   2. There's a long-running operation in flight (backup,
    ///      prune, check, restore, erase) → ask the user to confirm.
    ///      Cancel keeps the window open; Confirm cancels the ops
    ///      and proceeds.
    ///   3. Otherwise honour MinimizeToTrayOnClose: hide the window
    ///      instead of shutting down, leaving the tray icon as the
    ///      way back in. Matches what Slack / Discord / Signal /
    ///      Steam do — close-X means "out of sight", not "quit".
    /// </summary>
    private async void OnClosingAsync(object? sender, Avalonia.Controls.WindowClosingEventArgs e)
    {
        if (App.QuitRequested) return;

        var hasRunning = HasAnyRunningOperation();
        if (hasRunning)
        {
            // Cancel the close so the dialog can run; if the user
            // confirms we re-issue the close manually after the
            // cancellation propagates. The two cases match what the
            // user asked for: confirm BEFORE losing work.
            e.Cancel = true;
            try
            {
                var ok = await DestructiveConfirmWindow.ShowConfirmAsync(
                    this,
                    title: "Backups are running.",
                    subtitle: "Closing Backspace will cancel everything in flight (backup / prune / check / restore). " +
                              "Click Quit to stop them and exit, or Cancel to keep them running.",
                    confirmLabel: "Quit anyway",
                    cancelLabel: "Keep running");
                if (!ok) return;
                CancelEverything();
                App.QuitRequested = true;
                // Re-issue close after the dialog returns; the
                // re-entry will hit the QuitRequested short-circuit.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Close());
            }
            catch (Exception ex)
            {
                AppLogger.For<MainWindow>().Warning(ex, "Exit-with-running-ops confirm dialog threw");
            }
            return;
        }

        // No running ops. Hide-to-tray if the user has the
        // preference on (default) and a tray icon is available.
        var prefs = ServiceLocator.Config.Current.Preferences;
        if (prefs.MinimizeToTrayOnClose && prefs.ShowTrayIcon)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private static bool HasAnyRunningOperation()
    {
        try
        {
            var orch = ServiceLocator.Orchestrator;
            if (orch is null) return false;
            // Orchestrator tracks running backup ids; any non-empty
            // entry counts. Maintenance / erase / prune-to-latest
            // acquire the maintenance lock; we approximate "in
            // flight" by checking if the lock isn't currently
            // available. Both signals are conservative — false
            // positives at most prompt an extra confirm.
            foreach (var b in ServiceLocator.Config.Current.Backups)
                if (orch.IsRunning(b.Id)) return true;
            if (ServiceLocator.Maintenance?.IsActive == true) return true;
        }
        catch { /* defensive — if ServiceLocator wasn't initialised, assume nothing running */ }
        return false;
    }

    private static void CancelEverything()
    {
        try
        {
            var orch = ServiceLocator.Orchestrator;
            if (orch is null) return;
            foreach (var b in ServiceLocator.Config.Current.Backups)
                if (orch.IsRunning(b.Id))
                    try { orch.CancelRun(b.Id, "User quit Backspace"); } catch { }
        }
        catch (Exception ex)
        {
            AppLogger.For<MainWindow>().Warning(ex, "CancelEverything threw");
        }
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
        try { (DataContext as MainWindowViewModel)?.Dispose(); }
        catch (Exception ex) { AppLogger.For<MainWindow>().Warning(ex, "VM dispose threw"); }
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
        // finalisation reclaim the kernel handle. MainWindow is
        // process-lifetime today, but a future "settings reset reload"
        // path would otherwise leak the entire prior generation of
        // cached bitmaps until the GC's next gen-2 sweep.
        foreach (var b in _bitmapCache.Values)
        {
            try { b.Dispose(); } catch { /* best-effort */ }
        }
        _bitmapCache.Clear();
        _iconCache.Clear();
        Closed -= OnClosed;
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
