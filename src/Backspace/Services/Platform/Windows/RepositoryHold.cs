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
/// for the whole operation. The folders restic would otherwise make while it
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
        // made below it; each folder made is held before the next one.
        Hold(existing, cancellation);
        while (missing.TryPop(out var next)) { Directory.CreateDirectory(next); Hold(next, cancellation); }

        // A folder that holds something other than a repository (or nothing
        // yet) gets no repository folders of its own.
        var isRepository = File.Exists(Path.Combine(repository, "config"));
        var empty = !Directory.EnumerateFileSystemEntries(repository).Any(e => !Path.GetFileName(e).StartsWith(GuardPrefix, StringComparison.Ordinal));
        if (!isRepository && !(create && empty)) return;
        foreach (var name in Folders) Make(Path.Combine(repository, name), cancellation);
        var data = _leases[^1];
        for (var i = 0; i < 256; i++) Make(Path.Combine(repository, "data", i.ToString("x2")), cancellation);
        // restic opens everything directly in "data" as a folder: a guard file
        // there stops its listing (check and prune retry for many minutes, then
        // fail). Each of the 256 holds below already keeps "data" open and
        // nonempty, so its own hold, and its guard file, can go.
        _leases.Remove(data);
        data.Dispose();
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
