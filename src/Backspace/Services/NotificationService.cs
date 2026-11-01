using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Backspace.Models;
using Backspace.Services.Platform;
using Serilog;

namespace Backspace.Services;

public enum NotificationSeverity { Info, Success, Warning, Failure }

/// <summary>
/// User-facing alerts. Three channels, used together according to severity and user preference:
///
///   1. Windows toast (respects Focus Assist / Do Not Disturb)
///   2. Topmost app alert window (bypasses DND; optional via BypassDndOnFailure/Success)
///   3. Sound (bypasses DND)
///
/// We don't take a dependency on Microsoft.Toolkit.Uwp.Notifications or WinRT
/// ToastNotificationManager here for portability — we use a simple tray-style
/// popup window that matches the app's Archival Calm theme. The DND-bypass
/// path is our primary alert channel anyway for failures.
/// </summary>
public sealed class NotificationService
{
    internal Action<string, string, NotificationSeverity, Action?, NotificationChannels>? NotificationObserved { get; set; }
    internal Func<BackgroundNotification, Task>? DurableDelivery { get; set; }
    private static ILogger _log => AppLogger.For<NotificationService>();
    private readonly ConfigStore _config;
    public NotificationService(ConfigStore config) => _config = config;

    /// <summary>
    /// Backwards-compatible overload for existing call sites that don't
    /// pass an onClick action. Forwards to the click-aware overload
    /// with onClick=null (the toast body still dismisses, but doesn't
    /// navigate).
    /// </summary>
    public Task NotifyAsync(string title, string detail, NotificationSeverity severity)
        => NotifyAsync(title, detail, severity, onClick: null, dismissAfter: null);

    public Task NotifyAsync(string title, string detail, NotificationSeverity severity, Action? onClick)
        => NotifyAsync(title, detail, severity, onClick, dismissAfter: null);

    public Task NotifyAsync(string title, string detail, NotificationSeverity severity, TimeSpan? dismissAfter)
        => NotifyAsync(title, detail, severity, onClick: null, dismissAfter: dismissAfter);

    /// <summary>
    /// Show a notification. If <paramref name="onClick"/> is non-null,
    /// the toast body becomes clickable and invokes the action when the
    /// user clicks anywhere on it (in addition to the always-present ✕
    /// dismiss button). The orchestrator wires this to "open the main
    /// window → Activity & logs → select this backup's last run" so the
    /// user can jump straight to the failure detail from the toast.
    ///
    /// <paramref name="dismissAfter"/> overrides the default 8s tray
    /// auto-dismiss for callers whose toast carries a list users will
    /// want to actually read (the test-restore per-destination
    /// summary is one — 8s wasn't enough). Topmost alerts ignore this
    /// (they're sticky-by-design until the user clicks ✕).
    /// </summary>
    public Task NotifyAsync(string title, string detail, NotificationSeverity severity, Action? onClick, TimeSpan? dismissAfter)
        => NotifyAsync(title, detail, severity, onClick, dismissAfter, channelsOverride: null);

