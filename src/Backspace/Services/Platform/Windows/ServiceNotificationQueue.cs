using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Backspace.Services.Platform.Windows;

/// <summary>Private service state beside the executable. A short delivery lease
/// prevents two signed-in desktops from presenting the same pending alert.</summary>
internal sealed class ServiceNotificationQueue(string directory, Func<DateTime>? clock = null)
{
    internal sealed record Claim(BackgroundNotification Notice, string Receipt, DateTime ExpiresUtc);
    private sealed record State(int Version, List<BackgroundNotification> Pending, Claim? Active = null);
    private readonly string _path = Path.Combine(directory, "desktop-notifications.json");
    private DateTime Now => clock?.Invoke() ?? DateTime.UtcNow;

    internal void Add(BackgroundNotification notice)
    {
        Validate(notice);
        using var transaction = StateFileLock.Acquire(_path);
        var state = Read();
        if (state.Pending.Any(n => n.Id == notice.Id)) return;
        state.Pending.Add(notice);
        Save(state);
    }

    internal Claim? Take()
    {
        using var transaction = StateFileLock.Acquire(_path);
        var state = Read();
        if (state.Pending.Count == 0 || state.Active is { } held && held.ExpiresUtc > Now) return null;
        var claim = new Claim(state.Pending[0], Guid.NewGuid().ToString("N"), Now.AddSeconds(60));
        Save(state with { Active = claim });
        return claim;
    }

    internal bool Acknowledge(string id, string receipt)
    {
        using var transaction = StateFileLock.Acquire(_path);
        var state = Read();
        if (state.Active is not { } active || active.Notice.Id != id || active.Receipt != receipt) return false;
        state.Pending.RemoveAll(n => n.Id == id);
        Save(state with { Active = null });
        return true;
    }

    private State Read()
    {
        if (!File.Exists(_path)) return new(1, new());
        using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("The pending notification file needs attention; it was retained.");
        var state = JsonSerializer.Deserialize<State>(file, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
        if (state.Version != 1 || state.Pending is null) throw new InvalidDataException();
        foreach (var notice in state.Pending) Validate(notice);
        if (state.Pending.Select(n => n.Id).Distinct().Count() != state.Pending.Count) throw new InvalidDataException();
        if (state.Active is { } active && (!Guid.TryParseExact(active.Receipt, "N", out _)
            || !state.Pending.Contains(active.Notice))) throw new InvalidDataException();
        return state;
    }

    private void Save(State state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, ServiceRunAuthorization.Json);
        if (bytes.Length > 8 * 1024 * 1024) throw new IOException("Pending desktop alerts are full. Open Backspace to receive them; previous alerts were retained.");
        using (var file = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        Publish(_path + ".tmp", _path);
    }

    internal static void Publish(string pending, string path, Action<string, string>? replace = null, Action<int>? delay = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { (replace ?? ((source, target) => File.Move(source, target, overwrite: true)))(pending, path); return; }
            catch (Exception ex) when (attempt < 5 && FileReplacementErrors.CanRetry(ex))
            { (delay ?? Thread.Sleep)(50 << attempt); }
        }
    }

    internal static void Validate(BackgroundNotification notice)
    {
        if (notice is null || !Guid.TryParseExact(notice.Id, "N", out _) || notice.CreatedUtc.Kind != DateTimeKind.Utc
            || notice.Title is null || notice.Title.Length > 1024 || notice.Detail is null || notice.Detail.Length > 8192
            || !Enum.IsDefined(notice.Severity) || (!notice.Toast && !notice.Sound)
            || notice.BackupId is { Length: > 256 } || (notice.BackupId is null) != (notice.RunId is null)
            || notice.RunId is not null && !Guid.TryParseExact(notice.RunId, "N", out _)) throw new InvalidDataException("Invalid background alert.");
    }
}
