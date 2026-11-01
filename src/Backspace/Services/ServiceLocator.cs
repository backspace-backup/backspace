using System;
using Backspace.Services.Platform;

namespace Backspace.Services;

/// <summary>
/// Light static DI: one-line construction, no container. The app creates all
/// services once (either from the GUI entry point or the unattended CLI) and
/// ViewModels ask for what they need. Good enough for an app this size.
/// </summary>
public static class ServiceLocator
{
    public static ConfigStore Config { get; private set; } = default!;
    public static SecretsStore Secrets { get; private set; } = default!;
    public static IBackupEngine Engine { get; private set; } = default!;

    /// <summary>
    /// Backrest sidecar — registered eagerly but lifecycle is lazy.
    /// <see cref="ResticBackrestEngine.EnsureClientAsync"/> calls
    /// <see cref="BackrestSidecar.EnsureStartedAsync"/> on first
    /// touch, so callers don't need to know about StartAsync.
    /// </summary>
    public static BackrestSidecar BackrestSidecar { get; private set; } = default!;
    public static LogStore Logs { get; private set; } = default!;
    public static HealthcheckService Healthcheck { get; private set; } = default!;
    public static MailService Mail { get; private set; } = default!;
    public static NotificationService Notifier { get; private set; } = default!;
    public static BackupOrchestrator Orchestrator { get; private set; } = default!;
    public static IScheduler Scheduler { get; private set; } = default!;
    /// <summary>The start-at-sign-in entry. Tests replace it, so the suite
    /// never adds a real one to the machine running it.</summary>
    public static ISignInStart SignInStart => SignInStartOverride?.Invoke() ?? Services.SignInStart.Create();
    internal static Func<ISignInStart>? SignInStartOverride { get; set; }
    public static FilterSimulator Filters { get; private set; } = default!;
    public static ConfigMigrator Migrator { get; private set; } = default!;
    public static RestoreEngine Restore { get; private set; } = default!;
    public static ActiveRestoreRegistry ActiveRestores { get; private set; } = default!;
    public static UpdateChecker UpdateCheck { get; private set; } = default!;
    public static Backspace.ViewModels.WindowsUpdateViewModel Updates { get; private set; } = default!;
    public static RevisionBrowser Revisions { get; private set; } = default!;
    public static DropboxAuthProbe DropboxProbe { get; private set; } = default!;
    public static DestinationProbe DestProbe { get; private set; } = default!;
    public static StorageCleaner Cleaner { get; private set; } = default!;
    public static BackupRemoval Removals { get; private set; } = default!;
    public static SourceSizeProbe SizeProbe { get; private set; } = default!;
    public static SavingsEstimator Savings { get; private set; } = default!;
    public static BackupVerifier Verifier { get; private set; } = default!;
    public static TestRestoreService TestRestores { get; private set; } = default!;
    public static BackupProgressService Progress { get; private set; } = default!;
#if WINDOWS
    internal static Platform.Windows.BackgroundServiceActivity? BackgroundActivity { get; private set; }
#endif
    public static RunActivityTracker Activity { get; private set; } = default!;
    public static MaintenanceCoordinator Maintenance { get; private set; } = default!;

    /// <summary>
    /// Launches the bundled Backrest web UI in the user's default
    /// browser. Used by the "Browse snapshots" command on backup
    /// cards. Initialized after <see cref="BackrestSidecar"/> so the
    /// launcher always has a sidecar reference to read the live
    /// port from.
    /// </summary>
    public static SnapshotBrowserLauncher SnapshotBrowser { get; private set; } = default!;

    /// <summary>
    /// Drives the in-app OAuth 2.0 + PKCE flow for cloud
    /// destinations. The "Connect" button on the destination
    /// editor delegates here; the coordinator opens the user's
    /// browser at the provider's authorize URL, listens on a
    /// loopback port for the redirect, exchanges the code for a
    /// token, and hands back an rclone-shaped JSON blob ready to
    /// store in <see cref="Models.Destination.OAuthTokenRef"/>.
    /// </summary>
    public static OAuth.OAuthFlowCoordinator OAuthFlow { get; private set; } = default!;

