using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// High-level UI interaction primitives for scenario tests. These
/// wrap Avalonia's headless dispatcher + visual-tree walk so a
/// scenario reads like a flow ("click Add a backup, type 'Hello',
/// click Save") rather than a chain of NestedFindControl visits.
///
/// Why not Avalonia.Headless mouse APIs directly? Two reasons:
///   1. The mouse APIs take pixel coordinates, which means every
///      click depends on layout passes settling. Click-by-content
///      ("the button labelled X") is more robust to layout tweaks.
///   2. Some scenario steps interact with VM-only state (no visible
///      control) — uniform Driver lets a scenario mix UI clicks
///      with direct VM property pokes without context-switching.
///
/// All methods that mutate UI state run synchronously on the UI
/// dispatcher and pump pending dispatch operations afterward so
/// bound properties have time to fire OnPropertyChanged before the
/// next assertion.
/// </summary>
public sealed class UiDriver : IDisposable
{
    private readonly ScenarioContext _ctx;
    private readonly List<Window> _ownedWindows = new();
    private readonly HashSet<Window> _closedWindows = new();
    private readonly Dictionary<Window, EventHandler> _closedHandlers = new();
    private bool _disposed;

    public UiDriver(ScenarioContext ctx) => _ctx = ctx;

    /// <summary>
    /// Show a window headlessly, run two layout passes, and remember
    /// it so Dispose can close any leftover windows. Returns the
    /// window for fluent chaining.
    /// </summary>
    public TWindow Show<TWindow>(TWindow window) where TWindow : Window
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TrackWindow(window);
        window.Show();
        window.UpdateLayout();
        Pump();
        window.UpdateLayout();
        return window;
    }

    /// <summary>
    /// Pump the Avalonia dispatcher until pending background jobs
    /// have settled. Avalonia.Headless installs a manual-pump
    /// dispatcher; without these RunJobs calls, an awaited
    /// Dispatcher.UIThread.InvokeAsync from the VM never fires
    /// during a synchronous scenario step.
    /// </summary>
    public void Pump(int iterations = 4)
    {
        for (int i = 0; i < iterations; i++)
            Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pump for a wall-clock window so timer-driven work
    /// (debouncers, animations, the 150ms ProgressLog flush) has a
    /// chance to fire.</summary>
    public async Task PumpAsync(int milliseconds = 200)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Find every descendant button on <paramref name="root"/> and
    /// invoke the first whose visible text matches
    /// <paramref name="content"/> (case-insensitive, trim, ignores
    /// inner Run / TextBlock structure). Returns true on a hit.
    ///
    /// We invoke the bound Command directly rather than firing a
    /// PointerPressed event — pixel-based clicks are flaky in
    /// headless layouts where bounds drift by a pixel between
    /// layout passes, and we don't lose any meaningful coverage
    /// (the Click→Command wiring is an Avalonia primitive, not
    /// our code). Headless tests focus on app behaviour, not
    /// Avalonia's own input pipeline.
    /// </summary>
    public bool ClickButton(Visual root, string content)
    {
        foreach (var btn in Descendants<Button>(root))
        {
            var text = ExtractText(btn);
            if (string.Equals(text, content, StringComparison.OrdinalIgnoreCase))
            {
                return Click(btn);
            }
        }
        return false;
    }

    /// <summary>Click the named control (x:Name). Throws if not found.</summary>
    public void ClickByName(Visual root, string name)
    {
        var btn = root.FindDescendantByName(name) as Button
                  ?? throw new InvalidOperationException($"No button named '{name}' under {root}");
        if (!Click(btn))
            throw new InvalidOperationException($"Button '{name}' is not enabled");
    }

    /// <summary>
    /// Set the Text property of the first TextBox under
    /// <paramref name="root"/> whose PlaceholderText / Name matches
    /// <paramref name="locator"/>. Two-way bindings see the change
    /// after the next dispatcher pump.
    /// </summary>
    public bool EnterText(Visual root, string locator, string value)
    {
        foreach (var tb in Descendants<TextBox>(root))
        {
            if (string.Equals(tb.Name, locator, StringComparison.OrdinalIgnoreCase)
                || string.Equals(tb.PlaceholderText, locator, StringComparison.OrdinalIgnoreCase))
            {
                tb.Text = value;
                Pump();
                return true;
            }
        }
        return false;
    }

    /// <summary>Wait synchronously until <paramref name="predicate"/>
    /// returns true, pumping the dispatcher between checks. Returns
    /// false on timeout.</summary>
    public bool WaitFor(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            Pump();
            System.Threading.Thread.Sleep(20);
        }
        return predicate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Dispatcher.UIThread.VerifyAccess();
        // A scenario that started real work must await completion (including
        // cancellation acknowledgement) itself. Never make a green teardown by
        // killing a backend or disposing its VM while it still owns an operation.
        if (!WaitFor(() => !Backspace.Views.MainWindow.HasAnyRunningOperation(), timeoutMs: 1000))
            throw new InvalidOperationException("Scenario cleanup found active backup, restore, mount or maintenance work. Await its completion before disposing the UI driver; teardown will not terminate a backend.");

        var previousQuit = Backspace.App.QuitRequested;
        var failures = new List<Exception>();
        var visited = new HashSet<Window>();
        var disposedContexts = new HashSet<IDisposable>();
        Backspace.App.QuitRequested = true;
        try
        {
            // Test cleanup is not the user closing a window. In particular,
            // close-to-tray must not leave a hidden MainWindow subscribed to
            // Config/AppStatus events for the next scenario. Keep normal close
            // behavior in every scenario action; bypass it only in teardown.
            foreach (var window in _ownedWindows.ToArray().Reverse()) CloseWindow(window);
            Pump();
        }
        finally
        {
            Backspace.App.QuitRequested = previousQuit;
            foreach (var (window, handler) in _closedHandlers) window.Closed -= handler;
            _closedHandlers.Clear();
            _closedWindows.Clear();
            _ownedWindows.Clear();
            _disposed = true;
        }
        if (failures.Count > 0) throw new AggregateException("Scenario window cleanup failed.", failures);

        void CloseWindow(Window window)
        {
            if (!visited.Add(window)) return;
            // Dialogs opened in code-behind are not passed through Show().
            // Track and close them before the owning window so modal tasks can
            // finish without leaking an untracked child into xUnit teardown.
            foreach (var child in window.OwnedWindows.ToArray())
            {
                TrackWindow(child);
                CloseWindow(child);
            }
            try
            {
                if (!_closedWindows.Contains(window)) window.Close();
                if (!_closedWindows.Contains(window))
                    throw new InvalidOperationException($"{window.GetType().Name} refused to close during scenario cleanup.");
            }
            catch (Exception ex) { failures.Add(ex); }
            // MainWindow.OnClosed owns its actual DataContext. Plain host
            // windows do not; explicitly release those singleton subscriptions.
            if (window is not Backspace.Views.MainWindow && window.DataContext is IDisposable context
                && disposedContexts.Add(context))
                try { context.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            // A view hosted in a plain window carries its own view model. Left
            // alive, its cards posted work that ran in a later test's
            // dispatcher reset and failed there (U066, 1 November 2026).
            if (window is not Backspace.Views.MainWindow && window.Content is Control { DataContext: IDisposable hosted }
                && disposedContexts.Add(hosted))
                try { hosted.Dispose(); } catch (Exception ex) { failures.Add(ex); }
        }
    }

    // ---- internals ----

    private void TrackWindow(Window window)
    {
        if (_closedHandlers.ContainsKey(window)) return;
        EventHandler handler = (_, _) => _closedWindows.Add(window);
        _closedHandlers.Add(window, handler);
        _ownedWindows.Add(window);
        window.Closed += handler;
    }

    private bool Click(Button button)
    {
        if (!button.IsEffectivelyEnabled || !button.IsVisible) return false;
        var cmd = button.Command;
        var param = button.CommandParameter;
        if (cmd is not null)
        {
            if (!cmd.CanExecute(param)) return false;
            cmd.Execute(param);
        }
        else
        {
            // Exercise registered code-behind handlers through Avalonia's public
            // routed event API. The former optional reflection shim silently
            // did nothing on this Avalonia version while reporting a click.
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        }
        Pump();
        return true;
    }

    private static string ExtractText(Button btn)
    {
        // Buttons in this app use either Content="…" directly OR a
        // composite content with an inner TextBlock. Try both.
        if (btn.Content is string s) return s.Trim();
        if (btn.Content is Visual v)
        {
            foreach (var tb in Descendants<TextBlock>(v))
                if (!string.IsNullOrWhiteSpace(tb.Text)) return tb.Text.Trim();
        }
        return string.Empty;
    }

    private static IEnumerable<T> Descendants<T>(Visual root) where T : Visual
    {
        foreach (var v in root.GetVisualDescendants())
            if (v is T t) yield return t;
    }

    /// <summary>
    /// Hatch escape: a scenario can grab the underlying VM and
    /// poke properties directly when an interaction is awkward to
    /// drive through the visual tree (e.g. a TimePicker's hidden
    /// glass overlay). Use sparingly — every direct poke skips
    /// real wiring coverage.
    /// </summary>
    public T VmOf<T>(StyledElement element) where T : class =>
        (element.DataContext as T) ?? throw new InvalidOperationException(
            $"Element {element.GetType().Name} has DataContext {element.DataContext?.GetType().Name ?? "null"}, expected {typeof(T).Name}");

}

internal static class VisualLookup
{
    /// <summary>
    /// Find the first descendant whose <c>Name</c> property matches.
    /// LogicalTree walk catches templated parts that the visual
    /// tree alone misses (e.g. PART_BorderElement under a Button's
    /// template).
    /// </summary>
    public static Visual? FindDescendantByName(this Visual root, string name)
    {
        foreach (var v in root.GetVisualDescendants())
            if (v is StyledElement s && string.Equals(s.Name, name, StringComparison.Ordinal))
                return v;
        return null;
    }
}
