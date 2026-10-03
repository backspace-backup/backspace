using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Xunit;
using Xunit.Abstractions;

namespace Backspace.Tests.Screenshots;

/// <summary>
/// Diagnostic for the "Powered by Backrest" sidebar footer link.
/// History: the link's leading whitespace > the configured
/// StackPanel.Spacing and the dotted underline only appeared on
/// hover; both were fixed via this test's bounds + decoration
/// dump. Kept around so future regressions on the same link
/// surface as obvious deltas in the captured PNG / output log.
/// </summary>
public class PoweredByLinkTests
{
    private static readonly string OutputDir = ResolveOutputDir();
    private readonly ITestOutputHelper _out;
    public PoweredByLinkTests(ITestOutputHelper output) => _out = output;

    [AvaloniaFact]
    public void Shot_PoweredByBackrest_RestingState()
    {
        var window = new Window
        {
            Title = "Footer link audit",
            Width = 360,
            Height = 80,
            Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xF8, 0xF2)),
            SystemDecorations = SystemDecorations.None,
        };

        // Replicate the MainWindow footer's exact structure.
        var poweredBy = new TextBlock
        {
            Text = "Powered by",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        poweredBy.Classes.Add("dim");

        var linkLabel = new TextBlock
        {
            Text = "Backrest",
            FontSize = 12,
        };

        var linkButton = new Button
        {
            Content = linkLabel,
        };
        linkButton.Classes.Add("text-link");

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2, // matches MainWindow footer
            Margin = new Thickness(20),
        };
        stack.Children.Add(poweredBy);
        stack.Children.Add(linkButton);

        window.Content = stack;
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Diagnostic dump.
        _out.WriteLine("=== Footer link layout introspection ===");
        _out.WriteLine($"StackPanel bounds         = {stack.Bounds}");
        _out.WriteLine($"  TextBlock 'Powered by'  bounds = {poweredBy.Bounds}");
        _out.WriteLine($"  Button (text-link)      bounds = {linkButton.Bounds}");
        _out.WriteLine($"    .Padding              = {linkButton.Padding}");
        _out.WriteLine($"    .Margin               = {linkButton.Margin}");
        _out.WriteLine($"    .MinWidth             = {linkButton.MinWidth}");
        _out.WriteLine($"    .HorizontalContentAlignment = {linkButton.HorizontalContentAlignment}");
        _out.WriteLine($"    inner TextBlock 'Backrest' bounds = {linkLabel.Bounds}");
        _out.WriteLine($"      .Margin             = {linkLabel.Margin}");
        _out.WriteLine($"      .TextDecorations    = {DescribeDecorations(linkLabel.TextDecorations)}");

        // Walk the template tree under the button.
        foreach (var v in linkButton.GetVisualDescendants())
        {
            _out.WriteLine($"    visualDescendant {v.GetType().Name} bounds={v.Bounds}");
        }

        // Compute the actual gap between "Powered by" and "Backrest"
        // in screen pixels.
        var gap = linkLabel.Bounds.X
                  + linkButton.Bounds.X
                  + stack.Bounds.X
                  - (poweredBy.Bounds.X + stack.Bounds.X + poweredBy.Bounds.Width);
        _out.WriteLine($"  effective gap between 'Powered by' end and 'Backrest' start = {gap:F1} px " +
                       "(StackPanel.Spacing=4 ⇒ anything over ~5px is the bug)");

        Directory.CreateDirectory(OutputDir);
        var path = Path.Combine(OutputDir, "powered-by-link.png");
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Height)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        ScreenshotDiffTests.SaveAndVerify(bitmap, path);
        _out.WriteLine($"saved: {path}");
        window.Close();
    }

    private static string DescribeDecorations(TextDecorationCollection? decs)
    {
        if (decs is null || decs.Count == 0) return "null/empty";
        var parts = decs.Select(d =>
            $"[Location={d.Location} StrokeDashArray={(d.StrokeDashArray is null ? "null" : string.Join(",", d.StrokeDashArray))} " +
            $"StrokeOffset={d.StrokeOffset} StrokeOffsetUnit={d.StrokeOffsetUnit} " +
            $"StrokeThickness={d.StrokeThickness} StrokeThicknessUnit={d.StrokeThicknessUnit}]");
        return string.Join("; ", parts);
    }

    private static string ResolveOutputDir()
    {
        var asmDir = Path.GetDirectoryName(typeof(PoweredByLinkTests).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Backspace.Tests.csproj")))
            dir = dir.Parent;
        var root = dir?.FullName ?? asmDir;
        return Path.Combine(root, "artifacts", "screenshots");
    }
}
