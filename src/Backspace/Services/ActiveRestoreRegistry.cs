using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>Restore sessions remain inspectable until explicitly dismissed.
/// A separate concurrent map tracks operation ownership even before dispatcher
/// updates arrive, so quit and maintenance never mistake UI latency for idleness.</summary>
public sealed class ActiveRestoreRegistry
{
    private readonly ConcurrentDictionary<string, ActiveRestoreSession> _running = new();
    public ObservableCollection<ActiveRestoreSession> Sessions { get; } = new();
    public event Action? Changed;
    public event Action<ActiveRestoreSession>? SessionOpened;
    public bool HasAny => Sessions.Count > 0;
    public bool HasRunning => !_running.IsEmpty;

    public ActiveRestoreSession Begin(Backup backup, RestoreRequest request, Action? cancel = null)
    {
        var session = new ActiveRestoreSession
        {
            Id = Guid.NewGuid().ToString("N"), BackupId = backup.Id, BackupName = backup.Name,
            DestinationName = request.Destination.Name, TargetPath = request.TargetPath,
            SourcePath = request.SourcePath, Revision = request.Revision,
            TotalFiles = request.Files.Count, StartedUtc = DateTime.UtcNow,
            IsRunning = true, Owner = request.SessionOwner, CancelAction = cancel,
        };
        _running[session.Id] = session;
        MarshalToUi(() => { Sessions.Add(session); Changed?.Invoke(); });
        return session;
    }

    public void UpdateProgress(ActiveRestoreSession session, int totalFiles,
        int filesRestored, long totalBytes, long bytesRestored) => MarshalToUi(() =>
    {
        session.TotalFiles = Math.Max(session.TotalFiles, totalFiles);
        session.RestoredCount = filesRestored;
        session.TotalBytes = totalBytes;
        session.BytesRestored = bytesRestored;
        session.RaiseChanged();
    });

    public void End(ActiveRestoreSession session, RestoreTerminalStatus status, string? errorMessage)
    {
        _running.TryRemove(session.Id, out _);
        session.CompletionSource.TrySetResult();
        MarshalToUi(() =>
        {
            session.Status = status;
            session.ErrorMessage = errorMessage;
            session.IsRunning = false;
            session.CancelAction = null;
            session.RaiseChanged();
            Changed?.Invoke();
        });
    }

    public void Open(ActiveRestoreSession session) => MarshalToUi(() => SessionOpened?.Invoke(session));
    public void Dismiss(ActiveRestoreSession session) => MarshalToUi(() =>
    {
        if (!session.IsRunning && Sessions.Remove(session)) Changed?.Invoke();
    });
    public void CancelAll()
    {
        foreach (var session in _running.Values) session.Cancel();
    }
    public Task WaitForCompletionAsync() => Task.WhenAll(_running.Values.Select(s => s.Completion));
    private static void MarshalToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action(); else Dispatcher.UIThread.Post(action);
    }
}

public sealed class ActiveRestoreSession : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string BackupId { get; init; }
    public required string BackupName { get; init; }
    public required string DestinationName { get; init; }
    public required string TargetPath { get; init; }
    public string SourcePath { get; init; } = "";
    public int Revision { get; init; }
    public object? Owner { get; init; }
    public int TotalFiles { get; set; }
    public int RestoredCount { get; set; }
    public long TotalBytes { get; set; }
    public long BytesRestored { get; set; }
    public DateTime StartedUtc { get; init; }
    public bool IsRunning { get; set; }
    public RestoreTerminalStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    internal Action? CancelAction { get; set; }
    internal TaskCompletionSource CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => CompletionSource.Task;
    public bool IsComplete => !IsRunning;
    public string OpenActionLabel => IsRunning ? "View progress →" : "View results →";
    public bool IsProgressIndeterminate => IsRunning && TotalBytes == 0 && RestoredCount == 0;
    public double PercentComplete => TotalBytes > 0 ? Math.Clamp((double)BytesRestored / TotalBytes, 0, 1)
        : TotalFiles == 0 ? 0 : Math.Clamp((double)RestoredCount / TotalFiles, 0, 1);
    public string ProgressLabel => TotalFiles == 0 ? (IsRunning ? "Preparing restore…" : $"{RestoredCount} files recovered") : IsProgressIndeterminate
        ? $"Restoring {TotalFiles} files · final counts pending" : $"{RestoredCount} of {TotalFiles} files";
    public string StatusLabel => IsProgressIndeterminate ? "Restoring…" : IsRunning ? $"Restoring {(int)(PercentComplete * 100)}%" : Status switch
    {
        RestoreTerminalStatus.Success => "Done",
        RestoreTerminalStatus.Warning => "Done with warnings",
        RestoreTerminalStatus.Error => "Failed",
        RestoreTerminalStatus.Cancelled => "Cancelled",
        _ => Status.ToString(),
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;
    public void RaiseChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        Changed?.Invoke();
    }
    public void Cancel() { try { CancelAction?.Invoke(); } catch (ObjectDisposedException) { } }
}
