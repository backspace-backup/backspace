using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Backspace.ViewModels;
using Backspace.Views;

namespace Backspace.Services;

/// <summary>Releases the native window while keeping operation and navigation
/// state alive. A hidden native window can still own rendering resources.
/// <para>Only one window at a time (U201, 11 November 2026): after a start at
/// sign-in, a double-click on the tray icon opened two windows. Creating the
/// first native window lets Windows deliver messages, the second click among
/// them, before the window exists, so the second click made a window too. A
/// request that arrives while the window is being made now waits for it and
/// then shows that same window.</para></summary>
internal sealed class TrayWindowHost(Action<MainWindow?> changed, Func<MainWindowViewModel, MainWindow>? create = null) : IDisposable
{
    private MainWindowViewModel? _model;
    private bool _creating;
    public MainWindow? Window { get; private set; }

    /// <summary>True while the window is being made: a request now would
    /// make a second one.</summary>
    public bool IsCreating => _creating;

    public MainWindow GetOrCreate()
    {
        if (Window is not null) return Window;
        if (_creating) throw new InvalidOperationException("The window is being created.");
        _creating = true;
        MainWindow window;
        try
        {
            _model ??= new MainWindowViewModel();
            window = create?.Invoke(_model) ?? new MainWindow(routeForwardedArguments: false) { DataContext = _model };
            window.ReleaseWindowOnTrayClose = true;
        }
        finally { _creating = false; }
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(Window, window)) return;
            Window = null;
            if (!window.WasClosedToTray) _model = null; // MainWindow disposed it.
            changed(null);
        };
        Window = window;
        changed(window);
        return window;
    }

    /// <summary>Shows the window, made if needed, then runs <paramref name="then"/>
    /// with it. Asked again while the window is being made, it waits for it.</summary>
    public void Show(Action<MainWindow>? then = null)
    {
        if (_creating)
        {
            Dispatcher.UIThread.Post(() => Show(then));
            return;
        }
        var window = GetOrCreate();
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        then?.Invoke(window);
    }

    /// <summary>Runs <paramref name="action"/> with the window, made if needed;
    /// asked while it is being made, once it is.</summary>
    public void With(Action<MainWindow> action)
    {
        if (_creating) { Dispatcher.UIThread.Post(() => With(action)); return; }
        action(GetOrCreate());
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
    }
}
