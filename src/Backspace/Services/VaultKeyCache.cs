using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Backspace.Services.Platform;

namespace Backspace.Services;

/// <summary>
/// This computer's copy of a vault key (U153), so the password is asked once
/// per computer, not at every start. The key is protected by Windows for this
/// account (DPAPI, CurrentUser) and kept outside the portable folder, in
/// <c>%LOCALAPPDATA%\Backspace\keys\&lt;vault id&gt;.user.bin</c>. The
/// background service keeps its own copy, protected for Windows' own account,
/// as <c>&lt;vault id&gt;.service.bin</c>.
///
/// <para>A missing or unreadable copy means the vault is locked on this
/// computer. A copy that opens but no longer opens the vault is stale (the
/// vault was replaced); the store deletes it.</para>
/// </summary>
internal sealed class VaultKeyCache
{
    private readonly string _directory;
    private readonly string _suffix;
    private readonly ISecretsEncryption _protection;

    internal VaultKeyCache(string directory, string suffix, ISecretsEncryption protection)
    {
        _directory = Path.GetFullPath(directory);
        _suffix = suffix;
        _protection = protection;
    }

    /// <summary>The signed-in person's copy on this computer.</summary>
    internal static VaultKeyCache ForUser() => new(EngineWorkPaths.KeysDirectory, "user", DefaultProtection());

    /// <summary>The background service's copy (it runs as Windows' own account,
    /// so Windows protects it for that account).</summary>
    internal static VaultKeyCache ForService(string machineRoot)
        => new(Path.Combine(machineRoot, "keys"), "service", DefaultProtection(machineRoot));

    private static ISecretsEncryption DefaultProtection(string? machineRoot = null)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return new Platform.Windows.DpapiSecretsEncryption();
#endif
        return new Platform.Unix.FileKeySecretsEncryption(Path.Combine(machineRoot ?? EngineWorkPaths.MachineRoot, "keys", "key-file.bin"));
    }

    internal string PathFor(string vaultId)
    {
        if (!VaultFile.IsId(vaultId)) throw new ArgumentException("Invalid vault identity.");
        return Path.Combine(_directory, vaultId + "." + _suffix + ".bin");
    }

    private static byte[] Entropy(string vaultId) => Encoding.UTF8.GetBytes("Backspace vault key v1|" + vaultId);

    /// <summary>The key, or null when there is no usable copy on this computer.</summary>
    internal byte[]? TryRead(string vaultId)
    {
        var path = PathFor(vaultId);
        byte[] wrapped;
        try { wrapped = FileAccessRetry.Get(() => File.ReadAllBytes(path), path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            var key = _protection.Unprotect(wrapped, Entropy(vaultId));
            if (key.Length == VaultFile.KeyBytes) return key;
            CryptographicOperations.ZeroMemory(key);
            return null;
        }
        catch (CryptographicException) { return null; }
    }

    internal void Write(string vaultId, byte[] key)
    {
        var path = PathFor(vaultId);
        Directory.CreateDirectory(_directory);
        var wrapped = _protection.Protect(key, Entropy(vaultId));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options)) { file.Write(wrapped); file.Flush(true); }
            FileAccessRetry.Run(() => File.Move(temporary, path, overwrite: true), path);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    internal void Delete(string vaultId)
    {
        try { FileAccessRetry.DeleteIfExists(PathFor(vaultId)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppLogger.For<VaultKeyCache>().Warning(ex, "A stale key copy could not be removed"); }
    }

    internal bool Exists(string vaultId) => File.Exists(PathFor(vaultId));
}
