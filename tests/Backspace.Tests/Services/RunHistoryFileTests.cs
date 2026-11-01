using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class RunHistoryFileTests
{
    private static readonly JsonSerializerOptions Json = new();
    private static RunRecord Record(string id, BackupRunStatus status = BackupRunStatus.Success) => new()
    { Id=id, BackupId="backup", BackupName="history", StartedUtc=DateTime.UtcNow, Status=status };

    [Fact]
    public async Task RealTemporaryLockIsRetriedAndBothRunsSurvive()
    {
        using var ws=new TempWorkspace();var store=new LogStore();
        store.AppendRun(Record("first"));var path=store.GetHistoryFile("history");
        var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var append=Task.Run(()=>store.AppendRun(Record("second")));
        // NTFS refuses to replace a file another handle is reading, so the
        // writer retries. GitHub's runner keeps its temporary folder on a volume
        // that allows the replace at once (31 October 2026); there the append may
        // already be done. Either way both runs must survive (checked below).
        try { await Task.Delay(150, TestContext.Current.CancellationToken); }
        finally { held.Dispose(); }
        await append.WaitAsync(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken);
        Assert.Equal(new[]{"first","second"},store.LoadHistory("history").Select(r=>r.Id).OrderBy(x=>x));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!,"*.pending-*.json"));
    }

    [Fact]
    public void PersistentLockKeepsOldAndPendingHistory_AndNextAppendRecoversBoth()
    {
        using var ws=new TempWorkspace();var path=Path.Combine(ws.Root,"history.json");
        RunHistoryFile.Write(path,new(){Record("first")},Json);
        var previous=File.ReadAllBytes(path);var delays=0;
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            Assert.ThrowsAny<IOException>(()=>RunHistoryFile.Write(path,new(){Record("first"),Record("second")},Json,delay:_=>delays++));
        Assert.Equal(5,delays);Assert.Equal(previous,File.ReadAllBytes(path));
        Assert.Equal(2,RunHistoryFile.Read(path,Json).Count);
        var recovered=RunHistoryFile.Read(path,Json);recovered.Add(Record("third"));
        RunHistoryFile.Write(path,recovered,Json);
        Assert.Equal(3,RunHistoryFile.Read(path,Json).Count);
        Assert.Single(Directory.GetFiles(ws.Root,"history.json*"));
    }

    [Theory]
    [InlineData(1176)]
    [InlineData(1177)]
    public void FailureAfterOriginalRenamePreservesAndRecoversBothCopies(int error)
    {
        using var ws=new TempWorkspace();var path=Path.Combine(ws.Root,"history.json");
        RunHistoryFile.Write(path,new(){Record("first")},Json);
        var calls=0;
        Assert.Throws<IOException>(()=>RunHistoryFile.Write(path,new(){Record("first"),Record("second")},Json,
            (pending,current,previous)=>{calls++;File.Move(current,previous);throw new IOException("Simulated interrupted Windows replacement",unchecked((int)0x80070000)|error);},
            _=>throw new Exception("An uncertain rename must not be retried")));
        Assert.Equal(1,calls);Assert.False(File.Exists(path));Assert.True(RunHistoryFile.Exists(path));
        Assert.Equal(2,RunHistoryFile.Read(path,Json).Count);
        RunHistoryFile.Write(path,RunHistoryFile.Read(path,Json),Json);
        Assert.True(File.Exists(path));Assert.Equal(2,RunHistoryFile.Read(path,Json).Count);
        Assert.Single(Directory.GetFiles(ws.Root,"history.json*"));
    }

    [Fact]
    public void UndeletableOldPendingCopyCannotOverrideNewerCommittedStatus()
    {
        using var ws=new TempWorkspace();var path=Path.Combine(ws.Root,"history.json");
        RunHistoryFile.Write(path,new(){Record("same",BackupRunStatus.Failed)},Json);
        Assert.Throws<IOException>(()=>RunHistoryFile.Write(path,new(){Record("same",BackupRunStatus.Skipped)},Json,
            (_,_,_)=>throw new IOException("Disk full",unchecked((int)0x80070070))));
        var oldPending=Assert.Single(Directory.GetFiles(ws.Root,"*.pending-*.json"));
        using(var held=new FileStream(oldPending,FileMode.Open,FileAccess.Read,FileShare.Read))
            Assert.Throws<IOException>(()=>RunHistoryFile.Write(path,new(){Record("same",BackupRunStatus.Success)},Json));
        Assert.Equal(BackupRunStatus.Success,Assert.Single(RunHistoryFile.Read(path,Json)).Status);
        RunHistoryFile.Write(path,RunHistoryFile.Read(path,Json),Json);
        Assert.Equal(BackupRunStatus.Success,Assert.Single(RunHistoryFile.Read(path,Json)).Status);
        Assert.Single(Directory.GetFiles(ws.Root,"history.json*"));
    }

    [Theory]
    [InlineData(5,true)] [InlineData(32,true)] [InlineData(33,true)] [InlineData(1175,true)]
    [InlineData(112,false)] [InlineData(1176,false)] [InlineData(1177,false)] [InlineData(3,false)]
    public void OnlyKnownUnchangedFileFailuresAreRetried(int error,bool expected) =>
        Assert.Equal(expected,FileReplacementErrors.CanRetry(new IOException("Synthetic Windows error",unchecked((int)0x80070000)|error)));

    [Fact]
    public void ArbitrarySimilarlyNamedFilesAreNeverReadOrDeleted()
    {
        using var ws=new TempWorkspace();var path=Path.Combine(ws.Root,"history.json");
        var unrelated=path+".pending-my-notes.json";File.WriteAllText(unrelated,"Not a history file");
        RunHistoryFile.Write(path,new(){Record("first")},Json);
        Assert.Single(RunHistoryFile.Read(path,Json));Assert.Equal("Not a history file",File.ReadAllText(unrelated));
    }

    [Theory]
    [InlineData(1176)] [InlineData(1177)]
    public void VisibleFileWaitDoesNotReplayAnUncertainRename(int error)
    {
        using var ws = new TempWorkspace();
        var calls = 0;
        Assert.Throws<IOException>(() => FileAccessRetry.Run(() =>
        {
            calls++;
            throw new IOException("Synthetic partial rename", unchecked((int)0x80070000) | error);
        }, Path.Combine(ws.Root, "config.json"), FileWaitReason.Engine, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void InterruptedSerializationIsNotTreatedAsCompleteRecovery()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.Root, "history.json");
        RunHistoryFile.Write(path, new() { Record("first") }, Json);
        var incomplete = path + $".pending-{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.json.tmp";
        File.WriteAllText(incomplete, "[{\"Id\":");
        Assert.Equal("first", Assert.Single(RunHistoryFile.Read(path, Json)).Id);
        RunHistoryFile.Write(path, new() { Record("first"), Record("second") }, Json);
        Assert.Equal(2, RunHistoryFile.Read(path, Json).Count);
    }
}
