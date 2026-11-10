using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.Services;
using Backspace.ViewModels;

namespace Backspace.Views;

/// <summary>
/// One line of the Activity &amp; logs console (U176), built in code so a
/// virtualizing list can recycle it cheaply: the time column, the line with
/// its colour (a class per <see cref="ConsoleLineKind"/>, styled in
/// LogsView.axaml from the theme's status colours) and its brighter parts
/// (tag, percentage, paths); for a section, a thin rule above a bold header
/// with a Copy button for that run, check or cleanup.
/// </summary>
public sealed class ConsoleLineRow : Border
{
    private readonly Border _rule = new() { Classes = { "ln-rule" }, IsVisible = false };
    private readonly TextBlock _time = new() { Classes = { "ln-time" } };
    private readonly TextBlock _text = new() { Classes = { "ln" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { Classes = { "ln-subtitle" }, IsVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _copy;
    private ConsoleLine? _line;
    private string? _kindClass;

    public ConsoleLineRow()
    {
        Classes.Add("ln-row");
        _copy = new Button
        {
            Classes = { "ghost", "console-action", "ln-copy" },
            Content = "Copy",
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_copy, "Copy this part of the log");
        _copy.Click += OnCopyClick;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        Grid.SetColumnSpan(_rule, 4);
        Grid.SetRow(_time, 1);
        Grid.SetRow(_text, 1); Grid.SetColumn(_text, 1);
        Grid.SetRow(_subtitle, 1); Grid.SetColumn(_subtitle, 2);
        Grid.SetRow(_copy, 1); Grid.SetColumn(_copy, 3);
        grid.Children.Add(_rule);
        grid.Children.Add(_time);
        grid.Children.Add(_text);
        grid.Children.Add(_subtitle);
        grid.Children.Add(_copy);
        Child = grid;
    }

    internal ConsoleLine? Line => _line;

    // A row made before it joined the console finds the console's colours now.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Build();
    }
    internal TextBlock TextBlock => _text;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_line is not null) _line.PropertyChanged -= OnLinePropertyChanged;
        _line = DataContext as ConsoleLine;
        if (_line is not null) _line.PropertyChanged += OnLinePropertyChanged;
        Build();
    }

    private void OnLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConsoleLine.IsSelected) or nameof(ConsoleLine.IsFlashing) or nameof(ConsoleLine.IsFirst)) UpdateState();
    }

    private void UpdateState()
    {
        Classes.Set("selected", _line?.IsSelected == true);
        Classes.Set("flash", _line?.IsFlashing == true);
        // No rule above the first header: the console's edge is already there.
        _rule.IsVisible = _line is { Kind: ConsoleLineKind.Section, IsFirst: false };
    }

    private void Build()
    {
        UpdateState();
        var line = _line;
        if (_kindClass is not null) { _text.Classes.Remove(_kindClass); Classes.Remove(_kindClass); }
        if (line is null) { _text.Text = ""; _time.Text = ""; return; }
        _kindClass = "ln-" + line.Kind.ToString().ToLowerInvariant();
        _text.Classes.Add(_kindClass);
        Classes.Add(_kindClass);
        var section = line.Kind == ConsoleLineKind.Section;
        // A header's title carries its time.
        _time.Text = section ? "" : line.TimeLabel;
        _rule.IsVisible = section && !line.IsFirst;
        _copy.IsVisible = section;
        _subtitle.IsVisible = section && !string.IsNullOrEmpty(line.Subtitle);
        _subtitle.Text = section ? line.Subtitle : null;
        if (section) { SetPlain(line.SectionTitle); return; }

        var spans = ConsoleLineClassifier.Spans(line.Text, line.Kind);
        if (spans.Count == 0) { SetPlain(line.Text); return; }
        var inlines = new InlineCollection();
        var text = line.Text;
        var at = 0;
        foreach (var span in spans.OrderBy(s => s.Start))
        {
            if (span.Start < at) continue;
            if (span.Start > at) inlines.Add(new Run(Shown(text[at..span.Start])));
            var run = new Run(Shown(text.Substring(span.Start, span.Length)));
            switch (span.Kind)
            {
                case ConsoleLineClassifier.SpanKind.Tag:
                    run.Foreground = Brush("BS.Brush.ConsoleTag");
                    break;
                case ConsoleLineClassifier.SpanKind.Percent:
                    run.Foreground = Brush("BS.Brush.FgPrimary");
                    run.FontWeight = FontWeight.SemiBold;
                    break;
                case ConsoleLineClassifier.SpanKind.Path:
                    run.Foreground = Brush("BS.Brush.FgPrimary");
                    break;
            }
            inlines.Add(run);
            at = span.Start + span.Length;
        }
        if (at < text.Length) inlines.Add(new Run(Shown(text[at..])));
        _text.Text = null;
        _text.Inlines = inlines;
    }

    private void SetPlain(string text)
    {
        _text.Inlines = null;
        _text.Text = Shown(text);
    }

    // Backrest separates its fields with tabs, which a proportional tab stop
    // spreads across the line; two spaces read as the same separation.
    private static string Shown(string text) => text.Contains('\t') ? text.Replace("\t", "  ") : text;

    // The console always uses the dark theme's colours (it is dark in both themes).
    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ThemeVariant.Dark, out var value) && value is IBrush brush ? brush
        : Application.Current?.TryGetResource(key, ThemeVariant.Dark, out value) == true && value is IBrush appBrush ? appBrush
        : null;

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_line is null) return;
        if (this.FindAncestorOfType<LogsView>()?.DataContext is LogsViewModel vm)
            vm.CopySectionCommand.Execute(_line);
        e.Handled = true;
    }
}
