using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Backspace.Services;
using Backspace.Tests.TestHelpers;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Per-scenario lifecycle wrapper. Owns the throwaway directories,
/// the report, and the UiDriver. Used by every scenario class via
/// <see cref="ScenarioBase.RunAsync"/>; the base class instantiates
/// it, hands it to the concrete scenario, and disposes it after.
///
/// Working layout (under the operating system's temporary directory):
///   backspace-scenarios/&lt;name&gt;-&lt;unique id&gt;/
///     sources/        — synthetic dummy file trees (one subdir per source)
///     destinations/   — local-folder destinations the scenario uses
///     restored/       — restore targets so a scenario can compare hashes
/// Screenshots normally remain under tests/Backspace.Tests/artifacts/scenarios/&lt;name&gt;/screenshots/.
/// BACKSPACE_SCENARIO_ARTIFACTS_ROOT can redirect all scenario artifacts to an isolated folder.
///
/// Each scenario gets a fresh unique dir on every run. Source content is regenerated deterministically from a
/// seed so the same scenario produces byte-identical fixtures across runs
/// — handy for "did this change my output?" diffs.
/// </summary>
public sealed class ScenarioContext : IDisposable
{
    public string Name { get; }
    public string RootDir { get; }
    public string SourcesDir { get; }
    public string DestinationsDir { get; }
    public string RestoredDir { get; }
    public string ScreenshotsDir { get; }
    public ScenarioReport Report { get; }
    public SecretsLoader Secrets { get; }
    public UiDriver Driver { get; }

    private int _stepCounter;
    private bool _disposed;

