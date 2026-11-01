using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// The settings task that applies saved changes without a prompt (U043) also
/// clears the run history the background service kept for a removed backup
/// (U029): the service's folder is protected, so the desktop cannot.
/// </summary>
public sealed class ServiceSettingsTaskTests
{
    [Fact]
    public void Clears_only_the_service_history_of_removed_backups()
    {
        using var workspace = new TempWorkspace();
        var engine = workspace.Sub("engine");
        var store = new LogStore(engine);
        RunRecord Run(string backupId, string name) => new()
        {
            Id = Guid.NewGuid().ToString("N"), BackupId = backupId, BackupName = name,
            StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow, Status = BackupRunStatus.Success, Summary = "ok",
        };
        // "documents" holds a kept backup and a removed one with the same name.
        store.AppendRun(Run("kept", "documents"));
        store.AppendRun(Run("removed", "documents"));
        store.AppendRun(Run("waiting", "photos"));
        var pending = Path.Combine(workspace.Root, "pending-removals.json");
        File.WriteAllText(pending, JsonSerializer.Serialize(new[] { new { Backup = new { Id = "waiting", Name = "photos" }, DestinationId = "d", StorageName = "default" } }));
        var config = new AppConfig();
        config.Backups.Add(new Backup { Id = "kept", Name = "documents" });

        ServiceSettingsTask.ForgetRemovedBackups(engine, pending, config);

        var after = new LogStore(engine);
        Assert.Single(after.LoadHistory("documents"), r => r.BackupId == "kept");
        Assert.DoesNotContain(after.LoadHistory("documents"), r => r.BackupId == "removed");
        // A backup whose unplugged drive still holds copies keeps its history.
        Assert.Single(after.LoadHistory("photos"), r => r.BackupId == "waiting");
    }

    [Fact]
    public void An_unreadable_pending_list_keeps_everything()
    {
        using var workspace = new TempWorkspace();
        var engine = workspace.Sub("engine");
        var store = new LogStore(engine);
        store.AppendRun(new RunRecord { Id = Guid.NewGuid().ToString("N"), BackupId = "removed", BackupName = "documents",
            StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow, Status = BackupRunStatus.Success, Summary = "ok" });
        var pending = Path.Combine(workspace.Root, "pending-removals.json");
        File.WriteAllText(pending, "{ not json");

        ServiceSettingsTask.ForgetRemovedBackups(engine, pending, new AppConfig());

        Assert.Single(new LogStore(engine).LoadHistory("documents"));
    }
}
