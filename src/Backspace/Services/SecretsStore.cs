using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Serilog;

namespace Backspace.Services;

/// <summary>Where the saved passwords stand on this computer (U153).</summary>
public enum VaultState
{
    /// <summary>No vault yet: nothing was ever saved. Reading finds nothing;
    /// saving needs a password chosen first.</summary>
    NoVault,
    /// <summary>Open on this computer.</summary>
    Unlocked,
    /// <summary>The vault exists but this computer has no key for it yet: the
    /// password is needed once.</summary>
    Locked,
    /// <summary>The file is damaged, missing beside its backup, or made by a
    /// newer Backspace. It is preserved and never overwritten.</summary>
    Unreadable,
}

/// <summary>A credential was read or saved while the vault was not open.
/// Nothing was changed. The UI asks for the password (<c>VaultGate</c>);
/// background work reports it.</summary>
public sealed class VaultLockedException(VaultState state, string? detail = null) : InvalidOperationException(MessageFor(state, detail))
{
    public VaultState State { get; } = state;

    internal const string LockedMessage =
        "Your saved passwords are locked on this computer. Open Backspace on this computer and enter your password once.";

    internal const string ConversionMessage =
        "Your saved passwords need one more step on this computer: open Backspace and choose a password for them. Nothing was changed.";

    private static string MessageFor(VaultState state, string? detail) => detail ?? state switch
    {
        VaultState.NoVault => "Backspace has no saved passwords yet. Choose a password for them first; nothing was saved.",
        VaultState.Locked => LockedMessage,
        _ => "Backspace can't open its saved passwords. They were kept as they are; nothing was changed.",
    };
}

/// <summary>
/// The saved credentials: storage passwords, cloud sign-ins, API keys
/// (U153). One file, <c>Backspace.config\vault.bin</c> (<see cref="VaultFile"/>),
/// travels with the portable folder and is opened by both the app and the
/// background service. Keys are opaque references from Destination,
/// MailSettings and MonitoringSettings; values are never logged.
///
/// <para>The file is encrypted with a vault key that only the person's
/// password unwraps. Each computer keeps its own copy of that key
/// (<see cref="VaultKeyCache"/>), so the password is asked once per computer.
/// The store never prompts: reading or saving while <see cref="VaultState.Locked"/>
/// throws <see cref="VaultLockedException"/>, and saving needs
/// <see cref="VaultState.Unlocked"/>. The UI asks first (<c>VaultGate</c>).</para>
///
/// <para>Every read-modify-write holds the cross-process
/// <see cref="StateFileLock"/> and reloads inside it, so two processes keep
/// each other's changes. Writes are atomic with a <c>.bak</c>; a missing file
/// with a <c>.bak</c> beside it, a damaged file or a newer format is refused
/// and preserved, never replaced. Another program holding the file is waited
/// out without a limit (U135).</para>
/// </summary>
public sealed class SecretsStore
{
    private static ILogger _log => AppLogger.For<SecretsStore>();
    private readonly object _gate = new();
    private readonly string _path;
    private readonly VaultKeyCache? _keys;
    private readonly ISecretsBackend? _backend;
    private byte[]? _key;
    private string? _keyId;

    /// <summary>Raised with the vault's path after this process created, unlocked, re-keyed or
    /// replaced a vault, so the window can update its banner.</summary>
    public static event Action<string>? StateChanged;

    public SecretsStore() : this(AppPaths.VaultFile, VaultKeyCache.ForUser()) { }

    /// <summary>An explicit vault and key cache: the background service opens
    /// the portable folder's vault with its own key copy.</summary>
    internal SecretsStore(string vaultPath, VaultKeyCache keys)
    {
        _path = Path.GetFullPath(vaultPath);
        _keys = keys;
    }

    /// <summary>Services set up by Backspace 1.0 (versions 1 and 2) keep their
    /// own earlier vault until their upgrade.</summary>
    internal SecretsStore(ISecretsBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _path = "";
    }

    internal string FilePath => _path;

