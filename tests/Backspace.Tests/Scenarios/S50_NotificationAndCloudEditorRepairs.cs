using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
using Backspace.Services.OAuth;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S50_ExactFailureNotification : ScenarioBase
{
    protected override string ScenarioName => "S50-exact-failure-notification";
    [AvaloniaFact] public async Task Run_S50() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var backup = new Backup { Name = "Family archive — notification proof" };
        ServiceLocator.Config.Update(c =>
        {
            c.Backups.Add(backup);
            c.Notifications.OnFailure = new NotificationChannels { Toast = true, Sound = false, Email = false };
        });
        var failedLog = Path.Combine(ctx.RootDir, "failed-run.log");
        File.WriteAllText(failedLog, "FAILED RUN — earlier backup\nAccess denied to the archive. This is the run described by the alert.\n");
        var failure = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Status = BackupRunStatus.Failed,
            StartedUtc = DateTime.UtcNow.AddHours(-2), EndedUtc = DateTime.UtcNow.AddHours(-2).AddMinutes(1),
            Summary = "Earlier backup could not reach archive", LogPath = failedLog,
            Errors = { "[C:\\private-source\\" + new string('x', 400) + "] failed to get snapshots: command restic snapshots failed. Output: {\"message\":\"Fatal: wrong password or no key found\"}" } };
        var success = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Status = BackupRunStatus.Success,
            StartedUtc = DateTime.UtcNow.AddHours(-1), EndedUtc = DateTime.UtcNow.AddHours(-1).AddMinutes(1), Summary = "Newer backup completed" };
        ServiceLocator.Logs.AppendRun(failure); ServiceLocator.Logs.AppendRun(success);
        using var vm = new MainWindowViewModel();
        var window = ctx.Driver.Show(new MainWindow { DataContext = vm });
        var clicked = new TaskCompletionSource<(string title, string detail, Action click)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notify = new NotificationService(ServiceLocator.Config)
        {
            NotificationObserved = (title, detail, severity, click, _) => { if (severity == NotificationSeverity.Failure && click is not null) clicked.TrySetResult((title, detail, click)); }
        };
        var orchestrator = new BackupOrchestrator(ServiceLocator.Config, ServiceLocator.Secrets, ServiceLocator.Engine,
            ServiceLocator.Logs, ServiceLocator.Healthcheck, ServiceLocator.Mail, notify, ServiceLocator.Progress);
        await orchestrator.SendAlertsAsync(backup, failure);
        var alert = await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("could not unlock this destination", alert.detail);
        Assert.Contains("backup password in Destinations", alert.detail);
        Assert.DoesNotContain("private-source", alert.detail);
        Assert.DoesNotContain("command restic", alert.detail);
        var popup = NotificationService.BuildAlertWindow(alert.title, alert.detail,
            NotificationSeverity.Failure, false, null, alert.click)!;
        ctx.Driver.Show(popup);
        await ctx.Driver.PumpAsync(150);
        AssertCompactAlert(popup);
        ctx.Snapshot(popup, "failure-popup-before-click", "The production popup names the failed backup and offers an explicit Open run details button.");
        Screenshots.ScreenshotDiffTests.SettleInitialTransitions(popup);
        var open = popup.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Open run details");
        var point = open.TranslatePoint(new Point(open.Bounds.Width / 2, open.Bounds.Height / 2), popup)!.Value;
        popup.MouseDown(point, MouseButton.Left); popup.MouseUp(point, MouseButton.Left);
        await ctx.Driver.PumpAsync(150);
        Assert.False(popup.IsVisible);
        Assert.Equal(NavItem.Logs, vm.SelectedNav);
        Assert.Equal(failure.Id, vm.Logs.SelectedRun?.Id);
        Assert.Equal(2, vm.Logs.History.Count);
        Assert.Contains("earlier backup", vm.Logs.DisplayText);
        ctx.Snapshot(window, "alert-selects-earlier-failed-run", "The actual failure-alert callback opens Activity & logs and selects the earlier failure, although a newer successful run exists.");
        vm.Logs.SelectRunById(backup.Id, "no-longer-in-history");
        Assert.Null(vm.Logs.SelectedRun); // Never substitute a different run.
        ServiceLocator.Config.Update(c =>
        {
            c.Backups.Single(b => b.Id == backup.Id).SkipNotificationGraceDays = 0;
            c.Notifications.OnSkipped = new NotificationChannels { Toast = true, Sound = false, Email = false };
        });
        var warned = new TaskCompletionSource<(string title, string detail, Action? click)>(TaskCreationOptions.RunContinuationsAsynchronously);
        notify.NotificationObserved = (title, detail, severity, click, _) =>
        {
            Assert.Equal(NotificationSeverity.Warning, severity);
            warned.TrySetResult((title, detail, click));
        };
        await orchestrator.SendAlertsAsync(backup, new RunRecord { BackupId = backup.Id, BackupName = backup.Name,
            Status = BackupRunStatus.Skipped, Summary = "Archive drive is disconnected. No new backup was made.", StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow });
        var warning = await warned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("skipped", warning.title); Assert.DoesNotContain("FAILED", warning.title);
        var warningPopup = NotificationService.BuildAlertWindow(warning.title, warning.detail, NotificationSeverity.Warning, false, null, warning.click)!;
        ctx.Driver.Show(warningPopup); await ctx.Driver.PumpAsync(100);
        AssertCompactAlert(warningPopup);
        ctx.Snapshot(warningPopup, "skipped-warning-is-not-a-failure", "The same notification path renders a skipped run in amber with the word skipped, rather than the red FAILED notification. The test disables the grace period so the warning is due.");
        warningPopup.Close();
        var navigatedOnDismiss = false;
        var longPopup = NotificationService.BuildAlertWindow("A backup needs your attention",
            string.Join("\n", Enumerable.Repeat("A long diagnostic message must stay readable in a compact popup.", 40)),
            NotificationSeverity.Failure, true, null, () => navigatedOnDismiss = true)!;
        ctx.Driver.Show(longPopup); await ctx.Driver.PumpAsync(100);
        Assert.InRange(longPopup.Bounds.Width, 350, 421);
        Assert.InRange(longPopup.Bounds.Height, 200, 481);
        var scroll = longPopup.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        Assert.True(ctx.Driver.ClickButton(longPopup, "✕"));
        Assert.False(longPopup.IsVisible);
        Assert.False(navigatedOnDismiss); // Closing must never open a run.
        ctx.Report.RecordNote("scope of proof", "Clicked the real popup's Open run details button using the callback from SendAlertsAsync. The earlier failed run is selected despite a newer success; a missing run never selects another one. Backspace uses its own Avalonia popup. Headless rendering does not prove Windows popup placement or focus behavior.");
        Backspace.App.QuitRequested = true; window.Close(); Backspace.App.QuitRequested = false;
    }

    private static void AssertCompactAlert(Window popup)
    {
        Assert.InRange(popup.Bounds.Width, 350, 421);
        Assert.InRange(popup.Bounds.Height, 100, 320);
        foreach (var text in popup.GetVisualDescendants().OfType<TextBlock>().Where(t => t.TextLayout is not null))
            Assert.True(text.TextLayout!.Height <= text.Bounds.Height + 1, $"Clipped alert text: {text.Text}");
    }
}

