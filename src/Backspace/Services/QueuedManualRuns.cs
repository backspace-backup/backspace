using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Backspace.Services;

/// <summary>
/// Runs you started that wait for their conditions (U226, owner: "people will
/// not keep the app open just for this!"). Saved in Backspace.config, so a run
/// queued with "Run when ready" still starts by itself after Backspace or
/// Windows restarts; closing the window never mattered, since Backspace stays
/// in the tray. Only Quit drops them, as it stops every backup.
/// </summary>
internal static class QueuedManualRuns
{
    internal sealed record Entry(string BackupId, string[]? CheckIds, bool WaiveBattery, bool WaiveMetered, DateTime SinceUtc);

    private static readonly object Gate = new();
    internal static string PathOf => System.IO.Path.Combine(AppPaths.ConfigRoot, "queued-runs.json");

    internal static IReadOnlyList<Entry> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(PathOf)) return [];
                return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(PathOf)) ?? [];
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                AppLogger.For(nameof(QueuedManualRuns)).Warning(ex, "Queued runs could not be read");
                return [];
            }
        }
    }

    internal static void Add(Entry entry) => Change(list => { list.RemoveAll(e => e.BackupId == entry.BackupId); list.Add(entry); });
    internal static void Remove(string backupId) => Change(list => list.RemoveAll(e => e.BackupId == backupId));
    internal static void Clear() => Change(list => list.Clear());

    private static void Change(Action<List<Entry>> change)
    {
        lock (Gate)
        {
            try
            {
                var list = Load().ToList();
                change(list);
                if (list.Count == 0) { FileAccessRetry.DeleteIfExists(PathOf); return; }
                TestRestoreContext.WriteAtomic(PathOf, list);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.For(nameof(QueuedManualRuns)).Warning(ex, "Queued runs could not be saved");
            }
        }
    }
}
