using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Layout;

/// <summary>
/// Optical alignment on Your backups (U037-U039, 30 October 2026): the
/// symbol inside the notice disc and the card tile sat off-centre, the
/// notice's detail text did not share the headline's line, and the count
/// pill beside the title sat off the title's letters. These measure the
/// rendered pixels and font metrics rather than layout slots, because each
/// bug had correct slots and wrong ink.
/// </summary>
public class IconAndBadgeAlignmentTests
{
    private readonly ITestOutputHelper _out;
    public IconAndBadgeAlignmentTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// The PathIcon template draws every icon on Lucide's 24-unit canvas,
    /// so a geometry outside it would spill past the icon's own bounds.
    /// Measured from pixels: Geometry.Bounds counts curve control points.
    /// </summary>
    [AvaloniaFact]
    public void Every_icon_draws_inside_its_own_bounds()
    {
        var keys = IconKeys();
        Assert.True(keys.Length > 30, $"Found only {keys.Length} icons.");
        foreach (var key in keys)
        {
            Assert.True(Application.Current!.TryFindResource(key, out var res) && res is Geometry, key);
            var icon = new PathIcon { Data = (Geometry)res!, Width = 48, Height = 48, Foreground = Brushes.Black };
            var window = new Window { Width = 96, Height = 96, Background = Brushes.White, Content = icon };
            window.Show();
            window.UpdateLayout();
            var origin = icon.TranslatePoint(default, window)!.Value;
            var ink = Pixels.Render(window, 1).Ink(new Rect(0, 0, 96, 96), (r, g, b) => r < 200 && g < 200 && b < 200);
            window.Close();
            Assert.True(new Rect(origin, new Size(48, 48)).Contains(ink),
                $"{key} draws over {ink}, outside its 48 px icon at {origin}.");
        }
    }

    /// <summary>
    /// The symbol's ink is centred in its disc or tile. Before the fix the
    /// template sized each icon to its geometry's far edge, so the empty
    /// margin of the 24-unit canvas stayed on the top-left only and every
    /// symbol drifted right and down.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Icon.Backup", 36, 18)]   // card tile
    [InlineData("Icon.Warn", 24, 14)]     // notice disc
    [InlineData("Icon.Idle", 24, 14)]
    [InlineData("Icon.Check", 24, 14)]
    [InlineData("Icon.Fail", 24, 14)]
    [InlineData("Icon.Skipped", 24, 14)]
    [InlineData("Icon.Clock", 24, 14)]
    public void Icon_ink_is_centred_in_its_container(string key, double box, double iconSize)
    {
        var geometry = (Geometry)Application.Current!.FindResource(key)!;
        var container = new Border
        {
            Width = box, Height = box,
            Background = Brushes.White,
            Child = new PathIcon { Data = geometry, Width = iconSize, Height = iconSize, Foreground = Brushes.Black },
        };
        var window = new Window { Width = 80, Height = 80, Background = Brushes.White, Content = container };
        window.Show();
        window.UpdateLayout();

        // Render at 4x so a quarter-pixel drift is measurable.
        var shot = Pixels.Render(window, 4);
        var origin = container.TranslatePoint(default, window)!.Value;
        var ink = shot.Ink(new Rect(origin, new Size(box, box)), (r, g, b) => r < 200 && g < 200 && b < 200);
        var dx = ink.Center.X - (origin.X + box / 2);
        var dy = ink.Center.Y - (origin.Y + box / 2);
        _out.WriteLine($"{key}: ink centre off by ({dx:0.00}, {dy:0.00}) px");
        window.Close();

        // The geometry's own bounding box is centred on (12,12) to within
        // half a unit (Icon.Check sits on 11.5), a quarter pixel here.
        Assert.True(Math.Abs(dx) <= 0.3 && Math.Abs(dy) <= 0.3,
            $"{key} ink centre is off by ({dx:0.00}, {dy:0.00}) px in a {box}px container.");
    }

