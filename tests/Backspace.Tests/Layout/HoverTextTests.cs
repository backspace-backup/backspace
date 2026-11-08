using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Layout;

/// <summary>U164 (8 November 2026): the Last backup hover was cut after two
/// lines ("since the previo…") because a tooltip inherits MaxLines and
/// TextTrimming from its card label.</summary>
public class HoverTextTests
{
    private const string Hover = "Finished Sat 7 Nov, 20:11 · took 11m 29s · 150 new, 195 changed and 0 deleted files since the previous backup · 127.42 MB of new data uploaded.";

    [Fact]
    public void The_first_two_segments_share_the_first_line_and_each_other_has_its_own()
    {
        Assert.Equal("Finished Sat 7 Nov, 20:11 · took 11m 29s\n150 new, 195 changed and 0 deleted files since the previous backup\n127.42 MB of new data uploaded.",
            HoverSegments.Layout(Hover));
        Assert.Equal("A plain sentence.", HoverSegments.Layout("A plain sentence."));
        Assert.Equal("one · two", HoverSegments.Layout("one · two"));
    }

    [AvaloniaFact]
    public void A_tooltip_inside_a_two_line_label_is_never_cut()
    {
        var tip = new ToolTip { Content = Hover };
        var host = new Border { Child = tip };
        TextBlock.SetMaxLines(host, 2);
        TextBlock.SetTextTrimming(host, Avalonia.Media.TextTrimming.CharacterEllipsis);
        var window = new Window { Width = 800, Height = 400, Content = host };
        window.Show();
        window.UpdateLayout();
        var text = tip.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.Contains("Finished") == true);
        Assert.Equal(0, text.MaxLines);
        Assert.Equal(Avalonia.Media.TextTrimming.None, text.TextTrimming);
        Assert.Equal(3, text.Text!.Split('\n').Length);
        Assert.False(text.TextLayout.TextLines.Any(l => l.HasCollapsed), "Some text was cut.");
        window.Close();
    }
}
