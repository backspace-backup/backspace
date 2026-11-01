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
    protected override string ScenarioName => "S61-tray-schedule-states";
    [AvaloniaFact] public async Task Run_S61() => await Run();
    protected override Task RunAsync(ScenarioContext ctx)
    {
        var panel = new StackPanel { Spacing=18,Margin=new Thickness(28) };
        panel.Children.Add(new TextBlock { Text="Tray schedule status",FontSize=26 });
        foreach(var (state,title) in new[]{(ScheduleReadiness.Ready,"Schedules ready"),(ScheduleReadiness.Paused,"Schedules paused"),(ScheduleReadiness.Off,"Schedules off"),(ScheduleReadiness.Attention,"Schedules need attention"),(ScheduleReadiness.Unknown,"Schedules not verified")})
        {
            using var bitmap=TrayStatusPresenter.RenderIcon(AccentColor.Ocean,state,false);
            // Show the actual pixels used by the native tray, plus a readable
            // enlargement. The platform menu itself is not rendered headlessly.
            var memory=new System.IO.MemoryStream();bitmap.Save(memory);memory.Position=0;
            var image=new Avalonia.Media.Imaging.Bitmap(memory);
            var row=new StackPanel { Orientation=Orientation.Horizontal,Spacing=18 };
            row.Children.Add(new Image { Source=image,Width=20,Height=20 });
            row.Children.Add(new Image { Source=image,Width=48,Height=48 });
            row.Children.Add(new TextBlock { Text=title,FontSize=18,VerticalAlignment=VerticalAlignment.Center });panel.Children.Add(row);
            using var icon=new TrayIcon { Menu=new NativeMenu() };icon.Menu.Items.Add(new NativeMenuItem());
            new TrayStatusPresenter().Apply(icon,new(state,title,""),AccentColor.Ocean,false);
            Assert.Contains(title,icon.ToolTipText);Assert.Equal(title,((NativeMenuItem)icon.Menu.Items[0]).Header);
        }
        panel.Children.Add(new TextBlock { Text="Windows tasks are checked. A ready schedule does not guarantee\nthat a drive, network or cloud account will be available later.",FontSize=15 });
        var window=ctx.Driver.Show(new Window { Width=720,Height=480,Content=panel });ctx.Driver.Pump();
        ctx.Snapshot(window,"actual-tray-status-icons","Production tray icons at 20 pixels and enlarged, with the exact menu labels. Ready, paused, off, attention and unverified stay distinct.");
        ctx.Report.RecordNote("Scope","Real icon renderer and TrayIcon/menu property updates; actual Windows shell placement and menu opening are separate native checks.");
        return Task.CompletedTask;
    }
}