    public ScenarioContext(string name, SecretsLoader secrets)
    {
        Name = name;
        Secrets = secrets;

        var artifactsRoot = ResolveArtifactsRoot();
        // Keep mutable fixtures outside synced checkouts. Sync clients can
        // change attributes between backup and validation (including Archive).
        // A unique directory also keeps concurrent test runs isolated.
        RootDir         = Path.Combine(Path.GetTempPath(), "backspace-scenarios", name + "-" + Guid.NewGuid().ToString("N"));
        SourcesDir      = Path.Combine(RootDir, "sources");
        DestinationsDir = Path.Combine(RootDir, "destinations");
        RestoredDir     = Path.Combine(RootDir, "restored");
        ScreenshotsDir  = Path.Combine(artifactsRoot, name, "screenshots");

        // Each mutable workspace is unique; screenshots are overwritten by step.
        Directory.CreateDirectory(SourcesDir);
        Directory.CreateDirectory(DestinationsDir);
        Directory.CreateDirectory(RestoredDir);
        Directory.CreateDirectory(ScreenshotsDir);

        Report = new ScenarioReport(name, ScreenshotsDir);
        ScenarioRunSummary.Register(Report);
        if (ServiceLocator.ActiveRestores.HasRunning)
            throw new InvalidOperationException("The preceding scenario left a restore running. Stop and await it before starting a new scenario.");
        // Reset the in-process config so prior scenarios' Backups /
        // Destinations don't leak in. Every scenario starts from a
        // clean slate; the BACKSPACE_CONFIG_ROOT env var still keeps
        // the JSON itself isolated from the user's real install.
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });

        Driver = new UiDriver(this);
    }

    /// <summary>Dismiss saved results before constructing the next scenario's UI.
    /// Dismissal may write a journal on a worker thread; starting it without
    /// awaiting it can leave another scenario's card in the new window.</summary>
    internal Task PrepareAsync() => RestoreTestIsolation.ClearCompletedAsync(() => Driver.Pump());

    /// <summary>
    /// Materialise a synthetic source tree under a named subfolder of
    /// <see cref="SourcesDir"/>. Deterministic from <paramref name="seed"/>
    /// so two runs of the same scenario produce identical content.
    /// Defaults to ~2 MB so the suite stays fast.
    /// </summary>
    public string MakeSource(string name, int seed = 1, long targetBytes = 2_000_000)
    {
        var path = Path.Combine(SourcesDir, name);
        Directory.CreateDirectory(path);
        if (FixtureSizeResolver.ResolveSize(FixtureSize.Smoke) == FixtureSize.Huge)
        {
            FixtureFactory.GenerateScaleSource(path, seed);
            Report.RecordNote("Production-scale fixture", "20,000 files and 2 GiB generated outside the synced checkout: " + path);
        }
        else SyntheticFileTree.Generate(path, seed: seed, targetBytes: targetBytes);
        return path;
    }

    /// <summary>Allocate a named local-folder destination directory.</summary>
    public string MakeLocalDestination(string name)
    {
        var path = Path.Combine(DestinationsDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Allocate a named restore-target directory.</summary>
    public string MakeRestoreTarget(string name)
    {
        var path = Path.Combine(RestoredDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Render the given window to PNG and record the step in the
    /// scenario report. <paramref name="label"/> is used both as the
    /// filename suffix and the step row in SUMMARY.md.
    /// </summary>
    public string Snapshot(Window window, string label, string? note = null)
    {
        _stepCounter++;
        var safe = SafeFilename(label);
        var fileName = $"{_stepCounter:D2}-{safe}.png";
        var path = Path.Combine(ScreenshotsDir, fileName);

        // Pump + layout 6 times so bound-property cascades have a
        // chance to settle before the bitmap captures. Two passes
        // were enough for static views but missed bindings that
        // re-evaluate on the second cycle (e.g. IsEnabled gated
        // through an IsNotNull converter on a freshly-set
        // SelectedDestination — the S04 snapshot was capturing the
        // disabled-from-prior-frame state). Six is overkill for
        // static layouts but cheap, and reliably catches the
        // settle-after-binding-change case.
        for (int i = 0; i < 6; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Height)));
        using (var bmp = new RenderTargetBitmap(size, new Vector(96, 96)))
        {
            bmp.Render(window);
            // A sync client (Dropbox, in this checkout) can briefly hold the
            // previous run's file; S71 failed that way on 1 November 2026.
            for (var attempt = 1; ; attempt++)
            {
                try { bmp.Save(path); break; }
                catch (IOException) when (attempt < 20) { System.Threading.Thread.Sleep(250); }
            }
        }

        Report.RecordStep(label, path, note);

        // Audit immediately so the anomaly shows up next to the
        // step that produced it. Cheap (few hundred pixel samples).
        var anomaly = VisualAuditor.Audit(path);
        if (!string.IsNullOrEmpty(anomaly))
            Report.RecordAnomaly($"{label}: {anomaly}");

        return path;
    }

    /// <summary>Mark the scenario as skipped with a recorded reason.</summary>
    public void Skip(string reason)
    {
        Report.MarkSkipped(reason);
        throw new ScenarioSkippedException(reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Closing windows the driver opened — best-effort, the
        // headless lifecycle does most cleanup itself.
        Driver.Dispose();
        if (Report.Status == ScenarioStatus.Pending) Report.MarkPassed();
    }

    private static string SafeFilename(string label)
    {
        var sb = new System.Text.StringBuilder(label.Length);
        foreach (var ch in label)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch is ' ' or '_' or '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        if (s.Length == 0) s = "step";
        if (s.Length > 60) s = s[..60];
        return s;
    }

    public static string ResolveArtifactsRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("BACKSPACE_SCENARIO_ARTIFACTS_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Path.GetFullPath(configuredRoot);

        var asmDir = Path.GetDirectoryName(typeof(ScenarioContext).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Backspace.Tests.csproj")))
            dir = dir.Parent;
        var root = dir?.FullName ?? asmDir;
        return Path.Combine(root, "artifacts", "scenarios");
    }
}

/// <summary>Throw-and-catch sentinel used by <see cref="ScenarioContext.Skip"/>
/// to short-circuit a scenario body without confusing xUnit. The
/// scenario base class catches it and re-routes to xUnit's
/// SkipException so the test runner reports the scenario as skipped
/// rather than failed.</summary>
public sealed class ScenarioSkippedException : Exception
{
    public ScenarioSkippedException(string reason) : base(reason) { }
}
