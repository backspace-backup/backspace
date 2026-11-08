using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U154: a running restore's card names the temporary folder its
/// files arrive in, and stops naming it once the restore has finished.</summary>
public sealed class RestoreStagingFolderTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_names_the_staging_folder_only_while_the_restore_runs(bool cancel)
    {
        using var workspace = new TempWorkspace();
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
        var target = workspace.Sub("restore-target");
        var engine = DispatchProxy.Create<IBackupEngine, StagingEngine>();
        var fake = (StagingEngine)(object)engine;
        var restore = new RestoreEngine(ServiceLocator.Config, engine, ServiceLocator.Secrets);
        var observed = new List<string?>();
        restore.StagingChanged += observed.Add;
        using var cts = new CancellationTokenSource();
        var backup = new Backup { Name = "Staging " + Guid.NewGuid().ToString("N") };
        var request = new RestoreRequest
        {
            Backup = backup, SourcePath = workspace.Sub("source"), StorageName = "default", Revision = 1,
            Destination = new Destination { Name = "Archive", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("archive") },
            Files = new[] { "file.txt" }, TargetPath = target, MaintenanceLeaseHeld = true, CancelOperation = cts.Cancel,
        };
        ActiveRestoreSession? session = null;
        void Opened() => session ??= Find(backup.Id);
        ServiceLocator.ActiveRestores.Changed += Opened;
        try
        {
            var run = restore.RunAsync(request, cts.Token);
            var staging = await fake.Staged.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            session ??= Find(backup.Id);
            Assert.NotNull(session);
            Assert.True(session.IsRunning);
            Assert.Equal(staging, session.StagingPath);
            Assert.True(session.HasStagingFolder);
            Assert.StartsWith(Path.Combine(target, ".backspace-restore-"), staging);
            Assert.Equal(new[] { staging }, observed);

            // The Open command launches exactly the staging folder.
            var opened = new List<string>();
            FolderLauncher.LaunchOverride = opened.Add;
            try { new ActiveRestoresViewModel().OpenStagingFolderCommand.Execute(session); }
            finally { FolderLauncher.LaunchOverride = null; }
            Assert.Equal(new[] { staging }, opened);

            if (cancel) session.Cancel(); else fake.Finish.SetResult();
            var outcome = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(cancel ? RestoreTerminalStatus.Cancelled : RestoreTerminalStatus.Success, outcome.TerminalStatus);
            Assert.False(session.IsRunning);
            Assert.Null(session.StagingPath);
            Assert.False(session.HasStagingFolder);
            Assert.Equal(new[] { staging, null }, observed);
            Assert.False(Directory.Exists(staging));

            // A late report after the end never brings the folder back.
            ServiceLocator.ActiveRestores.SetStagingPath(session, staging);
            Dispatcher.UIThread.RunJobs();
            Assert.False(session.HasStagingFolder);
        }
        finally
        {
            ServiceLocator.ActiveRestores.Changed -= Opened;
            if (!fake.Finish.Task.IsCompleted) fake.Finish.TrySetResult();
            if (session is not null) await ServiceLocator.ActiveRestores.DismissAsync(session);
        }
    }

    [AvaloniaFact]
    public async Task Test_restores_never_show_a_staging_folder_and_missing_folders_are_not_opened()
    {
        using var workspace = new TempWorkspace();
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
        var session = ServiceLocator.ActiveRestores.BeginTest(new Backup { Name = "Test restore" }, "Check", DateTime.UtcNow, null);
        try
        {
            var folder = workspace.Sub(".backspace-restore-test");
            ServiceLocator.ActiveRestores.SetStagingPath(session, folder);
            Dispatcher.UIThread.RunJobs();
            Assert.False(session.HasStagingFolder);

            var opened = new List<string>();
            FolderLauncher.LaunchOverride = opened.Add;
            try
            {
                Assert.False(FolderLauncher.Open(Path.Combine(workspace.Root, "gone")));
                Assert.True(FolderLauncher.Open(folder));
            }
            finally { FolderLauncher.LaunchOverride = null; }
            Assert.Equal(new[] { folder }, opened);
        }
        finally
        {
            ServiceLocator.ActiveRestores.End(session, RestoreTerminalStatus.Cancelled, null);
            Dispatcher.UIThread.RunJobs();
            await ServiceLocator.ActiveRestores.DismissAsync(session);
        }
    }

    private static ActiveRestoreSession? Find(string backupId)
    {
        foreach (var s in ServiceLocator.ActiveRestores.Sessions) if (s.BackupId == backupId) return s;
        return null;
    }

    /// <summary>Stages like the real engine: creates the folder, reports it,
    /// waits for completion or cancellation, removes it, reports null.</summary>
    public class StagingEngine : DispatchProxy
    {
        public TaskCompletionSource<string> Staged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(IBackupEngine.RestoreFilesAsync)) throw new NotSupportedException(method.Name);
            return Restore((string)args![6]!, (CancellationToken)args[8]!, (Action<string?>?)args[14]);
        }
        private async Task<RestoreOpResult> Restore(string target, CancellationToken ct, Action<string?>? onStaging)
        {
            var staging = Directory.CreateDirectory(Path.Combine(target, ".backspace-restore-" + Guid.NewGuid().ToString("N"))).FullName;
            onStaging?.Invoke(staging);
            Staged.SetResult(staging);
            try
            {
                await Finish.Task.WaitAsync(ct);
                return new RestoreOpResult(1, 1, 0, 0, RestoreTerminalStatus.Success, null);
            }
            finally
            {
                Directory.Delete(staging, true);
                onStaging?.Invoke(null);
            }
        }
    }
}
