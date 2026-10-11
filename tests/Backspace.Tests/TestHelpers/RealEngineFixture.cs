using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// Test-scoped helper that puts the active <see cref="IBackupEngine"/>
/// on the real <see cref="ResticBackrestEngine"/> instead of the
/// in-memory test double. Used by the E2E scenarios that need to
/// exercise real cloud or local-restic flows.
///
/// Preconditions: backrest, restic, and rclone executables must be
/// on disk at <see cref="AppPaths.BackrestBinary"/> /
/// <see cref="AppPaths.ResticBinary"/> / <see cref="AppPaths.RcloneBinary"/>.
/// CI runs default to <c>-p:SkipBackrestDownload=true</c> so the
/// fixture skips cleanly when the binaries aren't present — point
/// the suite at a checkout that built without that flag and the
/// real-engine scenarios light up automatically.
///
/// The fixture is intentionally short-lived: each scenario creates
/// its own instance, runs, disposes. The sidecar process gets killed
/// on dispose so leftover backrest.exe instances don't accumulate
/// during a test run.
/// </summary>
public sealed class RealEngineFixture : IAsyncDisposable
{
    private readonly ITestOutputHelper _log;
    private readonly Func<SecretsStore, IBackupEngine>? _previousOverride;
    private readonly IBackupEngine _previousActiveEngine;
    private readonly BackrestSidecar _sidecar;
    private readonly ResticBackrestEngine _engine;
    private bool _disposed;

    private RealEngineFixture(ITestOutputHelper log, BackrestSidecar sidecar, ResticBackrestEngine engine,
        Func<SecretsStore, IBackupEngine>? previousOverride, IBackupEngine previousActiveEngine)
    {
        _log = log;
        _sidecar = sidecar;
        _engine = engine;
        _previousOverride = previousOverride;
        _previousActiveEngine = previousActiveEngine;
    }

    /// <summary>The active engine while this fixture is alive. Equal
    /// to <see cref="ServiceLocator.Engine"/>; held as a typed
    /// property for scenarios that want to call engine-specific
    /// helpers (e.g. for direct cleanup).</summary>
    public ResticBackrestEngine Engine => _engine;
    public BackrestSidecar Sidecar => _sidecar;

    /// <summary>True iff every required binary is on disk. Tests
    /// short-circuit with a Skipped log line when this is false.</summary>
    public static bool BinariesPresent =>
        File.Exists(AppPaths.BackrestBinary) &&
        File.Exists(AppPaths.ResticBinary) &&
        File.Exists(AppPaths.RcloneBinary);

    /// <summary>Setup-failure message tests log when binaries are
    /// missing. Names which binaries are missing so the operator knows
    /// what to fix without checking three paths by hand.</summary>
    public static string MissingBinariesReason()
    {
        var missing = new System.Collections.Generic.List<string>();
        if (!File.Exists(AppPaths.BackrestBinary)) missing.Add("backrest");
        if (!File.Exists(AppPaths.ResticBinary))   missing.Add("restic");
        if (!File.Exists(AppPaths.RcloneBinary))   missing.Add("rclone");
        return missing.Count == 0
            ? "All required binaries present."
            : $"Skipped: missing engine binaries ({string.Join(", ", missing)}). " +
              $"Run `dotnet build -p:SkipBackrestDownload=false` once so FetchBackrest.targets " +
              $"populates {Path.GetDirectoryName(AppPaths.BackrestBinary)}.";
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

        // Fresh sidecar per fixture so the on-disk data dir doesn't
        // bleed state between scenarios. Lives under TEMP; cleaned
        // up on dispose.
        var dataDir = Path.Combine(Path.GetTempPath(),
            "backspace-real-engine-" + Guid.NewGuid().ToString("N"));
        var sidecar = new BackrestSidecar(
            AppPaths.BackrestBinary, AppPaths.ResticBinary, dataDir);
        var engine = new ResticBackrestEngine(sidecar, ServiceLocator.Secrets);

        await sidecar.StartAsync(ct).ConfigureAwait(false);

        // Reach into the locator, swap the engine pointer, and
        // rebuild every service that captured the old engine
        // reference at construction. Without the rebuild the
        // orchestrator / restore engine / revision browser / storage
        // cleaner / verifier keep the OLD engine for life — the
        // scenario suite was silently running every "real engine"
        // test against the InMemory fake because of this. No env-var
        // gate: scenarios MUST exercise the real Backrest path or
        // they aren't earning their keep.
        SwapActiveEngine(engine);
        ServiceLocator.RebuildEngineDependents();

        log.WriteLine($"RealEngineFixture: sidecar listening on 127.0.0.1:{sidecar.Port}");
        return new RealEngineFixture(log, sidecar, engine, previousOverride, previousActiveEngine);
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

        // If BACKSPACE_E2E_KEEP_DATA=1, also copy the data dir
        // contents into the scenario's artifacts folder before
        // disposing the sidecar — so a developer chasing a real-
        // engine failure has access to Backrest's per-op logs
        // (tasklogs/*.log), config.json, sqlite oplog, etc.
        if (string.Equals(Environment.GetEnvironmentVariable("BACKSPACE_E2E_KEEP_DATA"), "1", StringComparison.Ordinal))
        {
            try { _log.WriteLine($"RealEngineFixture: BACKSPACE_E2E_KEEP_DATA=1 — preserving {_sidecar.DataDir}"); }
            catch { }
        }
        try { await _sidecar.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.WriteLine($"RealEngineFixture: sidecar stop threw: {ex.Message}"); }
        if (string.Equals(Environment.GetEnvironmentVariable("BACKSPACE_E2E_KEEP_DATA"), "1", StringComparison.Ordinal))
        {
            return; // skip the data-dir cleanup so the developer can inspect it
        }
        // AV scanners or restic's own brief-held handles can defeat a
        // single delete attempt. Retry briefly so we don't accumulate
        // orphan temp dirs in %TEMP% across hundreds of test runs.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_sidecar.DataDir))
                    Directory.Delete(_sidecar.DataDir, recursive: true);
                break;
            }
            catch when (attempt < 2)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.WriteLine($"RealEngineFixture: data-dir cleanup gave up after 3 attempts: {ex.Message}");
            }
        }
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
