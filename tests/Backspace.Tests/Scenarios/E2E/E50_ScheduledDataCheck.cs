using System;
using Avalonia;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

[Collection(ScenarioCollection.Name)]
public sealed class E50_ScheduledDataCheck(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E50-scheduled-data-check";
    [AvaloniaFact] public Task Run_E50() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var source = Path.Combine(ctx.SourcesDir, "documents"); Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "photo.bin"), RandomNumberGenerator.GetBytes(256 * 1024));
        var destination = new Destination { Name = "Disposable encrypted archive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repository"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e50", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Documents", UseVss = false, VssDisabledAcknowledged = true,
            AbortOnMeteredNetwork = false, SourcePaths = { source }, Targets = { new() { DestinationId = destination.Id } },
            PruneSchedule = new(), CheckSchedule = new() { Frequency = ScheduleFrequency.Monthly } };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        Assert.Equal(BackupRunStatus.Success,
            (await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", deadline.Token)).Status);
        var structureOnly = await ServiceLocator.Orchestrator.RunCheckAsync(backup, deadline.Token);
        Assert.Equal(BackupRunStatus.Success, structureOnly.Status);
        Assert.Contains("were not downloaded", Assert.Single(structureOnly.Cells).Summary);

        var editor = new BackupEditorViewModel(backup, false);
        Assert.False(editor.CheckReadDataSample);
        var window = ctx.Driver.Show(new BackupEditorWindow { DataContext = editor, Width = 1024, Height = 768 });
        window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
        await ctx.Driver.PumpAsync(100);
        var option = window.FindControl<CheckBox>("SavedDataCheckChoice")!;
        option.BringIntoView();
        await ctx.Driver.PumpAsync(120);
        var scroll = option.GetVisualAncestors().OfType<ScrollViewer>().First();
        var comparison = window.FindControl<Border>("CheckComparison")!;
        scroll.Offset = new Avalonia.Vector(0, scroll.Offset.Y + comparison.TranslatePoint(default, scroll)!.Value.Y - 8);
        await ctx.Driver.PumpAsync(100);
        ctx.Snapshot(window, "data-check-off-by-default", "The catalog check is always included. Independent checkboxes add data reading and file recovery; both start off.");
        Assert.True(option.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        await ctx.Driver.PumpAsync(100);
        Assert.True(editor.CheckReadDataSample);
        Assert.False(editor.CheckRestoreFiles);
        var saved = editor.CommitToWorking(); Assert.NotNull(saved); Assert.True(saved.CheckReadDataSample);
        ServiceLocator.Config.Update(c => c.Backups.Single(b => b.Id == backup.Id).CheckReadDataSample = true);
        var reopened = new BackupEditorViewModel(saved, false); Assert.True(reopened.CheckReadDataSample);
        scroll.Offset = new Avalonia.Vector(0, scroll.Offset.Y + comparison.TranslatePoint(default, scroll)!.Value.Y - 8);
        await ctx.Driver.PumpAsync(100);
        ctx.Snapshot(window, "data-check-enabled", "Verify saved data was selected by keyboard and saved. Recovery remains off, so it does not perform a second comparison download.");
        scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height);
        await ctx.Driver.PumpAsync(100);
        ctx.Snapshot(window, "data-check-limits", "The selected data check explains sample limits, the missing recovery test, and the approximate download budget.");
        window.Close();

        // Today's source is allowed to differ from the immutable saved backup.
        File.WriteAllText(Path.Combine(source, "photo.bin"), "A newer local version is not backup corruption.");
        var healthy = await ServiceLocator.Orchestrator.RunCheckAsync(backup, deadline.Token);
        Assert.True(healthy.Status == BackupRunStatus.Success, string.Join("\n", healthy.Cells.Select(c => c.Summary).Concat(healthy.Errors)));
        Assert.Contains("sample of saved data", Assert.Single(healthy.Cells).Summary);
        Assert.Contains("not a full-data check", healthy.Cells[0].Summary);
        ctx.Report.RecordNote("Real sample and changed local file", "The scheduler's production check path invoked bundled Backrest/restic, sized and read a data sample, and succeeded. Changing the original file did not produce a corruption warning. The result explicitly says this is not a full-data check.");

        // Only a disposable repository is damaged, and its original bytes are
        // restored in finally. Length corruption is deterministic even when a
        // random sample selects another pack: the structure pass must reject it.
        var pack = Directory.GetFiles(destination.PathOrSubpath, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p).Length == 64 && p.Contains(Path.DirectorySeparatorChar + "data" + Path.DirectorySeparatorChar))
            .OrderByDescending(p => new FileInfo(p).Length).First();
        var original = File.ReadAllBytes(pack);
        try
        {
            File.WriteAllBytes(pack, original[..(original.Length / 2)]);
            var damaged = await ServiceLocator.Orchestrator.RunCheckAsync(backup, deadline.Token);
            Assert.Equal(BackupRunStatus.Failed, damaged.Status);
            Assert.NotEmpty(damaged.Errors);
            Assert.Equal(BackupRunStatus.Failed, ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).Single(r => r.Id == damaged.Id).Status);
            ctx.Report.RecordNote("Damaged repository is not successful", "A saved data pack was deliberately truncated in the disposable repository. The same production check path reported Failed and preserved the failure in history. This proves failure handling; because pack size is invalid, it does not prove every possible byte corruption would be selected by a 1% random sample.");
        }
        finally { File.WriteAllBytes(pack, original); }
        var recovered = await ServiceLocator.Orchestrator.RunCheckAsync(backup, deadline.Token);
        Assert.Equal(BackupRunStatus.Success, recovered.Status);
        ctx.Report.RecordNote("Proof limits", "Real encrypted local repository and production check coordinator. Interface input and screenshots use headless Avalonia. This did not launch a Windows scheduled task, change real schedules, or measure live-cloud traffic. Random sampling can miss damage outside the selected packs.");
    }
}
