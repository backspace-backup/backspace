using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Backspace.Views;

/// <summary>Shown when this copy opens while Backspace is set up in another
/// folder (U100): update that copy, move Backspace here, or cancel.</summary>
internal sealed class InstallationChoiceWindow : Window
{
    internal enum Choice { Cancel, Update, Move }
    private Choice _choice = Choice.Cancel;

    internal InstallationChoiceWindow(string otherFolder, string otherVersion, string thisVersion, bool canMove, string? moveUnavailableReason)
    {
        Title = "Backspace";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BS.Brush.BgBase");

        var title = new TextBlock { Text = "Backspace is already set up on this PC", TextWrapping = TextWrapping.Wrap };
        title.Classes.Add("card-title");
        var body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Your settings and backups are in {otherFolder} (version {otherVersion}). This copy is version {thisVersion}.\n\n"
                + "Backspace keeps one copy per Windows account. Update that copy with this version, or move Backspace here: "
                + "your settings, history and saved sign-ins come along, and the other Backspace.exe is deleted."
                + (canMove ? "" : "\n\n" + moveUnavailableReason),
        };
        body.Classes.Add("body");

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Classes.Add("ghost");
        cancel.Click += (_, _) => Close();
        var move = new Button { Content = "Move Backspace here", IsEnabled = canMove };
        move.Classes.Add("ghost");
        move.Click += (_, _) => { _choice = Choice.Move; Close(); };
        var update = new Button { Content = "Update that copy", IsDefault = true };
        update.Classes.Add("primary");
        update.Click += (_, _) => { _choice = Choice.Update; Close(); };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(34, 32, 34, 28),
            Spacing = 18,
            Children =
            {
                new StackPanel { Spacing = 8, Children = { title, body } },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10,
                    Children = { cancel, move, update } },
            },
        };
    }

    internal async System.Threading.Tasks.Task<Choice> AskAsync()
    {
        var closed = new System.Threading.Tasks.TaskCompletionSource();
        Closed += (_, _) => closed.TrySetResult();
        Show();
        Activate();
        await closed.Task;
        return _choice;
    }
}
