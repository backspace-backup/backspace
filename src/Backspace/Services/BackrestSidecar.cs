using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Owns the bundled backrest.exe (or its Unix equivalent) as a child
/// process bound to <c>127.0.0.1:&lt;ephemeral&gt;</c>. Hands the rest
/// of the app a <see cref="BackrestClient"/> configured to talk to the
/// daemon.
///
/// Lifecycle:
///   1. <see cref="StartAsync"/> picks an ephemeral high port, pre-seeds
///      a Backrest config.json under <see cref="DataDir"/>, spawns
///      backrest with <c>--bind-address 127.0.0.1:&lt;port&gt;</c> and
///      <c>--data-dir &lt;DataDir&gt;</c>, then polls until the daemon
///      responds.
///   2. <see cref="Client"/> exposes the configured RPC client. The
///      sidecar is the only thing that knows the port + bearer token;
///      every caller routes through this client.
///   3. <see cref="StopAsync"/> sends SIGTERM (or terminates on Windows),
///      waits up to a grace period, and then force-kills if the daemon
///      is still alive.
///
/// Auth: prepare a random bcrypt credential before the daemon binds to
/// loopback, then authenticate the client
/// with a JWT. Subsequent starts reuse the protected companion file.
///
/// All paths and the port are testable independently of an actual
/// backrest binary — see <see cref="AllocateEphemeralPort"/> and
/// <see cref="WriteSeedConfig"/>.
/// </summary>
public sealed class BackrestSidecar : IAsyncDisposable
{
    private static ILogger _log => AppLogger.For<BackrestSidecar>();

    /// <summary>Filesystem path to the backrest executable. Must exist;
    /// fetched at build time by FetchBackrest.targets or dropped in
    /// manually.</summary>
    public string BackrestBinaryPath { get; }

    /// <summary>Filesystem path to the bundled restic executable. Passed
    /// to backrest via <c>--restic-cmd</c> so the daemon doesn't try to
    /// auto-download its own copy at first launch.</summary>
    public string ResticBinaryPath { get; }

    /// <summary>Per-installation data directory. Holds the seed
    /// config.json, Backrest's sqlite oplog, and the per-operation log
    /// store. Single-user desktop app, so we put this under
    /// <see cref="AppPaths"/>.</summary>
    public string DataDir { get; }

    private Process? _proc;
    private FileStream? _dataLease;
    private bool _wroteOwnership;
    private readonly bool _deleteDataOnDispose;
    private HttpClient? _http;
    private BackrestClient? _client;
    private int _port;

    public BackrestSidecar(string backrestBinaryPath, string resticBinaryPath, string dataDir,
        bool deleteDataOnDispose = false)
    {
        BackrestBinaryPath = backrestBinaryPath;
        ResticBinaryPath = resticBinaryPath;
        DataDir = dataDir;
        _deleteDataOnDispose = deleteDataOnDispose;
    }

    /// <summary>The port the daemon ended up binding to. 0 before
    /// <see cref="StartAsync"/> completes successfully.</summary>
    public int Port => _port;

    /// <summary>
    /// HTTP base URL the bundled Backrest binary serves both the
    /// Connect-Go RPC and its built-in web UI on. Empty string
    /// before <see cref="StartAsync"/> completes. The Snapshot
    /// Browser hands this URL (plus a plan-specific fragment) to
    /// the user's default browser so they get the full Backrest
    /// SPA — same UI Backrest's standalone deployment ships —
    /// without us reinventing snapshot navigation.
    /// </summary>
    public string WebUiUrl => _port == 0 ? "" : $"http://127.0.0.1:{_port}/";

    /// <summary>The configured RPC client. <c>null</c> before
    /// <see cref="StartAsync"/> completes. Internal because callers
    /// outside the engine implementation should never bypass
    /// <see cref="IBackupEngine"/>.</summary>
    internal BackrestClient? Client => _client;

    /// <summary><c>true</c> once the daemon has been launched and a
    /// readiness probe has succeeded.</summary>
    public bool IsRunning => IsAlive(_proc) && _client is not null;

    private static bool IsAlive(Process? process)
    {
        try { return process is not null && !process.HasExited; }
        catch (InvalidOperationException) { return false; } // start failed / already disposed
    }