    /// <summary>
    /// The count pill centres on the title's capital letters, and its
    /// figure centres in the pill. Your backups and Destinations share it.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Count_badge_lines_up_with_the_title_letters(bool destinations) => WithHeader(destinations, 1180, (view, window) =>
    {
        var badge = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("count-badge"));
        Assert.True(badge.IsEffectivelyVisible);
        var title = PageHeaderAlignmentTests.FindByClass(view, "page-title")!;

        // Capital letters measured from pixels: the title's leading "Y"
        // and the pill's figure, both free of descenders.
        const int scale = 4;
        var shot = Pixels.Render(window, scale);
        static bool Dark(byte r, byte g, byte b) => r + g + b < 3 * 140;
        var t = title.TranslatePoint(default, window)!.Value;
        var y = shot.Ink(new Rect(t.X, t.Y, 10, title.Bounds.Height), Dark);
        // A short lowercase letter (no ascender, past the Y's overhang) gives the lowercase height. The title is mostly
        // lowercase, so its optical middle lies between the two (U039
        // follow-up, 30 October 2026: centred on the capitals it read high).
        var titleText = ((TextBlock)title).Text!;
        var lower = Enumerable.Range(2, titleText.Length - 2).First(i => "acemnorsuvwxz".Contains(titleText[i]));
        var o = ((TextBlock)title).TextLayout.HitTestTextPosition(lower);
        var x = shot.Ink(new Rect(t.X + o.X + 1, t.Y, Math.Max(1, o.Width - 2), title.Bounds.Height), Dark);
        var optical = (y.Center.Y + x.Center.Y) / 2;
        var p = badge.TranslatePoint(default, window)!.Value;
        var pill = new Rect(p, badge.Bounds.Size);
        var figure = shot.Ink(pill.Deflate(2), Dark);
        var pillCentre = pill.Center.Y;
        _out.WriteLine($"title Y caps {y.Top:0.00}-{y.Bottom:0.00} (centre {y.Center.Y:0.00}), lowercase {x.Top:0.00}-{x.Bottom:0.00} (centre {x.Center.Y:0.00}), optical {optical:0.00}, pill {pill.Top:0.00}-{pill.Bottom:0.00} (centre {pillCentre:0.00}), figure {figure.Top:0.00}-{figure.Bottom:0.00} (centre {figure.Center.Y:0.00}, x {figure.Center.X - pill.Center.X:0.00})");

