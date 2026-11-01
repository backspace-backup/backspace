using System;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Backspace;

// Register the Avalonia test application Avalonia.Headless.XUnit will spin up.
// One process-wide app keeps the Dispatcher alive across tests; parallelism
// is disabled in the .csproj so tests share it without fighting.
[assembly: AvaloniaTestApplication(typeof(Backspace.Tests.TestHelpers.HeadlessApp))]

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// We reuse the real <see cref="App"/> class directly rather than
/// subclassing — that keeps App.axaml's resources loaded and skips the
/// MainWindow creation automatically. Why no MainWindow? Because the real
/// App only creates one under an <see cref="Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime"/>,
/// and Avalonia.Headless installs NO application lifetime at all
/// (verified against Avalonia.Headless 12.1.3; there is consequently
/// also no desktop.Windows list to enumerate for cleanup) — the
/// conditional inside App.OnFrameworkInitializationCompleted
/// short-circuits, so only <see cref="Backspace.Services.ServiceLocator.InitializeCore"/>
/// and resource setup run.
/// </summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                // Real Skia rasterizer so screenshot tests can produce
                // PNGs. The Avalonia.Skia package is a dependency; calling
                // .UseSkia() AFTER .UseHeadless() keeps the initialization
                // order Headless wants (windowing first, drawing second).
                UseHeadlessDrawing = false,
            })
            .UseSkia();

        // Per-test isolation clears Avalonia's UI dispatcher, and the next
        // thread to read Dispatcher.UIThread becomes the UI thread. A
        // background continuation left by an earlier test can win that race
        // during setup, so building the compositor fails with "a different
        // thread owns it". Claim the dispatcher for the session thread at the
        // first and last setup steps before the compositor is created.
        var runtime = builder.RuntimePlatformServicesInitializer!;
        var windowing = builder.WindowingSubsystemInitializer!;
        return builder
            .UseRuntimePlatformSubsystem(() => { ClaimUiThread(); runtime(); }, builder.RuntimePlatformServicesName ?? "")
            .UseWindowingSubsystem(() => { ClaimUiThread(); windowing(); }, builder.WindowingSubsystemName!);
    }

    // Avalonia.Headless calls this same internal reset before every isolated test.
    private static readonly Action ResetDispatcher = typeof(Dispatcher)
        .GetMethod("ResetBeforeUnitTests", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Action>();

    internal static void ClaimUiThread()
    {
        for (var attempt = 0; attempt < 5 && !Dispatcher.UIThread.CheckAccess(); attempt++)
            ResetDispatcher();
        Dispatcher.UIThread.VerifyAccess();
    }
}
