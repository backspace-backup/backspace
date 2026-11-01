using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Backspace.ViewModels;
using System.Linq;
using System.Windows.Input;

namespace Backspace.Views;

public partial class BackupCard : UserControl
{
    // Retain the drive/root and enough context, then give the rest of the
    // available width to the final path segments. Full text remains accessible.
    public static Avalonia.Media.TextTrimming SourcePathTrimming { get; } =
        new Avalonia.Media.TextLeadingPrefixTrimming("…", 12);

    public BackupCard()
    {
        InitializeComponent();
    }

    private Avalonia.Threading.DispatcherTimer? _highlightTimer;

    /// <summary>Outline the card in the accent colour for a moment, so a
    /// jump from a notice pill visibly lands here (U036).</summary>
    internal void Highlight()
    {
        if (CardSurface is null) return;
        CardSurface.Classes.Add("jump-highlight");
        _highlightTimer?.Stop();
        _highlightTimer = new Avalonia.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(1600) };
        _highlightTimer.Tick += (_, _) => { _highlightTimer?.Stop(); CardSurface.Classes.Remove("jump-highlight"); };
        _highlightTimer.Start();
    }

    internal bool IsHighlighted => CardSurface?.Classes.Contains("jump-highlight") == true;

    private void OnMoreActions(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not BackupCardViewModel card) return;
        var backups = this.GetVisualAncestors().OfType<ItemsControl>()
            .Select(c => c.DataContext).OfType<BackupsViewModel>().FirstOrDefault();
        if (backups is null) return;
        MenuItem Action(string label, ICommand command, object? parameter = null)
        {
            var item = new MenuItem { Header = label, Command = command, CommandParameter = parameter };
            item.Bind(IsEnabledProperty, new Binding(nameof(BackupCardViewModel.CanEditWhileIdle)) { Source = card });
            return item;
        }
        var menu = new MenuFlyout
        {
            Items =
            {
                Action("Edit backup settings", backups.EditCommand, card),
                Action("Browse snapshots in browser", card.BrowseSnapshotsCommand),
                new Separator(),
                Action("Remove backup…", backups.DeleteCommand, card),
            },
        };
        button.Flyout = menu;
        menu.ShowAt(button);
    }
}
