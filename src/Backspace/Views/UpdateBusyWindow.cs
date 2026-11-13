using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Backspace.Views;

/// <summary>
/// Asked when an update is ready while work runs (U238, owner: "The modal
/// isn't very clear. What is 'when it finishes'. Styling of this modal could
/// be improved a lot."). It names the update, lists what runs, each with the
/// turning icon the cards use, and offers plain choices: restart once it is
/// done, or stop it and restart now.
/// </summary>
internal sealed class UpdateBusyWindow : Window
{
    internal enum Choice { Cancel, WhenDone, StopNow }
    private Choice _choice = Choice.Cancel;

    internal UpdateBusyWindow(string version, IReadOnlyList<string> running)
    {
        Title = "Backspace";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("BS.Brush.BgBase");
        var one = running.Count == 1;

        var heading = new TextBlock { Text = $"Restart to install Backspace {version}?", TextWrapping = TextWrapping.Wrap };
        heading.Classes.Add("card-title");
        var lead = new TextBlock
        {
            Text = one ? "This is still running. Backspace can restart as soon as it is done, or stop it now:"
                       : "These are still running. Backspace can restart as soon as they are done, or stop them now:",
            TextWrapping = TextWrapping.Wrap,
        };
        lead.Classes.Add("body");

        var list = new StackPanel { Spacing = 8 };
        foreach (var item in running)
        {
            var icon = new PathIcon { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
            icon.Classes.Add("status-icon");
            icon.Classes.Add("running");
            RunningPulse.SetIsActive(icon, true);
            var text = new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            text.Classes.Add("body");
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,10,*") };
            row.Children.Add(icon);
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            list.Children.Add(row);
        }
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12), Child = list };
        box[!Border.BackgroundProperty] = new DynamicResourceExtension("BS.Brush.BgSurface");
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("BS.Brush.BorderSubtle");
        box.BorderThickness = new Thickness(1);

        var note = new TextBlock
        {
            Text = "Stopping is safe: a stopped backup or check runs again at its next time, and a backup only sends what changed. "
                 + "Your settings stay in place; mounted snapshots close when Backspace restarts.",
            TextWrapping = TextWrapping.Wrap,
        };
        note.Classes.Add("helper");

        var cancel = new Button { Content = "Not now", IsCancel = true };
        cancel.Classes.Add("ghost");
        cancel.Click += (_, _) => Close();
        var stop = new Button { Name = "StopAndRestart", Content = one ? "Stop it and restart now" : "Stop them and restart now" };
        stop.Classes.Add("ghost");
        stop.Click += (_, _) => { _choice = Choice.StopNow; Close(); };
        var wait = new Button { Name = "RestartWhenDone", Content = "Restart when done", IsDefault = true };
        wait.Classes.Add("primary");
        wait.Click += (_, _) => { _choice = Choice.WhenDone; Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10,
            Children = { cancel, stop, wait } };

        Content = new StackPanel
        {
            Margin = new Thickness(32, 28, 32, 24),
            Spacing = 16,
            Children = { heading, lead, box, note, buttons },
        };
    }

    internal async Task<Choice> AskAsync(Window? owner)
    {
        if (owner is not null) await ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            Closed += (_, _) => closed.TrySetResult();
            Show();
            await closed.Task;
        }
        return _choice;
    }
}
