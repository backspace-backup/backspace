using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Backspace.Services;

internal sealed record FileWaitReason(string Title, string StopLabel, string Consequence)
{
    internal static readonly FileWaitReason TestRestoreOwner = new("Waiting to start Test restore", "Stop test restore",
        "Another check or program is using the saved progress for this backup. Stopping leaves that progress unchanged.");
    internal static readonly FileWaitReason Quit = new("Waiting to pause scheduled backups", "Cancel quit",
        "Cancelling keeps Backspace open. Your scheduled backups keep their previous settings.");
    internal static readonly FileWaitReason Resume = new("Waiting to resume scheduled backups", "Leave tasks paused",
        "Stopping this save keeps the previous pause in place. Reopen Backspace to try again.");
    internal static readonly FileWaitReason UndoQuit = new("Waiting to undo the task pause", "Leave tasks paused",
        "Backspace will stay open, but scheduled tasks remain paused until you reopen it or sign out.");
    internal static readonly FileWaitReason Engine = new("Waiting to start the backup engine", "Stop this attempt",
        "Stopping cancels this attempt to start the engine. Existing backups and the previous configuration are kept.");
    internal static readonly FileWaitReason Progress = new("Waiting to save Test restore progress", "Stop test restore",
        "Stopping ends this check. Previously saved progress is kept; Backspace will also try to save that the check was stopped.");
    internal static readonly FileWaitReason Result = new("Waiting to save the Test restore result", "Stop saving",
        "The previous saved result stays in place. If the stop cannot be saved, this check may resume when Backspace next opens.");
    internal static readonly FileWaitReason Pause = new("Waiting to save the Test restore pause", "Cancel this change",
        "The check keeps its previous pause setting. You can try the pause or resume action again.");
    internal static readonly FileWaitReason Dismiss = new("Waiting to hide this saved result", "Keep the result",
        "The result stays in the list. You can hide it later.");
    internal static readonly FileWaitReason Cleanup = new("Waiting to remove temporary restore files", "Leave temporary files",
        "Stopping leaves the remaining temporary copies in the folder below. Cleanup will be reported as unfinished; it does not delete your backup or recovered destination files.");
    internal static readonly FileWaitReason Restore = new("Waiting to place a restored file", "Stop restore",
        "Another program is using this file. Stopping cancels the restore and recovers replaced originals. If a file remains locked during recovery, its saved copy is kept and its location is reported.");
}

/// <summary>Retries only Windows file access conflicts. Call from a worker,
/// never the dispatcher. A slow wait is visible and cancellable; it has no
/// arbitrary failure deadline and never changes permissions or old file data.</summary>
internal static class FileAccessRetry
{
    private static readonly ConcurrentDictionary<Guid, FileWaitNotice> Waiting = new();
    internal static event Action? Changed;
    internal static FileWaitNotice[] Snapshot() => Waiting.Values.OrderBy(n => n.Started).ToArray();

    internal static async Task RunAsync(Func<CancellationToken, Task> action, string path, FileWaitReason reason,
        Func<Exception, bool> retryable, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        FileWaitNotice? notice = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                try { await action(stop.Token).ConfigureAwait(false); return; }
                catch (Exception ex) when (retryable(ex))
                {
                    if (notice is null && elapsed.Elapsed >= TimeSpan.FromSeconds(5))
                    {
                        notice = new FileWaitNotice(path, reason, stop); Waiting[notice.Id] = notice; Changed?.Invoke();
                        AppLogger.For("FileAccessRetry").Warning("{Reason}: {Path}. Retrying until access returns or the operation is stopped.", reason.Title, path);
                    }
                    await Task.Delay(1000, stop.Token).ConfigureAwait(false);
                }
            }
        }
        finally { if (notice is not null) { Waiting.TryRemove(notice.Id, out _); Changed?.Invoke(); } }
    }

    internal static void Run(Action action, string path, FileWaitReason reason,
        CancellationToken ct = default, bool directoryCleanup = false)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        FileWaitNotice? notice = null;
        var elapsed = Stopwatch.StartNew();
        var attempt = 0;
        try
        {
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                try { action(); return; }
                catch (Exception ex) when (IsAccessConflict(ex, path, directoryCleanup))
                {
                    if (notice is null && elapsed.Elapsed >= TimeSpan.FromSeconds(5))
                    {
                        notice = new FileWaitNotice(path, reason, stop);
                        Waiting[notice.Id] = notice;
                        AppLogger.For("FileAccessRetry").Warning(
                            "Waiting for file access at {Path}: {Reason}. Retrying until access returns or the operation is stopped. {Error}",
                            path, reason.Title, ex.Message);
                        Changed?.Invoke();
                    }
                    var delay = attempt++ switch { 0 => 100, 1 => 200, 2 => 400, 3 => 800, _ => 1000 };
                    // WaitOne wakes immediately on Cancel, without a busy loop.
                    if (stop.Token.WaitHandle.WaitOne(delay)) stop.Token.ThrowIfCancellationRequested();
                }
            }
        }
        finally
        {
            if (notice is not null) { Waiting.TryRemove(notice.Id, out _); Changed?.Invoke(); }
        }
    }

    private static bool IsAccessConflict(Exception error, string path, bool directoryCleanup) =>
        OperatingSystem.IsWindows() && error is IOException or UnauthorizedAccessException
        && (directoryCleanup || !Directory.Exists(path))
        && (FileReplacementErrors.CanRetry(error) || (directoryCleanup && (error.HResult & 0xFFFF) == 145));
}

internal sealed partial class FileWaitNotice : ObservableObject
{
    private readonly CancellationTokenSource _stop;
    internal Guid Id { get; } = Guid.NewGuid();
    internal DateTime Started { get; } = DateTime.UtcNow;
    public string Path { get; }
    public string Title { get; }
    public string StopLabel { get; }
    public string Consequence { get; }
    [ObservableProperty] private bool _showDetails = true;
    [ObservableProperty] private bool _canStop = true;

    internal FileWaitNotice(string path, FileWaitReason reason, CancellationTokenSource stop)
    {
        Path = path; Title = reason.Title; StopLabel = reason.StopLabel;
        Consequence = reason.Consequence; _stop = stop;
    }

    [RelayCommand] private void KeepWaiting() => ShowDetails = false;
    [RelayCommand] private void Details() => ShowDetails = true;
    [RelayCommand] private void Stop()
    {
        CanStop = false;
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
    }
}
