using System;
using System.Collections.Generic;
using System.IO;
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
///         user can decrypt; copying secrets.bin to another account
///         or another machine fails on Unprotect, exactly the trip-wire
///         this class is designed for.</item>
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
    private readonly string _path = AppPaths.SecretsFile;
    private readonly ISecretsEncryption _crypto;

    public SecretsStore() : this(SelectProvider()) { }

    /// <summary>For tests / DI: inject a custom encryption provider
    /// (e.g. an in-memory pass-through).</summary>
    public SecretsStore(ISecretsEncryption crypto) => _crypto = crypto;

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
    /// load — typically because the file (or, on Unix, the keyfile) was
    /// copied without its counterpart. The UI consults this on startup
    /// to warn the user that previously-saved tokens/passwords are
    /// unavailable and that they need to re-enter them. Empty when
    /// load was clean.
    /// </summary>
    public string? LoadErrorMessage { get; private set; }
    /// <summary>Path where unreadable bytes remain preserved: the original vault on
    /// reads, or a unique recovery copy before an explicit credential save.</summary>
    public string? PreservedCorruptPath { get; private set; }

    /// <summary>Opens the vault without exposing a credential. Startup must call
    /// this before consulting LoadErrorMessage; construction is deliberately lazy.
    /// Transient access failures propagate and never reset or rename the vault.</summary>
    public void ValidateReadable()
    {
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            _ = Load();
        }
    }

    public string Get(string key)
    {
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var map = Load();
            return map.TryGetValue(key, out var v) ? v : "";
        }
    }

    public bool TryGet(string key, out string value)
    {
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var map = Load();
            return map.TryGetValue(key, out value!);
        }
    }

    public void Set(string key, string value)
    {
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
        catch (FileNotFoundException) { return new(); }
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

    /// <summary>
    /// Produces the user-facing "saved tokens couldn't be decrypted"
    /// blurb. Wording acknowledges the platform-specific reason
    /// without overwhelming a non-technical reader.
    /// </summary>
    private string BuildLoadErrorMessage(string providerName)
    {
        var why = PlatformInfo.IsWindows
            ? "DPAPI (the Windows secret-protection API) ties encrypted blobs to a specific account+machine pair, " +
              "so a config copied from another user account or another computer won't open here."
            : "The vault is encrypted with a per-user key file kept alongside it. " +
              "If the key file was lost, replaced, or copied without its companion, the saved secrets become unreadable.";
        var preserved = PreservedCorruptPath is null
            ? ""
            : $" The unreadable encrypted file remains preserved as {Path.GetFileName(PreservedCorruptPath)}.";
        return
            "Saved cloud tokens and storage passwords couldn't be decrypted on this machine. " +
            why +
            " Re-enter the tokens/passwords for each destination via Destinations → Edit." +
            preserved +
            $" (provider: {providerName})";
    }

    private void Save(Dictionary<string, string> map)
    {
        AppPaths.EnsureAll();
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
        try
        {
            using (var fs = new FileStream(tmp, fileOpts))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: bak);
            else File.Move(tmp, path);
        }
        finally { try { File.Delete(tmp); } catch { } }
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
