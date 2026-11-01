using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>The ordinary desktop pulls metadata and writes its own portable
/// cache. Neither a cache path nor its contents are sent to the service.</summary>
internal sealed class ServiceHistoryCache
{
    private readonly string _directory;
    private readonly Func<ServiceHistoryProtocol.Request, CancellationToken, Task<ServiceHistoryProtocol.Response>> _read;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private sealed class Entry { internal DateTime NextAttempt; internal bool Busy; }
    internal event Action<string>? Refreshed;

    internal ServiceHistoryCache(string directory,
        Func<ServiceHistoryProtocol.Request, CancellationToken, Task<ServiceHistoryProtocol.Response>> read)
    { _directory = directory; _read = read; }

    internal static ServiceHistoryCache? ForDesktop()
        => ForDesktop(AppContext.BaseDirectory.TrimEnd('\\'), AppPaths.ConfigRoot);

    internal static ServiceHistoryCache? ForDesktop(string installation, string configurationRoot)
    {
        if (!ServiceOwnerSecrets.Applies(installation, configurationRoot)) return null;
        return new(Path.Combine(configurationRoot, "logs", "service-history"), async (request, token) =>
        {
            if (ServiceRetirement.Read(installation) is { Stage: "complete" })
                return new(1, Array.Empty<RunRecord>());
            var path = ServicePortableLayout.Record(installation, ServiceWorkerBinding.FileName);
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) { return new(1, Array.Empty<RunRecord>()); }
            catch (DirectoryNotFoundException) { return new(1, Array.Empty<RunRecord>()); }
            var binding = ServiceWorkerBinding.LoadForOwner(installation);
            if (!binding.CanRun) return new(1, Array.Empty<RunRecord>());
            return await ServiceHistoryProtocol.ReadAsync(binding, request, token).ConfigureAwait(false);
        });
    }

    internal List<RunRecord> ReadAndRefresh(string backupId)
    {
        var saved = ReadCached(backupId);
        RequestRefresh(backupId);
        return saved;
    }

    internal void RequestRefresh(string backupId)
    {
        var entry = _entries.GetOrAdd(backupId, _ => new());
        lock (entry)
        {
            if (entry.Busy || entry.NextAttempt > DateTime.UtcNow) return;
            entry.Busy = true;
        }
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(backupId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                AppLogger.For<ServiceHistoryCache>().Warning(ex,
                    "Background service history could not be refreshed for {Backup}; saved outcomes were retained", backupId);
            }
            finally { lock (entry) { entry.NextAttempt = DateTime.UtcNow.AddSeconds(3); entry.Busy = false; } }
        });
    }

    internal List<RunRecord> ReadCached(string backupId)
    {
        var path = PathFor(backupId);
        if (!Directory.Exists(_directory)) return new();
        using var transaction = StateFileLock.Acquire(path);
        return RunHistoryFile.Read(path, ServiceRunAuthorization.Json).Where(r => r.BackupId == backupId).ToList();
    }

    internal async Task RefreshAsync(string backupId, CancellationToken cancellationToken)
    {
        var prior = ReadCached(backupId);
        var observed = prior.ToDictionary(r => r.Id, r => JsonSerializer.Serialize(r, ServiceRunAuthorization.Json), StringComparer.Ordinal);
        var known = prior.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var received = new List<RunRecord>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        do
        {
            var response = await _read(new(1, backupId, cursor), deadline.Token).ConfigureAwait(false);
            if (response.Error is not null)
                throw new IOException("The service could not supply complete history: " + response.Error);
            if (response.Version != 1 || response.Runs is null
                || response.Runs.Any(r => r is null || r.BackupId != backupId))
                throw new InvalidDataException("The service returned history for a different backup.");
            received.AddRange(response.Runs);
            // Existing older metadata stays in the desktop cache, even after
            // the service expires its detailed transcripts. Once this page
            // reaches a known outcome, no older page needs to be downloaded.
            if (response.Runs.Any(r => known.Contains(r.Id))) break;
            cursor = response.NextAfterRunId;
            if (cursor is not null && (!cursors.Add(cursor) || response.Runs.Length == 0 || cursors.Count > 1000))
                throw new InvalidDataException("The service history page did not advance.");
        } while (cursor is not null);
        deadline.Token.ThrowIfCancellationRequested();
        if (received.Count == 0) return;
        Directory.CreateDirectory(_directory);
        var path = PathFor(backupId);
        List<RunRecord> changed;
        using (var transaction = StateFileLock.Acquire(path))
        {
            // Re-read after the network trip. Another desktop must not lose
            // outcomes it saved while this one was waiting for a reply.
            var current = RunHistoryFile.Read(path, ServiceRunAuthorization.Json);
            var before = current.ToDictionary(r => r.Id, r => JsonSerializer.Serialize(r, ServiceRunAuthorization.Json), StringComparer.Ordinal);
            var safe = received.Where(r => !before.TryGetValue(r.Id, out var text)
                || (observed.TryGetValue(r.Id, out var earlier) && earlier == text)).ToList();
            changed = safe.Where(r => !before.TryGetValue(r.Id, out var text)
                || text != JsonSerializer.Serialize(r, ServiceRunAuthorization.Json)).ToList();
            if (changed.Count == 0) return;
            var merged = current.Concat(safe).GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.Last())
                .OrderByDescending(r => r.StartedUtc).ToList();
            RunHistoryFile.Write(path, merged, ServiceRunAuthorization.Json);
        }
        try { Refreshed?.Invoke(backupId); } catch { /* Persisted history does not depend on subscribers. */ }
    }

    private string PathFor(string backupId)
        => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(backupId))) + ".json");
}
