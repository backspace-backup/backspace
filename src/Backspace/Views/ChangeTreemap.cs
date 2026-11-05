using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Backspace.Services;

namespace Backspace.Views;

/// <summary>
/// A WinDirStat-like map of what a backup added (U143): each box is a folder
/// or file, its area its bytes. The folders of the current level get a colour
/// and show their own folders inside; clicking a folder opens it.
/// </summary>
public sealed class ChangeTreemap : Control
{
    public static readonly StyledProperty<object?> NodeProperty =
        AvaloniaProperty.Register<ChangeTreemap, object?>(nameof(Node));

    /// <summary>The folder shown (a <see cref="ChangeNode"/>).</summary>
    public object? Node { get => GetValue(NodeProperty); set => SetValue(NodeProperty, value); }

    public event Action<ChangeNode>? Opened;
    public event Action<ChangeNode?>? Hovered;

    // Muted hues that keep dark text readable, in light and dark themes.
    private static readonly Color[] Palette =
    {
        Color.Parse("#7FA7E0"), Color.Parse("#E7A86B"), Color.Parse("#8CC79A"), Color.Parse("#D98C9A"),
        Color.Parse("#B39DDB"), Color.Parse("#E3CF6E"), Color.Parse("#7CC6C8"), Color.Parse("#C7A58A"),
    };

    private readonly List<(ChangeNode Node, Rect Box, int Depth)> _boxes = new();
    private ChangeNode? _hover;

    static ChangeTreemap()
    {
        AffectsRender<ChangeTreemap>(NodeProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ChangeTreemap>(true);
    }

    public override void Render(DrawingContext context)
    {
        _boxes.Clear();
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds);
        if (Node is not ChangeNode node || node.Bytes <= 0) return;
        var items = Items(node);
        var laid = Treemap.Layout(items, new Treemap.Box(0, 0, bounds.Width, bounds.Height));
        var i = 0;
        foreach (var (child, box) in laid)
        {
            var color = Palette[i++ % Palette.Length];
            var rect = new Rect(box.X, box.Y, box.W, box.H).Deflate(1);
            if (rect.Width < 1 || rect.Height < 1) continue;
            context.FillRectangle(new SolidColorBrush(color), rect, 3);
            _boxes.Add((child, rect, 0));
            var header = child.HasChildren && rect.Width >= 70 && rect.Height >= 46;
            Label(context, rect, child, bold: true);
            if (!header) continue;
            // The folder's own folders and files inside it, a lighter shade.
            var inner = new Rect(rect.X + 3, rect.Y + 20, rect.Width - 6, rect.Height - 23);
            var shade = new SolidColorBrush(Color.FromArgb(255,
                (byte)(color.R + (255 - color.R) * 0.45), (byte)(color.G + (255 - color.G) * 0.45), (byte)(color.B + (255 - color.B) * 0.45)));
            foreach (var (grandchild, b) in Treemap.Layout(Items(child), new Treemap.Box(inner.X, inner.Y, inner.Width, inner.Height)))
            {
                var r = new Rect(b.X, b.Y, b.W, b.H).Deflate(0.75);
                if (r.Width < 1 || r.Height < 1) continue;
                context.FillRectangle(shade, r, 2);
                _boxes.Add((grandchild, r, 1));
                if (r.Width >= 56 && r.Height >= 16) Label(context, r, grandchild, bold: false);
            }
        }
        if (_hover is not null && _boxes.FirstOrDefault(b => ReferenceEquals(b.Node, _hover)) is { Node: not null } hovered)
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#1B1F24")), 2), hovered.Box, 3, 3);
    }

    private static List<(ChangeNode Item, double Weight)> Items(ChangeNode node)
        => node.Children.Select(c => (c, (double)c.Bytes)).ToList();

    private static void Label(DrawingContext context, Rect rect, ChangeNode node, bool bold)
    {
        if (rect.Width < 40 || rect.Height < 15) return;
        var text = node.Name + "  " + BackupLiveProgress.Bytes(node.Bytes);
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), 11.5,
            new SolidColorBrush(Color.Parse("#1B1F24")))
        {
            MaxTextWidth = Math.Max(1, rect.Width - 8),
            MaxTextHeight = 16,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(formatted, new Point(rect.X + 4, rect.Y + 2));
    }

    private ChangeNode? Hit(Point p)
    {
        // Deepest box under the pointer: second-level boxes come after their parent.
        ChangeNode? hit = null;
        foreach (var (node, box, _) in _boxes)
            if (box.Contains(p)) hit = node;
        return hit;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = Hit(e.GetPosition(this));
        if (ReferenceEquals(hit, _hover)) return;
        _hover = hit;
        Hovered?.Invoke(hit);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        Hovered?.Invoke(null);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Open the folder clicked: the inner one if it is a folder, else its parent box.
        var hit = Hit(e.GetPosition(this));
        while (hit is not null && (hit.IsFile || !hit.HasChildren)) hit = hit.Parent is { } parent && !ReferenceEquals(parent, Node) ? parent : null;
        if (hit is not null) { Opened?.Invoke(hit); e.Handled = true; }
    }
}
