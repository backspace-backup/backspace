using System.Collections.Concurrent;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform;

namespace Backspace.Tests.TestHelpers;

/// <summary>Scheduler for the test suite: records what would have been
/// registered and reports every recorded backup as ready. It never touches
/// the operating system's scheduler.</summary>
internal sealed class RecordingScheduler : IScheduler
{
    internal ConcurrentDictionary<string, Backup> Registered { get; } = new();

    public ScheduleCheck InspectBackup(Backup backup, string exePath) => Registered.ContainsKey(backup.Id)
        ? new(ScheduleReadiness.Ready, "Recorded by the test scheduler.")
        : new(ScheduleReadiness.Attention, "This schedule is not registered with Windows yet. Open the backup and save it to schedule it again.");

    public void UpsertTask(Backup backup, string exePath) => Registered[backup.Id] = backup;
    public void DeleteTask(Backup backup) => Registered.TryRemove(backup.Id, out _);
    public string TaskNameFor(Backup backup) => "Backspace — " + backup.Name;
}