[Collection(ScenarioCollection.Name)]
public sealed class S51_OneDriveEditorLoopback : ScenarioBase
{
    protected override string ScenarioName => "S51-onedrive-editor-loopback";
    [AvaloniaFact] public async Task Run_S51() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        ServiceLocator.Config.Update(c =>
        {
            c.RcloneOAuthApps.RemoveAll(a => a.Provider is DestinationKind.OneDrivePersonal or DestinationKind.OneDriveBusiness);
            c.RcloneOAuthApps.Add(new RcloneOAuthApp { Provider = DestinationKind.OneDrivePersonal, ClientId = "fixture-client-id" });
        });
        var requestCount = 0;
        var discoveredDriveType = "personal";
        using var fakeHttp = new HttpClient(new FixtureHandler(async (request, ct) =>
        {
            Interlocked.Increment(ref requestCount);
            if (request.RequestUri!.Host == "graph.microsoft.com")
            {
                Assert.Equal("fixture-access", request.Headers.Authorization?.Parameter);
                Assert.Equal("/v1.0/me/drive/special/approot", request.RequestUri.AbsolutePath);
                return Json("{\"id\":\"fixture-app-folder\",\"folder\":{},\"parentReference\":{\"driveId\":\"fixture-drive-id\",\"driveType\":\"" + discoveredDriveType + "\"}}");
            }
            var form = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("code_verifier=", form); Assert.Contains("code=fixture-code", form);
            return Json("{\"access_token\":\"fixture-access\",\"refresh_token\":\"fixture-refresh\",\"token_type\":\"Bearer\",\"expires_in\":3600,\"scope\":\"Files.ReadWrite.AppFolder offline_access\"}");
        }));
        using var loopback = new HttpClient();
        Task<HttpResponseMessage>? callbackRequest = null;
        var denied = false;
        var coordinator = new OAuthFlowCoordinator(fakeHttp, url =>
        {
            var query = new Uri(url).Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal("Files.ReadWrite.AppFolder offline_access", query["scope"]);
            Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
            var response = denied ? "error=access_denied" : "code=fixture-code";
            callbackRequest = loopback.GetAsync(query["redirect_uri"] + "?" + response + "&state=" + Uri.EscapeDataString(query["state"]));
        });
        using var vm = new DestinationEditorViewModel(new Destination { Name = "OneDrive fixture", Kind = DestinationKind.OneDrivePersonal }, true)
        {
            OAuthFlowOverride = coordinator, OneDriveDiscoveryOverride = new OneDriveDiscovery(fakeHttp)
        };
        Assert.Contains(DestinationKind.OneDrivePersonal, vm.AvailableKinds);
        Assert.DoesNotContain(DestinationKind.OneDriveBusiness, vm.AvailableKinds);
        var window = ctx.Driver.Show(new DestinationEditorWindow { DataContext = vm, Width = 900, Height = 850 });
        Assert.True(ctx.Driver.ClickButton(window, "Connect to OneDrive"));
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while ((!vm.AuthOk || vm.AuthBusy) && DateTime.UtcNow < deadline) await ctx.Driver.PumpAsync(50);
        Assert.True(vm.AuthOk, vm.AuthStatus); Assert.False(vm.AuthBusy);
        Assert.Equal("fixture-drive-id", vm.Working.OneDriveDriveId); Assert.Equal("personal", vm.Working.OneDriveDriveType);
        Assert.Equal("fixture-app-folder", vm.Working.OneDriveAppFolderId);
        Assert.True(vm.Working.CloudAppFolderOnly);
        Assert.Equal(2, requestCount);
        if (callbackRequest is not null) using (await callbackRequest) { }
        ctx.Snapshot(window, "onedrive-connected-through-editor", "The editor's Connect button completed a real local callback, PKCE token exchange and drive discovery using simulated provider responses. Connected status is visible.");
        vm.OAuthTokenInput = "";
        Assert.Null(vm.Working.OneDriveDriveId); Assert.Null(vm.Working.OneDriveDriveType);
        Assert.Null(vm.Working.OneDriveAppFolderId); Assert.False(vm.Working.CloudAppFolderOnly);
        denied = true;
        Assert.True(ctx.Driver.ClickButton(window, "Connect to OneDrive"));
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.AuthBusy && DateTime.UtcNow < deadline) await ctx.Driver.PumpAsync(50);
        Assert.False(vm.AuthOk); Assert.False(vm.AuthBusy); Assert.Contains("refused", vm.AuthStatus);
        Assert.Equal(2, requestCount); // Denied consent never exchanges a token.
        if (callbackRequest is not null) using (await callbackRequest) { }
        ctx.Snapshot(window, "onedrive-denied-consent-is-visible", "Denying consent clears the busy state and displays a sign-in refusal; it never claims the account connected.");
        denied = false;
        discoveredDriveType = "business";
        Assert.True(ctx.Driver.ClickButton(window, "Connect to OneDrive"));
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.AuthBusy && DateTime.UtcNow < deadline) await ctx.Driver.PumpAsync(50);
        Assert.False(vm.AuthOk); Assert.False(vm.AuthBusy);
        Assert.Contains("personal Microsoft account", vm.AuthStatus);
        Assert.Null(vm.Working.OneDriveDriveId);
        Assert.False(vm.Working.CloudAppFolderOnly);
        Assert.Equal(4, requestCount);
        if (callbackRequest is not null) using (await callbackRequest) { }
        ctx.Snapshot(window, "work-account-is-not-supported", "A work or school account cannot be presented as a supported Personal connection. No drive details are saved and the editor asks for a personal account.");
        ctx.Report.RecordNote("scope of proof", "The actual DestinationEditor button command, local callback listener, state/PKCE flow, token exchange, automatic validation and drive discovery run together. External provider HTTP responses are fake; no real Microsoft account or registry client id is validated. The browser-launch seam sends the callback locally instead of launching a real consent page.");
        window.Close();

        using var legacy = new DestinationEditorViewModel(new Destination
        {
            Name = "Earlier OneDrive connection", Kind = DestinationKind.OneDrivePersonal,
            OAuthTokenRef = "existing-account-token", PathOrSubpath = "existing-backups",
            LastTokenValidatedUtc = DateTime.UtcNow, OneDriveDriveId = "earlier-drive", OneDriveDriveType = "personal"
        }, false) { OAuthFlowOverride = coordinator, OneDriveDiscoveryOverride = new OneDriveDiscovery(fakeHttp) };
        var legacyWindow = ctx.Driver.Show(new DestinationEditorWindow { DataContext = legacy, Width = 900, Height = 850 });
        await ctx.Driver.PumpAsync(100);
        Assert.True(legacy.IsAuthError);
        Assert.Contains("Create a new destination", legacy.AuthStatus);
        Assert.True(ctx.Driver.ClickButton(legacyWindow, "Connect to OneDrive"));
        await ctx.Driver.PumpAsync(100);
        Assert.False(legacy.AuthOk);
        Assert.False(legacy.Working.CloudAppFolderOnly);
        Assert.Equal("existing-backups", legacy.Working.PathOrSubpath);
        Assert.Equal("earlier-drive", legacy.Working.OneDriveDriveId);
        Assert.Equal("existing-account-token", legacy.Working.OAuthTokenRef);
        Assert.Equal(4, requestCount); // Older repositories never reconnect at a different root.
        ctx.Snapshot(legacyWindow, "older-cloud-connection-needs-new-destination", "An older saved connection shows a clear warning immediately. Clicking Connect leaves its account and repository unchanged; it cannot silently switch to the new app folder.");
        legacyWindow.Close();
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