    /// <summary>Cheap no-op when already running; spins up the daemon
    /// otherwise. Lets callers (the engine, RPC-using services) call
    /// this at the top of every operation without worrying about
    /// double-launch races. The actual start work happens inside a
    /// lock so concurrent first-callers serialize on a single launch.</summary>
    public async Task EnsureStartedAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            await StartCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private readonly System.Threading.SemaphoreSlim _startGate = new(initialCount: 1, maxCount: 1);

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try { await StartCoreAsync(ct).ConfigureAwait(false); }
        finally { _startGate.Release(); }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        if (IsAlive(_proc))
            throw new InvalidOperationException("BackrestSidecar is already running.");
        if (_dataLease is not null)
            await StopCoreAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
        if (!File.Exists(BackrestBinaryPath))
            throw new EngineBinaryMissingException(
                $"Backrest engine isn't installed alongside Backspace.exe. Expected at {BackrestBinaryPath}. " +
                "Re-run the installer or do a full build (without -p:SkipBackrestDownload). Retrying won't help.",
                BackrestBinaryPath);
        if (!File.Exists(ResticBinaryPath))
            throw new EngineBinaryMissingException(
                $"Restic engine isn't installed alongside Backspace.exe. Expected at {ResticBinaryPath}. " +
                "Re-run the installer or do a full build (without -p:SkipBackrestDownload). Retrying won't help.",
                ResticBinaryPath);

        Directory.CreateDirectory(DataDir);

        // Hold this directory exclusively for the whole daemon lifetime.
        // A second owner must never rewrite its live database or kill it.
        _dataLease = AcquireDataDirectoryLease(DataDir);
        try
        {
            RecoverOwnedSidecar();

            _port = AllocateEphemeralPort();
            var configPath = Path.Combine(DataDir, "config.json");
            WriteSeedConfig(configPath);
            PrepareAuthentication();

            var psi = new ProcessStartInfo(BackrestBinaryPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = DataDir,
            };
            psi.ArgumentList.Add("--bind-address");
            psi.ArgumentList.Add($"127.0.0.1:{_port}");
            psi.ArgumentList.Add("--data-dir");
            psi.ArgumentList.Add(DataDir);
            psi.ArgumentList.Add("--config-file");
            psi.ArgumentList.Add(configPath);
            psi.ArgumentList.Add("--restic-cmd");
            psi.ArgumentList.Add(ResticBinaryPath);
            // Backrest defaults --windows-tray=true which puts its OWN
            // icon next to Backspace's in the system tray on every launch.
            // The user reported this looked like an orphan Backrest, but
            // it's just Backrest's daemon-with-tray default. Backspace is
            // the only UI surface; the sidecar runs headless. Suppress.
            if (OperatingSystem.IsWindows())
            {
                psi.ArgumentList.Add("--windows-tray=false");
            }

            _log.Information("Starting Backrest sidecar at 127.0.0.1:{Port} (binary={Binary})", _port, BackrestBinaryPath);

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) _log.Debug("[backrest] {Line}", e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log.Warning("[backrest] {Line}", e.Data); };
            if (!_proc.Start()) throw new InvalidOperationException("Failed to start backrest.");

            // Job-object: tie backrest's lifetime to ours so a Backspace
            // crash, an unhandled exception, or the user closing the app
            // can't leave the engine running in the background as an
            // orphan. Windows tears down the job (KILL_ON_JOB_CLOSE)
            // when the parent process dies, taking every member with it.
            // The graceful StopAsync path is still the primary teardown
            // mechanism — the job object is just the safety net.
            //
            // If the assignment fails (nested job we couldn't break away
            // from, restricted token, etc.), we have a hard choice: keep
            // running an un-supervised sidecar that WILL outlive a crash,
            // or kill the sidecar we just started and surface a clear
            // error. Past user-visible orphan bug: chose the former and
            // got "Backrest in systray after Backspace died". Choose the
            // latter from now on — better a startup error the user sees
            // than a silent orphan they can't explain.
            if (!KillOnExitJobObject.TryAssign(_proc))
            {
                // The outer teardown owns termination and retains our lease if
                // it fails. Do not drop the process handle before death is known.
                throw new InvalidOperationException(
                    "Couldn't tie the Backrest sidecar to Backspace's lifetime via a Windows Job Object. " +
                    "An un-supervised sidecar would outlive a Backspace crash as an orphan process; " +
                    "Backspace refuses to proceed. Run Backspace from File Explorer / Start Menu " +
                    "instead of nested inside another job (some debugger / build-farm wrappers " +
                    "create a job that refuses nested assignment).");
            }

            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            // Stamp the pid file as soon as we have a live PID. Doing this
            // before the readiness probe means even a startup that hangs
            // leaves a recoverable pid-file for next launch's orphan-kill.
            try { WritePidFile(); }
            catch (Exception ex) { _log.Warning(ex, "Failed to write Backrest sidecar pid-file (continuing)"); }

