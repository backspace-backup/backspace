using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Lists the snapshot-ids already present inside a Destination's storage.
///
/// We use this at Backup-save time so the auto-name generator's 6-char
/// suffix can be proven unique against the REMOTE state as well as the
/// local config. That closes the cross-machine collision window the user
/// flagged (two machines rolling the same <c>{leaf}-{rand6}</c> into the
/// same Dropbox folder would otherwise mix unrelated snapshot chains).
///
/// Strategy per destination kind:
///   • Local / External / Network : enumerate <c>{root}/snapshots/*</c>
///     directly via the filesystem — instant, no side effects.
///   • Cloud (Dropbox, OneDrive, Google Drive) and S3 : shell out to
///     <c>engine init</c> + <c>engine list</c> against an ephemeral
///     repo. This is the same bridge <see cref="DropboxAuthProbe"/> uses
///     and it reuses the exact credential/OAuth path a real backup will
///     take, so if it works here it works later. ~2–5s round-trip
///     latency, acceptable for a one-shot save-time check.
///
/// Accepted race: two machines saving a Backup at the exact same second
/// can each see the other's candidate as "not taken yet" and both roll
/// the same suffix. Combined with 36^6 ≈ 2.2B, the practical failure
/// rate is negligible. (User has explicitly accepted this.)
/// </summary>
public sealed class DestinationProbe
{
    private readonly SecretsStore _secrets;
    private static ILogger _log => AppLogger.For<DestinationProbe>();

    // Actual restic `-log list` output:
    //   2026-05-13 18:20:07.523 INFO SNAPSHOT_INFO Snapshot my-backup revision 42 created at ...
    // (without -log, the line starts at "Snapshot ..." but with the
    // same `Snapshot <id> revision N` shape — the older "Snapshot <id>
    // at revision N" form documented in earlier restic versions
    // doesn't show up in current output anymore.) This regex tolerates
    // either: a leading timestamp/log-level prefix, and either "at
    // revision" or just "revision" between the id and the number.
    //
    // BUG HISTORY: the prior anchor `^Snapshot\s+...\s+at\s+revision`
    // never matched real output, so every cloud probe returned an
    // empty snapshot-id list. That made `WipeEntireDestinationAsync`
    // believe there was nothing to prune for any Dropbox / OneDrive
    // / GDrive / S3 destination — so "Delete Destination" left the
    // remote folder untouched. Caught by DropboxWipeDiagnosticTests.
    private static readonly Regex SnapshotLineRegex = new(
        @"\bSnapshot\s+(?<id>[A-Za-z0-9_\-]+)\s+(?:at\s+)?revision\s+\d",
        RegexOptions.Compiled);

    public DestinationProbe(SecretsStore secrets) => _secrets = secrets;

    /// <summary>
    /// Local-storage state used by the destination-editor pre-flight.
    /// Cheap synchronous check: just <see cref="File.Exists(string)"/>
    /// against the well-known restic artifacts at the storage root.
    /// We do NOT shell out to restic — the only thing we need to know
    /// at save time is "does this folder already contain a restic
    /// storage we'd be re-initialising on top of". If yes, the editor
    /// surfaces a recovery path (turn on Encryption + supply the
    /// existing password, or pick a different folder, or manually wipe
    /// the folder) BEFORE the user discovers it via a failed run.
    /// Returns false for non-local-like kinds (probing cloud/S3 needs a
    /// live connection — handled at run-time by the typed-exception path
    /// in <see cref="IBackupEngine.EnsureInitializedAsync"/>).
    /// </summary>
    public static bool LocalStorageAlreadyInitialised(Destination destination)
    {
        if (!destination.IsLocalLike) return false;
        if (string.IsNullOrWhiteSpace(destination.PathOrSubpath)) return false;
        var root = destination.PathOrSubpath;
        try
        {
            // restic creates a top-level `config` file at storage init
            // time (the encrypted/plain key bundle). The presence of any
            // ONE of these is conclusive — chunks/ and snapshots/ are
            // populated on first backup but `config` lands on init.
            if (File.Exists(Path.Combine(root, "config"))) return true;
            if (Directory.Exists(Path.Combine(root, "chunks"))) return true;
            if (Directory.Exists(Path.Combine(root, "snapshots"))) return true;
        }
        catch
        {
            // Path inaccessible (offline drive, missing share, perms) —
            // treat as "not initialised here". The actual init at run
            // time will surface the real error if there is one.
            return false;
        }
        return false;
    }