    /// <summary>
    /// Windows-only registry of live WebDAV snapshot mounts. Null on
    /// macOS / Linux (the mount surface is Windows-only). The
    /// "Mount in Explorer" card command goes through this so a
    /// second click re-opens the existing drive letter instead of
    /// dialling a fresh one, and so app shutdown can unmount
    /// cleanly. Typed as the cross-platform
    /// <see cref="WebDav.ISnapshotMountRegistry"/> interface so this
    /// declaration compiles on every TFM; the concrete Windows
    /// implementation lives in MountedSnapshotRegistry.cs.
    /// </summary>
    public static WebDav.ISnapshotMountRegistry? SnapshotMounts { get; private set; }

    public static bool Initialized { get; private set; }

    /// <summary>
    /// Optional override for the <see cref="IBackupEngine"/> created
    /// during <see cref="InitializeCore"/>. Production code leaves this
    /// alone — the default is a real <see cref="ResticBackrestEngine"/>.
    /// Tests set this BEFORE <see cref="InitializeCore"/> runs to
    /// substitute an in-memory engine so orchestrator-touching tests
    /// don't need a backrest.exe on disk. Visible only via
    /// <c>InternalsVisibleTo</c> to the test assembly; production
    /// callers can't reach it.
    /// </summary>
    internal static Func<SecretsStore, IBackupEngine>? EngineOverride { get; set; }

    /// <summary>Tests set this so saving a backup never registers a real
    /// Windows task on the machine running the suite. Test-only, like
    /// <see cref="EngineOverride"/>.</summary>
    internal static Func<IScheduler>? SchedulerOverride { get; set; }

    public static void InitializeCore(string? backrestDataDir = null)
        => InitializeCore(backrestDataDir, null, null);

