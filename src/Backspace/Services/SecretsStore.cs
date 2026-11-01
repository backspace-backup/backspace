using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Backspace.Services.Platform;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Tiny secrets store: string-keyed values encrypted via the per-platform
/// <see cref="ISecretsEncryption"/> provider. The whole blob is a JSON
/// dict, serialised, then encrypted, then written to <c>secrets.bin</c>
/// atomically. Keys are treated as opaque refs from Destination
/// / MailSettings / MonitoringSettings.
///
/// <para>
/// Provider per platform:
/// <list type="bullet">
///   <item><b>Windows</b> — DPAPI (CurrentUser scope). Same machine +
///         user are required, but some logon contexts (including S4U before
///         sign-in) still cannot decrypt. Copying to another account or
///         machine also fails on Unprotect.</item>
///   <item><b>macOS / Linux</b> — AES-GCM with a chmod-600 keyfile next
///         to the vault. Same trip-wire applies if the keyfile and
///         ciphertext are separated.</item>
/// </list>
/// </para>
/// </summary>
public sealed class SecretsStore
{
    private static ILogger _log => AppLogger.For<SecretsStore>();
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ISecretsEncryption _crypto;
    private readonly ISecretsBackend? _backend;

    public SecretsStore() : this(SelectProvider())
    {
#if WINDOWS
        var installation = AppContext.BaseDirectory.TrimEnd('\\');
        if (Platform.Windows.ServiceOwnerSecrets.Applies(installation, AppPaths.ConfigRoot))
            _backend = new Platform.Windows.ServiceOwnerSecrets(installation, new SecretsStore(_crypto, _path));
#endif
    }

    /// <summary>For tests / DI: inject a custom encryption provider
    /// (e.g. an in-memory pass-through).</summary>
    public SecretsStore(ISecretsEncryption crypto) : this(crypto, AppPaths.SecretsFile) { }

    /// <summary>The service uses its own protected path and encryption identity.
    /// This path is chosen by trusted installation code, never an IPC request.</summary>
    internal SecretsStore(ISecretsEncryption crypto, string path)
    {
        _crypto = crypto;
        _path = Path.GetFullPath(path);
    }

    /// <summary>Used after a verified service handoff. There is deliberately
    /// no fallback path or local encryption provider on this instance.</summary>
    internal SecretsStore(ISecretsBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _path = "";
        _crypto = null!;
    }

