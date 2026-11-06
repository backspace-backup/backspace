using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Holds every folder of a repository on this PC while the engine
/// works in it (U152). Other accounts may be able to change such a folder: an
/// external drive or a folder made on a second drive lets every account
/// modify it by default. restic deletes files by name (its lock files, old
/// packs and indexes), so a folder emptied and turned into a link while it
/// works would send those deletes onto any file, with the rights of whoever
/// runs it: Windows itself in the background service. Each folder is checked
/// for links and held, with a guard file that keeps it from being emptied,
/// for the whole operation: every engine call that runs restic there takes it
/// (round 4), since even a listing writes and deletes a lock file. The
/// folders restic would otherwise make while it
/// runs (keys, locks, index, snapshots, data and data\00 to data\ff) are made
/// first, under the hold, so none of them is ever left unheld. restic ignores
/// the guard files, except directly in "data", which is held through its 256
/// subfolders and keeps no guard file of its own.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RepositoryHold : IDisposable
{
    internal const string GuardPrefix = ".backspace-path-guard-";
    private static readonly string[] Folders = { "keys", "locks", "index", "snapshots", "data" };
    private readonly List<ServicePathLease> _leases = new();

    /// <summary>The repository folder of a destination on this PC, or null
    /// for a cloud, a network share or a drive that isn't attached.</summary>
    internal static string? LocalPath(Destination destination) => destination.Kind switch
    {
        DestinationKind.ExternalDrive => DriveRepositoryGuard.RepositoryPath(destination),
        DestinationKind.LocalFolder when !string.IsNullOrWhiteSpace(destination.PathOrSubpath)
            && Path.IsPathFullyQualified(destination.PathOrSubpath.Trim()) => Path.GetFullPath(destination.PathOrSubpath.Trim()),
        _ => null,
    };

    /// <summary>Holds the repository of <paramref name="destination"/>, or
    /// returns null when it has none on this PC. With
    /// <paramref name="create"/>, a repository not yet made (a first backup)
    /// gets its folders made under the hold; otherwise only what exists is held.</summary>
    internal static RepositoryHold? For(Destination destination, bool create, CancellationToken cancellation = default)
        => LocalPath(destination) is { } path ? For(path, create, cancellation) : null;

    internal static RepositoryHold For(string repository, bool create, CancellationToken cancellation = default)
    {
        var hold = new RepositoryHold();
        try { hold.Take(Path.GetFullPath(repository), create, cancellation); return hold; }
        catch { hold.Dispose(); throw; }
    }

    private static readonly object SharedGate = new();
    private static readonly Dictionary<string, (RepositoryHold Hold, int Users)> SharedHolds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Holds the repository of <paramref name="destination"/> for
    /// one engine call (U152, round 4): every call that runs restic there
    /// (listing revisions or files, restoring, cleaning up, changing the
    /// password) writes and deletes a lock file by name. Calls on the same
    /// repository, nested or concurrent, share one hold, released when the
    /// last of them ends. Returns null when the repository isn't on this PC.</summary>
    internal static IDisposable? Use(Destination destination, CancellationToken cancellation = default)
    {
        if (LocalPath(destination) is not { } path) return null;
        var key = Path.GetFullPath(path);
        lock (SharedGate)
        {
            if (SharedHolds.TryGetValue(key, out var shared))
                SharedHolds[key] = (shared.Hold, shared.Users + 1);
            else
                SharedHolds[key] = (For(key, create: false, cancellation), 1);
        }
        return new SharedUse(key);
    }

    private sealed class SharedUse(string key) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (SharedGate)
            {
                if (!SharedHolds.TryGetValue(key, out var shared)) return;
                if (shared.Users > 1) { SharedHolds[key] = (shared.Hold, shared.Users - 1); return; }
                SharedHolds.Remove(key);
                shared.Hold.Dispose();
            }
        }
    }

    private void Take(string repository, bool create, CancellationToken cancellation)
    {
        var missing = new Stack<string>();
        var existing = repository;
        while (!Directory.Exists(existing))
        {
            missing.Push(existing);
            existing = Path.GetDirectoryName(existing);
            if (existing is null) return; // no such drive: the engine reports it
        }
        if (missing.Count > 0 && !create) return; // nothing to work in: the engine reports it
        // Every folder above is checked for links and held before anything is
        // made below it; each folder made is held before the next one. An
        // existing repository folder is held below, with its own folders.
        var made = missing.Count > 0;
        if (made)
        {
            Hold(existing, cancellation);
            while (missing.TryPop(out var next)) { Directory.CreateDirectory(next); Hold(next, cancellation); }
        }

        // Without create (a check, a listing, a restore), a folder that isn't
        // a repository gets no repository folders. With create (a backup),
        // restic's init would make them anyway, even in a folder that already
        // holds something, so they are made here first, held (round 4).
        if (!create && !File.Exists(Path.Combine(repository, "config"))) return;

        // restic opens everything directly in "data" as a folder: a guard file
        // there stops its listing (check and prune retry for many minutes, then
        // fail). "data" is held only as the folder above data\00..ff, which
        // keeps it open and nonempty. The folders that already exist (usually
        // all of them) are held in one go: every engine call takes this hold.
        var dataFolder = Path.Combine(repository, "data");
        var top = Folders.Where(n => n != "data").Select(n => Path.Combine(repository, n)).ToArray();
        var packs = Enumerable.Range(0, 256).Select(i => Path.Combine(dataFolder, i.ToString("x2"))).ToArray();
        var present = top.Concat(packs).Where(Directory.Exists).ToArray();
        var batch = made ? present : present.Prepend(repository).ToArray();
        if (batch.Length > 0) _leases.Add(ServicePathLease.Acquire(batch, cancellation));
        foreach (var folder in top) if (!present.Contains(folder)) Make(folder, cancellation);
        var missingPacks = packs.Where(p => !present.Contains(p)).ToArray();
        if (missingPacks.Length == 0) return;
        // "data" is already held through a pack folder, or gets a hold of its
        // own (with a guard) while its folders are made under it.
        ServicePathLease? dataHold = null;
        if (missingPacks.Length == packs.Length) { Directory.CreateDirectory(dataFolder); dataHold = ServicePathLease.Acquire(new[] { dataFolder }, cancellation); }
        try { foreach (var pack in missingPacks) Make(pack, cancellation); }
        finally { dataHold?.Dispose(); }
    }

    private void Make(string folder, CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder); // inside a held folder: it can't be redirected
        Hold(folder, cancellation);        // a link put at its name meanwhile stops the run
    }

    private void Hold(string folder, CancellationToken cancellation)
        => _leases.Add(ServicePathLease.Acquire(new[] { folder }, cancellation));

    public void Dispose()
    {
        for (var i = _leases.Count - 1; i >= 0; i--) _leases[i].Dispose();
        _leases.Clear();
    }
}