            // HttpClient.Timeout = InfiniteTimeSpan is mandatory here.
            // Backrest's blocking RPCs (Forget, Check, Backup, Restore)
            // hold the connection open until the underlying op terminates;
            // for a large repo a Forget/Prune can run for tens of minutes,
            // and the operation-event stream is *meant* to be long-lived.
            // The .NET default of 100s would fire TaskCanceledException
            // mid-flight and mark the cell Skipped — observed in E03's
            // two-concurrent-backups scenario where contention on Backrest's
            // global task queue made one inline Forget cross the 100s mark.
            // Cancellation is still cooperatively respected via the
            // per-call CancellationToken; the only thing this disables is
            // the wall-clock timer.
            _http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{_port}/"),
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };
            var client = new BackrestClient(_http);
            await WaitForReadyAsync(client, ct).ConfigureAwait(false);
            await EnsureAuthAsync(client, ct).ConfigureAwait(false);
            // Expose the client only after authenticated startup has finished.
            _client = client;
        }
        catch
        {
            await StopCoreAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal static FileStream AcquireDataDirectoryLease(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        try
        {
            return new FileStream(Path.Combine(dataDir, "sidecar.owner.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException("Another Backspace process is using this engine data folder. " +
                "Its running work was left untouched. Try again when that process finishes.", ex);
        }
    }

    /// <summary>Per-run engine state stays within the portable config
    /// root. It is separate from the GUI daemon; run transcripts are still copied
    /// to the app's common log store by the engine.</summary>
    internal static string CreateIsolatedDataDirectory(string purpose)
        => Path.Combine(AppPaths.ConfigRoot, "engine-runs", purpose,
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");

    /// <summary>Prepare a protected configuration while no daemon is running.
    /// Preserve all unrelated configuration, including fields unknown to our
    /// typed model. Missing credentials are replaced locally; no unauthenticated
    /// HTTP setup endpoint is ever needed.</summary>
    internal void PrepareAuthentication()
    {
        var configPath = Path.Combine(DataDir, "config.json");
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))
            as System.Text.Json.Nodes.JsonObject
            ?? throw new IOException("The engine configuration is not a JSON object. It was left unchanged.");
        var auth = config["auth"] as System.Text.Json.Nodes.JsonObject;
        if (ConfigHasAuthUsers(configPath) && auth?["disabled"]?.GetValue<bool>() != true
            && ReadAuthFile().Length > 0)
            return;

        var password = GenerateRandomPassword();
        // Backrest's protobuf passwordBcrypt is base64 of a normal bcrypt hash.
        // Use the same work factor as Go bcrypt.DefaultCost.
        var hash = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            BCrypt.Net.BCrypt.HashPassword(password, workFactor: 10)));
        WriteAuthFile(password);
        config["auth"] = new System.Text.Json.Nodes.JsonObject
        {
            ["disabled"] = false,
            ["users"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = AuthUserName,
                ["passwordBcrypt"] = hash,
            }),
        };
        WriteConfigAtomically(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Sign in to the already-protected daemon. Any failure stops
    /// startup; no caller receives a client without a nonempty bearer token.</summary>
    internal async Task EnsureAuthAsync(BackrestClient client, CancellationToken ct)
    {
        var password = ReadAuthFile();
        if (password.Length == 0)
            throw new IOException("The backup engine's local sign-in password is unavailable. Startup was stopped.");

        string jwt;
        try
        {
            jwt = await client.LoginAsync(AuthUserName, password, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new IOException("Backspace could not sign in to its backup engine. Startup was stopped.", ex);
        }
        if (string.IsNullOrWhiteSpace(jwt))
            throw new IOException("The backup engine returned an empty sign-in token. Startup was stopped.");
        client.SetBearerToken(jwt);
    }

    /// <summary>The username we seed Backrest with. Fixed value
    /// because the user never sees it — Backspace is the only RPC
    /// client.</summary>
    internal const string AuthUserName = "backspace";

    private string AuthFilePath => Path.Combine(DataDir, "sidecar-auth.txt");

    private string ReadAuthFile()
    {
        try
        {
            if (!File.Exists(AuthFilePath)) return "";
            return File.ReadAllText(AuthFilePath).Trim();
        }
        catch { return ""; }
    }

    private void WriteAuthFile(string password)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(AuthFilePath, password);
        // On Windows, restrict the file to the current user so even
        // another local account on the same box can't read it. Best-
        // effort: if ACL setting fails (network drive, atypical FS,
        // etc.) we still wrote the file — the next-best protection is
        // the parent dir being inside the user's Backspace install
        // folder.
        if (OperatingSystem.IsWindows())
        {
            try { RestrictFileToCurrentUser(AuthFilePath); }
            catch { /* best-effort */ }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictFileToCurrentUser(string path)
    {
        var info = new FileInfo(path);
        var sec = info.GetAccessControl();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        if (user is not null)
        {
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                user,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
        }
        info.SetAccessControl(sec);
    }

    /// <summary>Generate a per-instance random password. 32 bytes of
    /// cryptographic randomness hex-encoded — 64-char string, ~256
    /// bits of entropy. Enough to make brute-force over the loopback
    /// bind a non-starter even without rate-limiting.</summary>
    private static string GenerateRandomPassword()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>True iff the on-disk config has at least one entry in
    /// auth.users. We read the file directly rather than going through
    /// GetConfig because at the start of EnsureAuthAsync the bearer
    /// token isn't set yet, and a future Backrest could make GetConfig
    /// auth-gated; the disk file is always readable from our process.</summary>
    private static bool ConfigHasAuthUsers(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return false;
            var text = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("auth", out var auth)) return false;
            if (!auth.TryGetProperty("users", out var users)) return false;
            return users.ValueKind == JsonValueKind.Array && users.GetArrayLength() > 0;
        }
        catch { return false; }
    }



    public async Task StopAsync(TimeSpan? gracePeriod = null, CancellationToken ct = default)
    {
        await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await StopCoreAsync(gracePeriod, ct).ConfigureAwait(false); }
        finally { _startGate.Release(); }
    }

    private async Task StopCoreAsync(TimeSpan? gracePeriod, CancellationToken ct)
    {
        var process = _proc;
        try
        {
            if (IsAlive(process))
            {
                var grace = gracePeriod ?? Timing.StopGracePeriod;
                _log.Information("Stopping owned Backrest sidecar (pid={Pid})", process!.Id);
                try { process.CloseMainWindow(); } catch { }
                try { await WaitForExitAsync(process, grace, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* teardown still must terminate our child */ }
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Keep the lease and ownership record if termination failed. A
            // second process must never open a still-live daemon's database.
            if (!IsAlive(process))
            {
                if (_wroteOwnership) TryDeletePidFile();
                _wroteOwnership = false;
                _http?.Dispose();
                process?.Dispose();
                _proc = null;
                _http = null;
                _client = null;
                _port = 0;
                _dataLease?.Dispose();
                _dataLease = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_deleteDataOnDispose)
        {
            // Only disposable per-run folders explicitly created under this
            // root qualify; never recursively remove the persistent GUI data.
            var root = Path.GetFullPath(Path.Combine(AppPaths.ConfigRoot, "engine-runs"))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(DataDir);
            if (target.StartsWith(root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); }
                catch (Exception ex) { _log.Debug(ex, "Could not remove disposable engine state"); }
            }
        }
    }

    // ---- helpers, public-internal for unit testing ----

    /// <summary>Asks the OS for a free 127.0.0.1 port by binding a
    /// listener to port 0 and reading the assigned value back. Has a
    /// trivial TOCTOU window between when we close the listener and
    /// when backrest binds to the same port; in practice the kernel
    /// avoids immediate reuse and a fresh ephemeral port is essentially
    /// always available.</summary>
    internal static int AllocateEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>Create a new engine configuration, or preserve an existing one.
    /// Authentication is completed on disk before Process.Start. A corrupt or
    /// newer configuration must never be replaced with an empty repository list.</summary>
    internal static void WriteSeedConfig(string configPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        if (File.Exists(configPath))
        {
            var existing = File.ReadAllText(configPath);
            int? version;
            try
            {
                using var parsed = JsonDocument.Parse(existing);
                version = TryExtractConfigVersion(existing);
            }
            catch (JsonException ex)
            {
                throw new IOException("The engine configuration is unreadable. It was left unchanged; recover a known-good copy before retrying.", ex);
            }
            if (version is null || version > SeedConfigVersion)
                throw new IOException("The engine configuration has an unsupported schema version. It was left unchanged; use a compatible Backspace version.");
            return;
        }

        var cfg = new BackrestConfig
        {
            Modno = 0,
            Version = SeedConfigVersion,
            Instance = "backspace",
            Auth = new BackrestAuth { Disabled = false },
        };

        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        WriteConfigAtomically(configPath, json);
    }

    private static void WriteConfigAtomically(string configPath, string json)
    {
        var tmp = configPath + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            if (File.Exists(configPath))
                File.Replace(tmp, configPath, destinationBackupFileName: null);
            else
                File.Move(tmp, configPath);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* best-effort; next launch retries */ }
        }
    }

    /// <summary>
    /// The config schema version Backrest's bundled binary expects.
    /// Bump this whenever we upgrade the bundled binary to a release
    /// whose migrations target a newer version. Check the pinned release's
    /// migrations and confirm by starting an isolated daemon with a fresh
    /// config: its "migrating config from version 0 to N" log gives the
    /// target version. The --version command does not run migrations.
    ///
    /// Bundled Backrest 1.14.1 still uses version 6, as declared in
    /// internal/config/migrations/migrations.go at that release tag. Earlier code shipped
    /// "Version = 4" which silently downgraded the seed on every
    /// launch, then forced Backrest to migrate it forward on read.
    /// The migration was nominally lossless, but combined with our
    /// JSON model dropping unknown fields (no JsonExtensionData on
    /// BackrestConfig at the time), the SetConfig round-trip
    /// occasionally left the daemon's persisted plan list in a
    /// state that didn't match what we sent — surfacing as
    /// "plan not found" on the immediately-following Backup RPC.
    /// </summary>
    internal const int SeedConfigVersion = 6;

    /// <summary>
    /// Sidecar lifecycle timing knobs, collected so the values are
    /// easy to audit / tune in one place rather than scattered as
    /// inline literals. Comments document the rationale for each
    /// duration so future tuning is informed.
    /// </summary>
    private static class Timing
    {
        /// <summary>How long <see cref="StopAsync"/> waits for the
        /// daemon to exit gracefully after <c>CloseMainWindow</c>
        /// before force-killing. Backrest's shutdown handler flushes
        /// the sqlite oplog + closes the listener; on slow disks
        /// this can take a couple of seconds. 5 s is the default
        /// caller-overridable grace window.</summary>
        public static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(5);

        /// <summary>Total wait the readiness-probe loop spends polling
        /// GetConfig after spawning the daemon. Backrest binds the
        /// listener early but the RPC handlers come up after
        /// sqlite/oplog/logstore init, which can take a few seconds
        /// on a cold start. 30 s is generous enough to absorb AV
        /// scans of the freshly-extracted binary; the message at
        /// <see cref="WaitForReadyAsync"/> surfaces this value to
        /// the user verbatim, so it stays here as a constant.</summary>
        public static readonly TimeSpan ReadinessProbeTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Interval between readiness-probe attempts.
        /// 250 ms is short enough that a successful cold start
        /// returns within a second, and large enough that we don't
        /// hammer the daemon's network stack while it's coming up.</summary>
        public static readonly TimeSpan ReadinessProbeInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>Bound on the orphan-kill <c>WaitForExit</c>.
        /// We've already issued <c>Kill(entireProcessTree)</c>; this
        /// is just the kernel-side reaping window. 5 s covers slow
        /// per-PID reaping under heavy system load.</summary>
        public const int OrphanKillReapMs = 5000;
    }

    /// <summary>Pull the "version" integer out of a Backrest config.json
    /// without taking a full deserialize dependency on the schema —
    /// the whole point of this helper is to handle configs whose
    /// schema we don't fully model yet.</summary>
    private static int? TryExtractConfigVersion(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number)
                return v.GetInt32();
        }
        catch { /* caller rejects malformed or unsupported configuration */ }
        return null;
    }

    private static async Task WaitForReadyAsync(BackrestClient client, CancellationToken ct)
    {
        // A saved configuration already requires authentication. HTTP 401
        // proves that the server is ready for the login step, not that startup
        // should keep waiting for an anonymous GetConfig to succeed.
        // Backrest binds the listener early but the RPC handlers come
        // up after sqlite/oplog/logstore initialization, so the first
        // few tries can hit "connection refused." Try for up to 30s.
        var deadline = DateTime.UtcNow + Timing.ReadinessProbeTimeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _ = await client.CallAsync<EmptyRequest, BackrestConfig>("/v1.Backrest/GetConfig", new EmptyRequest(), ct).ConfigureAwait(false);
                return;
            }
            catch (BackrestRpcException ex) when (ex.HttpStatus == 401)
            {
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(Timing.ReadinessProbeInterval, ct).ConfigureAwait(false);
            }
        }
        throw new TimeoutException(
            $"The Backrest engine didn't respond within {Timing.ReadinessProbeTimeout.TotalSeconds:F0} s. " +
            "Check the app log; common causes are antivirus interference or a stale process holding the port.",
            last);
    }

    private static async Task<bool> WaitForExitAsync(Process proc, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!proc.HasExited && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        return proc.HasExited;
    }

    /// <summary>Marker type for RPCs that take <c>google.protobuf.Empty</c>.</summary>
    internal sealed class EmptyRequest { }

    // The JSON ownership record includes both process creation times and paths.
    // A legacy PID|path record cannot establish ownership and never permits a kill.
    internal string PidFilePath => Path.Combine(DataDir, "sidecar.pid");

    private void WritePidFile()
    {
        if (_proc is null) throw new InvalidOperationException("Sidecar has not started.");
        OwnedProcessRegistry.Write(PidFilePath, OwnedProcessRegistry.CaptureOwnership(_proc));
        _wroteOwnership = true;
    }

    private void DeletePidFile() => File.Delete(PidFilePath);

    private void RecoverOwnedSidecar()
    {
        var ownership = OwnedProcessRegistry.Read(PidFilePath);
        if (ownership is null) return;
        OwnedProcessRegistry.Reap(PidFilePath);
        // Do not open a database still held by a live or unidentified daemon.
        if (OwnedProcessRegistry.Inspect(ownership.Child)
            is OwnedProcessRegistry.Presence.Matching or OwnedProcessRegistry.Presence.Unknown)
            throw new IOException("The previous Backspace engine still owns this data folder. " +
                "Its process was left untouched because ownership could not be safely released.");
    }

    private void TryDeletePidFile()
    {
        try { DeletePidFile(); } catch { }
    }

    /// <summary>Parse the <c>"&lt;pid&gt;|&lt;binary-path&gt;"</c> wire
    /// format. Retained only for migration diagnostics and tests; it never
    /// authorizes termination. Returns false on any malformed input.</summary>
    internal static bool TryParsePidFile(string raw, out int pid, out string binaryPath)
    {
        pid = 0;
        binaryPath = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        var pipe = trimmed.IndexOf('|');
        if (pipe <= 0 || pipe >= trimmed.Length - 1) return false;
        if (!int.TryParse(trimmed.AsSpan(0, pipe), out var parsed) || parsed <= 0) return false;
        pid = parsed;
        binaryPath = trimmed.Substring(pipe + 1);
        return true;
    }
}
