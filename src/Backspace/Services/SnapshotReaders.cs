using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Keeps a destination's snapshots in place while someone reads them (U159,
/// owner's request of 7 November 2026). A mounted snapshot and a running
/// restore or test restore hold the destination for as long as they read;
/// nothing may then remove or rewrite what they read: cleanup (forget,
/// prune), deleting snapshots, erasing the destination or changing its
/// password. Automatic cleanup is skipped with the reason and catches up after
/// a later backup; an operation the person started is refused with the reason.
///
/// <para>A backup into the same destination is safe: restic only adds a new
/// snapshot and never changes an existing one, so a mounted snapshot stays the
/// one it opened.</para>
///
/// <para>Works across Backspace processes (the window, scheduled runs, the
/// background service): one lock file per destination and kind beside the
/// other operation locks. Readers open it shared; a change opens it
/// exclusively, and keeps it until it ends so no reader starts meanwhile. A
/// lock goes with the process holding it, so a crash never leaves a
/// destination held.</para>
/// </summary>
internal static class SnapshotReaders
{
    internal enum Kind { Mount, Restore }

    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Overrides the lock folder (tests).</summary>
    internal static string? DirectoryOverride { get; set; }

    internal static string Key(Destination destination, string storageName)
        => ResticBackrestEngine.MakeRepoId(destination, storageName);

    internal static string Reason(Kind kind) => kind == Kind.Mount
        ? "a snapshot of this backup is mounted as a drive"
        : "files are being restored from this backup";

    /// <summary>Holds the destination for reading; waits while a change runs
    /// there, telling <paramref name="waiting"/> why (then "").</summary>
    internal static async Task<IDisposable> HoldAsync(Destination destination, string storageName, Kind kind,
        Action<string>? waiting, CancellationToken ct)
    {
        var names = new List<string> { FileName(kind, Key(destination, storageName)) };
        if (kind == Kind.Mount) names.Add(FileName(kind, "*"));
        var told = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var handles = new List<FileStream>();
            foreach (var name in names)
            {
                var handle = Open(name, exclusive: false);
                if (handle is null) break;
                handles.Add(handle);
            }
            if (handles.Count == names.Count)
            {
                if (told) waiting?.Invoke("");
                return new Lease(handles);
            }
            foreach (var handle in handles) handle.Dispose();
            if (!told) { told = true; waiting?.Invoke("Waiting for the cleanup of this backup's storage to finish"); }
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Starts a change that removes or rewrites stored data. Null with
    /// the reason while a snapshot there is mounted or restored; otherwise the
    /// change holds the destination until disposed.</summary>
    internal static IDisposable? TryBeginChange(Destination destination, string storageName, out string reason)
    {
        var key = Key(destination, storageName);
        var handles = new List<FileStream>();
        foreach (var kind in new[] { Kind.Mount, Kind.Restore })
        {
            var handle = Open(FileName(kind, key), exclusive: true);
            if (handle is null)
            {
                foreach (var held in handles) held.Dispose();
                reason = Reason(kind);
                return null;
            }
            handles.Add(handle);
        }
        reason = "";
        return new Lease(handles);
    }

    /// <summary>For changes that are not tied to one destination (the
    /// exclusive maintenance lease): null with the reason while any snapshot is
    /// mounted, otherwise holds new mounts back until disposed.</summary>
    internal static IDisposable? TryBeginChangeAnywhere(out string reason)
    {
        var handle = Open(FileName(Kind.Mount, "*"), exclusive: true);
        reason = handle is null ? "a snapshot of a backup is mounted as a drive" : "";
        return handle is null ? null : new Lease(new List<FileStream> { handle });
    }

    /// <summary>The reason a cleanup was skipped, for the run record and log:
    /// "Cleanup waits: a snapshot of this backup is mounted as a drive. …"</summary>
    internal static string CleanupSkipped(string reason)
        => $"Cleanup waits: {reason}. It runs after a later backup, once the drive is unmounted and restores have finished.";

    /// <summary>The refusal shown when the person starts a change.</summary>
    internal static string Refusal(string action, string reason)
        => $"{action} has to wait: {reason}. Unmount the drive or let the restore finish, then try again. Nothing was changed.";

    internal static string Directory()
    {
        if (DirectoryOverride is { } overridden) return overridden;
#if WINDOWS
        if (Platform.Windows.SystemServiceLocks.Directory() is { } shared) return shared;
#endif
        return Path.Combine(EngineWorkPaths.MachineRoot, "locks");
    }

    /// <summary>The lock file for one kind of reader of one destination ("*": any mount).</summary>
    internal static string LockPath(Kind kind, string key) => Path.Combine(Directory(), FileName(kind, key));

    private static string FileName(Kind kind, string key)
        => "readers-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(kind + "\0" + key.ToLowerInvariant())))[..32] + ".lock";

    private static FileStream? Open(string name, bool exclusive)
    {
        var directory = Directory();
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(Path.Combine(directory, name), FileMode.OpenOrCreate,
                exclusive ? FileAccess.ReadWrite : FileAccess.Read, exclusive ? FileShare.None : FileShare.Read);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException ex) when (FileAccessRetry.IsLock(ex)) { return null; }
    }

    private sealed class Lease(List<FileStream> handles) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var handle in handles) handle.Dispose();
        }
    }
}
