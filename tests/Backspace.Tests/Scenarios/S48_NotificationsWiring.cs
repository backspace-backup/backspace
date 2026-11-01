using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Notifications wiring: covers the matrix rows "Toast on Success /
/// Skipped / Failure" and "Sound alert on Failure".
///
/// The test surface is the public <see cref="NotificationService.NotifyAsync"/>
/// contract — we don't try to capture an actual Windows toast (the
/// headless test lifetime has no desktop UI; the service explicitly
/// falls back to "log only" when <c>Application.Current</c> isn't a
/// classic desktop lifetime). What we DO assert:
///   1. Calling NotifyAsync with each severity completes without
///      throwing under the user's configured channels — proves
///      the wiring is alive for every per-event matrix combination
///      (3 severities × 3 channels).
///   2. The matching NotificationSettings round-trip through
///      ConfigStore.Update preserves channel state — covered in
///      detail by <see cref="S45_SettingsTogglePersistence"/>.
///
/// The actual UI emission (the tray popup window for Success/Skipped,
/// the topmost sticky alert for Failure, and the
/// <c>WindowsSystemSounds.PlayFor</c> call for Sound=true) is exercised
/// by humans via the live app — by design, since the headless
/// lifetime can't construct an Avalonia <c>Window</c> safely.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S48_NotificationsWiring : ScenarioBase
{
    protected override string ScenarioName => "S48-notifications-wiring";

    [AvaloniaFact] public async Task Run_S48() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var svc = new NotificationService(ServiceLocator.Config);

        // -------- Every (severity × channel-config) combo dispatches cleanly --------
        var configs = new[]
        {
            new NotificationChannels { Toast = true,  Sound = true,  Email = false },
            new NotificationChannels { Toast = true,  Sound = false, Email = false },
            new NotificationChannels { Toast = false, Sound = true,  Email = false },
            new NotificationChannels { Toast = false, Sound = false, Email = false },
        };

        foreach (var ch in configs)
        foreach (var severity in new[] { NotificationSeverity.Success, NotificationSeverity.Warning, NotificationSeverity.Failure })
        {
            // No exception = the dispatch path is alive for this
            // combination. Anything that survives Toast=true Sound=true
            // on the headless lifetime proves the wiring routes
            // gracefully through `IsDesktopUi() == false` instead of
            // throwing on the Window construction.
            await svc.NotifyAsync(
                title: $"Test {severity}",
                detail: $"Channels: Toast={ch.Toast} Sound={ch.Sound} Email={ch.Email}",
                severity: severity,
                onClick: null,
                dismissAfter: null,
                channelsOverride: ch);
        }

        // -------- Failure severity uses the topmost-alert path when Toast=on --------
        // The dispatch is fire-and-forget on the dispatcher; just
        // assert it completes without throwing. The actual sticky
        // window is asserted-by-eye.
        await svc.NotifyAsync(
            "Backup failed",
            "Test failure for the topmost-alert wiring path.",
            NotificationSeverity.Failure,
            onClick: null,
            dismissAfter: null,
            channelsOverride: new NotificationChannels { Toast = true, Sound = true, Email = false });

        ctx.Report.RecordNote(
            "notification dispatch wiring",
            "All 4 channel-config × 3 severity combinations (12 dispatches) plus a topmost-failure " +
            "alert completed without throwing under the headless lifetime. The actual UI " +
            "emission (tray popup, topmost sticky alert, system sound) is exercised by hand " +
            "in the live app because the headless lifetime has no desktop UI to host them.");
    }
}
