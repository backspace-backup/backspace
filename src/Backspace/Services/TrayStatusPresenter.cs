using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// The tray icon with its status badge; no idle animation or polling timer.
/// Sharp at every display scale (U241, owner: "ensure we're using retina
/// quality icons"): each size Windows may ask for (16 px at 100 % up to 64 px
/// at 400 %) is drawn on its own from the 256 px artwork, and handed over as
/// one multi-size icon, so Windows never enlarges or squeezes a single image.
/// </summary>
internal sealed class TrayStatusPresenter
{
    /// <summary>The sizes in the icon: the tray's 16 px at 100 %, 125 %,
    /// 150 %, 175 %, 200 %, 250 %, 300 % and 400 %.</summary>
    internal static readonly int[] Sizes = [16, 20, 24, 28, 32, 40, 48, 64];

    private readonly Dictionary<string, WindowIcon> _icons = new();

    /// <summary>The badge drawn at <paramref name="size"/> pixels square.</summary>
    internal static RenderTargetBitmap RenderIcon(AccentColor accent, ScheduleReadiness state, bool busy, int size = 64)
    {
        var output = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using var source = LargestFrame(accent);
        using (var dc = output.CreateDrawingContext())
        {
            using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                dc.DrawImage(source, new Rect(0, 0, size, size));
            // Drawn on a 64-unit grid, scaled to the size.
            using var scale = dc.PushTransform(Matrix.CreateScale(size / 64.0, size / 64.0));
            var color = busy ? "#2563EB" : state switch {
                ScheduleReadiness.Ready => "#15803D", ScheduleReadiness.Attention => "#B45309",
                ScheduleReadiness.Paused => "#B45309", _ => "#64748B" };
            // A larger badge (U260, owner 26 November 2026: "we can't see the
            // indicator clearly"): drawn on the same 13-unit design, enlarged
            // about its centre and moved into the corner, over most of a
            // quarter of the icon, with a thicker white ring and marks.
            using var badge = dc.PushTransform(Matrix.CreateTranslation(-49, -49) * Matrix.CreateScale(1.45, 1.45) * Matrix.CreateTranslation(45, 45));
            dc.DrawEllipse(new SolidColorBrush(Color.Parse(color)), new Pen(Brushes.White, 2.6), new Point(49,49), 13,13);
            var pen = new Pen(Brushes.White, 3.4, lineCap: PenLineCap.Round);
            if (busy) { dc.DrawLine(pen,new Point(44,49),new Point(54,49)); dc.DrawLine(pen,new Point(50,45),new Point(54,49)); dc.DrawLine(pen,new Point(50,53),new Point(54,49)); }
            else if (state == ScheduleReadiness.Ready) { dc.DrawLine(pen,new Point(42,49),new Point(47,54)); dc.DrawLine(pen,new Point(47,54),new Point(56,44)); }
            else if (state == ScheduleReadiness.Paused) { dc.DrawLine(pen,new Point(45,43),new Point(45,55)); dc.DrawLine(pen,new Point(53,43),new Point(53,55)); }
            else if (state == ScheduleReadiness.Attention) { dc.DrawLine(pen,new Point(49,41),new Point(49,50)); dc.DrawEllipse(Brushes.White,null,new Point(49,55),1.6,1.6); }
            else dc.DrawLine(pen,new Point(43,49),new Point(55,49));
        }
        return output;
    }