    // The protected service worker supplies its canonical vault and a scheduler
    // that cannot register interactive-user tasks. Ordinary startup never takes
    // these overrides from configuration or environment variables.
    internal static void InitializeCore(string? backrestDataDir, SecretsStore? secrets, IScheduler? scheduler)
    {
        if (Initialized) return;
        AppPaths.EnsureAll();

        Config = new ConfigStore();
        Config.Load();

        // Initialize structured logging now that we have LoggingSettings.
        AppLogger.Initialize(Config.Current.Logging);
        Config.Changed += (_, __) => AppLogger.Initialize(Config.Current.Logging);

        Secrets = secrets ?? new SecretsStore();
        BackrestSidecar = new BackrestSidecar(
            backrestBinaryPath: AppPaths.BackrestBinary,
            resticBinaryPath: AppPaths.ResticBinary,
            dataDir: backrestDataDir ?? AppPaths.BackrestDataDir,
            deleteDataOnDispose: backrestDataDir is not null);
        Engine = EngineOverride?.Invoke(Secrets)
            ?? new ResticBackrestEngine(BackrestSidecar, Secrets);
        Logs = new LogStore();
        Healthcheck = new HealthcheckService(Config, Secrets);
        Mail = new MailService(Config, Secrets);
        Notifier = new NotificationService(Config);
        Progress = new BackupProgressService();
        Activity = new RunActivityTracker();
        Maintenance = new MaintenanceCoordinator();
        Orchestrator = new BackupOrchestrator(Config, Secrets, Engine, Logs, Healthcheck, Mail, Notifier, Progress);
        Scheduler = scheduler ?? SchedulerOverride?.Invoke() ?? TaskSchedulerService.Create();
        Filters = new FilterSimulator();
        Migrator = new ConfigMigrator(Config);
        Restore = new RestoreEngine(Config, Engine, Secrets);
        ActiveRestores = new ActiveRestoreRegistry();
        Revisions = new RevisionBrowser(Engine, Secrets);
        DropboxProbe = new DropboxAuthProbe(Secrets);
        DestProbe = new DestinationProbe(Secrets);
        Cleaner = new StorageCleaner(Config, Secrets, Engine) { Probe = DestProbe, LogStore = Logs };
        Removals = new BackupRemoval(Config, Cleaner, Maintenance, logs: Logs);
        Cleaner.PendingRemovalsFor = Removals.PendingFor;
        SizeProbe = new SourceSizeProbe();
        Savings = new SavingsEstimator();
        Verifier = new BackupVerifier(Config, Restore, Revisions, Engine);
        TestRestores = new TestRestoreService(Config, Verifier);
        UpdateCheck = new UpdateChecker();
        Updates?.Dispose();
        Updates = new Backspace.ViewModels.WindowsUpdateViewModel();
        // One-shot housekeeping: prior verify drills can leave scratch
        // dirs under ConfigRoot/verify/ if AV / another process held a
        // handle on a restored file when the recursive cleanup ran.
        // Without this sweep the portable config dir grows unbounded.
        BackupVerifier.SweepStaleScratchDirs();
        SnapshotBrowser = new SnapshotBrowserLauncher(BackrestSidecar);
        OAuthFlow = new OAuth.OAuthFlowCoordinator();
        // MountedSnapshotRegistry is excluded from the cross-platform
        // TFM at the csproj level, so we can only reference it from
        // a Windows TFM build. The runtime check is redundant on
        // Windows (the TFM constant is the gate) but keeps the
        // intent visible at the call site.
#if WINDOWS
        if (OperatingSystem.IsWindows())
            SnapshotMounts = new WebDav.MountedSnapshotRegistry();
#endif

        Initialized = true;
#if WINDOWS
        BackgroundActivity = Platform.Windows.BackgroundServiceActivity.ForDesktop();
#endif
    }

    /// <summary>
    /// Rebuild every service that captured the previous <see cref="Engine"/>
    /// reference, after a test fixture has swapped Engine for a different
    /// concrete (e.g. <c>InMemoryBackupEngine</c> → <c>ResticBackrestEngine</c>).
    ///
    /// Why this exists: the orchestrator + filters + restore engine +
    /// revision browser + storage cleaner + verifier all take an
    /// <see cref="IBackupEngine"/> at CONSTRUCTION. They keep that
    /// reference for their lifetime. So a mid-life swap of
    /// <see cref="Engine"/> alone leaves every higher-level service still
    /// pointing at the OLD engine — the orchestrator routes Backup RPCs
    /// to the fake even though the swap "succeeded" on paper. We hit
    /// this in the scenario suite: every E2E that claimed to run
    /// against the real Backrest sidecar was actually hitting the
    /// in-memory fake because the orchestrator had captured it at
    /// <see cref="InitializeCore"/> time. <see cref="RealEngineFixture"/>
    /// now calls this after its swap so the next orchestrator call
    /// reaches the real engine.
    ///
    /// Internal — only the test fixture has any business calling this.
    /// </summary>
    internal static void RebuildEngineDependents()
    {
        Orchestrator    = new BackupOrchestrator(Config, Secrets, Engine, Logs, Healthcheck, Mail, Notifier, Progress);
        // FilterSimulator walks the local filesystem directly and has no
        // engine reference, so it doesn't need a rebuild on engine swap.
        Restore         = new RestoreEngine(Config, Engine, Secrets);
        Revisions       = new RevisionBrowser(Engine, Secrets);
        Cleaner         = new StorageCleaner(Config, Secrets, Engine) { Probe = DestProbe, LogStore = Logs };
        Verifier        = new BackupVerifier(Config, Restore, Revisions, Engine);
        TestRestores    = new TestRestoreService(Config, Verifier);
    }
}
