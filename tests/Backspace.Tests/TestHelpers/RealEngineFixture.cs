using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// Test-scoped helper that puts the active <see cref="IBackupEngine"/>
/// on the real <see cref="ResticEngine"/> instead of the
/// in-memory test double. Used by the E2E scenarios that need to
/// exercise real cloud or local-restic flows.
///
/// Preconditions: restic and rclone executables must be on disk at
/// <see cref="AppPaths.ResticBinary"/> / <see cref="AppPaths.RcloneBinary"/>.
/// CI runs default to <c>-p:SkipEngineDownload=true</c> so the
/// fixture skips cleanly when the binaries aren't present — point
/// the suite at a checkout that built without that flag and the
/// real-engine scenarios light up automatically.
///
/// The fixture is intentionally short-lived: each scenario creates
/// its own instance, runs, disposes.
/// </summary>
public sealed class RealEngineFixture : IAsyncDisposable
{
    private readonly ITestOutputHelper _log;
    private readonly Func<SecretsStore, IBackupEngine>? _previousOverride;
    private readonly IBackupEngine _previousActiveEngine;
    private readonly ResticEngine _engine;
    private bool _disposed;

    private RealEngineFixture(ITestOutputHelper log, ResticEngine engine,
        Func<SecretsStore, IBackupEngine>? previousOverride, IBackupEngine previousActiveEngine)
    {
        _log = log;
        _engine = engine;
        _previousOverride = previousOverride;
        _previousActiveEngine = previousActiveEngine;
    }

    /// <summary>The active engine while this fixture is alive. Equal
    /// to <see cref="ServiceLocator.Engine"/>; held as a typed
    /// property for scenarios that want to call engine-specific
    /// helpers (e.g. for direct cleanup).</summary>
    public ResticEngine Engine => _engine;

    /// <summary>True iff every required binary is on disk. Tests
    /// short-circuit with a Skipped log line when this is false.</summary>
    public static bool BinariesPresent =>
        File.Exists(AppPaths.ResticBinary) &&
        File.Exists(AppPaths.RcloneBinary);

    /// <summary>Setup-failure message tests log when binaries are
    /// missing. Names which binaries are missing so the operator knows
    /// what to fix without checking three paths by hand.</summary>
    public static string MissingBinariesReason()
    {
        var missing = new System.Collections.Generic.List<string>();
        if (!File.Exists(AppPaths.ResticBinary))   missing.Add("restic");
        if (!File.Exists(AppPaths.RcloneBinary))   missing.Add("rclone");
        return missing.Count == 0
            ? "All required binaries present."
            : $"Skipped: missing engine binaries ({string.Join(", ", missing)}). " +
              $"Run `dotnet build -p:SkipEngineDownload=false` once so FetchEngines.targets " +
              $"populates {Path.GetDirectoryName(AppPaths.ResticBinary)}.";
    }

    /// <summary>Build the fixture if binaries are present; returns
    /// null otherwise. Caller convention:
    /// <code>
    ///   await using var fixture = await RealEngineFixture.TryCreateAsync(_out);
    ///   if (fixture is null) return; // skipped, reason already logged
    /// </code></summary>
    public static async Task<RealEngineFixture?> TryCreateAsync(ITestOutputHelper log, CancellationToken ct = default)
    {
        if (!BinariesPresent)
        {
            log.WriteLine(MissingBinariesReason());
            return null;
        }

        // Save current state so we can restore on dispose. The
        // ServiceLocator is process-wide; if we swap the engine
        // without restoring, every subsequent test in this xUnit
        // collection runs against the real engine too.
        var previousOverride = ServiceLocator.EngineOverride;
        var previousActiveEngine = ServiceLocator.Engine;

        // The engine runs the bundled restic directly (U175).
        var engine = new ResticEngine(ServiceLocator.Secrets);

        // Reach into the locator, swap the engine pointer, and
        // rebuild every service that captured the old engine
        // reference at construction. Without the rebuild the
        // orchestrator / restore engine / revision browser / storage
        // cleaner / verifier keep the OLD engine for life — the
        // scenario suite was silently running every "real engine"
        // test against the InMemory fake because of this. No env-var
        // gate: scenarios MUST exercise the real restic path or
        // they aren't earning their keep.
        SwapActiveEngine(engine);
        ServiceLocator.RebuildEngineDependents();

        log.WriteLine("RealEngineFixture: the engine runs restic directly.");
        return new RealEngineFixture(log, engine, previousOverride, previousActiveEngine);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Restore the previous engine pointer + rebuild dependents
        // so the next scenario doesn't inherit this fixture's
        // orchestrator still pointing at the soon-to-be-killed
        // sidecar.
        SwapActiveEngine(_previousActiveEngine);
        ServiceLocator.RebuildEngineDependents();
        ServiceLocator.EngineOverride = _previousOverride;
    }

    /// <summary>Reflectively swap the ServiceLocator's Engine
    /// property setter. Cleaner than re-running InitializeCore which
    /// rebuilds every service.</summary>
    private static void SwapActiveEngine(IBackupEngine engine)
    {
        var prop = typeof(ServiceLocator).GetProperty(
            nameof(ServiceLocator.Engine),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        prop?.SetMethod?.Invoke(null, new object[] { engine });
    }
}
