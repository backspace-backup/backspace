using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S72_BackgroundNotifications : ScenarioBase
{
    protected override string ScenarioName => "S72-background-notifications";
    [AvaloniaFact] public async Task Run_S72() => await Run();
    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var backup = new Backup { Name = "Documents" };
        ServiceLocator.Config.Update(c => c.Backups.Add(backup));
        var failed = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Status = BackupRunStatus.Failed,
            StartedUtc = DateTime.UtcNow.AddHours(-2), EndedUtc = DateTime.UtcNow.AddHours(-2).AddSeconds(5),
            Summary = "Backup drive was disconnected before completion." };
        var newer = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Status = BackupRunStatus.Success,
            StartedUtc = DateTime.UtcNow.AddHours(-1), EndedUtc = DateTime.UtcNow.AddHours(-1).AddSeconds(5), Summary = "Later backup completed." };
        ServiceLocator.Logs.AppendRun(failed); ServiceLocator.Logs.AppendRun(newer);
        using var vm = new MainWindowViewModel();
        var window = ctx.Driver.Show(new MainWindow { DataContext = vm });
        var queue = new ServiceNotificationQueue(System.IO.Path.Combine(AppPaths.ConfigRoot, "synthetic-service-notifications"));
        queue.Add(new(Guid.NewGuid().ToString("N"), DateTime.UtcNow, "Backspace: «Documents» failed",
            "Backup drive was disconnected. Open this run to see what happened.", NotificationSeverity.Failure,
            true, false, backup.Id, failed.Id));
        var protocol = new ServiceNotificationProtocol(queue); Window? popup = null; var visible = false;
        using var consumer = new BackgroundServiceNotifications((request, _) => Task.FromResult(
            JsonSerializer.Deserialize<ServiceNotificationProtocol.Response>(protocol.Dispatch(
                JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json)), ServiceRunAuthorization.Json)!),
            (notice, _) =>
            {
                popup = NotificationService.BuildAlertWindow(notice.Title, notice.Detail, notice.Severity, false, null,
                    () => BackgroundServiceNotifications.Open(notice))!;
                ctx.Driver.Show(popup);
                return Task.CompletedTask;
            }, () => visible);
        Assert.False(await consumer.DeliverOneAsync(default)); Assert.Null(popup);
        visible = true; Assert.True(await consumer.DeliverOneAsync(default));
        await ctx.Driver.PumpAsync(100); Assert.True(popup!.IsVisible);
        ctx.Snapshot(popup, "pending-alert-after-sign-in", "A retained service alert names Documents and offers Open run details. The queue withheld it while the desktop was unavailable.");
        Screenshots.ScreenshotDiffTests.SettleInitialTransitions(popup);
        var button = popup.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Open run details");
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), popup)!.Value;
        popup.MouseDown(point, MouseButton.Left); popup.MouseUp(point, MouseButton.Left);
        await ctx.Driver.PumpAsync(100);
        Assert.False(popup.IsVisible); Assert.Equal(NavItem.Logs, vm.SelectedNav);
        Assert.Equal(failed.Id, vm.Logs.SelectedRun?.Id); Assert.NotEqual(newer.Id, vm.Logs.SelectedRun?.Id);
        Assert.False(await consumer.DeliverOneAsync(default));
        ctx.Snapshot(window, "pending-alert-opens-exact-run", "Open run details selects the earlier failed run, even though a more recent backup succeeded. The acknowledged alert is not presented again.");
        ctx.Report.RecordNote("Limits", "Real queue, consumer and production popup/navigation callback in headless Avalonia. Desktop availability is controlled; actual Windows lock/unlock, logon-task launch and popup positioning require native VM acceptance.");
    }
}
