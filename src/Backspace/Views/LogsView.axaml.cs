using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Backspace.Services;
using Backspace.ViewModels;

namespace Backspace.Views;

/// <summary>
/// The log follows its last line the way a terminal does (U083, 2 November
/// 2026): while the reader is at the end, new lines keep it there; once they
/// scroll up (to read or select), new lines never pull them back, and "Jump to
/// latest" appears. Scrolling back to the end, the button, or opening another
/// run resumes following. Before, every refresh scrolled to the bottom, even
/// with nothing running.
///
/// Since U176 (10 November 2026) the lines are rows of a virtualizing list:
/// dragging over them selects whole lines, Ctrl+A selects all, Ctrl+C copies
/// the selection, and an error in the failed run's list scrolls to its line.
/// </summary>
public partial class LogsView : UserControl
{
    private LogsViewModel? _boundVm;
    // Whether new text should keep the view at its last line.
    private bool _follow = true;
    // Set while this view moves the offset itself, so that move is not
    // mistaken for the reader scrolling.
    private bool _scrollingProgrammatically;
    private bool _scrollPending;
    private string? _shown;
    // Line selection: the line the drag started on and the selected range.
    private ConsoleLine? _anchor;
    private int _selectedFrom = -1, _selectedTo = -1;
    private bool _dragging;
    private ConsoleLine? _flashing;
    private DispatcherTimer? _flashTimer;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => UnhookViewModel();
        LogLines.AddHandler(PointerPressedEvent, OnLinesPointerPressed, RoutingStrategies.Tunnel);
        LogLines.AddHandler(PointerMovedEvent, OnLinesPointerMoved, RoutingStrategies.Tunnel);
        LogLines.AddHandler(PointerReleasedEvent, OnLinesPointerReleased, RoutingStrategies.Tunnel);
        LogScroll.AddHandler(KeyDownEvent, OnLogKeyDown, RoutingStrategies.Tunnel);
    }

    internal bool IsFollowing => _follow;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnhookViewModel();
        if (DataContext is LogsViewModel vm)
        {
            _boundVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.DisplayLines.CollectionChanged += OnLinesChanged;
            vm.JumpRequested += JumpTo;
        }
    }

    private void UnhookViewModel()
    {
        if (_boundVm is null) return;
        _boundVm.PropertyChanged -= OnVmPropertyChanged;
        _boundVm.DisplayLines.CollectionChanged -= OnLinesChanged;
        _boundVm.JumpRequested -= JumpTo;
        _boundVm = null;
        _flashTimer?.Stop();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Another run or backup: start at its last line, where the verdict is.
        // The history refresh every few seconds re-selects the same run; that
        // is not a new run and must not pull the reader back.
        // Checked once the change settles: rebuilding the run list clears the
        // selection for a moment, which is not another run.
        if (e.PropertyName is nameof(LogsViewModel.SelectedRun) or nameof(LogsViewModel.SelectedBackup) && sender is LogsViewModel vm)
        {
            if (_shown is null) CheckShownRun(vm);
            else Dispatcher.UIThread.Post(() => CheckShownRun(vm), DispatcherPriority.Background);
        }
        if (e.PropertyName == nameof(LogsViewModel.DisplayText)) ScheduleScrollToBottom();
    }

    private void CheckShownRun(LogsViewModel vm)
    {
        if (!ReferenceEquals(vm, _boundVm)) return;
        var shown = vm.SelectedBackup + "|" + (vm.SelectedRun?.Id ?? "live");
        if (shown == _shown) return;
        _shown = shown;
        ClearSelection();
        SetFollow(true);
        ScrollLogToBottom();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Lines replaced or gone: the selected range no longer means the same lines.
        if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove && _selectedFrom >= 0)
            ClearSelection();
        ScheduleScrollToBottom();
    }

    private void ScheduleScrollToBottom()
    {
        if (!_follow || _scrollPending) return;
        _scrollPending = true;
        // Defer until layout has measured the new lines (Loaded runs after Layout).
        Dispatcher.UIThread.Post(() => { _scrollPending = false; ScrollLogToBottom(); }, DispatcherPriority.Loaded);
    }

    private void ScrollLogToBottom()
    {
        if (LogScroll is null || !_follow || _boundVm is null) return;
        _scrollingProgrammatically = true;
        try
        {
            var count = _boundVm.DisplayLines.Count;
            // The list estimates the height of lines it hasn't drawn; bring
            // the last one in, then settle on the true end.
            if (count > 0) LogLines.ScrollIntoView(count - 1);
            for (var i = 0; i < 4; i++)
            {
                LogScroll.UpdateLayout();
                // The ScrollViewer clamps to (Extent - Viewport).
                LogScroll.Offset = new Vector(LogScroll.Offset.X, LogScroll.Extent.Height);
                LogScroll.UpdateLayout();
                if (IsAtBottom()) break;
            }
        }
        finally { _scrollingProgrammatically = false; }
    }

    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (LogScroll is null || _scrollingProgrammatically) return;
        // Only the reader's own vertical moves change following; growing text
        // or a resized window does not. A shrinking log that clamps the
        // offset to its new end is not the reader either.
        if (e.OffsetDelta.Y == 0 || e.ViewportDelta.Y != 0) return;
        if (e.ExtentDelta.Y < 0 && IsAtBottom()) return;
        if (e.OffsetDelta.Y < 0) SetFollow(IsAtBottom());
        else if (e.ExtentDelta.Y == 0 || IsAtBottom()) SetFollow(IsAtBottom());
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

    // ---- Error list: show a line (U176) ----

    /// <summary>Scrolls to <paramref name="line"/>, stops following and
    /// highlights the line for a moment.</summary>
    internal void JumpTo(ConsoleLine line)
    {
        if (_boundVm is null) return;
        var index = _boundVm.DisplayLines.IndexOf(line);
        if (index < 0) return;
        SetFollow(false);
        _scrollingProgrammatically = true;
        try
        {
            LogLines.ScrollIntoView(index);
            LogScroll.UpdateLayout();
            // Not flush with the top edge: a few lines of context above it.
            if (LogLines.ContainerFromIndex(index) is Control row
                && row.TranslatePoint(new Point(0, 0), LogLines) is { } at)
            {
                LogScroll.Offset = new Vector(LogScroll.Offset.X, Math.Max(0, at.Y - LogScroll.Viewport.Height / 3));
                LogScroll.UpdateLayout();
            }
        }
        finally { _scrollingProgrammatically = false; }
        if (IsAtBottom()) SetFollow(true);
        else SetFollow(false);
        Flash(line);
    }

    private void Flash(ConsoleLine line)
    {
        if (_flashing is not null) _flashing.IsFlashing = false;
        _flashing = line;
        line.IsFlashing = true;
        _flashTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _flashTimer.Stop();
        _flashTimer.Tick -= OnFlashTick;
        _flashTimer.Tick += OnFlashTick;
        _flashTimer.Start();
    }

    private void OnFlashTick(object? sender, EventArgs e)
    {
        _flashTimer?.Stop();
        if (_flashing is not null) _flashing.IsFlashing = false;
        _flashing = null;
    }

    // ---- Line selection ----

    private ConsoleLine? LineAt(PointerEventArgs e)
    {
        // The row under the pointer, even between the lines' text.
        if (e.Source is Visual source && source.FindAncestorOfType<ConsoleLineRow>(includeSelf: true) is { Line: { } hit })
            return hit;
        var point = e.GetPosition(LogLines);
        foreach (var row in LogLines.GetRealizedContainers().Select(c => c.FindDescendantOfType<ConsoleLineRow>(includeSelf: true)))
        {
            if (row?.Line is null) continue;
            var top = row.TranslatePoint(new Point(0, 0), LogLines)?.Y ?? double.NaN;
            if (point.Y >= top && point.Y < top + row.Bounds.Height) return row.Line;
        }
        return null;
    }

    private void OnLinesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(LogLines).Properties;
        if (!props.IsLeftButtonPressed) return;
        // A header's Copy button keeps its own click.
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        var line = LineAt(e);
        LogScroll.Focus();
        if (line is null) { ClearSelection(); return; }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _anchor is not null) SelectRange(_anchor, line);
        else { _anchor = line; SelectRange(line, line); }
        _dragging = true;
        e.Pointer.Capture(LogLines);
        e.Handled = true;
    }

    private void OnLinesPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _anchor is null) return;
        if (LineAt(e) is { } line) SelectRange(_anchor, line);
    }

    private void OnLinesPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    /// <summary>Selects the lines from <paramref name="a"/> to <paramref name="b"/>, in either order.</summary>
    internal void SelectRange(ConsoleLine a, ConsoleLine b)
    {
        if (_boundVm is null) return;
        var lines = _boundVm.DisplayLines;
        int i = lines.IndexOf(a), j = lines.IndexOf(b);
        if (i < 0 || j < 0) return;
        var from = Math.Min(i, j); var to = Math.Max(i, j);
        // Clear what leaves the range, mark what enters it.
        if (_selectedFrom >= 0)
            for (var k = _selectedFrom; k <= _selectedTo && k < lines.Count; k++)
                if (k < from || k > to) lines[k].IsSelected = false;
        for (var k = from; k <= to; k++) lines[k].IsSelected = true;
        _selectedFrom = from; _selectedTo = to;
    }

    internal void ClearSelection()
    {
        if (_boundVm is not null && _selectedFrom >= 0)
        {
            var lines = _boundVm.DisplayLines;
            for (var k = _selectedFrom; k <= _selectedTo && k < lines.Count; k++) lines[k].IsSelected = false;
        }
        // Lines removed from the list may still say they are selected.
        _anchor = null;
        _selectedFrom = _selectedTo = -1;
    }

    internal IReadOnlyList<ConsoleLine> SelectedLines =>
        _boundVm is null || _selectedFrom < 0 ? Array.Empty<ConsoleLine>()
        : _boundVm.DisplayLines.Skip(_selectedFrom).Take(_selectedTo - _selectedFrom + 1).ToList();

    private void OnLogKeyDown(object? sender, KeyEventArgs e)
    {
        if (_boundVm is null) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key == Key.A && _boundVm.DisplayLines.Count > 0)
        {
            SelectRange(_boundVm.DisplayLines[0], _boundVm.DisplayLines[^1]);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.C && _selectedFrom >= 0)
        {
            _ = _boundVm.CopyLinesAsync(SelectedLines);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _selectedFrom >= 0)
        {
            ClearSelection();
            e.Handled = true;
        }
    }

    private void OnCopySelected(object? sender, RoutedEventArgs e)
    {
        if (_boundVm is not null && _selectedFrom >= 0) _ = _boundVm.CopyLinesAsync(SelectedLines);
    }

    private void OnSelectAll(object? sender, RoutedEventArgs e)
    {
        if (_boundVm is { DisplayLines.Count: > 0 } vm) SelectRange(vm.DisplayLines[0], vm.DisplayLines[^1]);
    }
}
