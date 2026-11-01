using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Backspace.ViewModels;

namespace Backspace.Views;

public partial class BackupsView : UserControl
{
    public BackupsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Also detach when the view leaves the visual tree without a
        // DataContext change. Otherwise the long-lived BackupsViewModel
        // keeps a delegate pointing at this dead view (and its visual
        // subtree) across page-swaps through ViewLocator. Matches the
        // pattern already used in LogsView for OnVmPropertyChanged.
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_wired is not null)
        {
            _wired.TestRestoreOwner = null;
            _wired.JumpToBackupRequested -= OnJumpToBackup;
            _wired = null;
        }
        DataContextChanged -= OnDataContextChanged;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
    }

    private BackupsViewModel? _wired;

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        // Detach from the previous VM so a re-template (rare — only when
        // hot-reload swaps the DataContext) doesn't end up double-wired
        // and double-scrolling on a single chip click.
        if (_wired is not null) { _wired.JumpToBackupRequested -= OnJumpToBackup; _wired.TestRestoreOwner = null; }
        _wired = DataContext as BackupsViewModel;
        if (_wired is not null)
        {
            _wired.JumpToBackupRequested += OnJumpToBackup;
            _wired.TestRestoreOwner = () => TopLevel.GetTopLevel(this) as Window;
        }
    }

    /// <summary>
    /// Triggered by JumpToBackupCommand on a notice pill: scroll the
    /// backup's card to the top of the list and briefly highlight it.
    /// The list virtualises, so a card below the fold may not exist yet;
    /// ScrollIntoView realises it first. The highlight shows the click
    /// worked even when the card was already on screen (U036).
    /// </summary>
    private async void OnJumpToBackup(string backupId)
    {
        if (_wired is null || CardsList is null || CardsScroller is null) return;
        var index = _wired.Cards.ToList().FindIndex(c => c.Id == backupId);
        if (index < 0) return;
        CardsList.ScrollIntoView(index);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var card = CardsList.ContainerFromIndex(index)?.GetVisualDescendants().OfType<BackupCard>().FirstOrDefault();
        if (card is null) return;
        if (card.TranslatePoint(new Point(0, 0), CardsScroller) is { } top)
        {
            var maxY = Math.Max(0, CardsScroller.Extent.Height - CardsScroller.Viewport.Height);
            CardsScroller.Offset = new Vector(CardsScroller.Offset.X, Math.Clamp(CardsScroller.Offset.Y + top.Y, 0, maxY));
        }
        card.Highlight();
    }

    /// <summary>
    /// Forward wheel events from the page-level Grid to the cards
    /// ScrollViewer when the wheel landed outside it (e.g. over the
    /// pinned header). Avalonia's default behaviour is for wheel
    /// events to scroll only the ScrollViewer directly under the
    /// cursor, which leaves the page header feeling dead — the user
    /// reported "scrollbar is only triggered if my mouse is under the
    /// backup section". When the wheel bubbles up to this handler
    /// without an inner scroller having handled it (e.Handled stays
    /// false), translate the delta into an Offset change on the cards
    /// scroller so the user can scroll the list from anywhere on the
    /// page.
    /// </summary>
    private void OnPagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled) return;
        if (CardsScroller is null || !CardsScroller.IsVisible) return;

        // 50px per wheel notch matches Avalonia's built-in ScrollViewer
        // default — keeps the felt-feel identical to scrolling directly
        // over the cards.
        var delta = e.Delta.Y * 50;
        var current = CardsScroller.Offset;
        var maxY = Math.Max(0, CardsScroller.Extent.Height - CardsScroller.Viewport.Height);
        var newY = Math.Clamp(current.Y - delta, 0, maxY);
        CardsScroller.Offset = new Vector(current.X, newY);
        e.Handled = true;
    }
}