    /// <summary>The state found by the last operation.</summary>
    public VaultState State { get; private set; } = VaultState.NoVault;
    /// <summary>The vault's random identity, once one exists and is readable.</summary>
    public string? VaultId { get; private set; }
    /// <summary>The vault was created without a password: its contents are only obscured.</summary>
    public bool NoPassword { get; private set; }
    /// <summary>A plain explanation when the vault can't be opened at all
    /// (<see cref="VaultState.Unreadable"/>); null otherwise.</summary>
    public string? LoadErrorMessage { get; private set; }
    /// <summary>Where unreadable bytes stay preserved.</summary>
    public string? PreservedCorruptPath { get; private set; }
    /// <summary>The unreadable vault was made by a newer Backspace.</summary>
    public bool MadeByNewerVersion { get; private set; }
    /// <summary>No vault yet, but the earlier per-computer credential file
    /// (secrets.bin) is beside it: the one-time conversion has not run.
    /// Reading and saving refuse until it has, so a run reports that instead
    /// of a missing password (U153).</summary>
    public bool ConversionPending { get; private set; }

    private sealed record Opened(VaultState State, VaultFile.Parsed? Parsed, Dictionary<string, string>? Map, byte[]? Key);

    /// <summary>Re-reads the vault and reports its state, without prompting
    /// or exposing a credential. An empty-password vault opens by itself.</summary>
    public VaultState Refresh()
    {
        if (_backend is not null) { _backend.ValidateReadable(); return State = VaultState.Unlocked; }
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            return Open().State;
        }
    }

    /// <summary>Startup check: opens the vault without exposing a credential.
    /// Transient access failures propagate and never change the file.</summary>
    public void ValidateReadable() => Refresh();

    public string Get(string key) => TryGet(key, out var value) ? value : "";

    /// <summary>False when nothing is saved under <paramref name="key"/>, or
    /// when there is no vault yet. Throws <see cref="VaultLockedException"/>
    /// while locked or unreadable, so callers report that instead of "missing".</summary>
    public bool TryGet(string key, out string value)
    {
        if (_backend is not null) return _backend.TryGet(key, out value);
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var opened = Open();
            value = "";
            switch (opened.State)
            {
                case VaultState.NoVault when ConversionPending: throw new VaultLockedException(VaultState.Locked, VaultLockedException.ConversionMessage);
                case VaultState.NoVault: return false;
                case VaultState.Unlocked:
                    if (!opened.Map!.TryGetValue(key, out var found)) return false;
                    value = found;
                    return true;
                default: throw new VaultLockedException(opened.State, LoadErrorMessage);
            }
        }
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        if (_backend is not null) { _backend.Set(key, value); return; }
        Mutate(map =>
        {
            var existed = map.ContainsKey(key);
            map[key] = value;
            // Never log the value: keys are public refs, values are secrets.
            _log.Debug("Secret {Action}: ref={Key} valueLength={Len}", existed ? "updated" : "stored", key, value.Length);
            return true;
        });
    }

    public void Remove(string key)
    {
        if (_backend is not null) { _backend.Remove(key); return; }
        Mutate(map =>
        {
            if (!map.Remove(key)) return false;
            _log.Debug("Secret removed: ref={Key}", key);
            return true;
        });
    }

    public string PutNew(string prefix, string value)
    {
        RequireWritable();
        var @ref = $"{prefix}:{Guid.NewGuid():N}";
        Set(@ref, value);
        return @ref;
    }

    /// <summary>Atomically replaces <paramref name="key"/>'s value with
    /// <paramref name="candidate"/> only when <paramref name="isNewer"/>
    /// (candidate, current) says so, inside one transaction: a sign-in saved
    /// meanwhile by another process is compared, never overwritten blindly.
    /// Returns true when it replaced the value.</summary>
    public bool SetIfNewer(string key, string candidate, Func<string, string, bool> isNewer)
    {
        ArgumentNullException.ThrowIfNull(isNewer);
        if (_backend is not null)
        {
            var current = _backend.TryGet(key, out var found) ? found : "";
            if (!isNewer(candidate, current)) return false;
            _backend.Set(key, candidate);
            return true;
        }
        return Mutate(map =>
        {
            var current = map.TryGetValue(key, out var found) ? found : "";
            if (!isNewer(candidate, current)) return false;
            map[key] = candidate;
            _log.Debug("Secret replaced by a newer copy: ref={Key}", key);
            return true;
        });
    }

    /// <summary>Throws unless the vault can be saved to right now.</summary>
    public void RequireWritable()
    {
        if (_backend is not null) return;
        var state = Refresh();
        if (state != VaultState.Unlocked) throw new VaultLockedException(state, ConversionPending ? VaultLockedException.ConversionMessage : LoadErrorMessage);
    }

    /// <summary>The conversion's vault (U153): written to <c>vault.bin.tmp</c>,
    /// opened again with <paramref name="password"/> and compared (count and
    /// digest) with <paramref name="map"/>, then published without replacing
    /// anything (a vault that appeared meanwhile wins and this throws).
    /// Returns the new vault's identity.</summary>
    internal string CreateVerified(string password, IReadOnlyDictionary<string, string> map)
    {
        RequireLocal();
        string id;
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            if (File.Exists(_path) || File.Exists(_path + ".bak"))
                throw new IOException("Saved passwords already exist here; nothing was replaced.");
            id = VaultFile.NewId();
            var key = RandomNumberGenerator.GetBytes(VaultFile.KeyBytes);
            var bytes = VaultFile.Compose(VaultFile.NewHeader(id, key, password ?? ""), VaultFile.SealBody(id, key, map));
            var temporary = _path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            FileAccessRetry.DeleteIfExists(temporary);
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var file = new FileStream(temporary, options)) { file.Write(bytes); file.Flush(true); }
                // Read back what is on disk, open it the way a new computer
                // would, and compare before anything else relies on it.
                var parsed = VaultFile.Parse(FileAccessRetry.Get(() => File.ReadAllBytes(temporary), temporary));
                var reopenedKey = VaultFile.Unwrap(parsed, password ?? "")
                    ?? throw new CryptographicException("The new saved-passwords file did not open with its password. Nothing was changed.");
                var reopened = VaultFile.OpenBody(parsed, reopenedKey);
                CryptographicOperations.ZeroMemory(reopenedKey);
                if (reopened is null || reopened.Count != map.Count || VaultFile.Digest(reopened) != VaultFile.Digest(map))
                    throw new CryptographicException("The new saved-passwords file did not match what was moved. Nothing was changed.");
                FileAccessRetry.Run(() => File.Move(temporary, _path, overwrite: false), _path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Remember(id, key);
            Open();
        }
        StateChanged?.Invoke(_path);
        return id;
    }

    private bool Mutate(Func<Dictionary<string, string>, bool> change)
    {
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            // Reload inside the shared lock: another process may have saved.
            var opened = Open();
            if (opened.State != VaultState.Unlocked)
                throw new VaultLockedException(opened.State, ConversionPending ? VaultLockedException.ConversionMessage : LoadErrorMessage);
            var map = new Dictionary<string, string>(opened.Map!);
            if (!change(map)) return false;
            var body = VaultFile.SealBody(opened.Parsed!.Id, opened.Key!, map);
            AtomicWrite(_path, VaultFile.Compose(opened.Parsed.HeaderBytes, body));
            return true;
        }
    }

    /// <summary>Creates the vault with <paramref name="password"/> (empty
    /// allowed) and keeps this computer's key. Refuses when one exists.</summary>
    public void Create(string password) => Create(password, new Dictionary<string, string>());

    internal void Create(string password, IReadOnlyDictionary<string, string> initial)
    {
        RequireLocal();
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            if (File.Exists(_path) || File.Exists(_path + ".bak"))
                throw new IOException("Saved passwords already exist here. Enter their password instead; nothing was replaced.");
            var id = VaultFile.NewId();
            var key = RandomNumberGenerator.GetBytes(VaultFile.KeyBytes);
            var bytes = VaultFile.Compose(VaultFile.NewHeader(id, key, password ?? ""), VaultFile.SealBody(id, key, initial));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicWrite(_path, bytes);
            Remember(id, key);
            _log.Information("Saved passwords created ({Kind}, {Count} entries)", string.IsNullOrEmpty(password) ? "no password" : "password", initial.Count);
            Open();
        }
        StateChanged?.Invoke(_path);
    }

    /// <summary>Opens the vault with <paramref name="password"/> and keeps
    /// this computer's key. False for a wrong password (nothing is written)
    /// and for a damaged vault (<see cref="State"/> is then
    /// <see cref="VaultState.Unreadable"/>; the file is preserved).</summary>
    public bool TryUnlock(string password)
    {
        RequireLocal();
        bool unlocked;
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var parsed = ReadParsed(out var state);
            State = state;
            if (parsed is null) return false;
            unlocked = UnlockWith(parsed, password ?? "");
        }
        if (unlocked) StateChanged?.Invoke(_path);
        return unlocked;
    }

    private bool UnlockWith(VaultFile.Parsed parsed, string password)
    {
        var key = VaultFile.Unwrap(parsed, password);
        if (key is null)
        {
            _log.Information("A password did not open the saved passwords; nothing was changed");
            return false;
        }
        Dictionary<string, string>? map;
        try { map = VaultFile.OpenBody(parsed, key); }
        catch (VaultFile.DamagedException ex) { CryptographicOperations.ZeroMemory(key); MarkUnreadable(ex.Message); return false; }
        if (map is null)
        {
            CryptographicOperations.ZeroMemory(key);
            MarkUnreadable("The saved passwords file is damaged: the password is right, but its contents fail their check. It was kept as it is.");
            return false;
        }
        Remember(parsed.Id, key);
        Open();
        return true;
    }

    /// <summary>A new password for the same vault: only the header is
    /// rewritten, so other computers' key copies keep working.</summary>
    public void ChangePassword(string newPassword)
    {
        RequireLocal();
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var opened = Open();
            if (opened.State != VaultState.Unlocked) throw new VaultLockedException(opened.State, LoadErrorMessage);
            var header = VaultFile.NewHeader(opened.Parsed!.Id, opened.Key!, newPassword ?? "");
            AtomicWrite(_path, VaultFile.Compose(header, opened.Parsed.Body));
            _log.Information("The saved passwords' password was changed ({Kind})", string.IsNullOrEmpty(newPassword) ? "none" : "password");
            Open();
        }
        StateChanged?.Invoke(_path);
    }

    /// <summary>Only for an unreadable vault the person chose to give up:
    /// the file is kept beside it as <c>vault.bin.unreadable-*</c> and a new,
    /// empty vault takes its place.</summary>
    public void StartOver(string password)
    {
        RequireLocal();
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            if (Open().State != VaultState.Unreadable) throw new InvalidOperationException("Only saved passwords that can't be opened can be started over.");
            var stamp = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            foreach (var suffix in new[] { "", ".bak" })
            {
                var file = _path + suffix;
                if (!File.Exists(file)) continue;
                var preserved = file + ".unreadable-" + stamp;
                FileAccessRetry.Run(() => File.Move(file, preserved, overwrite: false), file);
                if (suffix == "") PreservedCorruptPath = preserved;
            }
        }
        Create(password);
    }

    /// <summary>A copy of this computer's vault key, for the background
    /// service (sent over its authenticated pipe). Requires an open vault.</summary>
    internal (string VaultId, byte[] Key) ExportKey()
    {
        RequireLocal();
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var opened = Open();
            if (opened.State != VaultState.Unlocked) throw new VaultLockedException(opened.State, LoadErrorMessage);
            return (opened.Parsed!.Id, (byte[])opened.Key!.Clone());
        }
    }

    /// <summary>Opens the vault with a key received from the app, without a
    /// password, and keeps it for this account when it opens the vault. False
    /// (nothing written) when the key or vault identity does not match.</summary>
    internal bool TryUnlockWithKey(string vaultId, byte[] key, Func<IReadOnlyDictionary<string, string>, bool>? accept = null)
    {
        RequireLocal();
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var parsed = ReadParsed(out var state);
            State = state;
            if (parsed is null) return false;
            if (parsed.Id != vaultId) return false;
            Dictionary<string, string>? map;
            try { map = VaultFile.OpenBody(parsed, key); }
            catch (VaultFile.DamagedException) { return false; }
            if (map is null) return false;
            if (accept is not null && !accept(map)) return false;
            Remember(parsed.Id, (byte[])key.Clone());
            Open();
            return true;
        }
    }

    /// <summary>All credentials, for the one-time conversion's checks.</summary>
    internal Dictionary<string, string> Snapshot()
    {
        RequireLocal();
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            var opened = Open();
            return opened.State switch
            {
                VaultState.Unlocked => new Dictionary<string, string>(opened.Map!),
                VaultState.NoVault => new Dictionary<string, string>(),
                _ => throw new VaultLockedException(opened.State, LoadErrorMessage),
            };
        }
    }

    private void RequireLocal()
    {
        if (_backend is not null) throw new InvalidOperationException("This vault belongs to an earlier background service.");
    }

    private void Remember(string id, byte[] key)
    {
        if (_key is not null && !ReferenceEquals(_key, key)) CryptographicOperations.ZeroMemory(_key);
        _key = key;
        _keyId = id;
        try { _keys!.Write(id, key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // This process still has the key; another start asks again.
            _log.Warning(ex, "This computer's key copy could not be saved; the password will be asked again next time");
        }
    }

    private void Forget()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _keyId = null;
    }

    private void MarkUnreadable(string message, bool newer = false)
    {
        State = VaultState.Unreadable;
        MadeByNewerVersion = newer;
        PreservedCorruptPath ??= _path;
        LoadErrorMessage = message + (newer ? "" : "\n\nThe file was kept as it is (" + Path.GetFileName(_path) + "). " +
            "If you have a copy of it from before, put it back; otherwise you can start over with new saved passwords in Settings.");
        _log.Error("The saved passwords could not be opened: {Reason}", message);
    }

    /// <summary>Reads and parses the file; null with the state when there is
    /// nothing to open.</summary>
    private VaultFile.Parsed? ReadParsed(out VaultState state)
    {
        byte[] bytes;
        // Dropbox or a virus scanner holding the file is waited out (U135).
        try { bytes = FileAccessRetry.Get(() => File.ReadAllBytes(_path), _path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            if (File.Exists(_path + ".bak"))
            {
                // A replacement that stopped halfway: never start an empty vault over it.
                MarkUnreadable("The saved passwords file is missing, but its backup copy (" + Path.GetFileName(_path) + ".bak) is there. " +
                    "Rename the backup copy to " + Path.GetFileName(_path) + " to get them back; Backspace won't replace it.");
                PreservedCorruptPath = _path + ".bak";
                state = VaultState.Unreadable;
                return null;
            }
            state = VaultState.NoVault;
            VaultId = null; NoPassword = false; LoadErrorMessage = null; MadeByNewerVersion = false;
            var legacy = Path.Combine(Path.GetDirectoryName(_path)!, "secrets.bin");
            ConversionPending = File.Exists(legacy) || File.Exists(legacy + ".bak");
            return null;
        }
        ConversionPending = false;
        try
        {
            var parsed = VaultFile.Parse(bytes);
            VaultId = parsed.Id;
            NoPassword = parsed.Header.NoPassword;
            state = VaultState.Locked;
            return parsed;
        }
        catch (VaultFile.DamagedException ex)
        {
            MarkUnreadable(ex.Message, ex.Newer);
            state = VaultState.Unreadable;
            return null;
        }
    }

    /// <summary>Reads the vault and opens it with this computer's key. Must
    /// run inside the vault's file lock.</summary>
    private Opened Open()
    {
        var parsed = ReadParsed(out var state);
        if (parsed is null) { State = state; return new Opened(state, null, null, null); }
        var fromMemory = _keyId == parsed.Id && _key is not null;
        var key = fromMemory ? _key : _keys!.TryRead(parsed.Id);
        if (key is null && parsed.Header.NoPassword)
        {
            // No password was chosen: open it here as well, without asking.
            if (UnlockWith(parsed, "")) return Open();
            if (State == VaultState.Unreadable) return new Opened(State, parsed, null, null);
        }
        if (key is null)
        {
            State = VaultState.Locked; LoadErrorMessage = null;
            return new Opened(VaultState.Locked, parsed, null, null);
        }
        Dictionary<string, string>? map;
        try { map = VaultFile.OpenBody(parsed, key); }
        catch (VaultFile.DamagedException ex)
        {
            MarkUnreadable(ex.Message);
            return new Opened(VaultState.Unreadable, parsed, null, null);
        }
        if (map is null)
        {
            // This computer's key no longer opens the file: the vault was
            // replaced. Forget the key; the password opens the new one.
            _log.Information("This computer's key for the saved passwords is out of date; it was removed");
            if (fromMemory) Forget(); else CryptographicOperations.ZeroMemory(key);
            _keys!.Delete(parsed.Id);
            State = VaultState.Locked; LoadErrorMessage = null;
            return new Opened(VaultState.Locked, parsed, null, null);
        }
        if (!fromMemory) { if (_key is not null) CryptographicOperations.ZeroMemory(_key); _key = key; _keyId = parsed.Id; }
        State = VaultState.Unlocked; LoadErrorMessage = null; MadeByNewerVersion = false;
        return new Opened(VaultState.Unlocked, parsed, map, _key);
    }

    /// <summary>Atomic replacement with a <c>.bak</c> of the previous file.</summary>
    internal static void AtomicWrite(string path, byte[] bytes)
    {
        var tmp = path + $".{Guid.NewGuid():N}.tmp";
        var bak = path + ".bak";
        var fileOpts = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            fileOpts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, fileOpts))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        // Windows guarantees the original names survive the errors retried
        // here; an uncertain rename (1176/1177) is never retried. No limit
        // (U135): the vault waits until another program lets go of it.
        FileAccessRetry.Run(() =>
        {
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: bak);
            else File.Move(tmp, path);
        }, path);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best-effort */ }
        }
    }
}