    /// <summary>
    /// Show a notification. When <paramref name="channelsOverride"/>
    /// is supplied the caller's per-event matrix wins — used by the
    /// backup orchestrator so Success/Skipped/Failure each fire only
    /// the channels the user opted into for that verdict. With null,
    /// channels fall back to "always show" (interactive one-shots
    /// like 'Restore complete' or 'Test send succeeded' where the
    /// user just triggered the action and expects feedback).
    /// </summary>
    public async Task NotifyAsync(
        string title, string detail, NotificationSeverity severity,
        Action? onClick, TimeSpan? dismissAfter, NotificationChannels? channelsOverride,
        string? backupId = null, string? runId = null, Action? onPresented = null, Func<bool>? canPresent = null)
    {
        // Always log the notification, even when no UI is available — the
        // log is the canonical record of "what did the app tell the user".
        var level = severity switch
        {
            NotificationSeverity.Failure => Serilog.Events.LogEventLevel.Warning,
            NotificationSeverity.Warning => Serilog.Events.LogEventLevel.Warning,
            _ => Serilog.Events.LogEventLevel.Information,
        };
        _log.Write(level, "Notify {Severity}: {Title} — {Detail}", severity, title, detail);

        // Resolve effective channels: explicit override > severity default.
        // The interactive default is "show on every channel except email"
        // because the user just performed an action and wants confirmation,
        // but emailing themselves for a manual 'Restore complete' would
        // be noise.
        var effective = channelsOverride ?? new NotificationChannels { Toast = true, Sound = true, Email = false };
        if (DurableDelivery is { } save)
        {
            if (effective.Toast || effective.Sound)
                await save(new(Guid.NewGuid().ToString("N"), DateTime.UtcNow, title, detail,
                    severity, effective.Toast, effective.Sound, backupId, runId)).ConfigureAwait(false);
            return;
        }
        NotificationObserved?.Invoke(title, detail, severity, onClick, effective);
        if (canPresent is not null && !canPresent()) return;

        if (effective.Sound)
            PlaySoundFor(severity);

        // Pop-up windows only make sense under a real desktop lifetime.
        // Unattended CLI runs (`--run`) and the test harness both use a
        // headless/console lifetime where creating a Window would crash —
        // fall back to a silent notification in those contexts.
        if (!IsDesktopUi()) return;
        if (!effective.Toast) { onPresented?.Invoke(); return; }

        // Failure escalates to a topmost sticky alert IFF the user
        // opted Toast in for failures. The escalation itself isn't a
        // separate toggle — it's the intrinsic shape of a failure
        // notification (bypasses Focus Assist, never auto-dismisses).
        // The user can still mute failures entirely by leaving
        // Failure.Toast=false in Settings.
        if (severity == NotificationSeverity.Failure)
        {
            if (effective.Toast)
                await ShowTopmostAlertAsync(title, detail, severity, onClick, onPresented, canPresent);
            return;
        }

        if (effective.Toast)
            ShowTrayPopup(title, detail, severity, onClick, dismissAfter, onPresented, canPresent);
    }

    private static bool IsDesktopUi() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;

    // ---- sound ----

    private static void PlaySoundFor(NotificationSeverity s)
    {
        // System.Media.SystemSounds is a Windows-only assembly — its
        // type is forwarded to System.Windows.Extensions, which we only
        // reference on the Windows TFM. Cross-platform desktop tone-
        // playback is surprisingly ugly (no portable BCL surface). Skip
        // silently off-Windows; the visual notification still fires,
        // and the alert window's accent-bar plus optional sticky-
        // topmost behaviour cover the same "demand attention" intent.
        if (!PlatformInfo.IsWindows) return;
#if WINDOWS
        Platform.Windows.WindowsSystemSounds.PlayFor(s);
#endif
    }

    // ---- toasts / tray popups ----

