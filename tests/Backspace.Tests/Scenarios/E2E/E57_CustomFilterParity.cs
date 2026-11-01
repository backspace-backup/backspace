using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

[Collection(ScenarioCollection.Name)]
public sealed class E57_CustomFilterParity(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E57-custom-filter-parity";
    [AvaloniaFact] public async Task Run_E57() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source = Path.Combine(ctx.SourcesDir, "classes-and-exceptions");
        var expected = new[] { "drop3.txt", "keep.log", "nested/keep.log", "c.bin", "notx.txt" };
        var omitted = new[] { "drop1.txt", "drop2.txt", "other.log", "nested/other.log", "a.bin", "b.bin", "literal[1].txt", "notA.txt", "cache/hidden.txt" };
        foreach (var name in expected.Concat(omitted))
        {
            var path = Path.Combine(source, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "Distinct synthetic contents for " + name);
        }
        var destination = new Destination { Name="Custom filters", Kind=DestinationKind.LocalFolder,
            PathOrSubpath=ctx.MakeLocalDestination("repo"), Encrypted=true,
            StoragePasswordRef=ServiceLocator.Secrets.PutNew("e57",Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name="Custom filter comparison", SourcePaths={source},
            FiltersText="drop[12].txt\n*.log\n!keep.log\n[a-b].bin\nliteral[[]1].txt\nnot[^x].txt\ncache",
            UseVss=false,VssDisabledAcknowledged=true,PruneSchedule=new(),CheckSchedule=new(),
            Targets={new BackupTarget{DestinationId=destination.Id,StorageName="default"}} };
        ServiceLocator.Config.Update(c=>{c.Destinations.Add(destination);c.Backups.Add(backup);});
        var preview = new List<SimulatedEntry>();
        await new FilterSimulator().SimulateAsync(backup, preview.Add, CancellationToken.None);
        var included = preview.Where(p=>p.Included && File.Exists(p.Path))
            .Select(p=>Path.GetRelativePath(source,p.Path).Replace('\\','/')).Order().ToArray();
        Assert.Equal(expected.Order(), included);
        var savings = await new SavingsEstimator().ComputeAsync(new[]{source},backup.FiltersText,CancellationToken.None);
        Assert.Equal(expected.Sum(n=>new FileInfo(Path.Combine(source,n)).Length),savings.IncludedBytes);
        Assert.Equal(omitted.Sum(n=>new FileInfo(Path.Combine(source,n)).Length),savings.ExcludedBytes);
        var view = new BackupsView {DataContext=new BackupsViewModel()};
        var window = ctx.Driver.Show(new Window {Width=1100,Height=760,Content=view});
        var run = await RunBackupViaUiAsync(ctx,backup,view);
        Assert.True(run.Status is BackupRunStatus.Success or BackupRunStatus.Warning,run.Summary);
        var target = ctx.MakeRestoreTarget("recovered");
        var recovered = await RestoreEverythingAsync(backup,source,destination,"default",target,CancellationToken.None);
        Assert.Equal(0,recovered.FailedCount);
        var actual = Directory.GetFiles(target,"*",SearchOption.AllDirectories).Select(p=>Path.GetRelativePath(target,p).Replace('\\','/')).Order().ToArray();
        Assert.Equal(expected.Order(),actual);
        foreach(var name in expected) Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(source,name)),await File.ReadAllBytesAsync(Path.Combine(target,name)));
        ctx.Snapshot(window,"custom-filter-backup-complete","The actual backup and byte-for-byte recovery retain the same five files as the preview; nine deliberately excluded files are absent.");
        ctx.Report.RecordNote("Custom filter comparison","Character sets, ranges, negated sets, escaped literal brackets, bare directory names and later ! exceptions agree across preview, byte estimate, real restic snapshot and recovered files. Five files survive; nine are omitted.");
    }
}
