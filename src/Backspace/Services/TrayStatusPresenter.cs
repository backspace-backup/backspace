using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>Static status badge; no idle animation or polling timer.</summary>
internal sealed class TrayStatusPresenter
{
    private readonly Dictionary<string, WindowIcon> _icons = new();
    internal static RenderTargetBitmap RenderIcon(AccentColor accent, ScheduleReadiness state, bool busy)
    {
        var output = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        using var stream = AssetLoader.Open(new Uri($"avares://Backspace/Assets/Backspace-{accent}.ico"));
        using var source = new Bitmap(stream);
        using (var dc = output.CreateDrawingContext())
        {
            dc.DrawImage(source, new Rect(0, 0, 64, 64));
            var color = busy ? "#2563EB" : state switch {
                ScheduleReadiness.Ready => "#15803D", ScheduleReadiness.Attention => "#B45309",
                ScheduleReadiness.Paused => "#B45309", _ => "#64748B" };
            dc.DrawEllipse(new SolidColorBrush(Color.Parse(color)), new Pen(Brushes.White, 3), new Point(49,49), 13,13);
            var pen = new Pen(Brushes.White, 3);
            if (busy) { dc.DrawLine(pen,new Point(44,49),new Point(54,49)); dc.DrawLine(pen,new Point(50,45),new Point(54,49)); dc.DrawLine(pen,new Point(50,53),new Point(54,49)); }
            else if (state == ScheduleReadiness.Ready) { dc.DrawLine(pen,new Point(42,49),new Point(47,54)); dc.DrawLine(pen,new Point(47,54),new Point(56,44)); }
            else if (state == ScheduleReadiness.Paused) { dc.DrawLine(pen,new Point(45,43),new Point(45,55)); dc.DrawLine(pen,new Point(53,43),new Point(53,55)); }
            else if (state == ScheduleReadiness.Attention) { dc.DrawLine(pen,new Point(49,41),new Point(49,50)); dc.DrawEllipse(Brushes.White,null,new Point(49,55),1.6,1.6); }
            else dc.DrawLine(pen,new Point(43,49),new Point(55,49));
        }
        return output;
    }

    internal void Apply(TrayIcon icon, TrayScheduleStatus status, AccentColor accent, bool busy)
    {
        var key = $"{accent}/{status.State}/{busy}";
        if (!_icons.TryGetValue(key, out var image))
        {
            using var bitmap = RenderIcon(accent,status.State,busy);
            image = new WindowIcon(bitmap); _icons.Add(key,image);
        }
        icon.Icon = image;
        icon.ToolTipText = "Backspace · " + (busy ? "Work in progress · " : "") + status.Title;
        if (icon.Menu?.Items[0] is NativeMenuItem item) item.Header = status.Title;
        if (icon.Menu?.Items.Count > 1 && icon.Menu.Items[1] is NativeMenuItem detail)
            detail.Header = status.Detail;
    }
}