    /// <summary>Read-only migration snapshot. A failed decrypt must never be
    /// mistaken for an empty vault or authorize changing the active provider.</summary>
    internal Dictionary<string, string> ExportForMigration()
    {
        RequireLocalVault();
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var map = Load();
            if (LoadErrorMessage is not null)
                throw new CryptographicException("The original vault could not be unlocked. Migration was not started.");
            return new Dictionary<string, string>(map);
        }
    }

    /// <summary>Hold the original vault's cross-process transaction through
    /// transfer, target verification and activation. The callback must be
    /// synchronous and must not activate a target it has not verified. Keeping
    /// the lease prevents a concurrent token refresh from silently becoming
    /// stale between the snapshot and the provider switch.</summary>
    internal T WithMigrationSnapshot<T>(Func<IReadOnlyDictionary<string, string>, T> transferVerifyAndActivate, bool requireExisting = false)
    {
        RequireLocalVault();
        ArgumentNullException.ThrowIfNull(transferVerifyAndActivate);
        // An async callback returns before its work completes and releases
        // this thread-owned file lock too soon. Reject it before invoking it.
        if (typeof(System.Threading.Tasks.Task).IsAssignableFrom(typeof(T))
            || typeof(T) == typeof(System.Threading.Tasks.ValueTask)
            || (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(System.Threading.Tasks.ValueTask<>)))
            throw new ArgumentException("Vault migration requires a synchronous callback.");
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            if (requireExisting && !File.Exists(_path))
                throw new IOException("The service vault is missing. No replacement was created.");
            return transferVerifyAndActivate(ExportForMigration());
        }
    }

    /// <summary>One-time import into a new service vault. Never merge over a
    /// refreshed token, replace an unreadable target or remove the source vault.
    /// The caller must verify the persisted target before enabling migration.</summary>
    internal void ImportIntoNewVault(IReadOnlyDictionary<string, string> secrets)
    {
        RequireLocalVault();
        ArgumentNullException.ThrowIfNull(secrets);
        var map = new Dictionary<string, string>(secrets);
        foreach (var pair in map)
            if (string.IsNullOrEmpty(pair.Key) || pair.Value is null)
                throw new ArgumentException("Every imported credential needs a reference and value.");
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            if (File.Exists(_path) || File.Exists(_path + ".bak"))
                throw new IOException("The target vault already exists. Its credentials were not changed.");
            var clear = JsonSerializer.SerializeToUtf8Bytes(map);
            byte[]? verified = null;
            try
            {
                var wrapped = _crypto.Protect(clear, Entropy);
                verified = _crypto.Unprotect(wrapped, Entropy);
                if (!CryptographicOperations.FixedTimeEquals(clear, verified))
                    throw new CryptographicException("The new vault failed its encryption check.");
                // Publish only a fully written file. A failed write leaves the
                // source authoritative and does not strand a partial target.
                var temporary = _path + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                    if (!OperatingSystem.IsWindows())
                        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using (var file = new FileStream(temporary, options))
                    {
                        file.Write(wrapped);
                        file.Flush(flushToDisk: true);
                    }
                    // Never replace a file created after the earlier check.
                    File.Move(temporary, _path, overwrite: false);
                }
                finally
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { /* A failed temporary file is not an active vault. */ }
                    catch (UnauthorizedAccessException) { /* Preserve it for inspection. */ }
                }
            }
            finally
            {
                if (verified is not null && !ReferenceEquals(verified, clear))
                    CryptographicOperations.ZeroMemory(verified);
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    /// <summary>Conditional replacement of a verified, inactive migration copy.
    /// The caller must exclude activation and retain the authoritative source.
    /// Missing or unreadable targets and changed target contents are refused.</summary>
    internal void ReplaceMigrationCopy(IReadOnlyDictionary<string, string> secrets,
        Func<IReadOnlyDictionary<string, string>, bool> matchesExpected)
    {
        WithMigrationSnapshot(map =>
        {
            if (!matchesExpected(map)) throw new IOException("The inactive credential copy changed. It was not replaced.");
            var replacement = new Dictionary<string, string>(secrets);
            if (replacement.Any(p => string.IsNullOrEmpty(p.Key) || p.Value is null))
                throw new ArgumentException("Every imported credential needs a reference and value.");
            Save(replacement);
            return true;
        }, requireExisting: true);
    }

    private static ISecretsEncryption SelectProvider()
    {
#if WINDOWS
        if (PlatformInfo.IsWindows) return new Platform.Windows.DpapiSecretsEncryption();
#endif
        // Unix: keyfile lives next to the vault under the config root.
        var keyPath = Path.Combine(AppPaths.ConfigRoot, "secrets-key.bin");
        return new Platform.Unix.FileKeySecretsEncryption(keyPath);
    }

    /// <summary>
    /// Set when an existing <c>secrets.bin</c> couldn't be decrypted on
    /// load, including when the current logon cannot unlock an otherwise
    /// valid file. The UI explains how to check access before replacing
    /// credentials. Empty when load was clean.
    /// </summary>
    private string? _loadErrorMessage;
    public string? LoadErrorMessage { get => _backend?.LoadErrorMessage ?? _loadErrorMessage; private set => _loadErrorMessage = value; }
    /// <summary>Path where unreadable bytes remain preserved: the original vault on
    /// reads, or a unique recovery copy before an explicit credential save.</summary>
    public string? PreservedCorruptPath { get; private set; }

    /// <summary>Opens the vault without exposing a credential. Startup must call
    /// this before consulting LoadErrorMessage; construction is deliberately lazy.
    /// Transient access failures propagate and never reset or rename the vault.</summary>
    public void ValidateReadable()
    {
        if (_backend is not null) { _backend.ValidateReadable(); return; }
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            _ = Load();
        }
    }

    public string Get(string key)
    {
        if (_backend is not null) return _backend.TryGet(key, out var value) ? value : "";
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var map = Load();
            return map.TryGetValue(key, out var v) ? v : "";
        }
    }

    public bool TryGet(string key, out string value)
    {
        if (_backend is not null) return _backend.TryGet(key, out value);
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var map = Load();
            return map.TryGetValue(key, out value!);
        }
    }

    public void Set(string key, string value)
    {
        if (_backend is not null) { _backend.Set(key, value); return; }
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            // Reload while holding the shared file lock; another process may have saved credentials.
            var loaded = Load(preserveBeforeReplacement: true);
            var map = new Dictionary<string, string>(loaded);
            var existed = map.ContainsKey(key);
            map[key] = value;
            Save(map);
            LoadErrorMessage = null;
            // Never log the value — keys are public refs but values are
            // tokens, passwords, OAuth refresh tokens.
            _log.Debug("Secret {Action}: ref={Key} valueLength={Len}",
                existed ? "updated" : "stored", key, value?.Length ?? 0);
        }
    }

    public void Remove(string key)
    {
        if (_backend is not null) { _backend.Remove(key); return; }
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var loaded = Load();
            if (!loaded.ContainsKey(key)) return;
            // Mutate a private copy only after a successful read.
            var map = new Dictionary<string, string>(loaded);
            map.Remove(key);
            Save(map);
            _log.Debug("Secret removed: ref={Key}", key);
        }
    }

    public string PutNew(string prefix, string value)
    {
        var @ref = $"{prefix}:{Guid.NewGuid():N}";
        Set(@ref, value);
        return @ref;
    }

    private Dictionary<string, string> Load(bool preserveBeforeReplacement = false)
    {
        byte[] wrapped;
        try { wrapped = File.ReadAllBytes(_path); }
        catch (FileNotFoundException) when (!File.Exists(_path + ".bak")) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }

        try
        {
            var clear = _crypto.Unprotect(wrapped, Entropy);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(clear)
                ?? throw new JsonException("The saved secrets must contain a credential map.");
            LoadErrorMessage = null;
            _log.Debug("Secrets vault opened: {Count} entries", map.Count);
            return map;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // A read must not hide the failure from the next process. Keep the
            // unreadable vault in place so GUI and unattended launches all see
            // the same cause. Explicitly saving re-entered credentials is the
            // existing recovery action; preserve an independent copy before it
            // may replace anything, and refuse replacement if that copy fails.
            PreservedCorruptPath = _path;
            if (preserveBeforeReplacement)
            {
                var preserved = _path + $".unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
                try
                {
                    File.Copy(_path, preserved, overwrite: false);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(preserved, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    PreservedCorruptPath = preserved;
                }
                catch (Exception copyEx)
                {
                    _log.Warning(copyEx, "Couldn't preserve unreadable secrets vault; refusing replacement");
                    throw new IOException("Saved secrets could not be read or preserved. No credentials were changed.", copyEx);
                }
            }
            LoadErrorMessage = BuildLoadErrorMessage(_crypto.DisplayName);
            _log.Error(ex, "Couldn't decrypt secrets vault; encrypted bytes preserved at {Preserved}; provider={Provider}",
                PreservedCorruptPath, _crypto.DisplayName);
            return new();
        }
    }

    private void RequireLocalVault()
    {
        if (_backend is not null)
            throw new InvalidOperationException("This vault is managed by the backup service. Local migration is unavailable.");
    }

    /// <summary>
    /// Produces the user-facing "saved tokens couldn't be decrypted"
    /// blurb. Wording acknowledges the platform-specific reason
    /// without overwhelming a non-technical reader.
    /// </summary>
    private string BuildLoadErrorMessage(string providerName)
    {
        var recovery = PlatformInfo.IsWindows
            ? "Windows may not be able to unlock them before sign-in, or from a different account or computer.\n\n" +
              "First, sign in with the Windows account that set up these backups and open Backspace again. " +
              "If that works, the scheduled task's sign-in settings need attention; your saved passwords do not need replacing.\n\n" +
              "If it still fails, check the account and computer before reconnecting destinations through Destinations → Edit."
            : "The vault is encrypted with a per-user key file kept alongside it. " +
              "If the key file was lost, replaced, or copied without its companion, the saved secrets become unreadable. " +
              "Check for the original key file before reconnecting destinations through Destinations → Edit.";
        var preserved = PreservedCorruptPath is null
            ? ""
            : $"\n\nThe original encrypted file is preserved as {Path.GetFileName(PreservedCorruptPath)}.";
        return
            "Backspace couldn't unlock its saved passwords and cloud connections. " +
            recovery +
            preserved +
            $"\nProtection: {providerName}";
    }

    private void Save(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var clear = JsonSerializer.SerializeToUtf8Bytes(map);
        var wrapped = _crypto.Protect(clear, Entropy);
        AtomicWrite(_path, wrapped);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var tmp = path + $".{Guid.NewGuid():N}.tmp";
        var bak = path + ".bak";
        // Pre-delete + FileMode.CreateNew + UnixCreateMode 0600 so the
        // .tmp lands at owner-only mode atomically rather than at the
        // default umask (typically 0644). The blob is AES-GCM
        // ciphertext today so confidentiality holds without the keyfile,
        // but matching the other secret-write call sites in the codebase
        // (rclone.conf, DropboxAuthProbe, restic password tmp) closes the
        // hardening gap a future audit would flag.
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
        var fileOpts = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            fileOpts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        // Pass a backup filename to File.Replace so a power-cut
        // between write and rename leaves a recoverable .bak. Without
        // this the prior secrets file is gone the moment Replace
        // succeeds; with this the user can rename .bak → secrets if
        // their main file is somehow corrupted post-crash.
        // On publication failure, preserve the encrypted candidate and .bak.
        // A missing primary with a backup must not open as an empty vault.
        using (var fs = new FileStream(tmp, fileOpts))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: bak);
                else File.Move(tmp, path);
                break;
            }
            catch (Exception ex) when (attempt < 5 && FileReplacementErrors.CanRetry(ex))
            {
                // Windows guarantees the original names survive these errors.
                // A failed save stays a failure; never retry an uncertain rename.
                System.Threading.Thread.Sleep(50 << attempt);
            }
        }
        // File.Replace preserves the destination's mode; on Unix the
        // very first launch wrote secrets-key.bin at whatever umask
        // was active, so reassert 0600 after every rename to converge.
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// App-specific entropy makes the ciphertext useless to other apps running
    /// as the same user — not a real secret, just hygiene. Used as the
    /// "additional authenticated data" input on Unix (AES-GCM) and as the
    /// optionalEntropy parameter on Windows (DPAPI).
    /// </summary>
    private static readonly byte[] Entropy =
    {
        0x45, 0x44, 0x42, 0x2D, 0x73, 0x65, 0x63, 0x72, 0x65, 0x74, 0x73, 0x2D, 0x76, 0x31
    };
}
