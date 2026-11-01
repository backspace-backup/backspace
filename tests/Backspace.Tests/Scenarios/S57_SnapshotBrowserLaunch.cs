using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S57_SnapshotBrowserLaunch : ScenarioBase
{
    protected override string ScenarioName => "S57-snapshot-browser-launch";
    [AvaloniaFact] public Task Run_S57() => Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        var destination = new Destination { Name = "Local browser fixture", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("browser") };
        var backup = new Backup { Name = "Documents browser proof", LastRunStatus = BackupRunStatus.Success };
        backup.SourcePaths.Add(ctx.MakeSource("browser-source", 62, 4096));
        backup.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "archive" });
        ServiceLocator.Config.Update(c => { c.Destinations.Add(destination); c.Backups.Add(backup); });
        await using var sidecar = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary,
            System.IO.Path.Combine(ctx.RootDir, "owned-browser-daemon"));
        var property = typeof(ServiceLocator).GetProperty(nameof(ServiceLocator.SnapshotBrowser), BindingFlags.Public | BindingFlags.Static)!;
        var priorLauncher = ServiceLocator.SnapshotBrowser;
        var priorObserver = ServiceLocator.Notifier.NotificationObserved;
        var urls = new ConcurrentQueue<string>();
        var failures = new ConcurrentQueue<string>();
        try
        {
            property.SetValue(null, new SnapshotBrowserLauncher(sidecar, urls.Enqueue));
            ServiceLocator.Notifier.NotificationObserved = (title, detail, severity, _, _) =>
            { if (severity == NotificationSeverity.Failure) failures.Enqueue(title + ": " + detail); };
            using var vm = new BackupsViewModel();
            var window = ctx.Driver.Show(new Window { Width = 1100, Height = 760,
                Content = new BackupsView { DataContext = vm } });
            var card = window.GetVisualDescendants().OfType<BackupCard>().Single();
            var cardVm = (BackupCardViewModel)card.DataContext!;
            var button = card.FindControl<Button>("MoreActionsButton")!;

            Assert.False(sidecar.IsRunning);
            ClickBrowse();
            await Until(() => !cardVm.BrowseSnapshotsCommand.IsRunning && !urls.IsEmpty);
            Assert.True(sidecar.IsRunning);
            var url = Assert.Single(urls);
            Assert.Equal(sidecar.WebUiUrl.TrimEnd('/') + "/#/plan/" + Uri.EscapeDataString(
                ResticBackrestEngine.MakePlanId(backup, backup.SourcePaths[0], destination, "archive")), url);
            Assert.Equal("127.0.0.1", new Uri(url).Host);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(sidecar.WebUiUrl);
            response.EnsureSuccessStatusCode();
            Assert.Contains("<html", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(failures);
            ctx.Snapshot(window, "browse-button-launches-local-plan-url",
                "The card's More menu and Browse snapshots action started an exclusively owned real Backrest daemon and passed its local plan URL to an observed browser opener. No operating-system browser was opened.");

            property.SetValue(null, new SnapshotBrowserLauncher(sidecar, _ => throw new InvalidOperationException("fixture browser launch refused")));
            ClickBrowse();
            await Until(() => !cardVm.BrowseSnapshotsCommand.IsRunning && !failures.IsEmpty);
            Assert.Single(urls);
            Assert.Contains(failures, message => message.Contains("Couldn't open the snapshot browser")
                && message.Contains("fixture browser launch refused"));
            Assert.True(sidecar.IsRunning); // A shell launch failure must not kill the engine.
            var invalid = await ServiceLocator.SnapshotBrowser.OpenForAsync(null!);
            Assert.False(invalid.Succeeded);
            ctx.Report.RecordNote("Actual operation and negative control",
                "Pointer clicks invoke the real card command and launcher. A cold owned daemon starts, its HTTP UI responds successfully, and the opener receives the exact local plan URL. An opener exception produces the actual failure notification without killing the daemon. No shell/browser process is launched; snapshot navigation inside Backrest remains separate browser-level proof.");

            void ClickBrowse()
            {
                button.BringIntoView(); ctx.Driver.Pump(); window.UpdateLayout();
                var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left); ctx.Driver.Pump();
                var menu = Assert.IsType<MenuFlyout>(button.Flyout);
                Assert.True(menu.IsOpen);
                var browse = Assert.Single(menu.Items.OfType<MenuItem>(), i => i.Command == cardVm.BrowseSnapshotsCommand);
                browse.BringIntoView(); ctx.Driver.Pump();
                var popup = TopLevel.GetTopLevel(browse)!;
                popup.UpdateLayout();
                var itemAt = browse.TranslatePoint(new Point(browse.Bounds.Width / 2, browse.Bounds.Height / 2), popup)!.Value;
                popup.MouseDown(itemAt, MouseButton.Left); popup.MouseUp(itemAt, MouseButton.Left); ctx.Driver.Pump();
            }
            async Task Until(Func<bool> condition)
            {
                var stop = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < stop)
                { ctx.Driver.Pump(); if (condition()) return; await Task.Delay(20); }
                throw new TimeoutException("Browse command did not complete: " + string.Join("; ", failures));
            }
        }
        finally
        {
            property.SetValue(null, priorLauncher);
            ServiceLocator.Notifier.NotificationObserved = priorObserver;
            await sidecar.StopAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }
}
