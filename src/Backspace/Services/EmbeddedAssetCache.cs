using System;
using System.IO;
using System.Security.Cryptography;

namespace Backspace.Services;

/// <summary>Recreates disposable embedded dependencies without accepting a
/// same-size stale copy or exposing a partially written replacement.</summary>
internal static class EmbeddedAssetCache
{
    internal static bool WriteIfDifferent(Stream resource, string target)
    {
        var start = resource.Position;
        var expectedHash = SHA256.HashData(resource);
        resource.Position = start;
        if (File.Exists(target))
        {
            try
            {
                using var existing = File.OpenRead(target);
                if (existing.Length == resource.Length - start &&
                    CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(existing)))
                    return false;
            }
            catch (IOException) { /* Attempt replacement; a locked target still fails safely. */ }
        }

        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
