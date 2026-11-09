using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Backspace.Services;

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// The saved review sign-ins (<c>audit-dashboard/private/temp/BackspaceReviewTests/*/config/secrets.bin</c>)
/// are kept in the format Backspace used before its portable vault (U153): a
/// JSON map protected by Windows for this account (DPAPI). Backspace itself no
/// longer reads that format; the cloud scenarios read and refresh their tokens
/// through this helper so the saved sign-ins stay usable.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ReviewSignInFile(string path)
{
    private static readonly byte[] Entropy =
    {
        0x45, 0x44, 0x42, 0x2D, 0x73, 0x65, 0x63, 0x72, 0x65, 0x74, 0x73, 0x2D, 0x76, 0x31
    };

    internal string FilePath { get; } = Path.GetFullPath(path);

    internal bool TryGet(string key, out string value)
    {
        using var transaction = StateFileLock.Acquire(FilePath);
        return Load().TryGetValue(key, out value!);
    }

    internal void Set(string key, string value)
    {
        using var transaction = StateFileLock.Acquire(FilePath);
        var map = Load();
        map[key] = value;
        var clear = JsonSerializer.SerializeToUtf8Bytes(map);
        try { SecretsStore.AtomicWrite(FilePath, ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(FilePath)) return new();
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(clear) ?? new(); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
