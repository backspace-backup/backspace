using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Backspace.ViewModels;

namespace Backspace.Views;

/// <summary>
/// The log follows its last line the way a terminal does (U083, 2 November
/// 2026): while the reader is at the end, new lines keep it there; once they
/// scroll up (to read or select), new lines never pull them back, and "Jump to
/// latest" appears. Scrolling back to the end, the button, or opening another
/// run resumes following. Before, every refresh scrolled to the bottom, even
/// with nothing running.
/// </summary>
public partial class LogsView : UserControl
{
    private LogsViewModel? _boundVm;
    // Whether new text should keep the view at its last line.
    private bool _follow = true;
    // Set while this view moves the offset itself, so that move is not
    // mistaken for the reader scrolling.
    private bool _scrollingProgrammatically;
    private string? _shown;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => UnhookViewModel();
    }

    internal bool IsFollowing => _follow;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnhookViewModel();
        if (DataContext is LogsViewModel vm)
        {
            _boundVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void UnhookViewModel()
    {
        if (_boundVm is null) return;
        _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Another run or backup: start at its last line, where the verdict is.
        // The history refresh every few seconds re-selects the same run; that
        // is not a new run and must not pull the reader back.
        if (e.PropertyName is nameof(LogsViewModel.SelectedRun) or nameof(LogsViewModel.SelectedBackup) && sender is LogsViewModel vm)
        {
            var shown = vm.SelectedBackup + "|" + (vm.SelectedRun?.Id ?? "live");
            if (shown != _shown) { _shown = shown; SetFollow(true); }
        }
        if (e.PropertyName != nameof(LogsViewModel.DisplayText) || !_follow) return;
        // Defer until layout has measured the new text (Loaded runs after Layout).
        Dispatcher.UIThread.Post(ScrollLogToBottom, DispatcherPriority.Loaded);
    }

    private void ScrollLogToBottom()
    {
        if (LogScroll is null || !_follow) return;
        _scrollingProgrammatically = true;
        try
        {
            // The ScrollViewer clamps to (Extent - Viewport).
            LogScroll.Offset = new Vector(LogScroll.Offset.X, LogScroll.Extent.Height);
        }
        finally { _scrollingProgrammatically = false; }
    }

    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (LogScroll is null || _scrollingProgrammatically) return;
        // Only the reader's own vertical moves change following; growing text
        // or a resized window does not.
        if (e.OffsetDelta.Y == 0 || e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) return;
        SetFollow(IsAtBottom());
    }

    private bool IsAtBottom()
        => LogScroll.Offset.Y >= LogScroll.Extent.Height - LogScroll.Viewport.Height - 4;

    private void SetFollow(bool follow)
    {
        _follow = follow;
        if (JumpToLatest is not null) JumpToLatest.IsVisible = !follow;
    }

    private void OnJumpToLatest(object? sender, RoutedEventArgs e)
    {
        SetFollow(true);
        ScrollLogToBottom();
    }
}
