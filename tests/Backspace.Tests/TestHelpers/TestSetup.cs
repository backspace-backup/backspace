using System;
using System.IO;
using System.Runtime.CompilerServices;
using Backspace.Services;
using Xunit;

// Tests mutate process-wide state (the BACKSPACE_CONFIG_ROOT env var,
// ServiceLocator, Avalonia's dispatcher). If xUnit ran collections in
// parallel, one TempWorkspace's setenv would clobber another's and
// subsequent Process.Start calls would use the wrong config dir.
// Disable parallelization at the assembly level so every test runs serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// Process-wide bootstrap that runs before anything else in this assembly,
/// including xUnit's discovery. Sets up an isolated config root and spins
/// up <see cref="ServiceLocator"/> so plain [Fact] tests that don't touch
/// Avalonia can still use Config, SecretsStore, Orchestrator, etc. without
/// waiting for the headless app to come up.
///
/// [ModuleInitializer] was added in C# 9 and executes exactly once per
/// loaded assembly. We use it here because static constructors on test
/// classes aren't reliable for setup that must happen before the xUnit
/// test-discovery pass.
/// </summary>
internal static class TestSetup
{
    private static string? _suiteRoot;

    [ModuleInitializer]
    internal static void Init()
    {
        // Drain the cell-retry backoff to a few hundred milliseconds
        // for the whole suite. Production scenarios benefit from the
        // 0/5/20/60s schedule (transient network blip recovery); the
        // E2E suite hits real Backrest and a failing scenario would
        // otherwise burn ~85s per cell × N cells before surfacing.
        Environment.SetEnvironmentVariable("BACKSPACE_FAST_RETRIES", "1");

        // Make window.Close() actually CLOSE in tests. The defaults
        // (ShowTrayIcon + MinimizeToTrayOnClose both true) route
        // MainWindow.Close() into the hide-to-tray branch, which
        // CANCELS the close: OnClosed never runs, the DataContext VM
        // never disposes, and every "closed" shell keeps its
        // Config.Changed / AppStatus.Changed / Engine.LineWritten
        // subscriptions plus a per-window icon-animation timer. Over
        // a full 812-test run those leaked shells made every config
        // mutation and engine line slower until click/lookup tests
        // started blowing their timeouts (the "~70s cliff").
        Backspace.App.QuitRequested = true;

        // No live GitHub release checks mid-suite: every MainWindow
        // construction otherwise fires a real HTTPS GET (and possibly
        // a toast window) at unpredictable points in the run.
        Environment.SetEnvironmentVariable("BACKSPACE_NO_UPDATE_CHECK", "1");

        var existing = Environment.GetEnvironmentVariable("BACKSPACE_CONFIG_ROOT");
        if (string.IsNullOrWhiteSpace(existing))
        {
            _suiteRoot = Path.Combine(Path.GetTempPath(),
                $"backspace-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_suiteRoot);
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", _suiteRoot);

            // Without this, every test run on a dev box leaks the
            // suite-level temp dir (config + extracted the engine binary
            // ~12 MB each). Hooked on ProcessExit so even a Ctrl-C
            // tries to clean up; best-effort because the runtime can
            // give us only a couple of seconds in that handler.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteSuiteRoot();
        }

        // Tests use an InMemoryBackupEngine so they don't need a real
        // backrest.exe on disk. Set the factory before InitializeCore
        // so the locator picks it up; production callers can't see
        // this property (internal + InternalsVisibleTo).
        ServiceLocator.EngineOverride ??= _ => new InMemoryBackupEngine();
        // Schedule calls are recorded, so the suite never depends on the
        // developer machine's background service or tasks.
        ServiceLocator.SchedulerOverride ??= () => new RecordingScheduler();
        // Never add a real start-at-sign-in entry to the machine running the suite.
        ServiceLocator.SignInStartOverride ??= () => RecordingSignInStart.Shared;
        // The test runner's network cost must not cancel unrelated backup/UI
        // scenarios. RuntimeGuardCoverageTests inject actual metered events
        // through the per-instance probe, which takes precedence over this.
        MeteredNetworkWatchdog.PlatformProbeOverride = (_, _) => System.Threading.Tasks.Task.CompletedTask;

        // Idempotent — fine to call again when the Avalonia test app boots.
        ServiceLocator.InitializeCore();
    }

    private static void TryDeleteSuiteRoot()
    {
        if (string.IsNullOrEmpty(_suiteRoot)) return;
        try
        {
            if (Directory.Exists(_suiteRoot)) Directory.Delete(_suiteRoot, recursive: true);
        }
        catch { /* best-effort — locked files, AV, etc. */ }
    }
}
