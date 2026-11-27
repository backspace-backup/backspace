using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S61_TrayScheduleStates : ScenarioBase
{
    private sealed class ReadyScheduler : Backspace.Services.Platform.IScheduler
    {
        public void UpsertTask(Backup b, string p) { }
        public void DeleteTask(Backup b) { }
        public string TaskNameFor(Backup b) => b.Name;
        public ScheduleCheck InspectBackup(Backup b, string p) => new(ScheduleReadiness.Ready, "");
    }

    protected override string ScenarioName => "S61-tray-schedule-states";
    [AvaloniaFact] public async Task Run_S61() => await Run();
    protected override Task RunAsync(ScenarioContext ctx)
    {
        var panel = new StackPanel { Spacing=18,Margin=new Thickness(28) };
        panel.Children.Add(new TextBlock { Text="Tray schedule status",FontSize=26 });
        // U260: a backup running shows the syncing mark (two arrows round a circle).
        foreach(var (state,title,busy) in new[]{(ScheduleReadiness.Ready,"Backups up to date",false),(ScheduleReadiness.Attention,"Backing up (the last backup had failed)",true),(ScheduleReadiness.Paused,"Automatic backups paused",false),(ScheduleReadiness.Off,"Automatic backups off",false),(ScheduleReadiness.Attention,"local_c: last backup failed",false),(ScheduleReadiness.Unknown,"Schedules not verified",false)})
        {
            using var bitmap=TrayStatusPresenter.RenderIcon(AccentColor.Ocean,state,busy);
            // Show the actual pixels used by the native tray, plus a readable
            // enlargement. The platform menu itself is not rendered headlessly.
            var memory=new System.IO.MemoryStream();bitmap.Save(memory);memory.Position=0;
            var image=new Avalonia.Media.Imaging.Bitmap(memory);
            var row=new StackPanel { Orientation=Orientation.Horizontal,Spacing=18 };
            row.Children.Add(new Image { Source=image,Width=20,Height=20 });
            row.Children.Add(new Image { Source=image,Width=48,Height=48 });
            row.Children.Add(new TextBlock { Text=title,FontSize=18,VerticalAlignment=VerticalAlignment.Center });panel.Children.Add(row);
            using var icon=new TrayIcon { Menu=new NativeMenu() };icon.Menu.Items.Add(new NativeMenuItem());
            new TrayStatusPresenter().Apply(icon,new(state,title,""),AccentColor.Ocean,busy);
            Assert.Contains(title,icon.ToolTipText);Assert.Equal(title,((NativeMenuItem)icon.Menu.Items[0]).Header);
        }
        // U241: sharp at every display scale. The artwork is the 256 px frame,
        // and the icon holds each tray size drawn at its own size.
        using (var art = TrayStatusPresenter.LargestFrame(AccentColor.Ocean)) Assert.Equal(256, art.PixelSize.Width);
        var file = TrayStatusPresenter.RenderIconFile(AccentColor.Ocean, ScheduleReadiness.Ready, false);
        Assert.Equal(TrayStatusPresenter.Sizes.Length, BitConverter.ToUInt16(file, 4));
        for (var i = 0; i < TrayStatusPresenter.Sizes.Length; i++) Assert.Equal(TrayStatusPresenter.Sizes[i], file[6 + i * 16]);
        var sizes = new StackPanel { Orientation=Orientation.Horizontal,Spacing=14 };
        foreach (var size in TrayStatusPresenter.Sizes)
        {
            using var native = TrayStatusPresenter.RenderIcon(AccentColor.Ocean, ScheduleReadiness.Ready, false, size);
            Assert.Equal(size, native.PixelSize.Width);
            var m = new System.IO.MemoryStream(); native.Save(m); m.Position = 0;
            var img = new Image { Source = new Avalonia.Media.Imaging.Bitmap(m), Width = size, Height = size, VerticalAlignment = VerticalAlignment.Bottom };
            Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
            sizes.Children.Add(img);
        }
        panel.Children.Add(new TextBlock { Text="Each size Windows uses (100 % to 400 % display scale), drawn at its own size:",FontSize=15 });
        panel.Children.Add(sizes);
        var syncing = new StackPanel { Orientation=Orientation.Horizontal,Spacing=14 };
        foreach (var size in TrayStatusPresenter.Sizes)
        {
            using var native = TrayStatusPresenter.RenderIcon(AccentColor.Ocean, ScheduleReadiness.Ready, true, size);
            var m = new System.IO.MemoryStream(); native.Save(m); m.Position = 0;
            var img = new Image { Source = new Avalonia.Media.Imaging.Bitmap(m), Width = size, Height = size, VerticalAlignment = VerticalAlignment.Bottom };
            Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
            syncing.Children.Add(img);
        }
        panel.Children.Add(new TextBlock { Text="Backing up, at each size:",FontSize=15 });
        panel.Children.Add(syncing);
        panel.Children.Add(new TextBlock { Text="Windows tasks are checked. A ready schedule does not guarantee\nthat a drive, network or cloud account will be available later.",FontSize=15 });
        var window=ctx.Driver.Show(new Window { Width=720,Height=820,Content=panel });ctx.Driver.Pump();
        ctx.Snapshot(window,"actual-tray-status-icons","Production tray icons at 20 pixels and enlarged, with the exact menu labels. Ready, paused, off, attention and unverified stay distinct.");
        // U258: the menu's top, built from real backups as the tray builds it,
        // drawn here line by line with the same icons (the native menu itself
        // cannot be drawn headlessly).
        var now = DateTime.UtcNow;
        Backup Make(string name, BackupRunStatus status, double hoursAgo, string summary) => new()
        {
            Name = name, SourcePaths = { "C:\\" + name }, Targets = { new BackupTarget() }, LastRunStatus = status, Schedule = TestSchedules.DueInAnHour(),
            LastRunEndUtc = now.AddHours(-hoursAgo), LastSuccessUtc = status == BackupRunStatus.Success ? now.AddHours(-hoursAgo) : now.AddDays(-2), LastRunSummary = summary,
        };
        var status = TrayScheduleStatus.Inspect([
            Make("local_c", BackupRunStatus.Success, 3, "1.2 GB uploaded · 120 new / 40 mod / 0 rem · in 9m 12s"),
            Make("local_d", BackupRunStatus.Success, 0.5, "293 B uploaded · 0 new / 0 mod / 0 rem · in 12s"),
            Make("local_dropbox", BackupRunStatus.Skipped, 2, @"M:\ not mounted · skipped after 1s"),
            Make("photos", BackupRunStatus.Failed, 26, "Google Drive sign-in expired. Sign in again in the destination's settings."),
        ], new ReadyScheduler(), "app.exe", now);
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem("Open Backspace"));
        TrayStatusPresenter.Fill(menu, status, () => { });
        Assert.Equal("2 backups need attention", ((NativeMenuItem)menu.Items[0]).Header);
        Assert.Equal("photos  ·  Google Drive sign-in expired · 1d ago", ((NativeMenuItem)menu.Items[1]).Header);
        Assert.NotNull(((NativeMenuItem)menu.Items[1]).Icon);
        Assert.IsType<NativeMenuItemSeparator>(menu.Items[5]);
        var preview = new StackPanel { Spacing = 6, Margin = new Thickness(12), Background = Avalonia.Media.Brushes.White };
        preview.Children.Add(new TextBlock { Text = status.Title, FontSize = 14 });
        foreach (var line in status.Lines)
        {
            var glyph = TrayStatusPresenter.Glyph(line.Tone);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new Image { Source = glyph, Width = 16, Height = 16 });
            row.Children.Add(new TextBlock { Text = line.Name + "  ·  " + line.Text, FontSize = 14 });
            preview.Children.Add(row);
        }
        preview.Children.Add(new Border { Height = 1, Background = Avalonia.Media.Brushes.LightGray });
        preview.Children.Add(new TextBlock { Text = "Open Backspace", FontSize = 14 });
        preview.Children.Add(new TextBlock { Text = "Quit Backspace", FontSize = 14 });
        var menuWindow = ctx.Driver.Show(new Window { Width = 560, Height = 260, Content = preview });
        ctx.Driver.Pump();
        ctx.Snapshot(menuWindow, "tray-menu-lines", "The tray menu's lines as built from four backups (drawn here; the native menu can't be captured headlessly): the headline as on the Backups tab, then each backup with its card's icon and Last backup line, the ones needing attention first.");
        ctx.Report.RecordNote("Scope","Real icon renderer and TrayIcon/menu property updates; actual Windows shell placement and menu opening are separate native checks.");
        return Task.CompletedTask;
    }
}