        Assert.True(x.Top > y.Top + 3, "The lowercase sample must be shorter than the capital.");
        Assert.True(Math.Abs(pillCentre - optical) <= 0.75,
            $"Count pill centre {pillCentre:0.00} vs the title's optical centre {optical:0.00}.");
        // Negative: centring on the capitals alone is what read too high.
        Assert.True(pillCentre > y.Center.Y + 0.5, $"Pill centre {pillCentre:0.00} still sits on the capitals ({y.Center.Y:0.00}).");
        Assert.True(Math.Abs(figure.Center.Y - pillCentre) <= 0.75,
            $"Figure centre {figure.Center.Y:0.00} vs pill centre {pillCentre:0.00}.");
        // A tabular "1" carries its ink slightly left of its advance.
        Assert.True(Math.Abs(figure.Center.X - pill.Center.X) <= 1,
            $"Figure is off the pill's middle by {figure.Center.X - pill.Center.X:0.00} px.");
    });

    /// <summary>
    /// The page's main action spans the title and subtitle, and the header
    /// text stops a short gap before it instead of running underneath (U040,
    /// 30 October 2026). The narrow width wraps the subtitle, which is where
    /// it used to run under Add a destination.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false, 1180)]
    [InlineData(true, 1180)]
    [InlineData(false, 760)]
    [InlineData(true, 760)]
    public void Header_action_spans_title_and_subtitle(bool destinations, double width) => WithHeader(destinations, width, (view, window) =>
    {
        var title = PageHeaderAlignmentTests.FindByClass(view, "page-title")!;
        var subtitle = PageHeaderAlignmentTests.FindByClass(view, "page-subtitle")!;
        var action = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("primary") && b.IsEffectivelyVisible);
        Rect Box(Visual v) => new(v.TranslatePoint(default, window)!.Value, v.Bounds.Size);
        var t = Box(title); var s = Box(subtitle); var a = Box(action);
        _out.WriteLine($"title {t}, subtitle {s}, action {a}");

        Assert.True(Math.Abs(a.Top - t.Top) <= 1, $"Action top {a.Top:0.0} vs title top {t.Top:0.0}.");
        Assert.True(Math.Abs(a.Bottom - s.Bottom) <= 1, $"Action bottom {a.Bottom:0.0} vs subtitle bottom {s.Bottom:0.0}.");
        var gap = a.Left - Math.Max(s.Right, t.Right);
        Assert.True(gap >= 16, $"Header text ends {gap:0.0} px before the action.");
        // Once the subtitle wraps it uses the room up to a modest gap.
        if (s.Height > 20) Assert.InRange(gap, 16, 32);
        // The text's own ink, not just its slot, stops before the button.
        var ink = Pixels.Render(window, 1).Ink(s, (r, g, b) => Math.Abs(r - g) < 12 && r < 200);
        Assert.True(ink.Right <= a.Left - 16, $"Subtitle ink reaches {ink.Right:0.0}, button starts at {a.Left:0.0}.");
    });

    /// <summary>
    /// Close-ups for review: the never-run state the VM showed, and
    /// Destinations narrow enough that its subtitle wraps. Rendered at 1x:
    /// a higher-DPI render target draws the virtualised cards at the wrong scale.
    /// </summary>
    [AvaloniaFact]
    public void Shot_header_closeups()
    {
        var dir = Path.Combine(RepoRoot(), "tests", "Backspace.Tests", "artifacts", "screenshots");
        Directory.CreateDirectory(dir);
        foreach (var (theme, label) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            WithHeader(false, 900, (view, window) => Save(window, Path.Combine(dir, $"header-closeup-backups-{label}.png")), theme, 400);
            WithHeader(true, 760, (view, window) => Save(window, Path.Combine(dir, $"header-closeup-destinations-{label}.png")), theme, 200);
            // A skipped backup's notice, wide (one line) and narrow (two lines).
            foreach (var width in new[] { 1000, 560 })
            {
                ServiceLocator.Config.Update(cfg =>
                {
                    cfg.Backups.Clear();
                    cfg.Backups.Add(new Backup { Name = "zulu-music", LastRunStatus = BackupRunStatus.Skipped,
                        LastRunStartUtc = DateTime.UtcNow.AddHours(-21), LastRunEndUtc = DateTime.UtcNow.AddHours(-21) });
                    cfg.Backups.Add(new Backup { Name = "documents", LastRunStatus = BackupRunStatus.Success,
                        LastRunStartUtc = DateTime.UtcNow.AddHours(-22), LastRunEndUtc = DateTime.UtcNow.AddHours(-22) });
                    cfg.Backups.Add(new Backup { Name = "photos", LastRunStatus = BackupRunStatus.Success,
                        LastRunStartUtc = DateTime.UtcNow.AddHours(-23), LastRunEndUtc = DateTime.UtcNow.AddHours(-23) });
                });
                var vm = new BackupsViewModel();
                var window = new Window { Width = width, Height = 230, Content = new BackupsView { DataContext = vm }, RequestedThemeVariant = theme };
                window.Show();
                window.UpdateLayout(); window.UpdateLayout();
                try { Save(window, Path.Combine(dir, $"notice-skipped-{width}-{label}.png")); }
                finally { window.Close(); vm.Dispose(); ServiceLocator.Config.Update(cfg => cfg.Backups.Clear()); }
            }
        }

        static void Save(Window window, string path)
        {
            // Cards fade and slide in; let that finish.
            for (var i = 0; i < 4; i++) Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
            var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(path);
        }
    }

    private static void WithHeader(bool destinations, double width, Action<Control, Window> check, Avalonia.Styling.ThemeVariant? theme = null, double height = 600)
    {
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
            cfg.Backups.Add(new Backup { Name = "documents" });
            cfg.Destinations.Add(new Destination { Name = "Backup drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"D:\Backups" });
        });
        Control view = destinations
            ? new DestinationsView { DataContext = new DestinationsViewModel() }
            : new BackupsView { DataContext = new BackupsViewModel() };
        var window = new Window { Width = width, Height = height, Content = view, RequestedThemeVariant = theme ?? Avalonia.Styling.ThemeVariant.Light };
        window.Show();
        window.UpdateLayout(); window.UpdateLayout();
        try { check(view, window); }
        finally
        {
            window.Close();
            (view.DataContext as IDisposable)?.Dispose();
            ServiceLocator.Config.Update(cfg => { cfg.Backups.Clear(); cfg.Destinations.Clear(); });
        }
    }

    /// <summary>
    /// The notice's detail ("Last activity: never") shares the headline's
    /// baseline: both are runs of one text line, so they cannot drift apart
    /// when their sizes differ.
    /// </summary>
    [AvaloniaFact]
    public void Notice_detail_shares_the_headline_baseline()
    {
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Backups.Add(new Backup { Name = "documents" });
        });
        var vm = new BackupsViewModel();
        var view = new BackupsView { DataContext = vm };
        var window = new Window { Width = 1180, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout(); window.UpdateLayout();
        try
        {
            var notice = view.FindControl<Border>("HealthNotice")!;
            Assert.True(notice.IsEffectivelyVisible);
            Assert.False(string.IsNullOrEmpty(vm.HealthHeadline));
            Assert.False(string.IsNullOrEmpty(vm.HealthDetail));

            var text = notice.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Inlines?.OfType<Run>().Any(r => r.Text == vm.HealthHeadline) == true);
            var runs = text.Inlines!.OfType<Run>().ToList();
            Assert.Contains(runs, r => r.Text == KeepPhrasesTogether.Apply(vm.HealthDetail));
            // Negative: the detail is not a separate block that could sit on
            // its own baseline.
            Assert.DoesNotContain(notice.GetVisualDescendants().OfType<TextBlock>(), t => t != text && (t.Text ?? "").Replace(' ', ' ') == vm.HealthDetail);
            Assert.Single(text.TextLayout.TextLines);
        }
        finally
        {
            window.Close();
            vm.Dispose();
            ServiceLocator.Config.Update(cfg => cfg.Backups.Clear());
        }
    }

    /// <summary>
    /// A narrow notice never splits "Last activity: 21h ago" (U038 follow-up,
    /// 30 October 2026): the phrase stays beside the headline when it fits and
    /// otherwise moves whole to a second line set close under the first. The
    /// headline is a status label, so it has no full stop.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1180, 1)]
    [InlineData(600, 2)]
    [InlineData(540, 2)]
    [InlineData(500, 2)]
    public void Notice_keeps_last_activity_whole(double width, int expectedLines)
    {
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Backups.Add(new Backup { Name = "zulu-music", LastRunStatus = BackupRunStatus.Skipped,
                LastRunStartUtc = DateTime.UtcNow.AddHours(-21), LastRunEndUtc = DateTime.UtcNow.AddHours(-21) });
            cfg.Backups.Add(new Backup { Name = "documents", LastRunStatus = BackupRunStatus.Success,
                LastRunStartUtc = DateTime.UtcNow.AddHours(-22), LastRunEndUtc = DateTime.UtcNow.AddHours(-22) });
        });
        var vm = new BackupsViewModel();
        var view = new BackupsView { DataContext = vm };
        var window = new Window { Width = width, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout(); window.UpdateLayout();
        try
        {
            Assert.Equal("1 backup was skipped on its last run", vm.HealthHeadline);
            Assert.StartsWith("Last activity: 21h ago", vm.HealthDetail);
            var notice = view.FindControl<Border>("HealthNotice")!;
            var text = notice.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Inlines?.OfType<Run>().Any(r => r.Text == vm.HealthHeadline) == true);
            var full = string.Concat(text.Inlines!.OfType<Run>().Select(r => r.Text));
            var phrase = full.IndexOf("Last activity:", StringComparison.Ordinal);
            Assert.True(phrase > 0, full);
            var lines = text.TextLayout.TextLines;
            int LineOf(int index) => lines.ToList().FindIndex(l => index >= l.FirstTextSourceIndex && index < l.FirstTextSourceIndex + l.Length);
            var end = phrase + "Last activity: 21h ago".Length - 1;
            _out.WriteLine($"width {width}: text {text.Bounds.Width:0} px, {lines.Count} line(s); phrase on line {LineOf(phrase)}..{LineOf(end)}");
            Assert.Equal(expectedLines, lines.Count);
            Assert.Equal(LineOf(phrase), LineOf(end));
            if (expectedLines == 2)
            {
                // The headline is never split; the phrase starts the second line.
                Assert.Equal(0, LineOf(full.IndexOf("run", StringComparison.Ordinal)));
                Assert.Equal(1, LineOf(phrase));
                Assert.True(lines[1].Height <= 22, $"Second line is {lines[1].Height:0.0} px tall.");
            }
        }
        finally
        {
            window.Close();
            vm.Dispose();
            ServiceLocator.Config.Update(cfg => cfg.Backups.Clear());
        }
    }

    /// <summary>A window rendered to memory at a multiple of its size.</summary>
    private sealed class Pixels
    {
        private readonly byte[] _bgra;
        private readonly int _stride, _scale, _width, _height;

        private Pixels(byte[] bgra, int width, int height, int scale)
        { _bgra = bgra; _width = width; _height = height; _stride = width * 4; _scale = scale; }

        internal static Pixels Render(Window window, int scale)
        {
            var size = new PixelSize((int)window.Bounds.Width * scale, (int)window.Bounds.Height * scale);
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            var length = size.Width * 4 * size.Height;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                bitmap.CopyPixels(new PixelRect(size), buffer, length, size.Width * 4);
                var bytes = new byte[length];
                Marshal.Copy(buffer, bytes, 0, length);
                return new Pixels(bytes, size.Width, size.Height, scale);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        /// <summary>Bounds, in window units, of the ink inside a window-unit region.</summary>
        internal Rect Ink(Rect region, Func<byte, byte, byte, bool> isInk)
        {
            int x0 = Math.Max(0, (int)(region.Left * _scale)), x1 = Math.Min(_width, (int)Math.Ceiling(region.Right * _scale));
            int y0 = Math.Max(0, (int)(region.Top * _scale)), y1 = Math.Min(_height, (int)Math.Ceiling(region.Bottom * _scale));
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var i = y * _stride + x * 4;
                    if (!isInk(_bgra[i + 2], _bgra[i + 1], _bgra[i])) continue;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            Assert.True(maxX >= 0, $"Nothing was drawn in {region}.");
            return new Rect((double)minX / _scale, (double)minY / _scale, (double)(maxX - minX + 1) / _scale, (double)(maxY - minY + 1) / _scale);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Backspace")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static string[] IconKeys()
    {
        var file = Path.Combine(RepoRoot(), "src", "Backspace", "Themes", "Icons.axaml");
        return Regex.Matches(File.ReadAllText(file), "x:Key=\"(Icon\\.[^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
    }
}
