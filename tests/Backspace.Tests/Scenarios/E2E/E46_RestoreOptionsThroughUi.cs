using System;
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

[Collection(ScenarioCollection.Name)]
public sealed class E46_RestoreOptionsThroughUi : BackupE2EBase
{
    public E46_RestoreOptionsThroughUi(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E46-restore-options-through-ui";
    [AvaloniaFact] public Task Run_E46() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        var source = Path.Combine(ctx.SourcesDir, "documents");
        Directory.CreateDirectory(Path.Combine(source, "notes"));
        File.WriteAllText(Path.Combine(source, "notes", "keep.txt"), "BACKUP COPY");
        var dest = new Destination { Name = "Local test repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e46", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "My documents", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(backup); });
        for (var version = 1; version <= 3; version++)
        {
            File.WriteAllText(Path.Combine(source, "notes", "missing.txt"), "RECOVERED VERSION " + version);
            Assert.Equal(BackupRunStatus.Success, (await fixture.Engine.BackupPruneCheckAsync(backup, source, dest, "default", ct)).Status);
        }
        var revisions = await fixture.Engine.ListSnapshotsAsync(backup, source, dest, "default", ct);
        using var vm = new RestoreViewModel { SelectedBackup = backup, SelectedDestination = dest,
            SelectedSourcePath = source, CurrentStep = RestoreViewModel.Step.PickRevision };
        foreach (var revision in revisions) vm.Revisions.Add(revision);
        vm.SelectedRevision = revisions[0];
        var view = new RestoreView { DataContext = vm };
        var window = ctx.Driver.Show(new Window { Width = 1100, Height = 820, Content = view });
        var picker = window.GetVisualDescendants().OfType<ListBox>().Single(b => ReferenceEquals(b.ItemsSource, vm.Revisions));
        var olderRow = (Control)picker.ContainerFromIndex(1)!;
        olderRow.BringIntoView(); await ctx.Driver.PumpAsync(100); window.UpdateLayout();
        var rowPoint = olderRow.TranslatePoint(new Point(olderRow.Bounds.Width / 2, olderRow.Bounds.Height / 2), window)!.Value;
        window.MouseDown(rowPoint, MouseButton.Left); window.MouseUp(rowPoint, MouseButton.Left);
        await ctx.Driver.PumpAsync(100);
        Assert.Equal(revisions[1].SnapshotId, vm.SelectedRevision!.Value.SnapshotId);
        ctx.Snapshot(window, "chosen-older-snapshot", "Revision 2 is selected in the actual picker. The test then deletes revision 1 while this page remains open.");
        await fixture.Engine.PruneSnapshotIdAsync(backup, source, dest, "default", revisions.Last().SnapshotId!, ct);
        await PressButton(vm.NextFromRevisionCommand);
        await Until(() => vm.CurrentStep == RestoreViewModel.Step.PickFiles);
        Assert.Equal(2, vm.AllFiles.Count);
        foreach (var file in vm.AllFiles) file.IsSelected = true;
        await PressButton(vm.NextFromFilesCommand);
        Assert.Equal(RestoreViewModel.Step.PickTarget, vm.CurrentStep);
        var target = ctx.MakeRestoreTarget("selected-target");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "USER ORIGINAL");
        vm.TargetOriginalSource = false;
        Assert.True(ctx.Driver.EnterText(view, "Pick a folder", target));
        view.FindControl<Expander>("RestoreOptions")!.IsExpanded = true;
        await Toggle("PreserveFoldersOption");
        await Toggle("ReplaceFilesOption");
        Assert.False(vm.PreserveStructure);
        Assert.False(vm.OverwriteExisting);
        ctx.Snapshot(window, "restore-options-off", "Both options are off. Files should go directly into the selected folder, while existing files stay unchanged.");
        await PressButton(vm.NextFromTargetCommand);
        await Until(() => vm.CurrentStep == RestoreViewModel.Step.Summary);
        Assert.Equal("USER ORIGINAL", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.Equal("RECOVERED VERSION 2", File.ReadAllText(Path.Combine(target, "missing.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "notes")));
        Assert.Equal(1, vm.RestoredCount);
        Assert.True(vm.AnyFailures); // Skipping an existing file must not claim complete recovery.
        var explanation = view.FindControl<TextBlock>("RestoreSummaryDetails")!;
        await Until(() => explanation.IsEffectivelyVisible
            && explanation.Text?.Contains("existing item(s) unchanged") == true
            && vm.ProgressLog.Contains("existing item(s) unchanged"));
        Assert.Contains("replacing existing files is turned off", explanation.Text);
        var visibleText = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
        Assert.Contains("NOT RESTORED", visibleText);
        Assert.DoesNotContain("FAILED", visibleText);
        Assert.DoesNotContain("Failures", visibleText);
        Assert.DoesNotContain("RETRYING", visibleText);
        Assert.Equal(1, vm.FailedCount);
        var saved = Assert.Single(ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id)
            .Where(r => r.Kind == RunKind.Restore));
        Assert.Contains("1 not restored", saved.Summary);
        Assert.DoesNotContain("failed", saved.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("existing item(s) unchanged", File.ReadAllText(saved.LogPath));
        await ctx.Driver.PumpAsync(180);
        ctx.Snapshot(window, "restore-preserved-original", "The result reports attention needed: one missing file was recovered and the existing file was left unchanged. Actual file contents were checked after the button-triggered restore.");
        ctx.Report.RecordNote("Real engine and adversarial checks", "The actual RestoreView picker received a pointer click and the buttons received keyboard input. After choosing revision 2, retention removed revision 1: recovery still produced version 2 rather than version 3. Both folder preservation and replacement were turned off. The original file stayed byte-for-byte unchanged, the missing file appeared at the chosen root, and no unwanted notes folder appeared. This is headless Avalonia input on Windows with bundled Backrest/restic, not a native desktop screenshot or cloud-account test.");

        async Task Toggle(string name)
        {
            var control = view.FindControl<CheckBox>(name)!;
            control.BringIntoView(); await ctx.Driver.PumpAsync(100); control.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            await ctx.Driver.PumpAsync(100);
        }
        async Task PressButton(System.Windows.Input.ICommand command)
        {
            var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Command == command && b.IsVisible);
            button.BringIntoView(); await ctx.Driver.PumpAsync(100);
            Assert.True(button.IsEffectivelyEnabled); button.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            await ctx.Driver.PumpAsync(100);
        }
        async Task Until(Func<bool> ready)
        {
            while (!ready()) { ct.ThrowIfCancellationRequested(); await ctx.Driver.PumpAsync(50); }
        }
    }
}
