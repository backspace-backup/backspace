using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Backspace.Services;

namespace Backspace;

internal static class Program
{
    /// <summary>
    /// Set when the app is launched via the Explorer right-click "Restore
    /// an older version" verb — carries the file/folder path Windows
    /// passed on the command line. The MainWindow reads this on load
    /// and routes the user to the Restore tab pre-filled. Cleared after
    /// it's been consumed so a stale path doesn't fire on next launch.
    /// </summary>
    public static string? PendingRestorePath { get; set; }
    /// <summary>Started at sign-in (<see cref="SignInStart.Argument"/>): tray
    /// only, no window, until the person opens it.</summary>
    internal static bool StartedInBackground { get; private set; }
    internal static void OpenFromBackground()
    {
        if (!StartedInBackground) return;
        StartedInBackground = false;
        // The sign-in start was admitted only when this session had no Quit
        // pause. Opening its window starts normal housekeeping, but must not
        // queue a delayed resume that could undo a subsequent explicit Quit.
        RunPostInitStartup();
    }

    /// <summary>"--service-desktop" was the sign-in task of versions before
    /// U043; it means the same until that task is removed.</summary>
    private static bool IsBackgroundStart(string[] args)
        => args.Length == 1 && args[0] is SignInStart.Argument or "--service-desktop";

