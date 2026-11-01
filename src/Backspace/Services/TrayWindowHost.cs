using System;
using Avalonia.Controls;
using Backspace.ViewModels;
using Backspace.Views;

namespace Backspace.Services;

/// <summary>Releases the native window while keeping operation and navigation
/// state alive. A hidden native window can still own rendering resources.</summary>
internal sealed class TrayWindowHost(Action<MainWindow?> changed) : IDisposable
{
    private MainWindowViewModel? _model;
    public MainWindow? Window { get; private set; }

    public MainWindow GetOrCreate()
    {
        if (Window is not null) return Window;
        _model ??= new MainWindowViewModel();
        var window = new MainWindow(routeForwardedArguments: false)
        {
            DataContext = _model,
            ReleaseWindowOnTrayClose = true,
        };
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

    public MainWindow Show()
    {
        var window = GetOrCreate();
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        return window;
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
    }
}