    /// <summary>
    /// Inspects an existing restic <c>config</c> file at a local
    /// storage root and returns whether it was created with encryption.
    ///
    /// restic writes one of two formats:
    ///   • Unencrypted: a plain JSON document starting with <c>'{'</c>
    ///     (the chunk-size, hash-algo, and storage parameters are
    ///     all plaintext).
    ///   • Encrypted: an opaque binary blob (HMAC-prefixed AES-256
    ///     ciphertext); the first byte is never <c>'{'</c>.
    ///
    /// We don't parse the JSON; checking the first non-whitespace byte
    /// is enough to distinguish the two formats and lets the editor
    /// skip the "you must enter a password" gate when adopting an
    /// unencrypted storage. Returns:
    ///   • <c>true</c> if the file looks encrypted,
    ///   • <c>false</c> if it parses as plaintext JSON,
    ///   • <c>null</c> if the file doesn't exist, can't be read, or
    ///     is empty (caller should fall back to "treat as encrypted"
    ///     for safety, since prompting for a password is recoverable
    ///     while accidentally adopting an encrypted storage as
    ///     plaintext is not).
    /// </summary>
    public static bool? LocalStorageIsEncrypted(Destination destination)
    {
        if (!destination.IsLocalLike) return null;
        if (string.IsNullOrWhiteSpace(destination.PathOrSubpath)) return null;
        var configPath = Path.Combine(destination.PathOrSubpath, "config");
        try
        {
            if (!File.Exists(configPath)) return null;
            // Read just enough bytes to identify the format. JSON
            // configs are typically ~200-400 bytes; encrypted blobs
            // are larger but the first byte is always non-printable.
            using var fs = File.Open(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> buf = stackalloc byte[16];
            var n = fs.Read(buf);
            if (n <= 0) return null;
            // Skip leading whitespace just in case (unlikely but
            // robust against editors that wrote a BOM/newline).
            for (int i = 0; i < n; i++)
            {
                var b = buf[i];
                if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n') continue;
                return b != (byte)'{';
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the set of snapshot-ids (i.e. restic snapshot folder
    /// names) already present at this destination's storage root.
    /// Never throws — returns empty on any error, with a logged warning,
    /// so save-time checks stay best-effort.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> ListSnapshotIdsAsync(
        Destination destination, CancellationToken ct)
    {
        try
        {
            return destination.Kind switch
            {
                DestinationKind.LocalFolder
                    or DestinationKind.ExternalDrive
                    or DestinationKind.NetworkShare
                    => ListLocal(destination),

                DestinationKind.DropboxAppScoped
                    or DestinationKind.DropboxFullAccess
                    or DestinationKind.OneDrivePersonal
                    or DestinationKind.OneDriveBusiness
                    or DestinationKind.GoogleDrive
                    or DestinationKind.S3Compatible
                    => await ListViaEngineAsync(destination, ct),

                _ => Array.Empty<string>(),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warning(ex, "Snapshot-id listing failed for destination {Name} ({Kind}) — treating as empty",
                destination.Name, destination.Kind);
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyCollection<string> ListLocal(Destination destination)
    {
        if (string.IsNullOrWhiteSpace(destination.PathOrSubpath)) return Array.Empty<string>();

        // Walk every known snapshot-store layout under the destination
        // root. The restic engine that wrote to `snapshots/` is gone,
        // but legacy filesystem state from a prior
        // version may still live there, so we keep enumerating it for
        // the collision check until users have migrated their on-disk
        // state. The fake-snapshot store is what the InMemoryBackupEngine
        // writes in tests and what early Restic-side prototypes will
        // produce until cloud URI synthesis lands.
        var roots = new[]
        {
            Path.Combine(destination.PathOrSubpath, "snapshots"),
            Path.Combine(destination.PathOrSubpath, ".backspace-fake-snapshots"),
        };

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(name)) ids.Add(name);
            }
        }
        return ids;
    }

    /// <summary>
    /// Cloud/S3 snapshot-id listing for a destination. Known
    /// limitation: this returns an empty list. The probe is only used
    /// by <see cref="StorageCleaner"/>'s cascading-delete path, which
    /// currently acts on local-kind destinations only; cloud-destination
    /// wipes leave the remote skeleton intact and the UI shows an
    /// informational dialog explaining the user needs to clean up
    /// remotely via the provider's web UI. Implementing it cleanly
    /// means wrapping <c>IBackupEngine.ListSnapshotsAsync</c> with a
    /// (destination → all backups that target it) reverse-index pass,
    /// since this probe doesn't have a backup id to scope the call.
    /// Backlog item.
    /// </summary>
    private Task<IReadOnlyCollection<string>> ListViaEngineAsync(
        Destination destination, CancellationToken ct)
    {
        _ = destination;
        _ = ct;
        _log.Information(
            "Cloud snapshot-id probe is a stub; treating as empty.");
        return Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
    }

    public static HashSet<string> ExtractSnapshotIds(string engineListOutput)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in engineListOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var m = SnapshotLineRegex.Match(line);
            if (m.Success) ids.Add(m.Groups["id"].Value);
        }
        return ids;
    }

}
