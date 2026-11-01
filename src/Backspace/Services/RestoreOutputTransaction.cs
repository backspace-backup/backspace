using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Backspace.Services;

/// <summary>Retains replaced originals until the whole restore is accepted.
/// Cancellation reverses only this operation's output. Conflicting edits are
/// preserved with the original copies and reported for manual recovery.</summary>
internal sealed class RestoreOutputTransaction
{
    private sealed record Entry(string Destination, string? Original, string Returned, string Fingerprint, bool DirectoryLink)
    {
        public bool Published { get; set; }
    }
    private readonly List<Entry> _entries = new();
    private readonly List<string> _createdDirectories = new();
    private readonly Dictionary<string, string> _roots = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool _finished;
    internal bool HasOutput => _entries.Any(e => e.Published);

    internal void CreateDirectory(string path)
    {
        ResticBackrestEngine.ValidateRestoreTarget(path);
        if (Directory.Exists(path)) return;
        var missing = new Stack<string>();
        for (var p = Path.GetFullPath(path); !Directory.Exists(p); p = Path.GetDirectoryName(p)!)
        {
            if (File.Exists(p)) throw new IOException("A file occupies a restore folder: " + p);
            missing.Push(p);
        }
        foreach (var p in missing) { Directory.CreateDirectory(p); _createdDirectories.Add(p); }
    }

    internal void Publish(string staged, string destination, string targetRoot, bool overwrite, CancellationToken ct)
    {
        if (_finished) throw new InvalidOperationException("Restore output has already been finalized.");
        ct.ThrowIfCancellationRequested();
        ResticBackrestEngine.ValidateRestoreTarget(Path.GetDirectoryName(destination)!);
        CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!_roots.TryGetValue(targetRoot, out var recovery))
        {
            recovery = Path.Combine(targetRoot, ".backspace-undo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recovery); _roots.Add(targetRoot, recovery);
        }
        var directoryLink = (File.GetAttributes(staged) & FileAttributes.Directory) != 0;
        string fingerprint = "";
        FileAccessRetry.Run(() => fingerprint = Fingerprint(staged), staged, FileWaitReason.Restore, ct);
        string? original = null;
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            if (!overwrite) throw new IOException("An item appeared in the restore destination: " + destination);
            if (Directory.Exists(destination)) throw new IOException("A folder occupies a restored file's destination: " + destination);
            original = Path.Combine(recovery, Guid.NewGuid().ToString("N") + ".original");
        }
        var entry = new Entry(destination, original, Path.Combine(recovery, Guid.NewGuid().ToString("N") + ".returned"), fingerprint, directoryLink);
        // A forced process exit must not leave anonymous original copies. This
        // append-only map is flushed before an original is moved out of place.
        var journalPath = Path.Combine(recovery, "recovery.jsonl");
        FileStream? journal = null;
        FileAccessRetry.Run(() => journal = new FileStream(journalPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            journalPath, FileWaitReason.Restore, ct);
        using (journal)
        {
            JsonSerializer.Serialize(journal!, entry); journal!.WriteByte((byte)'\n'); journal.Flush(flushToDisk: true);
        }
        if (original is not null)
            FileAccessRetry.Run(() =>
            {
                ResticBackrestEngine.ValidateRestoreTarget(Path.GetDirectoryName(destination)!);
                File.Move(destination, original);
            }, destination, FileWaitReason.Restore, ct);
        _entries.Add(entry);
        FileAccessRetry.Run(() =>
        {
            ResticBackrestEngine.ValidateRestoreTarget(Path.GetDirectoryName(destination)!);
            if (directoryLink) Directory.Move(staged, destination); else File.Move(staged, destination);
        }, staged, FileWaitReason.Restore, ct);
        entry.Published = true;
    }

    internal void Commit()
    {
        if (_finished) return;
        if (_entries.Any(e => !e.Published && e.Original is not null && File.Exists(e.Original)))
            throw new IOException("An original file needs recovery. Its copy and path map are preserved in " + string.Join(", ", _roots.Values));
        // Completed files remain at their requested locations. Originals were
        // replaced only under the user's existing overwrite choice.
        CleanupRoots(); _finished = true;
    }

    internal void Rollback()
    {
        if (_finished) return;
        var errors = new List<string>();
        foreach (var entry in _entries.AsEnumerable().Reverse())
        {
            try
            {
                ResticBackrestEngine.ValidateRestoreTarget(Path.GetDirectoryName(entry.Destination)!);
                if (entry.Published && Exists(entry.Destination))
                {
                    // Move first, then identify the item we actually obtained.
                    // If another program replaced/edited it, preserve that edit;
                    // never delete it merely because the pathname matches.
                    MoveForRecovery(entry.Destination, entry.Returned, entry.DirectoryLink);
                    if (Fingerprint(entry.Returned) != entry.Fingerprint)
                    {
                        if (!Exists(entry.Destination))
                            MoveForRecovery(entry.Returned, entry.Destination, entry.DirectoryLink);
                        throw new IOException("Another program changed " + entry.Destination + "; both versions were preserved.");
                    }
                }
                if (entry.Original is not null)
                {
                    if (Exists(entry.Destination)) throw new IOException("Another item now occupies " + entry.Destination + "; the original was preserved.");
                    MoveForRecovery(entry.Original, entry.Destination, directory: false);
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        if (errors.Count > 0)
            throw new IOException("Restore was cancelled, but some files need attention. Recovery copies are kept in "
                + string.Join(", ", _roots.Values) + ". " + string.Join(" ", errors));
        CleanupRoots();
        foreach (var dir in _createdDirectories.AsEnumerable().Reverse())
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        _finished = true;
    }

    private void CleanupRoots()
    {
        foreach (var root in _roots.Values)
            if (Directory.Exists(root))
            {
                ResticBackrestEngine.ValidateRestoreTarget(root);
                ResticBackrestEngine.DeleteRestoreStaging(root);
            }
    }
    // A security scanner or indexer often opens a file right after it is
    // renamed into the recovery folder. A failed rename leaves both names
    // intact, so retry briefly; a lock that outlasts the 1.55-second bounded
    // backoff keeps the saved copy and is reported for manual recovery.
    private static void MoveForRecovery(string source, string destination, bool directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (directory) Directory.Move(source, destination); else File.Move(source, destination);
                return;
            }
            catch (Exception ex) when (attempt < 5 && FileReplacementErrors.CanRetry(ex))
            {
                Thread.Sleep(50 << attempt);
            }
        }
    }
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
    private static string Fingerprint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return "link:" + ((attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return "file:" + Convert.ToHexString(SHA256.HashData(input));
    }
}
