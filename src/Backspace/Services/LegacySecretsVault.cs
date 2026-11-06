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
/// The credential file used before the portable vault (U153): <c>secrets.bin</c>,
/// a JSON dictionary protected by Windows for one account on one computer
/// (DPAPI, CurrentUser; a key file beside it on macOS and Linux). It cannot
/// travel to another computer.
///
/// <para>Kept for three readers only: the one-time conversion to
/// <see cref="SecretsStore"/>'s <c>vault.bin</c>, the version-3 service's
/// earlier vault (returned over its return pipe during that conversion), and
/// the version-1 and version-2 services, which still use it until their
/// upgrade. Nothing new is written here.</para>
///
/// <para>Guarantees kept from its time as the main store: every
/// read-modify-write holds the cross-process <see cref="StateFileLock"/> and
/// reloads inside it; writes are atomic with a <c>.bak</c>; an unreadable file
/// is preserved (<c>.unreadable-*</c>) and never mistaken for an empty vault;
/// a missing file with a <c>.bak</c> beside it is refused.</para>
/// </summary>
internal sealed class LegacySecretsVault : ISecretsBackend
{
    private static ILogger _log => AppLogger.For<LegacySecretsVault>();
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ISecretsEncryption _crypto;

    /// <summary>The desktop's earlier vault, <c>Backspace.config\secrets.bin</c>.</summary>
    internal LegacySecretsVault() : this(SelectProvider(), AppPaths.LegacySecretsFile) { }

    /// <summary>The service uses its own path and encryption identity, chosen
    /// by trusted installation code, never an IPC request.</summary>
    internal LegacySecretsVault(ISecretsEncryption crypto, string path)
    {
        _crypto = crypto;
        _path = Path.GetFullPath(path);
    }

    internal string FilePath => _path;

    /// <summary>Read-only snapshot. A failed decrypt must never be mistaken
    /// for an empty vault or authorize changing the active provider.</summary>
    internal Dictionary<string, string> ExportForMigration()
    {
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var map = Load();
            if (LoadErrorMessage is not null)
                throw new CryptographicException("The original vault could not be unlocked. Migration was not started.");
            return new Dictionary<string, string>(map);
        }
    }

    /// <summary>Hold the vault's cross-process transaction through transfer,
    /// verification and activation. The callback must be synchronous.</summary>
    internal T WithMigrationSnapshot<T>(Func<IReadOnlyDictionary<string, string>, T> transferVerifyAndActivate, bool requireExisting = false)
    {
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

    /// <summary>One-time import into a new vault. Never merge over a refreshed
    /// token, replace an unreadable target or remove the source vault.</summary>
    internal void ImportIntoNewVault(IReadOnlyDictionary<string, string> secrets)
    {
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
                    FileAccessRetry.Run(() => File.Move(temporary, _path, overwrite: false), _path);
                }
                finally
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
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

    /// <summary>Conditional replacement of a verified, inactive migration copy.</summary>
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

    internal static ISecretsEncryption SelectProvider()
    {
#if WINDOWS
        if (PlatformInfo.IsWindows) return new Platform.Windows.DpapiSecretsEncryption();
#endif
        var keyPath = Path.Combine(AppPaths.ConfigRoot, "secrets-key.bin");
        return new Platform.Unix.FileKeySecretsEncryption(keyPath);
    }

    /// <summary>Set when an existing file couldn't be decrypted on load.</summary>
    public string? LoadErrorMessage { get; private set; }
    public string? PreservedCorruptPath { get; private set; }

    public void ValidateReadable()
    {
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            _ = Load();
        }
    }

    public string Get(string key) => TryGet(key, out var value) ? value : "";

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
            var loaded = Load(preserveBeforeReplacement: true);
            var map = new Dictionary<string, string>(loaded) { [key] = value };
            Save(map);
            LoadErrorMessage = null;
            _log.Debug("Secret stored in the earlier vault: ref={Key} valueLength={Len}", key, value?.Length ?? 0);
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var loaded = Load();
            if (!loaded.ContainsKey(key)) return;
            var map = new Dictionary<string, string>(loaded);
            map.Remove(key);
            Save(map);
        }
    }

    private Dictionary<string, string> Load(bool preserveBeforeReplacement = false)
    {
        byte[] wrapped;
        // Another program holding the vault (Dropbox, a virus scanner) is
        // waited out, as long as it takes (U135).
        try { wrapped = FileAccessRetry.Get(() => File.ReadAllBytes(_path), _path); }
        catch (FileNotFoundException) when (!File.Exists(_path + ".bak")) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }

        try
        {
            var clear = _crypto.Unprotect(wrapped, Entropy);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(clear)
                ?? throw new JsonException("The saved secrets must contain a credential map.");
            LoadErrorMessage = null;
            return map;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
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
            LoadErrorMessage = "Backspace couldn't unlock its earlier saved passwords (" + Path.GetFileName(_path) + "). " +
                "Windows may not be able to unlock them before sign-in, or from a different account or computer." +
                (PreservedCorruptPath is null ? "" : $"\n\nThe original encrypted file is preserved as {Path.GetFileName(PreservedCorruptPath)}.") +
                $"\nProtection: {_crypto.DisplayName}";
            _log.Error(ex, "Couldn't decrypt the earlier secrets vault; encrypted bytes preserved at {Preserved}; provider={Provider}",
                PreservedCorruptPath, _crypto.DisplayName);
            return new();
        }
    }

    private void Save(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var clear = JsonSerializer.SerializeToUtf8Bytes(map);
        var wrapped = _crypto.Protect(clear, Entropy);
        SecretsStore.AtomicWrite(_path, wrapped);
    }

    /// <summary>App-specific entropy (DPAPI optionalEntropy; AES-GCM AAD on Unix).</summary>
    private static readonly byte[] Entropy =
    {
        0x45, 0x44, 0x42, 0x2D, 0x73, 0x65, 0x63, 0x72, 0x65, 0x74, 0x73, 0x2D, 0x76, 0x31
    };
}
