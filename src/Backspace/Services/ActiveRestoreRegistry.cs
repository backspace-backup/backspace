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

    public ActiveRestoreSession BeginTest(Backup backup, string label, DateTime startedUtc, Action? cancel, RestoreControl? control = null)
    {
        var session = new ActiveRestoreSession { Id = Guid.NewGuid().ToString("N"), BackupId = backup.Id,
            BackupName = backup.Name, DestinationName = "All backup destinations", TargetPath = "Temporary copies only · your original files are unchanged",
            SourcePath = label, StartedUtc = startedUtc, IsRunning = true, IsTestRestore = true, CancelAction = cancel };
        AttachControl(session, control);
        _running[session.Id] = session;
        MarshalToUi(() => { Sessions.Add(session); Changed?.Invoke(); });
        return session;
    }

    public void Describe(ActiveRestoreSession session, string detail) => MarshalToUi(() => { session.Detail = detail; session.RaiseChanged(); });

    public ActiveRestoreSession Begin(Backup backup, RestoreRequest request, Action? cancel = null, RestoreControl? control = null)
    {
        var session = new ActiveRestoreSession
        {
            Id = Guid.NewGuid().ToString("N"), BackupId = backup.Id, BackupName = backup.Name,
            DestinationName = request.Destination.Name, TargetPath = request.TargetPath,
            SourcePath = request.SourcePath, Revision = request.Revision,
            TotalFiles = request.Files.Count, StartedUtc = DateTime.UtcNow,
            IsRunning = true, Owner = request.SessionOwner, CancelAction = cancel,
        };
        AttachControl(session, control);
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
        if (session.Control is not null && session.ControlChanged is not null)
            session.Control.Changed -= session.ControlChanged;
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

    internal void FinishTransfer(ActiveRestoreSession session, RestoreTerminalStatus status, string? errorMessage)
    {
        if (session.Control is null) { End(session, status, errorMessage); return; }
        session.TransferStatus = status;
        session.TransferError = errorMessage;
        session.TransferFinished = true;
        if (session.Control.State == RestoreControlState.Complete) FinishControlledSession(session);
        else MarshalToUi(session.RaiseChanged);
    }

    private void FinishControlledSession(ActiveRestoreSession session)
    {
        var control = session.Control!;
        var status = control.CompletionError is not null || control.RollbackRequired
            ? RestoreTerminalStatus.Error : control.WasCancelled ? RestoreTerminalStatus.Cancelled : session.TransferStatus;
        var error = control.CompletionError ?? (control.WasCancelled
            ? "Restore cancelled. Files copied by this attempt were removed; existing originals were preserved."
            : control.RollbackRequired ? "Restore could not finish. This attempt's output was removed; open the restore results for details."
            : session.TransferError);
        if (control.WasCancelled || control.RollbackRequired)
            UpdateProgress(session, session.TotalFiles, 0, session.TotalBytes, 0);
        End(session, status, error);
    }

    public void Open(ActiveRestoreSession session) => MarshalToUi(() => SessionOpened?.Invoke(session));
    public void Dismiss(ActiveRestoreSession session) => _ = DismissAsync(session);
    public Task DismissAsync(ActiveRestoreSession session)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MarshalToUi(async () =>
        {
            if (session.IsRunning || session.IsDismissing) { done.SetResult(); return; }
            session.IsDismissing = true;
            try
            {
                if (session.DismissAction is { } dismiss) await Task.Run(dismiss);
                if (Sessions.Remove(session)) Changed?.Invoke();
            }
            catch (OperationCanceledException) { /* Keep the result visible. */ }
            catch (Exception ex) { session.ErrorMessage = "Could not hide this result: " + ex.Message; session.RaiseChanged(); }
            finally { session.IsDismissing = false; done.SetResult(); }
        });
        return done.Task;
    }
    public void CancelAll()
    {
        foreach (var session in _running.Values) session.Cancel();
    }
    public Task WaitForCompletionAsync() => Task.WhenAll(_running.Values.Select(s => s.Completion));
    private void AttachControl(ActiveRestoreSession session, RestoreControl? control)
    {
        session.Control = control;
        if (control is null) return;
        session.ControlChanged = () =>
        {
            if (session.TransferFinished && control.State == RestoreControlState.Complete) FinishControlledSession(session);
            else MarshalToUi(session.RaiseChanged);
        };
        control.Changed += session.ControlChanged;
    }
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
    public bool IsTestRestore { get; init; }
    internal VerifyOutcome? VerificationOutcome { get; set; }
    internal string? VerificationError { get; set; }
    public bool IsCancelling { get; private set; }
    public string Detail { get; set; } = "";
    public string OperationLabel => IsTestRestore ? "TEST RESTORE" : "FILE RESTORE";
    internal RestoreControl? Control { get; set; }
    internal Action? ControlChanged { get; set; }
    internal bool TransferFinished { get; set; }
    internal RestoreTerminalStatus TransferStatus { get; set; }
    internal string? TransferError { get; set; }
    public bool IsPaused => Control?.State == RestoreControlState.Paused && IsRunning;
    public bool IsPausing => Control?.State == RestoreControlState.Pausing && IsRunning;
    public bool HasPauseControl => Control is not null && IsRunning;
    private bool _changingPause;
    internal bool IsDismissing { get; set; }
    public bool CanTogglePause => IsRunning && !IsCancelling && !_changingPause && Control?.State is RestoreControlState.Running or RestoreControlState.Paused;
    public string PauseLabel => IsPaused ? "Resume" : IsPausing ? "Pausing…" : "Pause";
    public string CancelLabel => IsCancelling ? "Cancelling…" : "Cancel";
    public bool CanCancel => IsRunning && !IsCancelling;
    public string TargetLabel => IsTestRestore ? TargetPath : $"Restoring into {TargetPath}";
    public RestoreTerminalStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    internal Action? CancelAction { get; set; }
    internal Action? DismissAction { get; set; }
    internal TaskCompletionSource CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => CompletionSource.Task;
    public bool IsComplete => !IsRunning;
    public string OpenActionLabel => IsRunning ? "View progress →" : "View results →";
    public bool IsProgressIndeterminate => IsRunning && !IsPaused && TotalBytes == 0 && RestoredCount == 0;
    public double PercentComplete => TotalBytes > 0 ? Math.Clamp((double)BytesRestored / TotalBytes, 0, 1)
        : TotalFiles == 0 ? 0 : Math.Clamp((double)RestoredCount / TotalFiles, 0, 1);
    public string ProgressLabel => IsTestRestore ? $"{RestoredCount:N0} of {TotalFiles:N0} files checked · {BytesRestored / 1048576d:N1} / {TotalBytes / 1048576d:N1} MiB" : TotalFiles == 0 ? (IsRunning ? "Preparing restore…" : $"{RestoredCount} files recovered") : IsProgressIndeterminate
        ? $"Restoring {TotalFiles} files · final counts pending" : $"{RestoredCount} of {TotalFiles} files";
    public string StatusLabel => IsCancelling && IsRunning ? "Cancelling…" : IsPaused ? "Paused" : IsPausing ? "Pausing…" : TransferFinished && IsRunning ? "Finishing restore…" : IsProgressIndeterminate ? (IsTestRestore ? "Preparing check…" : "Restoring…") : IsRunning ? $"{(IsTestRestore ? "Checking" : "Restoring")} {(int)(PercentComplete * 100)}%" : Status switch
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
    public void Cancel()
    {
        if (!CanCancel) return;
        IsCancelling = true; RaiseChanged();
        try { CancelAction?.Invoke(); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { ErrorMessage = "Could not save cancellation: " + ex.Message; IsCancelling = false; RaiseChanged(); }
    }
    public async void TogglePause() => await TogglePauseAsync();
    public async Task TogglePauseAsync()
    {
        if (!CanTogglePause) return;
        var resume = IsPaused;
        _changingPause = true; RaiseChanged();
        try { await Task.Run(() => { if (resume) Control!.Resume(); else Control!.Pause(); }); }
        catch (OperationCanceledException) { /* The previous pause setting is retained. */ }
        catch (Exception ex) { ErrorMessage = "Could not change pause state: " + ex.Message; RaiseChanged(); }
        finally { _changingPause = false; RaiseChanged(); }
    }
}
