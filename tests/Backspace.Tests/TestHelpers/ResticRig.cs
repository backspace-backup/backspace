using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// A real restic repository in a disposable folder, with the engine that runs
/// restic directly (U175) and every restic command it ran through
/// <see cref="ResticRunner"/>.
/// </summary>
internal sealed class ResticRig : IAsyncDisposable
{
    public readonly TempWorkspace Workspace;
    public readonly SecretsStore Secrets;
    public readonly Destination Destination;
    public readonly Backup Backup;
    public readonly string Source;
    public readonly string Password = Guid.NewGuid().ToString("N");
    public readonly ResticBackrestEngine Engine;
    public readonly ConcurrentQueue<(ResticCommand Command, ResticResult Result)> Commands = new();
    public readonly ConcurrentQueue<string> Log = new();

    /// <param name="fill">Writes the source's files; 20 random 8 KiB files when null.</param>
    public ResticRig(string label, Action<string>? fill = null)
    {
        Assert.True(File.Exists(AppPaths.ResticBinary), "restic is needed: " + AppPaths.ResticBinary);
        Workspace = new TempWorkspace(label);
        Secrets = new SecretsStore();
        Source = Workspace.Sub("source");
        if (fill is not null) fill(Source);
        else for (var i = 0; i < 20; i++) File.WriteAllBytes(Path.Combine(Source, $"file{i:00}.bin"), RandomNumberGenerator.GetBytes(8 * 1024));
        Destination = new Destination { Id = "dest-" + label, Name = "Local " + label, Kind = DestinationKind.LocalFolder,
            PathOrSubpath = Workspace.Sub("repo"), Encrypted = true, StoragePasswordRef = Secrets.PutNew("rig-" + label, Password) };
        // A stand-alone cleanup schedule: a backup here adds a snapshot only.
        Backup = new Backup { Id = "backup-" + label, Name = "Rig " + label, SourcePaths = { Source }, UseVss = false,
            VssDisabledAcknowledged = true, PruneSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = Destination.Id, StorageName = "default" } } };
        Engine = new ResticBackrestEngine(Secrets);
        Engine.ResticCommandFinished += (command, result) => Commands.Enqueue((command, result));
    }

    public string PlanId => ResticBackrestEngine.MakePlanId(Backup, Source, Destination, "default");

    public ResticRepository Repository => Engine.BuildResticRepository(Destination, Backup);

    /// <summary>A backup of the source; its snapshot's id.</summary>
    public async Task<string> BackUpAsync(CancellationToken ct = default)
    {
        var run = await Engine.BackupPruneCheckAsync(Backup, Source, Destination, "default", ct, Log.Enqueue);
        Assert.True(run.Status == BackupRunStatus.Success, run.Summary);
        return MaintenanceRecords.RememberedSnapshot(PlanId)!;
    }

    /// <summary>The restic commands this engine ran whose first word is <paramref name="first"/>.</summary>
    public (ResticCommand Command, ResticResult Result)[] Ran(string first) => Commands.Where(c => c.Command.Arguments[0] == first).ToArray();

    /// <summary>restic run by the test itself (not counted), which must succeed.</summary>
    public async Task<ResticResult> ResticAsync(params string[] arguments)
    {
        var result = await ResticRunner.RunAsync(new ResticCommand(AppPaths.ResticBinary, Repository, arguments) { CaptureOutput = 1 << 20 },
            null, null, CancellationToken.None);
        Assert.True(result.Succeeded, string.Join(' ', arguments) + ": " + result.ErrorText);
        return result;
    }

    public ValueTask DisposeAsync()
    {
        Workspace.Dispose();
        return ValueTask.CompletedTask;
    }
}
