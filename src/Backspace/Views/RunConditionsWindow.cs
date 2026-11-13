using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Backspace.ViewModels;

namespace Backspace.Views;

/// <summary>
/// Asked when a run you start isn't allowed by its conditions now (U226, owner:
/// "we should not conflate the metered with AC status"). Each condition not met
/// is listed on its own, with its own "anyway" choice; the main button says
/// what will happen: run now, or run as soon as what remains is met.
/// </summary>
internal sealed class RunConditionsWindow : Window
{
    private ManualRunConditions.Decision? _decision;

    internal RunConditionsWindow(string name, bool checks, ManualRunConditions.Assessment assessment)
    {
        Title = "Backspace";
        Width = 580;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("BS.Brush.BgBase");

        var heading = new TextBlock
        {
            Text = checks ? $"The checks of «{name}» are set to wait right now" : $"«{name}» is set to wait right now",
            TextWrapping = TextWrapping.Wrap,
        };
        heading.Classes.Add("card-title");

        CheckBox? battery = null, metered = null;
        var rows = new StackPanel { Spacing = 14 };
        foreach (var condition in assessment.Unmet)
        {
            var (title, detail, anyway) = condition switch
            {
                ManualRunConditions.Condition.Battery => ("Runs only on AC power", "This computer is on battery.", "Run on battery anyway"),
                ManualRunConditions.Condition.Metered => ("Runs only on an unmetered connection",
                    "This connection is metered" + (assessment.LocalAlsoRuns
                        ? $". Destinations on this computer are backed up now either way; this concerns {Names(assessment.NetworkDestinations)}."
                        : "."),
                    "Use this metered connection anyway"),
                _ => ("Needs a network connection", $"There is none now, and {Names(assessment.NetworkDestinations)} {(assessment.NetworkDestinations.Count == 1 ? "is" : "are")} only reachable over one.", null),
            };
            var icon = new PathIcon { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            icon.Classes.Add("status-icon");
            icon.Classes.Add("warn");
            var titleText = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            titleText.Classes.Add("body");
            var detailText = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap };
            detailText.Classes.Add("helper");
            var texts = new StackPanel { Spacing = 4, Children = { titleText, detailText } };
            if (anyway is not null)
            {
                var box = new CheckBox { Content = anyway, Margin = new Thickness(0, 4, 0, 0) };
                box.Name = condition == ManualRunConditions.Condition.Battery ? "WaiveBattery" : "WaiveMetered";
                texts.Children.Add(box);
                if (condition == ManualRunConditions.Condition.Battery) battery = box; else metered = box;
            }
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*") };
            row.Children.Add(icon);
            Grid.SetColumn(texts, 2);
            row.Children.Add(texts);
            rows.Children.Add(row);
        }
        var box2 = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14), Child = rows, BorderThickness = new Thickness(1) };
        box2[!Border.BackgroundProperty] = new DynamicResourceExtension("BS.Brush.BgSurface");
        box2[!Border.BorderBrushProperty] = new DynamicResourceExtension("BS.Brush.BorderSubtle");

        var note = new TextBlock { TextWrapping = TextWrapping.Wrap };
        note.Classes.Add("helper");
        var go = new Button { Name = "RunConditionsGo", IsDefault = true };
        go.Classes.Add("primary");
        void Refresh()
        {
            var decision = Current();
            var blocking = ManualRunConditions.Blocking(assessment, decision);
            go.Content = blocking.Count == 0 ? "Run now" : "Run when ready";
            note.Text = (blocking.Count == 0
                    ? "It starts now."
                    : $"It waits on its card for {string.Join(" and ", blocking.Select(ManualRunConditions.Awaited))}, then starts by itself; Stop on the card cancels it.")
                + (decision.WaiveBattery || decision.WaiveMetered
                    ? " What you ignore stays ignored until this run ends."
                    : "")
                + " A condition that stops holding while it runs pauses it, and Backspace tells you, so you can continue anyway.";
        }
        ManualRunConditions.Decision Current() => new(battery?.IsChecked == true, metered?.IsChecked == true);
        if (battery is not null) battery.IsCheckedChanged += (_, _) => Refresh();
        if (metered is not null) metered.IsCheckedChanged += (_, _) => Refresh();
        Refresh();
        go.Click += (_, _) => { _decision = Current(); Close(); };

        var cancel = new Button { Content = "Not now", IsCancel = true };
        cancel.Classes.Add("ghost");
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10,
            Children = { cancel, go } };

        Content = new StackPanel
        {
            Margin = new Thickness(32, 28, 32, 24),
            Spacing = 16,
            Children = { heading, box2, note, buttons },
        };
    }

    private static string Names(IReadOnlyList<string> names) => names.Count == 0 ? "its network destinations" : string.Join(", ", names.Select(n => "«" + n + "»"));

    internal async Task<ManualRunConditions.Decision?> AskAsync(Window? owner)
    {
        if (owner is not null) await ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            Closed += (_, _) => closed.TrySetResult();
            Show();
            await closed.Task;
        }
        return _decision;
    }
}
