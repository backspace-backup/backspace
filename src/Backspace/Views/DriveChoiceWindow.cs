using Avalonia.Controls;
using Avalonia.Layout;

namespace Backspace.Views;

/// <summary>Asked before a backup would start over on an external drive
/// that lacks it (U117): a new backup on this drive, never this drive for
/// this backup, or not now.</summary>
internal sealed class DriveChoiceWindow : Window
{
    internal enum Choice { Cancel, UseDrive, Decline }
    private Choice _choice = Choice.Cancel;

    internal DriveChoiceWindow(string title, string body, string useLabel, string? declineLabel)
    {
        Title = "Backspace";
        Width = 580;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BS.Brush.BgBase");

        var heading = new TextBlock { Text = title, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        heading.Classes.Add("card-title");
        var text = new TextBlock { Text = body, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        text.Classes.Add("body");

        var cancel = new Button { Content = "Not now", IsCancel = true };
        cancel.Classes.Add("ghost");
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        buttons.Children.Add(cancel);
        if (declineLabel is not null)
        {
            var decline = new Button { Content = declineLabel };
            decline.Classes.Add("ghost");
            decline.Click += (_, _) => { _choice = Choice.Decline; Close(); };
            buttons.Children.Add(decline);
        }
        var use = new Button { Content = useLabel };
        use.Classes.Add("primary");
        use.Click += (_, _) => { _choice = Choice.UseDrive; Close(); };
        buttons.Children.Add(use);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(34, 32, 34, 28),
            Spacing = 18,
            Children = { new StackPanel { Spacing = 8, Children = { heading, text } }, buttons },
        };
    }

    internal async System.Threading.Tasks.Task<Choice> AskAsync(Window? owner)
    {
        if (owner is not null) await ShowDialog(owner);
        else
        {
            var closed = new System.Threading.Tasks.TaskCompletionSource();
            Closed += (_, _) => closed.TrySetResult();
            Show();
            await closed.Task;
        }
        return _choice;
    }
}
