using System;
using System.Collections.ObjectModel;
using System.Threading;
using Avalonia.Threading;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Per-process registry of in-flight restore operations. Sessions
/// register on Start, update progress as the engine emits events,
/// and unregister on terminal status (Success / Warning / Error /
/// Cancelled). The registry's <see cref="Sessions"/> collection
/// drives the "Restores" left-nav cards view; <see cref="HasAny"/>
/// drives the sidebar item's <c>IsVisible</c>.
///
/// Concurrency: the engine layer can fire from arbitrary thread-pool
/// threads. Mutations of <see cref="Sessions"/> (an
/// <see cref="ObservableCollection{T}"/>) must happen on the UI
/// dispatcher — otherwise Avalonia throws when the bound
/// ItemsControl receives a change notification from a background
/// thread. <see cref="MarshalToUi"/> centralises that.
/// </summary>
public sealed class ActiveRestoreRegistry
{
    private static Serilog.ILogger _log => AppLogger.For<ActiveRestoreRegistry>();

    /// <summary>Live cards bound to the "Restores" nav view. Mutate
    /// only via the registry methods so UI-thread marshalling stays
    /// centralised.</summary>
    public ObservableCollection<ActiveRestoreSession> Sessions { get; } = new();

    public event Action? Changed;

    public bool HasAny => Sessions.Count > 0;

    /// <summary>Begin a restore session. Returns the session for the
    /// engine to update as progress lands. Idempotent on the same id
    /// (re-registering returns the existing session).</summary>
    public ActiveRestoreSession Begin(Backup backup, RestoreRequest request)
    {
        var id = Guid.NewGuid().ToString("N");
        var session = new ActiveRestoreSession
        {
            Id = id,
            BackupId = backup.Id,
            BackupName = backup.Name,
            DestinationName = request.Destination.Name,
            TargetPath = request.TargetPath,
            TotalFiles = request.Files.Count > 0 ? request.Files.Count
                       : request.IncludePatterns?.Count ?? 0,
            StartedUtc = DateTime.UtcNow,
            Status = RestoreTerminalStatus.Success,  // running placeholder; final on End
            IsRunning = true,
        };

        MarshalToUi(() =>
        {
            Sessions.Add(session);
            Changed?.Invoke();
        });
        _log.Information("Restore session {Id} begin: backup={Backup} dest={Dest}",
            id, backup.Name, request.Destination.Name);
        return session;
    }

    /// <summary>Update a live session with the latest progress numbers
    /// from the engine. Safe to call from any thread.</summary>
    public void UpdateProgress(ActiveRestoreSession session, int totalFiles,
        int filesRestored, long totalBytes, long bytesRestored)
    {
        MarshalToUi(() =>
        {
            session.TotalFiles    = Math.Max(session.TotalFiles, totalFiles);
            session.RestoredCount = filesRestored;
            session.TotalBytes    = totalBytes;
            session.BytesRestored = bytesRestored;
            session.RaiseChanged();
        });
    }

    /// <summary>Mark a session terminal and remove from the live list
    /// after a short fade. Removal is delayed by 4 s so the card
    /// stays visible long enough for the user to read the final
    /// status before it disappears — same UX pattern as a transient
    /// toast.</summary>
    public void End(ActiveRestoreSession session, RestoreTerminalStatus status, string? errorMessage)
    {
        MarshalToUi(() =>
        {
            session.Status = status;
            session.ErrorMessage = errorMessage;
            session.IsRunning = false;
            session.RaiseChanged();
            _log.Information("Restore session {Id} end: status={Status}", session.Id, status);
        });

        // Fade the card after 4 s. Timer fires on a thread-pool
        // thread; ALWAYS Post the removal back to the dispatcher —
        // never use the synchronous CheckAccess path, because
        // Dispatcher.UIThread.CheckAccess() can return TRUE on a
        // thread-pool thread when there's no Avalonia application
        // context (between test scenarios, headless teardown), which
        // would race the ObservableCollection mutation against the
        // real UI thread. Guard Remove with a Contains check so we
        // tolerate a double-fire (theoretical re-entry from the
        // Changed event handler) without throwing.
        var timer = new Timer(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Sessions.Contains(session) && Sessions.Remove(session))
                    Changed?.Invoke();
            });
        }, null, dueTime: TimeSpan.FromSeconds(4), period: Timeout.InfiniteTimeSpan);
        // Anchor the timer on the session so GC doesn't collect it
        // before fire. Disposes when the session is GC'd, after the
        // ObservableCollection no longer holds a reference.
        session.AttachTimer(timer);
    }

    private static void MarshalToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}

/// <summary>One in-flight restore. Bound to the ActiveRestores card
/// view; mutates on the UI thread only (the registry centralises the
/// dispatcher marshalling).</summary>
public sealed class ActiveRestoreSession
{
    private Timer? _fadeTimer;

    public required string Id { get; init; }
    public required string BackupId { get; init; }
    public required string BackupName { get; init; }
    public required string DestinationName { get; init; }
    public required string TargetPath { get; init; }
    public int TotalFiles { get; set; }
    public int RestoredCount { get; set; }
    public long TotalBytes { get; set; }
    public long BytesRestored { get; set; }
    public DateTime StartedUtc { get; init; }
    public bool IsRunning { get; set; }
    public RestoreTerminalStatus Status { get; set; }
    public string? ErrorMessage { get; set; }

    public double PercentComplete => TotalFiles == 0 ? 0
        : Math.Min(1.0, (double)RestoredCount / TotalFiles);
    public string ProgressLabel => $"{RestoredCount} of {TotalFiles} files";
    public string StatusLabel => IsRunning
        ? $"Restoring {(int)(PercentComplete * 100)}%"
        : Status switch
          {
              RestoreTerminalStatus.Success   => "Done",
              RestoreTerminalStatus.Warning   => "Done (with warnings)",
              RestoreTerminalStatus.Error     => "Failed",
              RestoreTerminalStatus.Cancelled => "Cancelled",
              _ => Status.ToString(),
          };

    public event Action? Changed;
    public void RaiseChanged() => Changed?.Invoke();

    /// <summary>Anchor the post-end fade timer on the session so it
    /// stays alive long enough to fire. Disposed if the registry
    /// removes the session via some other path.</summary>
    public void AttachTimer(Timer t)
    {
        _fadeTimer?.Dispose();
        _fadeTimer = t;
    }
}
