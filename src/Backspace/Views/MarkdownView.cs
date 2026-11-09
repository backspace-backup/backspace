using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Backspace.Services;

namespace Backspace.Views;

/// <summary>
/// Shows release notes written in Markdown (U169): headings, bullet lists,
/// bold, inline code and https links, in the app's fonts and theme colours.
/// Built from <see cref="MarkdownLite"/>; nothing else is interpreted.
/// </summary>
public sealed class MarkdownView : Decorator
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Opens a link; replaced by tests. Receives only https addresses.</summary>
    internal static Action<string> OpenLink { get; set; } = url =>
    {
        try { using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.For<MarkdownView>().Warning(ex, "Could not open {Url}", url); }
    };

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Build());
    }

    private void Build()
    {
        var panel = new StackPanel { Spacing = 6 };
        var first = true;
        foreach (var block in MarkdownLite.Parse(Markdown))
        {
            Control control = block.Kind switch
            {
                MarkdownLite.BlockKind.Heading => Heading(block, first),
                MarkdownLite.BlockKind.Bullet => Bullet(block),
                _ => Paragraph(block),
            };
            panel.Children.Add(control);
            first = false;
        }
        Child = panel;
    }

    private Control Heading(MarkdownLite.Block block, bool first)
    {
        var text = Text(block.Spans);
        text.FontWeight = FontWeight.Bold;
        text.FontSize = block.Level <= 1 ? 17 : block.Level == 2 ? 15 : 14;
        text.Margin = new Thickness(0, first ? 0 : 10, 0, 0);
        Brush(text, TextBlock.ForegroundProperty, "BS.Brush.FgPrimary");
        Font(text, "BS.Font.Heading");
        return text;
    }

    private Control Bullet(MarkdownLite.Block block)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*"), Margin = new Thickness(4, 0, 0, 0) };
        var dot = new TextBlock { Text = "•", VerticalAlignment = VerticalAlignment.Top };
        Brush(dot, TextBlock.ForegroundProperty, "BS.Brush.FgSecondary");
        Font(dot, "BS.Font.Body");
        var text = Text(block.Spans);
        Grid.SetColumn(text, 1);
        grid.Children.Add(dot);
        grid.Children.Add(text);
        return grid;
    }

    private Control Paragraph(MarkdownLite.Block block) => Text(block.Spans);

    private SelectableTextBlock Text(System.Collections.Generic.IReadOnlyList<MarkdownLite.Span> spans)
    {
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        Brush(text, TextBlock.ForegroundProperty, "BS.Brush.FgPrimary");
        Font(text, "BS.Font.Body");
        text.Inlines = new InlineCollection();
        foreach (var span in spans)
        {
            switch (span.Kind)
            {
                case MarkdownLite.SpanKind.Bold:
                    text.Inlines.Add(new Run(span.Text) { FontWeight = FontWeight.Bold });
                    break;
                case MarkdownLite.SpanKind.Code:
                    var code = new Run(" " + span.Text + " ") { FontSize = 12.5 };
                    Font(code, "BS.Font.Mono");
                    Brush(code, TextElement.BackgroundProperty, "BS.Brush.RaisedChip");
                    text.Inlines.Add(code);
                    break;
                case MarkdownLite.SpanKind.Link when span.Url is { } url:
                    text.Inlines.Add(new InlineUIContainer(Link(span.Text, url)));
                    break;
                default:
                    text.Inlines.Add(new Run(span.Text));
                    break;
            }
        }
        return text;
    }

    /// <summary>A link opens in the browser, and only to an https address.</summary>
    internal TextBlock Link(string label, string url)
    {
        var link = new TextBlock { Text = label, TextDecorations = Avalonia.Media.TextDecorations.Underline, Cursor = new Cursor(StandardCursorType.Hand),
            // A background, even transparent, makes the whole word take the click.
            Background = Brushes.Transparent };
        Brush(link, TextBlock.ForegroundProperty, "BS.Brush.Accent");
        Font(link, "BS.Font.Body");
        ToolTip.SetTip(link, url);
        // On press: the surrounding selectable text takes the pointer for
        // selection, so the link would never see the release.
        link.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(link).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (MarkdownLite.SafeUrl(url) is { } safe) OpenLink(safe);
        };
        return link;
    }

    private void Brush(AvaloniaObject target, AvaloniaProperty property, string key)
        => target.Bind(property, this.GetResourceObservable(key));

    private void Font(AvaloniaObject target, string key)
        => target.Bind(TextElement.FontFamilyProperty, this.GetResourceObservable(key));
}