    private void ShowTrayPopup(string title, string detail, NotificationSeverity s, Action? onClick, TimeSpan? dismissAfter = null, Action? onPresented = null, Func<bool>? canPresent = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (canPresent is not null && !canPresent()) return;
                var w = BuildAlertWindow(title, detail, s, topmost: false,
                    autoDismissAfter: dismissAfter ?? TimeSpan.FromSeconds(8), onClick: onClick);
                if (w is null) return;  // ctor failed — already logged
                PositionBottomRight(w);
                w.Show();
                onPresented?.Invoke();
            }
            catch (Exception ex)
            {
                AppLogger.For<NotificationService>().Warning(ex, "ShowTrayPopup threw");
            }
        });
    }

    private Task ShowTopmostAlertAsync(string title, string detail, NotificationSeverity s, Action? onClick, Action? onPresented = null, Func<bool>? canPresent = null)
    {
        var tcs = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (canPresent is not null && !canPresent()) { tcs.TrySetResult(); return; }
                var w = BuildAlertWindow(title, detail, s, topmost: true, autoDismissAfter: null, onClick: onClick);
                if (w is null) { tcs.TrySetResult(); return; }
                PositionBottomRight(w);
                w.Closed += (_, __) => tcs.TrySetResult();
                w.Show();
                onPresented?.Invoke();

                // Flash the taskbar so users notice even on a second monitor.
                // No-op on macOS / Linux — there's no portable equivalent
                // to FlashWindowEx; the topmost+sticky behaviour above
                // already keeps the alert visible and the dock icon
                // bouncing on macOS comes from being a foreground
                // application, which the topmost window triggers.
                if (PlatformInfo.IsWindows)
                {
#if WINDOWS
                    try
                    {
                        if (w.TryGetPlatformHandle()?.Handle is { } h && h != IntPtr.Zero)
                            Platform.Windows.WindowsTaskbarFlash.Flash(h);
                    }
                    catch { }
#endif
                }
            }
            catch (Exception ex)
            {
                AppLogger.For<NotificationService>().Warning(ex, "ShowTopmostAlertAsync threw");
                tcs.TrySetResult();
            }
        });
        return tcs.Task;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);

    /// <summary>Asks Windows to paint the whole window now, which redraws it
    /// even when its first frame was never shown. Elsewhere Avalonia's own
    /// invalidation is enough.</summary>
    internal static void Repaint(Window window)
    {
        try
        {
            const uint Invalidate = 0x0001, UpdateNow = 0x0100, AllChildren = 0x0080, Frame = 0x0400;
            if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle() is { } handle && handle.Handle != IntPtr.Zero)
                RedrawWindow(handle.Handle, IntPtr.Zero, IntPtr.Zero, Invalidate | UpdateNow | AllChildren | Frame);
            else
                window.InvalidateVisual();
        }
        catch (Exception ex) { AppLogger.For<NotificationService>().Debug(ex, "Alert repaint failed"); }
    }

    internal static Window? BuildAlertWindow(string title, string detail, NotificationSeverity s, bool topmost, TimeSpan? autoDismissAfter, Action? onClick)
    {
        var accent = s switch
        {
            NotificationSeverity.Failure => Color.Parse("#B5634B"),
            NotificationSeverity.Warning => Color.Parse("#C49A6C"),
            NotificationSeverity.Success => Color.Parse("#5B8E6E"),
            _ => Color.Parse("#9A9890"),
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("avares://Backspace/Assets/Fonts#Manrope"),
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Foreground = new SolidColorBrush(Color.Parse("#E8E6DF")),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var detailBlock = new TextBlock
        {
            Text = detail,
            FontFamily = FontFamily.Default,
            FontSize = 14,
            LineHeight = 22,
            Foreground = new SolidColorBrush(Color.Parse("#C8CDC7")),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var accentBar = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(accent),
        };

        var stack = new StackPanel
        {
            Margin = new Thickness(22, 20, 24, 22),
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Children = { titleBlock, detailBlock },
        };

        // Dismiss × — needed because Failure alerts are sticky (no auto-
        // close) and otherwise the only way to get rid of them is to
        // kill the app. Lives in the top-right of the content column.
        var dismissBtn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            FontSize = 14,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#9A9890")),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3,*,Auto") };
        grid.Children.Add(accentBar);
        Grid.SetColumn(accentBar, 0);
        // Long messages remain readable without growing beyond the screen.
        var scroll = new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        Grid.SetColumn(dismissBtn, 2);
        grid.Children.Add(dismissBtn);

        var host = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#171A17")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A2D29")),
            BorderThickness = new Thickness(1),
            Child = grid,
        };

        // Window construction itself can fail when Avalonia tries to
        // copy the App's default Icon onto the new window via
        // Win32Icon..ctor — observed crashing the entire process when
        // GDI handles were exhausted (the syncing animation used to
        // reallocate WindowIcon every 160ms, leaking ICON handles
        // until CreateIcon returned NULL with LastError 0). Caught
        // here so a transient platform failure becomes a missed
        // notification instead of an app-killing FATAL. We immediately
        // null out Icon below for the same defensive reason.
        Window window;
        try
        {
            window = new Window
            {
                Title = "Backspace",
                WindowDecorations = WindowDecorations.None,
                CanResize = false,
                ShowInTaskbar = topmost,
                Topmost = topmost,
                // Opaque: in the lab VM a transparent (layered) alert was never
                // drawn, and the main window stopped repainting while it was
                // open (31 October 2026). The card fills the window anyway.
                Background = new SolidColorBrush(Color.Parse("#171A17")),
                // A star-sized content column cannot determine its own window
                // width. Native Windows otherwise keeps a large initial client
                // area, even for a two-line alert. Measure the message ourselves
                // once its styles are attached; native auto-sizing also retained
                // the initial height with the Windows redirection compositor.
                Width = 420,
                Height = 240,
                MaxWidth = 420,
                MaxHeight = 480,
                SizeToContent = SizeToContent.Manual,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                Content = host,
            };
            // Drop the inherited default icon — this notification window
            // doesn't need one (no taskbar entry when topmost=true), and
            // re-reading the App's Icon every popup is what triggered
            // the GDI exhaustion crash. Best-effort.
            try { window.Icon = null; } catch { }
        }
        catch (Exception ex)
        {
            // Last-ditch: build the window without any icon-bearing
            // initial properties. Avalonia still copies the App icon
            // on the property-change side, but at least the throw
            // here would be from the property assignment, not the
            // ctor itself. Surface the failure via the regular log
            // and return null — caller short-circuits.
            AppLogger.For<NotificationService>().Warning(ex,
                "BuildAlertWindow: Window ctor threw — dropping toast for «{Title}»", title);
            return null!;
        }

        // Wire the × button to close the alert. Use a closure on the
        // outer `window` since the button was created before the window.
        dismissBtn.Click += (_, __) => { try { window.Close(); } catch { } };

        // U049: an alert first shown while the display is off (a scheduled
        // backup failing at night) was never drawn, not even once the screen
        // came back on: nothing asked the window to paint again. Forcing a
        // repaint of that exact window drew it at once (lab VM, 31 October
        // 2026), so ask for one regularly while the alert is open.
        var repaint = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        repaint.Tick += (_, __) => Repaint(window);
        window.Opened += (_, __) => repaint.Start();
        window.Closed += (_, __) => repaint.Stop();

        Button? openButton = null;
        if (onClick is not null)
        {
            openButton = new Button { Content = "Open run details", Margin = new Thickness(0, 8, 0, 0) };
            openButton.Classes.Add("primary");
            openButton.Click += (_, args) =>
            {
                args.Handled = true;
                try { onClick(); } finally { window.Close(); }
            };
            stack.Children.Add(openButton);
        }

        // Click-to-action: when onClick is provided, the toast body
        // becomes a clickable target. Used by the orchestrator to wire
        // failure toasts → "open Activity & logs with this run picked".
        // The dismiss button stops bubble so clicking ✕ doesn't also
        // navigate. Cursor changes to Hand so the affordance is
        // discoverable.
        if (onClick is not null)
        {
            host.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            host.PointerPressed += (sender, args) =>
            {
                if (args.Source is Control c)
                {
                    // Walk up from the source — if the user clicked the
                    // dismiss button, it'll handle close itself.
                    for (var node = c; node is not null; node = node.Parent as Control)
                        if (node == dismissBtn || node == openButton) return;
                }
                try { onClick(); } catch { }
                try { window.Close(); } catch { }
            };
        }

        if (autoDismissAfter is { } ad)
        {
            var timer = new System.Threading.Timer(_ =>
            {
                Dispatcher.UIThread.Post(() => { try { window.Close(); } catch { } });
            }, null, ad, System.Threading.Timeout.InfiniteTimeSpan);
            window.Closed += (_, __) => timer.Dispose();
        }

        void FitMessageHeight()
        {
            host.InvalidateMeasure();
            host.Measure(new Size(Math.Min(window.Width, window.MaxWidth), window.MaxHeight));
            var height = Math.Max(1, Math.Ceiling(host.DesiredSize.Height));
            if (Math.Abs(window.Height - height) > 0.5) window.Height = height;
        }
        // Loaded runs after styles and control templates have been applied.
        // Measuring before then sees an untemplated ScrollViewer, not its text.
        window.Loaded += (_, __) => FitMessageHeight();
        window.SizeChanged += (_, args) =>
        {
            if (args.PreviousSize.Width != args.NewSize.Width) FitMessageHeight();
        };

        return window;
    }

    private static void PositionBottomRight(Window w)
    {
        void Place()
        {
            var screen = w.Screens.Primary;
            if (screen is null) return;
            var wa = screen.WorkingArea;
            w.MaxWidth = Math.Min(420, Math.Max(1, (wa.Width - 48) / screen.Scaling));
            w.MaxHeight = Math.Min(480, Math.Max(1, (wa.Height - 48) / screen.Scaling));
            w.Width = w.MaxWidth;
            var size = w.Bounds.Size;
            w.Position = new PixelPoint(
                Math.Max(wa.X, (int)(wa.Right  - size.Width  * screen.Scaling - 24)),
                Math.Max(wa.Y, (int)(wa.Bottom - size.Height * screen.Scaling - 24)));
        }
        w.Opened += (_, __) => Place();
        // The native host may complete content sizing or change DPI after
        // Opened. Keep the bottom edge anchored to that final size as well.
        w.SizeChanged += (_, __) =>
        {
            if (w.IsVisible) Place();
        };
    }
}

