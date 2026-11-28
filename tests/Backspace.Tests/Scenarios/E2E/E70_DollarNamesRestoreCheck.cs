using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
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

/// <summary>
/// The owner's restore check of 27 November 2026 failed on local_c: four
/// Chocolatey and NuGet cache files with <c>$</c> in their path were "not
/// restored", because restic expands <c>$NAME</c> in an include file. This
/// backs up such names with the real restic, clicks Verify for a restore
/// check of every file, and requires every file restored and identical. A
/// decoy folder named as the unescaped pattern would expand must stay out.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E70_DollarNamesRestoreCheck : BackupE2EBase
{
    public E70_DollarNamesRestoreCheck(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E70-dollar-names-restore-check";
    [AvaloniaFact] public async Task Run_E70() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source = Path.Combine(ctx.SourcesDir, "c");
        // The owner's names, shortened only in their hash part.
        var names = new[]
        {
            "ChocolateyHttpCache/9310b7b8$community.chocolatey.org_api_v2_/list_FindPackagesById()_id='chocolatey-visualstudio.extension'&semVerLevel=2.0.0_page2.dat",
            "ChocolateyHttpCache/9310b7b8$community.chocolatey.org_api_v2_/list_Packages(Id='KB2919355',Version='1.0.20160915')_page1.dat",
            "NuGet/v3-cache/670c1461$ps_api.nuget.org_v3_index.json/nupkg_avalonia.headless.12.1.3.dat",
            "Notes/$PATH and ${HOME} cost $5.txt",
            "Notes/a$$b $-1 $.txt",
            "Notes/[draft] $1.txt",
        };
        foreach (var name in names)
        {
            var path = Path.Combine(source, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Original: " + name);
        }
        // What "/…/9310b7b8$community.chocolatey…" became once restic expanded
        // $community to nothing: a different folder, which must not be restored.
        var decoy = Path.Combine(source, "ChocolateyHttpCache", "9310b7b8.chocolatey.org_api_v2_");
        Directory.CreateDirectory(decoy);
        File.WriteAllText(Path.Combine(decoy, "list_Packages(Id='KB2919355',Version='1.0.20160915')_page1.dat"), "Decoy: never restored");
        var expectedFiles = names.Length + 1;

        var dest = new Destination { Name = "Disposable encrypted repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e70", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Names with $ (E70)", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(), Threads = 2,
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(backup); });
        var restores = new ConcurrentQueue<IReadOnlyList<string>?>();
        fixture.Engine.RestoreStarted += (_, include) => restores.Enqueue(include);

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.Equal(BackupRunStatus.Success, record.Status);
        var vm = (BackupsViewModel)view.DataContext!;
        var card = vm.Cards.Single(c => c.Id == backup.Id);

        // The restore check, every file, compared with the originals: Verify's button.
        host.MouseMove(new Point(2, 2)); await Task.Delay(150); ctx.Driver.Pump();
        var button = view.GetVisualDescendants().OfType<Button>().Single(b =>
            b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Verify"));
        button.BringIntoView(); ctx.Driver.Pump();
        var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host)!.Value;
        host.MouseDown(at, MouseButton.Left); host.MouseUp(at, MouseButton.Left); ctx.Driver.Pump();
        var task = vm.TestRestoreCommand.ExecutionTask;
        Assert.NotNull(task);
        Assert.True(ctx.Driver.WaitFor(() => host.OwnedWindows.OfType<VerifyWindow>().Any()), "The Verify window must open.");
        var options = host.OwnedWindows.OfType<VerifyWindow>().Single();
        var model = (VerifyViewModel)options.DataContext!;
        foreach (var c in model.Own.Concat(model.Other)) c.IsSelected = c.Title == "Restore check";
        ctx.Driver.Pump();
        void Click(Control control)
        {
            control.BringIntoView(); ctx.Driver.Pump(); options.UpdateLayout();
            var p = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), options)!.Value;
            options.MouseDown(p, MouseButton.Left); options.MouseUp(p, MouseButton.Left); ctx.Driver.Pump();
        }
        Click(options.GetVisualDescendants().OfType<RadioButton>().Single(b => b.Name == "FullChoice"));
        var compare = options.GetVisualDescendants().OfType<CheckBox>().Single(b => b.Name == "CompareOriginals");
        if (compare.IsChecked != true) Click(compare);
        var before = ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id).LastVerifyUtc;
        Click(options.FindControl<Button>("RunButton")!);
        await task!.WaitAsync(TimeSpan.FromMinutes(2));
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            while (ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id).LastVerifyUtc == before || card.IsRunning || card.IsVerifying)
            { await Task.Delay(50, deadline.Token); ctx.Driver.Pump(); }
        await ctx.Driver.PumpAsync(200);

        var verified = ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id);
        Assert.True(verified.LastVerifyPass, verified.LastVerifySummary);
        Assert.Contains($"{expectedFiles} files restored; {expectedFiles} matched today's originals", verified.LastVerifySummary);
        // The selection restic was given: each $ written $$, the bracket escaped.
        var lines = restores.Where(l => l is not null).SelectMany(l => l!).ToArray();
        Assert.Contains("/ChocolateyHttpCache/9310b7b8$$community.chocolatey.org_api_v2_/list_FindPackagesById()_id='chocolatey-visualstudio.extension'&semVerLevel=2.0.0_page2.dat", lines);
        Assert.Contains("/Notes/a$$$$b $$-1 $$.txt", lines);
        Assert.Contains("/Notes/[[]draft] $$1.txt", lines);
        ctx.Report.RecordNote("Restore check of names with $",
            $"Verify ran a restore check of every file with the real restic: {verified.LastVerifySummary} " +
            "The include file restic read had $$ for each $, so the Chocolatey and NuGet cache names from the owner's failed check were restored and compared.");

        // A plain restore of the one Chocolatey file: only it, not the decoy folder
        // the unescaped pattern named.
        var revision = (await ServiceLocator.Revisions.ListRevisionsAsync(backup, source, dest, "default", CancellationToken.None))[0].Number;
        var picked = names[1];
        var target = ctx.MakeRestoreTarget("one-file");
        var result = await ServiceLocator.Restore.RunAsync(new RestoreRequest {
            Backup = backup, SourcePath = source, Destination = dest, StorageName = "default", Revision = revision,
            Files = new[] { picked }, IncludePatterns = new[] { picked }, TargetPath = target }, CancellationToken.None);
        Assert.True(result.TerminalStatus == RestoreTerminalStatus.Success, result.ErrorMessage ?? result.TerminalStatus.ToString());
        Assert.Equal("Original: " + picked, File.ReadAllText(Path.Combine(target, picked.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Single(Directory.GetFiles(target, "*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(target, "ChocolateyHttpCache", "9310b7b8.chocolatey.org_api_v2_")));
        ctx.Report.RecordNote("One file with $ restored alone",
            "Restoring the KB2919355 Chocolatey file gave exactly that file with its original contents; the decoy folder named as the unescaped pattern would have read was not restored.");

        var label = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == card.VerifyLabel);
        var hover = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), host)!.Value;
        host.MouseMove(hover); await Task.Delay(600); ctx.Driver.Pump();
        ctx.Snapshot(host, "restore-check-dollar-names-pass",
            $"Verify's restore check passed: all {expectedFiles} files, the owner's Chocolatey and NuGet cache names with $ among them, restored and identical to the originals.");
    }
}