    [STAThread]
    public static int Main(string[] args)
    {
        if (IsBackgroundStart(args))
        {
            // After Quit, nothing restarts Backspace in this sign-in session.
            try { if (new SessionTaskControl().CurrentSessionPaused()) return 0; }
            catch (Exception ex) { Console.Error.WriteLine("Starting in the tray although the Quit state could not be checked: " + ex.Message); }
            StartedInBackground = true;
        }
#if WINDOWS
        if (args.Length > 0 && args[0] == "--service-update")
        {
            if (args.Length != 3) return 2;
            var root = Services.Platform.Windows.ServiceInstallationSecurity.CanonicalLocalPath(AppContext.BaseDirectory.TrimEnd('\\'));
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                Services.Platform.Windows.ServiceProgramUpdate.ApplyAsync(root, args[1], args[2], http, timeout.Token).GetAwaiter().GetResult();
                Services.Platform.Windows.ServiceProgramUpdateDesktop.WriteOutcome(root, true,
                    "Updated to Backspace " + args[1].TrimStart('v') + ". Your settings and backups were preserved.", args[2]);
                return 0;
            }
            catch (Exception ex)
            {
                try { Services.Platform.Windows.ServiceProgramUpdateDesktop.WriteOutcome(root, false, "The update could not finish. " + ex.Message, args[2]); } catch { }
                Console.Error.WriteLine("The background service update needs attention: " + ex.Message); return 4;
            }
        }
        if (args.Length > 0 && args[0] == "--service-recover-update")
        {
            if (args.Length is not (2 or 3) || args.Length == 3 && args[2] != "--finish-pending") return 2;
            try { return Services.Platform.Windows.ServiceProgramUpdateDesktop.RunRecovery(args[1], finishPending: args.Length == 3); }
            catch (Exception ex) { Console.Error.WriteLine("Background service update recovery could not finish: " + ex.Message); return 4; }
        }
        if (args.Length > 0 && args[0] is "--service-retire-prepare" or "--service-retire-complete")
        {
            if (args.Length != 1) return 2;
            try
            {
                var root = Services.Platform.Windows.ServiceInstallationSecurity.CanonicalLocalPath(AppContext.BaseDirectory.TrimEnd('\\'));
                if (args[0] == "--service-retire-prepare") Services.Platform.Windows.ServiceRetirement.Prepare(root);
                else Services.Platform.Windows.ServiceRetirement.Complete(root);
                return 0;
            }
            catch (Exception ex) { ReportSetupFailure("Background backup removal needs attention; retained data was preserved: " + ex.Message); return 4; }
        }
        if (args.Length > 0 && args[0] == "--service-retire")
        {
            if (args.Length != 1) return 2;
            try
            {
                var root = Services.Platform.Windows.ServiceInstallationSecurity.CanonicalLocalPath(AppContext.BaseDirectory.TrimEnd('\\'));
                Services.Platform.Windows.ServiceRetirement.Prepare(root);
                Services.Platform.Windows.ServiceRetirement.CompleteAfterReturn(root, TimeSpan.FromMinutes(10));
                return 0;
            }
            catch (Exception ex) { ReportSetupFailure("Background backup removal needs attention; retained data was preserved: " + ex.Message, ex); return 4; }
        }
        if (args.Length > 0 && args[0] == "--service-refresh")
        {
            if (args.Length is not (1 or 2)) return 2;
            try
            {
                Services.Platform.Windows.ServiceConfigurationUpdate.Apply(
                    Services.Platform.Windows.ServiceInstallationSecurity.CanonicalLocalPath(AppContext.BaseDirectory.TrimEnd('\\')),
                    args.Length == 2 ? args[1] : null);
                return 0;
            }
            catch (Exception ex) { ReportSetupFailure("Background backup settings need attention: " + ex.Message); return 4; }
        }
        if (args.Length > 0 && args[0] == Services.Platform.Windows.ServiceSettingsTask.Argument)
        {
            if (args.Length != 1) return 2;
            try { return Services.Platform.Windows.ServiceSettingsTask.ApplyAsSystem(AppContext.BaseDirectory.TrimEnd('\\')); }
            catch (Exception ex) { ReportSetupFailure("Your latest backup settings could not be applied: " + ex.Message, ex); return 4; }
        }
        if (args.Length > 0 && args[0] == "--service-activate")
        {
            if (args.Length != 1) return 2;
            try
            {
                Services.Platform.Windows.ServiceActivation.Activate(
                    Services.Platform.Windows.ServiceInstallationSecurity.CanonicalLocalPath(AppContext.BaseDirectory.TrimEnd('\\')));
                return 0;
            }
            catch (Exception ex) { ReportSetupFailure("Background backup activation needs attention: " + ex.Message); return 4; }
        }
        if (args.Length > 0 && args[0] == "--service-abort-prepare")
        {
            if (args.Length != 1) return 2;
            try
            {
                Services.Platform.Windows.ServiceSetup.AbortPreparation(
                    Services.Platform.Windows.ServiceInstallationSecurity.CanonicalLocalPath(AppContext.BaseDirectory.TrimEnd('\\')));
                return 0;
            }
            catch (Exception ex) { ReportSetupFailure("Background backup setup was retained: " + ex.Message); return 4; }
        }
        if (args.Length > 0 && args[0] == "--service-setup")
        {
            if (args.Length != 2) return 2;
            try
            {
                var root = Services.Platform.Windows.ServiceInstallationSecurity.CanonicalLocalPath(AppContext.BaseDirectory.TrimEnd('\\'));
                Services.Platform.Windows.ServiceSetup.Prepare(root, args[1]);
                Services.Platform.Windows.ServiceActivation.ActivateAfterHandoff(root, TimeSpan.FromMinutes(10));
                return 0;
            }
            catch (Exception ex) { ReportSetupFailure("Background backup setup could not finish: " + ex.Message, ex); return 4; }
        }
        if (args.Length > 0 && args[0] == "--service-prepare")
        {
            if (args.Length != 2) return 2;
            try
            {
                Services.Platform.Windows.ServiceSetup.Prepare(
                    Services.Platform.Windows.ServiceInstallationSecurity.CanonicalLocalPath(AppContext.BaseDirectory.TrimEnd('\\')), args[1]);
                Console.WriteLine("Background service prepared. Credential verification must finish before schedules are enabled.");
                return 0;
            }
            catch (Exception ex) { ReportSetupFailure("Background backup setup could not finish: " + ex.Message); return 4; }
        }
        if (args.Length > 0 && args[0] == "--service-host")
        {
            if (args.Length != 1) return 2;
            try { return Services.Platform.Windows.BackupWindowsService.RunInstalled(); }
            catch (Exception ex) { Console.Error.WriteLine("The installed backup service could not start: " + ex.Message); return 4; }
        }
        // This hidden worker is admitted by its protected installation and
        // actual virtual-service identity before normal GUI/scheduler startup.
        if (args.Length > 0 && args[0] is "--service-worker" or "--service-worker-owned" or "--service-worker-cli")
        {
            if (args.Length != 4) return 2;
            try
            {
                return (args[0] is "--service-worker-owned" or "--service-worker-cli"
                    ? Services.Platform.Windows.ServiceApprovedWorker.RunOwnedAsync(args[1], args[2], args[3], ownerCommand: args[0] == "--service-worker-cli")
                    : Services.Platform.Windows.ServiceApprovedWorker.RunAsync(args[1], args[2], args[3], CancellationToken.None))
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("The protected backup worker could not complete: " + ex.Message);
                return 4;
            }
        }
#endif
        IDisposable? updateRuntimeLease = null;
        SessionTaskControl.UnattendedLease? scheduledSession = null;
        try
        {
            if (OperatingSystem.IsWindows() && args.Length == 2 && args[0] == "--recover-update")
                return WindowsUpdateRuntime.Recover(args[1]);
            var initialMode = ParseMode(args).Mode;
            if (initialMode is RunMode.RunOne or RunMode.RunAll or RunMode.PruneOne or RunMode.CheckOne)
            {
#if WINDOWS
                var root = AppContext.BaseDirectory.TrimEnd('\\');
                try
                {
                    if (Services.Platform.Windows.ServiceCommandLineRun.FindBinding(root, AppPaths.ConfigRoot) is { } binding)
                    {
                        updateRuntimeLease = WindowsUpdateRuntime.AcquireShared(root, TimeSpan.FromSeconds(30));
                        var selected = ParseMode(args);
                        var operation = initialMode == RunMode.PruneOne ? "prune" : initialMode == RunMode.CheckOne ? "check" : "backup";
                        return Services.Platform.Windows.ServiceCommandLineRun.RunAsync(root, binding, selected.BackupName,
                            operation, Console.Out, CancellationToken.None).GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine("The backup command could not complete: " + ex.Message); return 4; }
#endif
                // Admit before loading secrets, starting engines or checking for
                // updates. The shared lease survives engine disposal in finally.
                if (!new SessionTaskControl().TryBeginUnattended(out scheduledSession, async () =>
                {
                    if (ServiceLocator.Initialized)
                        await ServiceLocator.BackrestSidecar.StopAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(2));
                }))
                {
                    Console.WriteLine(SessionTaskControl.PausedMessage);
                    return 0;
                }
            }
            if (OperatingSystem.IsWindows() && initialMode is not (RunMode.Version or RunMode.Help or RunMode.UsageError))
            {
#if WINDOWS
                if (initialMode == RunMode.Gui && Services.Platform.Windows.ServiceProgramUpdateDesktop.RecoverPendingBeforeStartup(
                    AppContext.BaseDirectory.TrimEnd('\\'), args)) return 0;
#endif
                if (WindowsUpdateRuntime.RecoverInterruptedBeforeStartup(AppContext.BaseDirectory, args)) return 0;
                updateRuntimeLease = WindowsUpdateRuntime.AcquireShared(AppContext.BaseDirectory, TimeSpan.FromSeconds(30));
            }
            // WinFSP DLL resolver: WinFSP's installer doesn't add
            // its bin folder to PATH, so the first reference to a
            // Fsp.* type otherwise throws on the type initializer.
            // Register the resolver before any code path can touch
            // the WinFsp.Net managed wrapper — applies to GUI launch,
            // headless --run, and scheduled-task invocations alike.
            // No-op on macOS / Linux.
#if WINDOWS
            if (initialMode is not (RunMode.Version or RunMode.Help or RunMode.UsageError))
                Backspace.Services.WinFsp.WinFspNativeLoader.EnsureRegistered();
#endif

            // CLI dispatch. GUI is the default; anything else runs headlessly and exits.
            var mode = ParseMode(args);

            // Log the invocation up front (before the dispatch) — useful when
            // a scheduled-task run silently fails to reach the orchestrator.
            // Skip the heavy ServiceLocator.InitializeCore for the
            // print-and-exit modes (Version, Help, UsageError); they
            // don't need config / secrets / a started Backrest sidecar
            // and the user shouldn't pay 200ms+ + AppData dir creation
            // for `--help`.
            // VSS helper mode initialises ServiceLocator inside
            // VssElevationCoordinator.RunHelperAsync so the engine
            // bootstrap matches whatever path the parent took. The
            // Program-level skipInit keeps us from double-initialising.
            var skipInit = mode.Mode is RunMode.Version
                                       or RunMode.Help
                                       or RunMode.UsageError
                                       or RunMode.VssElevatedHelper;
            if (!skipInit)
            {
                if (scheduledSession?.Token.IsCancellationRequested == true) return 0;
                // A headless scheduled run can overlap an open GUI. It shares
                // settings through transactional stores, but owns a separate
                // daemon/database so either process can exit independently.
                var headless = mode.Mode is RunMode.RunOne or RunMode.RunAll
                    or RunMode.PruneOne or RunMode.CheckOne;
                // Runs never raise a Windows prompt (VssElevationPolicy.PromptAllowed).
                ServiceLocator.InitializeCore(headless
                    ? BackrestSidecar.CreateIsolatedDataDirectory("scheduled") : null);
                AppLogger.For("Program").Information(
                    "Invoked: mode={Mode} args={Args}",
                    mode.Mode, string.Join(" ", args));

                // Reap orphan rclone processes from prior sessions
                // BEFORE the new Backrest sidecar (and its rclone
                // children) start. See RcloneOrphanReaper docs for
                // persisted ownership checks. Unregistered processes are
                // never terminated based on their executable name/path.
                RcloneOrphanReaper.Sweep();
            }

            var exit = mode.Mode switch
            {
                RunMode.Gui         => RunGui(args),
                RunMode.RunOne      => RunUnattended(mode.BackupName!, onlyThisOne: true, scheduledSession?.Token ?? default).GetAwaiter().GetResult(),
                RunMode.RunAll      => RunUnattended(null, onlyThisOne: false, scheduledSession?.Token ?? default).GetAwaiter().GetResult(),
                RunMode.PruneOne    => RunUnattendedMaintenance(mode.BackupName!, Models.RunKind.Prune, scheduledSession?.Token ?? default).GetAwaiter().GetResult(),
                RunMode.CheckOne    => RunUnattendedMaintenance(mode.BackupName!, Models.RunKind.Check, scheduledSession?.Token ?? default).GetAwaiter().GetResult(),
                RunMode.Migrate     => RunMigrate(mode.Path!),
                RunMode.Version     => PrintVersion(),
                RunMode.Help        => PrintUsage(exitCode: 0),
                RunMode.UsageError  => PrintUsage(exitCode: 2),
                RunMode.VssElevatedHelper => RunVssElevatedHelper(args),
                _                   => RunGui(args),
            };
            return exit;
        }
        catch (Exception ex)
        {
            // Last-chance catch so an unhandled exception still lands in the log.
            try { AppLogger.Log.Fatal(ex, "Unhandled exception in Main"); } catch { }
            throw;
        }
        finally
        {
            var enginesStopped = true;
            if (ServiceLocator.Initialized)
            {
                try
                {
                    ServiceLocator.BackrestSidecar.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    ServiceLocator.Maintenance.ReleaseEngineOwnershipAfterStop();
                }
                catch (Exception ex) { enginesStopped = false; AppLogger.For("Program").Warning(ex, "Engine teardown failed"); }
                ServiceLocator.Updates?.Dispose();
            }
            AppLogger.Shutdown();
            scheduledSession?.Dispose();
            updateRuntimeLease?.Dispose();
            WindowsUpdateRuntime.ApplyPendingAfterExit(enginesStopped);
        }
    }

    /// <summary>
    /// Process-wide single-instance coordinator. Owned by Program for
    /// the lifetime of the GUI; <c>App.OnFrameworkInitializationCompleted</c>
    /// reads <see cref="SingleInstanceCoordinator.SecondInstanceForwarded"/>
    /// to surface forwarded args (right-click "restore" paths, etc.)
    /// to the live MainWindow.
    /// </summary>
    public static SingleInstanceCoordinator? Coordinator { get; private set; }

    // ---- GUI ----

    private static int RunGui(string[] args)
    {
        // Single-instance gate: if another GUI is already running for
        // this user + this exe path, forward our args to it and exit.
        // The primary's pipe server picks them up and the existing
        // MainWindow handles the path (e.g. routes a --restore-prompt
        // path to the Restore tab).
        Coordinator = new SingleInstanceCoordinator();
        if (!Coordinator.TryAcquireMutex())
        {
            if (StartedInBackground) { Coordinator.Dispose(); return 0; }
            var forwarded = Coordinator.ForwardArgsToPrimary(args);
            AppLogger.For("Program").Information(
                "Second instance: forwarded={Forwarded}; exiting", forwarded);
            Coordinator.Dispose();
            // Exit 0 even if forward failed — there's already a primary;
            // failing here would just mean the user clicked too fast.
            return 0;
        }

        Coordinator.StartServer();

        // Stub-extract + scheduler-task re-upsert are deferred to
        // App.OnFrameworkInitializationCompleted so the Avalonia
        // IAssetLoader is registered before we resolve any avares://
        // URIs (the embedded stub binary). See RunPostInitStartup
        // below for what fires at that point.

        // Wrap the Avalonia run-loop in a defensive catch so a
        // platform-level Win32 exception thrown from a dispatcher post
        // (e.g. icon-handle exhaustion crashing a notification window
        // build) doesn't take down the whole process. Such a bug should
        // be visible in logs as a FATAL — but the user shouldn't lose
        // their session because a toast couldn't allocate a GDI
        // handle. Re-throw if cancellation/shutdown was the cause.
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (OperationCanceledException) { /* clean shutdown */ }
        catch (Exception ex)
        {
            try { AppLogger.Log.Fatal(ex, "Avalonia run loop crashed; shutting down"); } catch { }
        }
        Coordinator.Dispose();
        return 0;
    }

    /// <summary>
    /// Post-Avalonia-init startup work. Invoked from
    /// <see cref="App.OnFrameworkInitializationCompleted"/> once the
    /// IAssetLoader service is registered, so avares:// URI lookups
    /// (used by <see cref="StubEmbedder"/>) resolve correctly.
    ///
    /// Engine-binary readiness is handled lazily inside
    /// <c>BackrestSidecar.StartAsync</c> — a missing backrest /
    /// restic / rclone surfaces as a FileNotFoundException the
    /// orchestrator catches and renders as a normal cell failure;
    /// no need for a synchronous startup probe.
    ///
    /// Off-thread housekeeping covers stub extract, scheduled-task
    /// re-upsert, and the missed-run check. Background because
    /// schtasks.exe rebinds (50-200ms each) on a user with many
    /// backups would otherwise freeze the splash.
    /// </summary>
    public static void RunPostInitStartup()
    {
        // Fire deferred housekeeping off-thread. By now Avalonia is
        // initialized, so any avares:// resolution inside
        // ExtractStub() / re-upsert succeeds.
        _ = System.Threading.Tasks.Task.Run(RunDeferredStartupHousekeeping);
    }

    /// <summary>
    /// Stub-extract + scheduler-task re-upsert + missed-run check +
    /// eager Backrest sidecar boot. Runs after first paint so a slow
    /// path (user moved the exe → N schtasks.exe spawns) doesn't
    /// freeze the splash. Idempotent and failure-tolerant; the GUI
    /// is never blocked on this.
    ///
    /// Eager sidecar start: the SnapshotBrowser and SnapshotMounts
    /// commands need <c>BackrestSidecar.IsRunning == true</c> to
    /// not surface "isn't ready yet" the first time the user clicks
    /// them — lazy on-first-engine-RPC was correct for headless test
    /// scenarios but a poor user experience in the GUI. Boots the
    /// sidecar on the background thread so the splash isn't blocked
    /// on the ~1-2s readiness probe.
    /// </summary>
    private static void RunDeferredStartupHousekeeping()
    {
        try
        {
            // Versions before U043 kept a helper for Windows tasks that ran
            // after Backspace was moved. Nothing uses those files any more.
            foreach (var leftover in new[] { "Backspace-stub.exe", "last-exe-path.txt", "stub-fired.flag" })
            {
                try { System.IO.File.Delete(System.IO.Path.Combine(AppPaths.ConfigRoot, leftover)); }
                catch (Exception ex) { AppLogger.For("Program").Debug(ex, "Leaving {File} in place", leftover); }
            }

            // Sidecar bootstrap. Best-effort: a missing binary, port
            // conflict, or AV interference logs a warning and the
            // first engine RPC will retry. Subscribing commands
            // (Browse / Mount) check IsRunning themselves so they
            // can render a friendly toast if the boot failed.
            try
            {
                using var startupCancellation = new System.Threading.CancellationTokenSource();
                using var startupLease = ServiceLocator.Maintenance.AcquireReadAsync(
                    startupCancellation.Token, startupCancellation.Cancel).GetAwaiter().GetResult();
                ServiceLocator.BackrestSidecar
                    .EnsureStartedAsync(startupCancellation.Token)
                    .GetAwaiter().GetResult();
                AppLogger.For("Program").Information(
                    "Backrest sidecar ready at {Url}",
                    ServiceLocator.BackrestSidecar.WebUiUrl);
            }
            catch (Exception ex)
            {
                AppLogger.For("Program").Warning(ex,
                    "Backrest sidecar didn't start at app startup; first engine RPC will retry");
            }
        }
        catch (Exception ex)
        {
            AppLogger.For("Program").Warning(ex, "Deferred startup housekeeping failed");
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        ConfigureDesktopPlatform(AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace());

    /// <summary>Use the Windows redirection compositor. The bundled Avalonia
    /// WinUI/DirectComposition render timers can spin while an empty window is
    /// idle or hidden on some drivers. RedirectionSurface avoids that compositor
    /// loop while retaining Avalonia's normal GPU/software rendering fallbacks.</summary>
    public static AppBuilder ConfigureDesktopPlatform(AppBuilder builder)
    {
#if WINDOWS
        return builder.With(new Win32PlatformOptions
        {
            CompositionMode = new[] { Win32CompositionMode.RedirectionSurface },
        });
#else
        return builder;
#endif
    }

    // ---- unattended ----

    private static async Task<bool> ValidateUnattendedSecretsAsync()
    {
        // The store is lazy. Open it before any engine/network operation rather
        // than consulting an error flag that has not yet been populated.
        var warning = await SecretsStartupWarning.CheckAsync(ServiceLocator.Secrets).ConfigureAwait(false);
        if (warning is null) return true;
        Console.Error.WriteLine($"Backspace's saved credentials are unavailable. {warning.Message}");
        return false;
    }

    private static async Task<int> RunUnattended(string? backupName, bool onlyThisOne, CancellationToken sessionCancellation = default)
    {
        if (sessionCancellation.IsCancellationRequested) return 0;
        ServiceLocator.InitializeCore();
        if (!ServiceLocator.Config.IsLoaded)
        {
            // Load hit a transient IO error or a permission denial.
            // Don't enumerate cfg.Backups — it's the empty default
            // AppConfig and would silently report "No backup named X"
            // which a scheduled task would translate into a no-op
            // success. Fail loudly with a distinct exit code so the
            // operator can see in the scheduler log that the run was
            // skipped due to config-read failure, not config content.
            Console.Error.WriteLine("Backspace config couldn't be read at startup (see app log for the cause). Aborting unattended run.");
            return 3;
        }
        if (!await ValidateUnattendedSecretsAsync().ConfigureAwait(false)) return 4;
        var cfg = ServiceLocator.Config.Current;

        var backups = onlyThisOne
            ? cfg.Backups.Where(b => string.Equals(b.Name, backupName, StringComparison.OrdinalIgnoreCase)).ToList()
            : cfg.Backups.Where(b => b.Enabled).ToList();

        if (backups.Count == 0)
        {
            Console.Error.WriteLine(onlyThisOne
                ? $"No backup named '{backupName}' found."
                : "No enabled backups.");
            return 2;
        }

        int exit = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        foreach (var b in backups)
        {
            if (cts.IsCancellationRequested) break;
            Console.WriteLine($"=== {b.Name} ===");

            // Respect schedule's PausedUntilUtc when called headlessly.
            if (b.Schedule.PausedUntilUtc is DateTime until && DateTime.UtcNow < until)
            {
                Console.WriteLine($"Paused until {until:o}, skipping.");
                continue;
            }

            try
            {
                var r = await ServiceLocator.Orchestrator.RunBackupAsync(b, cts.Token);
                Console.WriteLine($"{b.Name}: {r.Status} - {r.Summary}");
                if (r.Status == Models.BackupRunStatus.Failed) exit = 1;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"{b.Name}: cancelled");
                exit = 1;
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{b.Name}: {ex.Message}");
                exit = 1;
            }
        }

        return exit;
    }

    // ---- standalone maintenance (prune / check) ----
    //
    // Fired by the OS scheduler from a Backup.PruneSchedule /
    // Backup.CheckSchedule entry. Same headless model as --run: the
    // task action is `Backspace.exe --prune <name>` (or --check), the
    // process starts, runs the maintenance, exits.
    private static async Task<int> RunUnattendedMaintenance(string backupName, Models.RunKind kind, CancellationToken sessionCancellation = default)
    {
        if (sessionCancellation.IsCancellationRequested) return 0;
        if (!ServiceLocator.Config.IsLoaded)
        {
            // Mirror RunUnattended — distinguish "config unread" from
            // "backup name doesn't exist" so a scheduled prune/check
            // failing because of a transient IO error doesn't look
            // identical to the user having deleted the backup.
            Console.Error.WriteLine("Backspace config couldn't be read at startup (see app log for the cause). Aborting unattended maintenance run.");
            return 3;
        }
        if (!await ValidateUnattendedSecretsAsync().ConfigureAwait(false)) return 4;
        var backup = ServiceLocator.Config.Current.Backups
            .FirstOrDefault(b => b.Name == backupName);
        if (backup is null)
        {
            Console.Error.WriteLine($"No backup named '{backupName}' found.");
            return 2;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Console.WriteLine($"=== {backup.Name} :: {kind} ===");
        try
        {
            var record = kind switch
            {
                Models.RunKind.Prune => await ServiceLocator.Orchestrator.RunPruneAsync(backup, cts.Token),
                Models.RunKind.Check => await ServiceLocator.Orchestrator.RunCheckAsync(backup, cts.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            Console.WriteLine($"{backup.Name}: {record.Status} - {record.Summary}");
            return record.Status == Models.BackupRunStatus.Failed ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"{backup.Name}: cancelled");
            return 1;
        }
    }

    // ---- migration ----

    private static int RunMigrate(string legacyPath)
    {
        ServiceLocator.InitializeCore();
        var report = ServiceLocator.Migrator.MigrateFrom(legacyPath);
        if (report.Skipped)
        {
            Console.WriteLine($"Skipped: {report.Reason}");
            return 0;
        }
        Console.WriteLine($"Imported {report.ImportedBackups.Count} backup(s) from {legacyPath}");
        foreach (var name in report.ImportedBackups) Console.WriteLine("  - " + name);
        foreach (var e in report.Errors) Console.Error.WriteLine("  ! " + e);
        return report.Errors.Count > 0 ? 1 : 0;
    }

    // ---- version ----

    private static int PrintVersion()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var ver = asm.GetName().Version?.ToString(3) ?? "?";
        Console.WriteLine($"Backspace {ver}");
        return 0;
    }

    /// <summary>
    /// Prints usage and returns exit code 2 (POSIX convention for
    /// usage/argument errors). Triggered by <c>--help</c>, <c>-h</c>,
    /// <c>/?</c>, AND any malformed CLI invocation that
    /// <see cref="ParseMode"/> classifies as <c>RunMode.Help</c>
    /// (e.g. <c>--run</c> with no backup name, <c>--migrate</c> with
    /// no path). Without this dispatch, those misuses silently
    /// fell through to <c>RunGui</c> — a scheduled task with a
    /// broken <c>--run</c> would launch the GUI under SYSTEM.
    /// </summary>
    private static int PrintUsage(int exitCode = 0)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var ver = asm.GetName().Version?.ToString(3) ?? "?";
        var bin = Services.Platform.PlatformInfo.IsWindows ? "Backspace.exe" : "Backspace";
        Console.WriteLine($"Backspace {ver}");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine($"  {bin}                    Launch the GUI (default).");
        Console.WriteLine($"  {bin} {SignInStart.Argument}       Start in the tray only, as at sign-in.");
        Console.WriteLine($"  {bin} --run <name>       Run one backup now, without the app.");
        Console.WriteLine($"  {bin} --run-all          Run every enabled backup now.");
        Console.WriteLine($"  {bin} --prune <name>     Apply the backup's retention policy (forget + repack).");
        Console.WriteLine($"  {bin} --check <name>     Check the backup's repositories.");
        Console.WriteLine($"  {bin} --migrate <path>   Import an existing Duplicacy setup (a repo folder or a parent of many).");
        Console.WriteLine($"  {bin} --version          Print version and exit.");
        Console.WriteLine($"  {bin} --help | -h | /?   Show this help.");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine($"  • Config + logs live under {AppPaths.ConfigRoot}");
        return exitCode;
    }

    // ---- argv parser ----

    /// <summary>
    /// CLI invocation modes. <see cref="Help"/> = explicit user request
    /// (--help / -h / /?), exits 0 (POSIX). <see cref="UsageError"/> =
    /// malformed CLI (e.g. --run with no name), exits 2 — same printed
    /// usage but a different exit code so a scheduled-task wrapper that
    /// runs `backspace --help && echo ok` sees the success.
    /// </summary>
    /// <summary>Helper-mode dispatch. Parses the per-cell request the
    /// parent encoded on argv, runs the backup through the same
    /// <see cref="ServiceLocator.Engine"/> the parent uses, and writes
    /// a JSON-encoded <see cref="Models.RunRecord"/> to the
    /// caller-supplied result file. Exit codes follow
    /// <see cref="VssElevationCoordinator.RunHelperAsync"/>: 0 = run
    /// committed (Success / Warning); 1 = engine threw; 2 = usage
    /// error / config lookup failed; 3 = cancelled; 4 = Skipped.</summary>
    private static int RunVssElevatedHelper(string[] args)
    {
        var parsed = VssElevationCoordinator.ParseHelperArgs(args);
        if (parsed is null)
        {
            Console.Error.WriteLine(
                "Backspace VSS helper: missing one of --backup-id / --source-path / " +
                "--destination-id / --storage-name / --result-file. Exiting with code 2.");
            return 2;
        }
        return VssElevationCoordinator.RunHelperAsync(parsed, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>The desktop starts setup steps through the Windows approval
    /// prompt, which cannot capture this process's output. Leave the reason
    /// beside the configuration so Settings can show it.</summary>
    private static void ReportSetupFailure(string message, Exception? detail = null)
    {
        Console.Error.WriteLine(message);
        try
        {
            var folder = System.IO.Path.Combine(AppContext.BaseDirectory, AppPaths.AppFolderName);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, SetupFailureFile),
                message.Length > 2000 ? message[..2000] : message);
            // Where it failed, for troubleshooting; the person sees only the message.
            if (detail is not null)
                System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "setup-failure-detail.txt"), detail.ToString());
        }
        catch (Exception) { /* The exit code still reports the failure. */ }
    }

    internal const string SetupFailureFile = "setup-failure.txt";

    private enum RunMode { Gui, RunOne, RunAll, Migrate, Version, Help, UsageError, VssElevatedHelper, PruneOne, CheckOne }

    private readonly record struct ParsedMode(RunMode Mode, string? BackupName = null, string? Path = null);

    private static ParsedMode ParseMode(string[] args)
    {
        if (args.Length == 0) return new(RunMode.Gui);
        // Validate the entire ordinary command before any startup side effects.
        // The second argument is positional data: backup names can start with '-'.
        var hasValue = args.Length == 2 && !string.IsNullOrWhiteSpace(args[1]);
        switch (args[0])
        {
            case SignInStart.Argument or "--service-desktop" when args.Length == 1:
                return new(RunMode.Gui);
            case "--run" when hasValue:
                return new(RunMode.RunOne, BackupName: args[1]);
            case "--prune" when hasValue:
                return new(RunMode.PruneOne, BackupName: args[1]);
            case "--check" when hasValue:
                return new(RunMode.CheckOne, BackupName: args[1]);
            case "--migrate" when hasValue:
                return new(RunMode.Migrate, Path: args[1]);
            case "--restore-prompt" when hasValue:
                PendingRestorePath = args[1];
                return new(RunMode.Gui);
            case "--run-all" when args.Length == 1:
                return new(RunMode.RunAll);
            case "--version" or "-v" when args.Length == 1:
                return new(RunMode.Version);
            case "--help" or "-h" or "/?" when args.Length == 1:
                return new(RunMode.Help);
            case VssElevationCoordinator.HelperCliArgument:
                // The dedicated helper validates its full IPC argument payload
                // before reading the request or running an elevated backup cell.
                return new(RunMode.VssElevatedHelper);
            default:
                return new(RunMode.UsageError);
        }
    }
}
