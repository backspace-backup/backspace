using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Reads revision lists and file lists for a backup's destination.
/// Engine-agnostic: every call routes through
/// <see cref="IBackupEngine.ListSnapshotsAsync"/> /
/// <see cref="IBackupEngine.ListSnapshotFilesAsync"/>. The browser keeps
/// the destination-paired ergonomic API the UI was built against and
/// adds nothing of its own beyond log statements — the engine owns
/// the storage-format details.
/// </summary>
public sealed class RevisionBrowser
{
    private static ILogger _log => AppLogger.For<RevisionBrowser>();

    private readonly IBackupEngine _engine;
    private readonly SecretsStore _secrets;

    public RevisionBrowser(IBackupEngine engine, SecretsStore secrets)
    {
        _engine = engine;
        _secrets = secrets;
    }

    public async Task<IReadOnlyList<RevisionSummary>> ListRevisionsAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        CancellationToken ct)
    {
        await _engine.EnsureInitializedAsync(backup, sourcePath, destination, storageName, ct);
        var revisions = await _engine.ListSnapshotsAsync(backup, sourcePath, destination, storageName, ct);
        _log.Information("Listed {Count} revisions for {Backup} at {Dest}",
            revisions.Count, backup.Name, destination.Name);
        return revisions;
    }

    public async Task<IReadOnlyList<RevisionFile>> ListFilesAsync(
        Backup backup, string sourcePath, Destination destination, string storageName,
        int revision, CancellationToken ct, string? expectedSnapshotId = null)
    {
        await _engine.EnsureInitializedAsync(backup, sourcePath, destination, storageName, ct);
        var files = await _engine.ListSnapshotFilesAsync(
            backup, sourcePath, destination, storageName, revision, ct, expectedSnapshotId);
        _log.Information("Listed {Count} files for {Backup} revision {Rev}",
            files.Count, backup.Name, revision);
        return files;
    }
}

public readonly record struct RevisionSummary(int Number, DateTime CreatedUtc, int FileCount, long TotalBytes)
{
    /// <summary>Immutable engine identity. Number is only a display position and can change after retention.</summary>
    public string? SnapshotId { get; init; }
}
public readonly record struct RevisionFile(string Path, long SizeBytes, DateTime ModifiedUtc);
