using System;
using System.Threading;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Unit-test the registry's session lifecycle. Each test exercises a
/// single concern: Begin adds, UpdateProgress mutates, End marks
/// terminal and keeps it until dismissal. Uses AvaloniaFact because
/// the registry marshals to the UI dispatcher.
/// </summary>
public class ActiveRestoreRegistryTests
{
    private static RestoreRequest BuildRequest(int fileCount)
    {
        var dest = new Destination { Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = "/tmp" };
        var backup = new Backup { Name = "TestBackup" };
        backup.SourcePaths.Add("/src");
        var files = new string[fileCount];
        for (int i = 0; i < fileCount; i++) files[i] = $"/src/file-{i}.txt";
        return new RestoreRequest
        {
            Backup = backup,
            SourcePath = "/src",
            Destination = dest,
            StorageName = "default",
            Revision = 1,
            Files = files,
            TargetPath = "/restore/target",
        };
    }

    [AvaloniaFact]
    public void Begin_adds_session_with_initial_counts()
    {
        var registry = new ActiveRestoreRegistry();
        var request = BuildRequest(fileCount: 100);

        var session = registry.Begin(request.Backup, request);

        Assert.Single(registry.Sessions);
        Assert.True(registry.HasAny);
        Assert.Equal("TestBackup", session.BackupName);
        Assert.Equal("Local", session.DestinationName);
        Assert.Equal("/restore/target", session.TargetPath);
        Assert.Equal(100, session.TotalFiles);
        Assert.True(session.IsRunning);
        Assert.True(session.IsProgressIndeterminate);
        Assert.Equal("Restoring…", session.StatusLabel);
        Assert.DoesNotContain("0%", session.StatusLabel);
    }

    [AvaloniaFact]
    public void UpdateProgress_mutates_session_counts()
    {
        var registry = new ActiveRestoreRegistry();
        var session = registry.Begin(BuildRequest(50).Backup, BuildRequest(50));

        registry.UpdateProgress(session, totalFiles: 50, filesRestored: 12,
            totalBytes: 4_000_000, bytesRestored: 1_200_000);

        Assert.Equal(50, session.TotalFiles);
        Assert.Equal(12, session.RestoredCount);
        Assert.Equal(4_000_000, session.TotalBytes);
        Assert.Equal(1_200_000, session.BytesRestored);
        Assert.Equal(0.30, session.PercentComplete, precision: 2);
        Assert.Equal("12 of 50 files", session.ProgressLabel);
        Assert.False(session.IsProgressIndeterminate);
    }

    [AvaloniaFact]
    public void End_marks_session_terminal_immediately()
    {
        var registry = new ActiveRestoreRegistry();
        var session = registry.Begin(BuildRequest(10).Backup, BuildRequest(10));

        registry.End(session, RestoreTerminalStatus.Success, errorMessage: null);

        Assert.False(session.IsRunning);
        Assert.Equal(RestoreTerminalStatus.Success, session.Status);
        Assert.Contains("Done", session.StatusLabel);
        // Completion does not dismiss a result before the user reads it.
        Assert.Single(registry.Sessions);
        Assert.False(registry.HasRunning);
        registry.Dismiss(session);
        Assert.Empty(registry.Sessions);
    }

    [AvaloniaFact]
    public void End_emits_changed_event()
    {
        var registry = new ActiveRestoreRegistry();
        var session = registry.Begin(BuildRequest(5).Backup, BuildRequest(5));
        int changedCount = 0;
        registry.Changed += () => changedCount++;

        registry.End(session, RestoreTerminalStatus.Error, "engine threw");

        // End triggers a Changed event during the fade-out timer
        // tick — but since we're synchronous + don't wait, we just
        // assert the session is now terminal.
        Assert.Equal(RestoreTerminalStatus.Error, session.Status);
        Assert.Equal("engine threw", session.ErrorMessage);
        Assert.Equal(1, changedCount);
    }

    [AvaloniaFact]
    public void Multiple_concurrent_sessions_coexist()
    {
        var registry = new ActiveRestoreRegistry();
        var s1 = registry.Begin(BuildRequest(10).Backup, BuildRequest(10));
        var s2 = registry.Begin(BuildRequest(20).Backup, BuildRequest(20));
        var s3 = registry.Begin(BuildRequest(30).Backup, BuildRequest(30));

        Assert.Equal(3, registry.Sessions.Count);
        Assert.Contains(s1, registry.Sessions);
        Assert.Contains(s2, registry.Sessions);
        Assert.Contains(s3, registry.Sessions);
        Assert.NotEqual(s1.Id, s2.Id);
        Assert.NotEqual(s2.Id, s3.Id);
    }

    [AvaloniaFact]
    public void Begin_does_not_throw_when_no_files()
    {
        // Restoring everything (no per-leaf list) should still
        // register a card; the engine fills in real numbers as it
        // progresses.
        var registry = new ActiveRestoreRegistry();
        var request = BuildRequest(0);
        var session = registry.Begin(request.Backup, request);
        Assert.NotNull(session);
        Assert.Equal(0, session.TotalFiles);
    }

    [AvaloniaFact]
    public void StatusLabel_changes_with_terminal_status()
    {
        var registry = new ActiveRestoreRegistry();
        var session = registry.Begin(BuildRequest(5).Backup, BuildRequest(5));

        Assert.Contains("Restoring", session.StatusLabel);

        registry.End(session, RestoreTerminalStatus.Cancelled, null);
        Assert.Equal("Cancelled", session.StatusLabel);
    }
}