    /// <summary>The accent's artwork at its largest (256 px), whatever frame a
    /// decoder would pick from the .ico by itself.</summary>
    internal static Bitmap LargestFrame(AccentColor accent)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Backspace/Assets/Backspace-{accent}.ico"));
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var ico = memory.ToArray();
        var count = BitConverter.ToUInt16(ico, 4);
        int best = -1, bestSize = 0;
        for (var i = 0; i < count; i++)
        {
            var at = 6 + i * 16;
            var width = ico[at] == 0 ? 256 : ico[at];
            if (width > bestSize) { bestSize = width; best = at; }
        }
        if (best < 0) { memory.Position = 0; return new Bitmap(memory); }
        var length = BitConverter.ToInt32(ico, best + 8);
        var offset = BitConverter.ToInt32(ico, best + 12);
        return new Bitmap(new MemoryStream(ico, offset, length, writable: false));
    }

    /// <summary>One .ico holding every size, each drawn at its own size (PNG frames).</summary>
    internal static byte[] RenderIconFile(AccentColor accent, ScheduleReadiness state, bool busy)
    {
        var frames = new List<byte[]>();
        foreach (var size in Sizes)
        {
            using var bitmap = RenderIcon(accent, state, busy, size);
            using var png = new MemoryStream();
            bitmap.Save(png);
            frames.Add(png.ToArray());
        }
        using var ico = new MemoryStream();
        using var w = new BinaryWriter(ico);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)frames.Count);
        var offset = 6 + 16 * frames.Count;
        for (var i = 0; i < frames.Count; i++)
        {
            w.Write((byte)Sizes[i]); w.Write((byte)Sizes[i]); w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32); w.Write(frames[i].Length); w.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var frame in frames) w.Write(frame);
        w.Flush();
        return ico.ToArray();
    }

    internal void Apply(TrayIcon icon, TrayScheduleStatus status, AccentColor accent, bool busy, Action? open = null)
    {
        var key = $"{accent}/{status.State}/{busy}";
        if (!_icons.TryGetValue(key, out var image))
        {
            image = new WindowIcon(new MemoryStream(RenderIconFile(accent, status.State, busy)));
            _icons.Add(key, image);
        }
        icon.Icon = image;
        icon.ToolTipText = "Backspace · " + (busy ? "Work in progress · " : "") + status.Title;
        if (icon.Menu is { } menu) Fill(menu, status, open);
    }

    /// <summary>The top of the menu (U258): the headline, as on the Backups
    /// tab, then each backup with its card's icon and Last backup line; a
    /// click opens Backspace. Everything above "Open Backspace" is redone.</summary>
    internal static void Fill(NativeMenu menu, TrayScheduleStatus status, Action? open)
    {
        var keep = 0;
        for (var i = 0; i < menu.Items.Count; i++)
            if (menu.Items[i] is NativeMenuItem { Header: "Open Backspace" }) { keep = i; break; }
            else keep = menu.Items.Count;
        for (var i = keep - 1; i >= 0; i--) menu.Items.RemoveAt(i);
        var at = 0;
        NativeMenuItem Item(string header, string? tone)
        {
            var item = new NativeMenuItem(header);
            if (tone is not null) item.Icon = Glyph(tone);
            if (open is not null) item.Click += (_, _) => open();
            else item.IsEnabled = false;
            return item;
        }
        menu.Items.Insert(at++, Item(status.Title, null));
        foreach (var line in status.Lines)
            menu.Items.Insert(at++, Item(Fit(line.Name + "  ·  " + line.Text), line.Tone));
        if (status.MoreBackups > 0)
            menu.Items.Insert(at++, Item(status.MoreBackups == 1 ? "1 more backup" : $"{status.MoreBackups} more backups", null));
        if (at < menu.Items.Count) menu.Items.Insert(at, new NativeMenuItemSeparator());
    }

    private static string Fit(string text) => text.Length > 80 ? text[..79].TrimEnd() + "…" : text;

    private static readonly Dictionary<string, Bitmap> Glyphs = new();

    /// <summary>The card's status icon in its colour, for a menu line: a
    /// disc with a check, an exclamation mark, a cross, a skip or a dash.</summary>
    internal static Bitmap Glyph(string tone)
    {
        if (Glyphs.TryGetValue(tone, out var cached)) return cached;
        const int size = 32;
        var output = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var dc = output.CreateDrawingContext())
        {
            var color = tone switch { "ok" => "#15803D", "warn" or "skipped" => "#B45309", "fail" => "#DC2626", _ => "#94A3B8" };
            dc.DrawEllipse(new SolidColorBrush(Color.Parse(color)), null, new Point(16, 16), 14, 14);
            var pen = new Pen(Brushes.White, 3.2, lineCap: PenLineCap.Round);
            switch (tone)
            {
                case "ok": dc.DrawLine(pen, new Point(9.5, 16.5), new Point(14, 21)); dc.DrawLine(pen, new Point(14, 21), new Point(22.5, 11.5)); break;
                case "warn": dc.DrawLine(pen, new Point(16, 8.5), new Point(16, 17.5)); dc.DrawEllipse(Brushes.White, null, new Point(16, 23), 2, 2); break;
                case "fail": dc.DrawLine(pen, new Point(11, 11), new Point(21, 21)); dc.DrawLine(pen, new Point(21, 11), new Point(11, 21)); break;
                case "skipped": dc.DrawLine(pen, new Point(11, 10), new Point(18, 16)); dc.DrawLine(pen, new Point(18, 16), new Point(11, 22)); dc.DrawLine(pen, new Point(22, 10), new Point(22, 22)); break;
                default: dc.DrawLine(pen, new Point(10, 16), new Point(22, 16)); break;
            }
        }
        Glyphs[tone] = output;
        return output;
    }
}
