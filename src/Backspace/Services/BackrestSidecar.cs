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
/// Auth: Backrest's auth is disabled (<c>Config.auth.disabled = true</c>);
/// protection relies on the 127.0.0.1-only bind. Same trade-off
/// Backrest itself uses when the operator hasn't configured a user.
/// Known limitation: a malicious page loaded in the user's browser
/// can issue XHR / fetch to localhost and drive the daemon. On the
/// backlog: pre-seeded bcrypt credential + per-instance JWT picked up
/// by every BackrestClient call.
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
    private HttpClient? _http;
    private BackrestClient? _client;
    private int _port;

    public BackrestSidecar(string backrestBinaryPath, string resticBinaryPath, string dataDir)
    {
        BackrestBinaryPath = backrestBinaryPath;
        ResticBinaryPath = resticBinaryPath;
        DataDir = dataDir;
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
    public bool IsRunning => _proc is { HasExited: false } && _client is not null;

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
            await StartAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private readonly System.Threading.SemaphoreSlim _startGate = new(initialCount: 1, maxCount: 1);

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_proc is { HasExited: false })
            throw new InvalidOperationException("BackrestSidecar is already running.");
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

        // Orphan kill: a previous Backspace process may have crashed
        // while the sidecar was running, leaving it bound to a stale
        // localhost port and holding a network handle on a slow cloud
        // upload. The pid-file records the daemon PID + a fingerprint
        // tying it to our binary, so we can confidently kill it on
        // next launch without nuking an unrelated process that
        // happens to have inherited the same PID.
        KillOrphanedSidecar();

        // Belt-and-braces: if Backspace crashed BEFORE WritePidFile()
        // completed (i.e. between Process.Start() and the pidfile
        // write), there's no recorded pid to recover from but the
        // sidecar is still running. Sweep for any backrest.exe whose
        // main module matches OUR bundled binary path and kill it —
        // we know it isn't a user-launched Backrest because the path
        // is inside our install dir. The user's "Backrest in systray,
        // no Backspace" report likely traces back to this exact
        // window.
        KillByExeFingerprint();

        _port = AllocateEphemeralPort();
        var configPath = Path.Combine(DataDir, "config.json");
        // Orphan-auth recovery (BEFORE we boot the daemon): if the
        // user deleted sidecar-auth.txt but kept config.json, the
        // daemon would come up bcrypt-protected with a password we
        // no longer hold and every RPC would 401. Detect that on
        // disk, strip the auth section back to seed state, and let
        // WriteSeedConfig + EnsureAuthAsync do a fresh bootstrap as
        // if the user had nuked the whole config dir. Critically:
        // this happens BEFORE Process.Start, so Backrest only ever
        // reads the already-recovered config — no re-entrant restart
        // logic needed.
        if (Directory.Exists(DataDir)
            && ConfigHasAuthUsers(configPath)
            && !File.Exists(AuthFilePath))
        {
            _log.Warning(
                "{File} is missing but {Config} still has auth users. " +
                "Recovering: stripping auth from config so we can re-bootstrap a fresh password on this launch.",
                AuthFilePath, configPath);
            try { StripAuthFromConfigOnDisk(configPath); }
            catch (Exception ex)
            {
                _log.Error(ex,
                    "Couldn't strip stale auth state from {Config}. The daemon will come up auth-gated and every RPC will 401. " +
                    "Delete {Config} (or the whole {Dir} folder) to reset; backup data at the destination is unaffected.",
                    configPath, configPath, DataDir);
            }
        }
        WriteSeedConfig(configPath);

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
            try { _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { DeletePidFile(); } catch { /* best-effort */ }
            _proc.Dispose();
            _proc = null;
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
        try { WritePidFile(_proc.Id, BackrestBinaryPath); }
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
        _client = new BackrestClient(_http);

        await WaitForReadyAsync(_client, ct).ConfigureAwait(false);
        await EnsureAuthAsync(_client, ct).ConfigureAwait(false);
    }

    /// <summary>One-shot bcrypt auth bootstrap.
    ///
    /// First launch ever (or after the user nuked the config) — the
    /// seed config wrote <c>auth.disabled = true</c>, so the daemon
    /// answers RPCs without a bearer. We use that window to:
    /// generate a random per-instance password, ask the daemon to
    /// bcrypt-hash it via <c>HashPasswordAsync</c>, splice the
    /// resulting user into the config via SetConfig, and persist
    /// the password to a sidecar-local file next to the data dir.
    ///
    /// Every subsequent launch — the seed-config writer preserves
    /// the existing config, so auth.users is already populated and
    /// the password file is on disk. We skip the setup leg and go
    /// straight to Login.
    ///
    /// Either way, we end with a JWT on the client. From this point
    /// on every RPC carries the bearer token; a malicious page in
    /// the user's browser can no longer drive the daemon by XHR'ing
    /// the loopback port (which was the open hole the localhost-only
    /// bind alone didn't close).</summary>
    private async Task EnsureAuthAsync(BackrestClient client, CancellationToken ct)
    {
        var configPath = Path.Combine(DataDir, "config.json");
        var alreadyConfigured = ConfigHasAuthUsers(configPath);

        // Why is the password kept in a SEPARATE file
        // (<see cref="AuthFilePath"/>) and not inlined into
        // config.json? Two reasons:
        //   1. config.json is OWNED by Backrest. Every time Backspace
        //      (or the user, via Backrest's own admin UI) calls
        //      /v1.Backrest/SetConfig, Backrest re-serializes the
        //      protobuf-defined Config struct back to disk — any
        //      top-level field NOT defined in Backrest's proto is
        //      silently dropped. Inlining the plaintext password
        //      there would lose it the next time anything wrote
        //      config.
        //   2. Keeping our per-instance secret in its own file lets
        //      us scope file-system ACLs (current user only) without
        //      having to argue about who owns config.json.
        //
        // Hard recovery: if you DELETE this file, the daemon is now
        // bcrypt-protected with a password only YOU know — but you
        // don't know it because we generated it randomly. The error
        // path below logs a clear actionable line; the fix is to also
        // delete config.json next to it and let Backspace re-seed
        // both files on next launch. (No data loss — the restic repo
        // at the destination is encrypted with a separate password
        // that is independent of this one.)
        string password;
        if (!alreadyConfigured)
        {
            password = GenerateRandomPassword();
            string hash;
            try
            {
                hash = await client.HashPasswordAsync(password, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Backrest HashPassword failed; leaving auth disabled and falling back to localhost-only protection");
                return;
            }

            BackrestConfig cfg;
            try
            {
                cfg = await client.CallAsync<EmptyRequest, BackrestConfig>(
                    "/v1.Backrest/GetConfig", new EmptyRequest(), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "GetConfig failed during auth bootstrap; leaving auth disabled");
                return;
            }

            cfg.Auth ??= new BackrestAuth();
            cfg.Auth.Disabled = false;
            cfg.Auth.Users.Add(new BackrestAuthUser
            {
                Name = AuthUserName,
                PasswordBcrypt = hash,
            });

            try
            {
                await client.CallAsync<BackrestConfig, BackrestConfig>(
                    "/v1.Backrest/SetConfig", cfg, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "SetConfig failed during auth bootstrap; leaving auth disabled");
                return;
            }

            try { WriteAuthFile(password); }
            catch (Exception ex)
            {
                _log.Warning(ex, "Couldn't persist sidecar password — next launch will have to re-seed");
                // Don't return — we still try to login below using the
                // password we have in memory for THIS session.
            }
            _log.Information("Backrest sidecar auth bootstrapped (bcrypt user '{User}' added)", AuthUserName);
        }
        else
        {
            password = ReadAuthFile();
            if (password.Length == 0)
            {
                _log.Error(
                    "Backrest config at {Config} has auth users but its companion " +
                    "password file at {AuthFile} is missing. The daemon will reject " +
                    "every RPC with 401 until this is fixed. To recover: DELETE " +
                    "{Config} (Backspace will re-seed both files cleanly on the next " +
                    "launch). The backup data at the destination is encrypted with " +
                    "a different password and is unaffected.",
                    configPath, AuthFilePath, configPath);
                return;
            }
        }

        string jwt;
        try
        {
            jwt = await client.LoginAsync(AuthUserName, password, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Backrest Login failed; subsequent RPCs will hit 401. Reset by deleting {Dir}.", DataDir);
            return;
        }
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

    /// <summary>Rewrite the on-disk config so the auth section is back
    /// to the seed-state (<c>{disabled: true}</c>) — every other key
    /// is preserved verbatim. Called from the orphan-auth recovery
    /// path BEFORE the daemon starts, so we have exclusive access
    /// to the file. Bumps the <c>modno</c> field if Backrest tracks
    /// one, since rewriting fields under Backrest's nose without
    /// touching modno can confuse Backrest's "concurrent edit"
    /// validation on the next SetConfig.</summary>
    internal static void StripAuthFromConfigOnDisk(string configPath)
    {
        var text = File.ReadAllText(configPath);
        using var parsed = JsonDocument.Parse(text);
        var rebuilt = new System.Text.Json.Nodes.JsonObject();
        foreach (var prop in parsed.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("auth"))
            {
                rebuilt[prop.Name] = new System.Text.Json.Nodes.JsonObject { ["disabled"] = true };
            }
            else
            {
                rebuilt[prop.Name] = System.Text.Json.Nodes.JsonNode.Parse(prop.Value.GetRawText());
            }
        }
        if (!rebuilt.ContainsKey("auth"))
            rebuilt["auth"] = new System.Text.Json.Nodes.JsonObject { ["disabled"] = true };
        File.WriteAllText(configPath, rebuilt.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }


    public async Task StopAsync(TimeSpan? gracePeriod = null, CancellationToken ct = default)
    {
        if (_proc is null) return;
        var grace = gracePeriod ?? Timing.StopGracePeriod;

        try
        {
            if (!_proc.HasExited)
            {
                _log.Information("Stopping Backrest sidecar (pid={Pid})", _proc.Id);
                try { _proc.CloseMainWindow(); } catch { /* headless */ }
                if (!await WaitForExitAsync(_proc, grace, ct).ConfigureAwait(false))
                {
                    _log.Warning("Backrest did not exit within {Grace}s, forcing kill", grace.TotalSeconds);
                    try { _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                }
                // Best-effort cleanup; if we crash before reaching this
                // the next StartAsync's KillOrphanedSidecar handles the
                // leftover.
                try { DeletePidFile(); } catch { /* ignore */ }
            }
        }
        finally
        {
            _http?.Dispose();
            _proc.Dispose();
            _proc = null;
            _http = null;
            _client = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
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

    /// <summary>Writes a minimal Backrest config.json that opts out of
    /// auth (relying on the localhost bind) and sets the instance name
    /// to "Backspace". Backrest will read this on startup. We use the
    /// proto field names directly so the file shape matches what
    /// backrest --config-file expects.</summary>
    internal static void WriteSeedConfig(string configPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        // Idempotent-with-version-guard: preserve a real Backrest
        // config from a previous launch (portability), but overwrite
        // when its schema version is newer than what our bundled
        // Backrest binary understands. The bundled Backrest 1.13.0
        // expects version 6; a future binary emitting v7 sitting on
        // disk would be downgraded silently and our typed-model
        // round-trip (GetConfig → mutate → SetConfig) might lose
        // fields the newer schema relies on. Overwriting in that
        // case re-anchors at the bundled binary's expected version;
        // the only thing lost is Backrest's auto-generated
        // multi-host sync identity, which Backrest regenerates on
        // next start and which Backspace doesn't use anyway.
        if (File.Exists(configPath))
        {
            try
            {
                var existing = File.ReadAllText(configPath);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    var existingVersion = TryExtractConfigVersion(existing);
                    if (existingVersion is int ev && ev > SeedConfigVersion)
                    {
                        // Newer-than-bundled schema. Fall through
                        // and overwrite with our SeedConfigVersion
                        // seed.
                    }
                    else if (existing.Contains("\"version\""))
                    {
                        // Compatible existing config — preserve it.
                        return;
                    }
                }
            }
            catch
            {
                // Couldn't read the existing file (lock / permission /
                // garbage) — fall through and overwrite so the daemon
                // doesn't choke on whatever's there.
            }
        }

        var cfg = new BackrestConfig
        {
            Modno = 0,
            Version = SeedConfigVersion,
            Instance = "backspace",
            Auth = new BackrestAuth { Disabled = true },
        };

        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        // Atomic write — two parents racing each other (the GUI plus a
        // simultaneous `--run` scheduled task) would otherwise leave a
        // half-written config.json that the daemon refuses to read.
        // Write-to-tmp + rename closes that window: each writer lands a
        // complete file, and the rename is OS-atomic on Windows
        // (File.Replace) and Unix (rename(2)).
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
    /// whose migrations target a newer version. Verified by booting
    /// the bundled <c>backrest --version</c> against a fresh empty
    /// config and reading the "migrating config from version 0 to N"
    /// log line; N is the value to use here.
    ///
    /// Bundled binary at the time of writing: Backrest 1.13.0
    /// (commit a389bbc7) → version 6. Earlier code shipped
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
        catch { /* malformed json — caller falls back to overwrite */ }
        return null;
    }

    private static async Task WaitForReadyAsync(BackrestClient client, CancellationToken ct)
    {
        // Poll GetConfig — the cheapest unary RPC — until it succeeds.
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

    // ---- pid file + orphan-safe kill ----
    //
    // The pid file lives at <DataDir>/sidecar.pid as a single line:
    //   "<pid>|<binary-path>"
    //
    // Killing on PID alone is unsafe: a process with the same PID may
    // have been recycled to an unrelated program. We compare the
    // recorded binary path to the live process's main module path to
    // confirm "yes, this PID is one of our backrest daemons" before
    // sending the kill. The path comparison tolerates Windows path
    // casing.

    /// <summary>Path of the pid-file. Public for testability; callers
    /// outside this class shouldn't touch it.</summary>
    internal string PidFilePath => Path.Combine(DataDir, "sidecar.pid");

    private void WritePidFile(int pid, string binaryPath)
    {
        Directory.CreateDirectory(DataDir);
        // pipe-separated is enough — neither field contains a pipe in any realistic case.
        // Atomic write (tmp + Move/Replace) so a concurrent reader
        // (e.g. KillOrphanedSidecar in another process racing the
        // first launch) never observes a torn half-written pid-file
        // and silently no-ops because the parsed pid is bogus.
        var path = PidFilePath;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, $"{pid}|{binaryPath}");
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
            else File.Move(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    private void DeletePidFile()
    {
        if (File.Exists(PidFilePath)) File.Delete(PidFilePath);
    }

    /// <summary>Read the pid file (if any) and kill the recorded process
    /// if and only if its main module path matches our backrest binary.
    /// Best-effort: every failure is logged at debug and swallowed so a
    /// stale or unreadable pid-file never blocks startup.</summary>
    private void KillOrphanedSidecar()
    {
        string? raw;
        try
        {
            if (!File.Exists(PidFilePath)) return;
            raw = File.ReadAllText(PidFilePath);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Couldn't read sidecar pid-file at {Path}", PidFilePath);
            return;
        }

        if (!TryParsePidFile(raw, out var pid, out var recordedBinary))
        {
            _log.Debug("Sidecar pid-file at {Path} is malformed; ignoring", PidFilePath);
            TryDeletePidFile();
            return;
        }

        Process? p = null;
        try { p = Process.GetProcessById(pid); }
        catch { /* PID isn't live — nothing to kill */ }

        if (p is null)
        {
            TryDeletePidFile();
            return;
        }

        // Confirm the live process is actually OUR backrest daemon and
        // not an unrelated program that inherited the PID. MainModule
        // throws on Windows if the target process is elevated and we're
        // not; swallow and conservatively skip the kill in that case.
        string? liveBinary = null;
        try { liveBinary = p.MainModule?.FileName; }
        catch { /* access denied / cross-arch / process died mid-read */ }

        if (string.IsNullOrEmpty(liveBinary) ||
            !string.Equals(liveBinary, recordedBinary, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warning(
                "Found pid-file PID={Pid} recorded for {Recorded}, but the live process is {Live}; leaving alone",
                pid, recordedBinary, liveBinary ?? "<unknown>");
            TryDeletePidFile();
            return;
        }

        try
        {
            _log.Information("Killing orphaned Backrest sidecar (pid={Pid}, binary={Binary})", pid, recordedBinary);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(Timing.OrphanKillReapMs);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to kill orphaned Backrest sidecar (pid={Pid})", pid);
        }
        finally
        {
            TryDeletePidFile();
            try { p.Dispose(); } catch { /* ignore */ }
        }
    }

    private void TryDeletePidFile()
    {
        try { DeletePidFile(); } catch { /* swallow */ }
    }

    /// <summary>Fingerprint-based orphan scan: every live backrest.exe
    /// process whose binary sits next to a Backspace.exe (any of our
    /// builds: prod install, dev build, test build) gets killed.
    /// Belt-and-braces against the pidfile path: that one fails when
    /// Backspace crashes between Process.Start() and WritePidFile(),
    /// AND it fails across DIFFERENT Backspace builds — a prod Backspace
    /// launching can't recognise a backrest left over from a `dotnet
    /// test` run because the binary paths don't match. The sibling-
    /// Backspace.exe heuristic catches both cases without false-killing
    /// a Backrest that some unrelated tool happens to ship.</summary>
    private void KillByExeFingerprint()
    {
        Process[] candidates;
        try
        {
            var name = Path.GetFileNameWithoutExtension(BackrestBinaryPath);
            candidates = Process.GetProcessesByName(name);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "KillByExeFingerprint: GetProcessesByName threw; skipping");
            return;
        }

        var me = Environment.ProcessId;
        foreach (var p in candidates)
        {
            string? path = null;
            try
            {
                if (p.Id == me) continue;
                path = p.MainModule?.FileName;
            }
            catch { /* access denied / elevated / died mid-read */ }

            if (string.IsNullOrEmpty(path) || !IsBackspaceBundledBackrest(path))
            {
                try { p.Dispose(); } catch { /* ignore */ }
                continue;
            }

            try
            {
                _log.Warning(
                    "Killing fingerprint-matched orphan Backrest sidecar (pid={Pid}, binary={Binary}). " +
                    "A previous Backspace process (this install or another) likely crashed before " +
                    "its pid-file was written, or its Job Object teardown didn't reach this child.",
                    p.Id, path);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(Timing.OrphanKillReapMs);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "KillByExeFingerprint: kill failed for pid {Pid}", SafePid(p));
            }
            finally
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>True iff <paramref name="backrestPath"/> sits next to a
    /// Backspace.exe (or Backspace.Tests.dll for the test build). The
    /// directory layout for every shape we ship — prod install, dev
    /// build, test build — colocates backrest.exe with the host
    /// executable, so this is a robust "is this OUR backrest" test
    /// that doesn't depend on the EXACT install path.</summary>
    private static bool IsBackspaceBundledBackrest(string backrestPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(backrestPath);
            if (string.IsNullOrEmpty(dir)) return false;
            // Prod / dev build → Backspace.exe alongside.
            if (File.Exists(Path.Combine(dir, "Backspace.exe"))) return true;
            // Test build → Backspace.Tests.dll alongside (host is testhost.exe,
            // not Backspace.exe, but the bundled binaries still land here).
            if (File.Exists(Path.Combine(dir, "Backspace.Tests.dll"))) return true;
            // Backspace.dll alongside catches the .NET runtime layout where
            // the entry-point exe is a stub and the real assembly is a DLL.
            if (File.Exists(Path.Combine(dir, "Backspace.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    private static int SafePid(Process p) { try { return p.Id; } catch { return -1; } }

    /// <summary>Parse the <c>"&lt;pid&gt;|&lt;binary-path&gt;"</c> wire
    /// format. Exposed for testing; the orphan-kill path uses it
    /// internally. Returns <c>false</c> on any malformed input.</summary>
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
