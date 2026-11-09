using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.Services;
using Backspace.Views;
using Xunit;
using static Backspace.Services.MarkdownLite;
using Span = Backspace.Services.MarkdownLite.Span;

namespace Backspace.Tests.Views;

/// <summary>U169: release notes in Settings → App updates showed GitHub's
/// Markdown as raw text (## headings, - bullets, **bold**, `code`). They are
/// now rendered. The fixture is the real 1.1.22 release body.</summary>
public sealed class MarkdownViewTests
{
    private static string Notes([CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Services", "Fixtures", "release-notes-v1.1.22.txt"));

    [Fact]
    public void The_1_1_22_notes_parse_into_headings_bullets_bold_and_code()
    {
        var blocks = Parse(Notes());
        Assert.Equal(new[] { "Backups with automatic backups off", "Online-only cloud files", "Saved passwords", "Other", "Known limits" },
            blocks.Where(b => b.Kind == BlockKind.Heading).Select(b => string.Concat(b.Spans.Select(s => s.Text))));
        Assert.All(blocks.Where(b => b.Kind == BlockKind.Heading), h => Assert.Equal(2, h.Level));
        Assert.Equal(15, blocks.Count(b => b.Kind == BlockKind.Bullet));
        Assert.DoesNotContain(blocks, b => b.Kind == BlockKind.Paragraph);

        var first = blocks.First(b => b.Kind == BlockKind.Bullet);
        Assert.Equal(new Span(SpanKind.Bold, "Run now goes through the background service"), first.Spans[0]);
        Assert.Equal(SpanKind.Text, first.Spans[1].Kind);
        Assert.StartsWith(" when it is installed", first.Spans[1].Text);

        var codes = blocks.SelectMany(b => b.Spans).Where(s => s.Kind == SpanKind.Code).Select(s => s.Text).ToList();
        Assert.Equal(new[] { "--run <name>", "unsupported file type \"irregular\"", "secrets.bin" }, codes);
        // No Markdown marker survives as text.
        var text = string.Concat(blocks.SelectMany(b => b.Spans).Where(s => s.Kind != SpanKind.Code).Select(s => s.Text));
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("`", text);
        Assert.DoesNotContain("## ", text);
    }

    [Fact]
    public void Paragraphs_join_their_lines_and_unclosed_markers_stay_as_written()
    {
        var blocks = Parse("First line\nsecond line.\n\n# Title #\n* star bullet\n  continued\nA **half bold and a ` tick");
        Assert.Equal(new Block(BlockKind.Paragraph, 0, new[] { new Span(SpanKind.Text, "First line second line.") }), blocks[0], BlockComparer.Instance);
        Assert.Equal(BlockKind.Heading, blocks[1].Kind);
        Assert.Equal(1, blocks[1].Level);
        Assert.Equal("Title", blocks[1].Spans.Single().Text);
        Assert.Equal("star bullet continued", string.Concat(blocks[2].Spans.Select(s => s.Text)));
        Assert.Equal("A **half bold and a ` tick", string.Concat(blocks[3].Spans.Select(s => s.Text)));
        Assert.All(blocks[3].Spans, s => Assert.Equal(SpanKind.Text, s.Kind));
        // "#hashtag" and "-dash" are not headings or bullets.
        Assert.All(Parse("#hashtag\n-dash"), b => Assert.Equal(BlockKind.Paragraph, b.Kind));
        Assert.Empty(Parse(null));
        Assert.Empty(Parse("  \n\n"));
    }

    [Theory]
    [InlineData("https://github.com/mayerwin/Backspace/releases", "https://github.com/mayerwin/Backspace/releases")]
    [InlineData("http://example.com", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///C:/Windows/System32/calc.exe", null)]
    [InlineData("/relative/path", null)]
    [InlineData("https://", null)]
    public void Only_https_links_open(string url, string? opens)
    {
        var span = Assert.Single(MarkdownLite.Inline($"[the page]({url})"));
        Assert.Equal(SpanKind.Link, span.Kind);
        Assert.Equal("the page", span.Text);
        Assert.Equal(opens, span.Url);
    }

    [AvaloniaFact]
    public void The_view_shows_the_notes_without_markdown_markers_and_opens_only_https_links()
    {
        // The links come first so they are on screen to be clicked, below
        // the window's own title bar area.
        var view = new MarkdownView { Markdown = "See [the release page](https://github.com/mayerwin/Backspace/releases) or [this](file:///C:/x).\n\n" + Notes(),
            Margin = new Thickness(0, 80, 0, 0) };
        var window = new Window { Width = 640, Height = 900, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            var texts = view.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
            var headings = texts.Where(t => t.FontWeight == FontWeight.Bold).Select(t => t.Inlines!.OfType<Run>().Single().Text).ToList();
            Assert.Equal(new[] { "Backups with automatic backups off", "Online-only cloud files", "Saved passwords", "Other", "Known limits" }, headings);
            var shown = string.Concat(texts.SelectMany(t => t.Inlines!.OfType<Run>()).Select(r => r.Text));
            Assert.DoesNotContain("**", shown);
            Assert.DoesNotContain("## ", shown);
            Assert.Contains("Run now goes through the background service", shown);
            var bold = texts.SelectMany(t => t.Inlines!.OfType<Run>()).First(r => r.Text == "Run now goes through the background service");
            Assert.Equal(FontWeight.Bold, bold.FontWeight);
            Assert.Equal(15, view.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == "•"));

            // One link opens, the file: one is plain text.
            var opened = new List<string>();
            var previous = MarkdownView.OpenLink;
            MarkdownView.OpenLink = opened.Add;
            try
            {
                var links = texts.SelectMany(t => t.Inlines!.OfType<InlineUIContainer>()).Select(c => (TextBlock)c.Child).ToList();
                var link = Assert.Single(links);
                Assert.Equal("the release page", link.Text);
                Assert.Contains("this", shown);
                // Render first: pointer hit-testing uses the rendered scene.
                Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
                window.MouseDown(link.TranslatePoint(new Point(4, 4), window)!.Value, Avalonia.Input.MouseButton.Left);
                window.MouseUp(link.TranslatePoint(new Point(4, 4), window)!.Value, Avalonia.Input.MouseButton.Left);
                var at = link.TranslatePoint(new Point(4, 4), window)!.Value;
                Assert.True(opened.Count == 1, $"Hit at {at}: " + string.Join(">", (window.InputHitTest(at) as Visual)?.GetSelfAndVisualAncestors().Select(v => v.GetType().Name) ?? []));
                Assert.Equal(new[] { "https://github.com/mayerwin/Backspace/releases" }, opened);
            }
            finally { MarkdownView.OpenLink = previous; }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Shot_release_notes_light() => Shot(ThemeVariant.Light, "release-notes-light.png");

    [AvaloniaFact]
    public void Shot_release_notes_dark() => Shot(ThemeVariant.Dark, "release-notes-dark.png");

    /// <summary>The App updates card as Settings shows it with an update
    /// available and "What changed" open.</summary>
    private static void Shot(ThemeVariant theme, string fileName, [CallerFilePath] string here = "")
    {
        Application.Current!.RequestedThemeVariant = theme;
        var window = new Window { Width = 760, Height = 1040, SizeToContent = SizeToContent.Height };
        try
        {
            window.Bind(Window.BackgroundProperty, window.GetResourceObservable("BS.Brush.BgBase"));
            var card = new Border { Classes = { "settings-card" }, Padding = new Thickness(20), Margin = new Thickness(24) };
            card.Bind(Border.BackgroundProperty, card.GetResourceObservable("BS.Brush.BgSurface"));
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(new TextBlock { Classes = { "section-title" }, Text = "App updates", Margin = new Thickness(0) });
            stack.Children.Add(new TextBlock { Classes = { "body" }, FontWeight = FontWeight.SemiBold, Text = "Update to 1.1.22" });
            stack.Children.Add(new Expander
            {
                Header = "What changed", IsExpanded = true, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Content = new MarkdownView { Markdown = Notes(), Margin = new Thickness(0, 12, 0, 0) },
            });
            card.Child = stack;
            window.Content = card;
            window.Show();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "artifacts", "screenshots"));
            Directory.CreateDirectory(dir);
            var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(dir, fileName));
        }
        finally
        {
            window.Close();
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }
    }

    private sealed class BlockComparer : IEqualityComparer<Block>
    {
        public static readonly BlockComparer Instance = new();
        public bool Equals(Block? x, Block? y) => x is not null && y is not null && x.Kind == y.Kind && x.Level == y.Level && x.Spans.SequenceEqual(y.Spans);
        public int GetHashCode(Block obj) => obj.Kind.GetHashCode();
    }
}
