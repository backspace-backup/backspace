using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP coverage-matrix gaps:
///   - Edit destination
///   - Delete destination
///   - Delete backup
///
/// All three are CRUD paths on AppConfig that the existing scenario
/// suite didn't cover (Add was covered via S08, Edit / Delete were
/// not). A regression here silently breaks user expectations:
/// editing a destination after it's been used by a backup must
/// preserve linkage; deleting a destination with linked backups
/// must either block or cascade-orphan; deleting a backup must NOT
/// remove the destination it pointed at.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S41_DestinationAndBackupCrud : ScenarioBase
{
    protected override string ScenarioName => "S41-destination-and-backup-crud";

    [AvaloniaFact] public async Task Run_S41() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // -------- Add destination --------
        var dest = new Destination
        {
            Name = "Local — crud",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("d1"),
        };
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(dest));
        Assert.Single(ServiceLocator.Config.Current.Destinations, d => d.Id == dest.Id);

        // -------- Add backup linked to destination --------
        var backup = new Backup { Name = "crud-backup" };
        backup.SourcePaths.Add(ctx.MakeSource("crud-src", seed: 1, targetBytes: 4_000));
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => cfg.Backups.Add(backup));

        var loaded = ServiceLocator.Config.Current.Backups.First(b => b.Id == backup.Id);
        Assert.Equal(dest.Id, loaded.Targets[0].DestinationId);

        // -------- Edit destination — rename and change path --------
        var newPath = ctx.MakeLocalDestination("d1-renamed");
        ServiceLocator.Config.Update(cfg =>
        {
            var target = cfg.Destinations.First(d => d.Id == dest.Id);
            target.Name = "Local — crud RENAMED";
            target.PathOrSubpath = newPath;
        });
        var afterEdit = ServiceLocator.Config.Current.Destinations.First(d => d.Id == dest.Id);
        Assert.Equal("Local — crud RENAMED", afterEdit.Name);
        Assert.Equal(newPath, afterEdit.PathOrSubpath);
        // Edit must NOT regenerate the destination ID — the backup's
        // linkage is by ID; a rename that breaks linkage would orphan
        // the backup.
        Assert.Equal(dest.Id, afterEdit.Id);
        ctx.Report.RecordNote("edit destination preserves id + linkage",
            $"backup.Targets[0].DestinationId still equals dest.Id");

        // -------- Delete backup — destination must stay --------
        ServiceLocator.Config.Update(cfg =>
        {
            var toRemove = cfg.Backups.First(b => b.Id == backup.Id);
            cfg.Backups.Remove(toRemove);
        });
        Assert.DoesNotContain(ServiceLocator.Config.Current.Backups, b => b.Id == backup.Id);
        Assert.Contains(ServiceLocator.Config.Current.Destinations, d => d.Id == dest.Id);
        ctx.Report.RecordNote("delete backup leaves destination intact",
            "removing the only consumer of a destination must not also remove the destination.");

        // -------- Delete destination — backups already gone, so no orphans --------
        ServiceLocator.Config.Update(cfg =>
        {
            var toRemove = cfg.Destinations.First(d => d.Id == dest.Id);
            cfg.Destinations.Remove(toRemove);
        });
        Assert.DoesNotContain(ServiceLocator.Config.Current.Destinations, d => d.Id == dest.Id);
        ctx.Report.RecordNote("delete destination",
            "destination removed cleanly after its only backup was already removed.");

        return Task.CompletedTask;
    }
}
