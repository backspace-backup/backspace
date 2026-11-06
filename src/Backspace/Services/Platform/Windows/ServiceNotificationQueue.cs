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
        using var file = FileAccessRetry.Get(() => new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), _path);
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
        WriteRecord(_path, bytes);
    }

    /// <summary>Writes a small record beside the settings: to a fresh
    /// temporary name, then over the record, waiting while another program
    /// has either open, as long as it takes (U135). With the portable folder
    /// in Dropbox, Dropbox (or a virus scanner) opened the fixed ".tmp" name
    /// and the record, and the service could neither save that a backup was
    /// interrupted nor start the resumed one (U131, 4 November 2026).</summary>
    internal static void WriteRecord(string path, byte[] bytes, CancellationToken ct = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            FileAccessRetry.Run(() =>
            {
                using var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
                file.Write(bytes); file.Flush(true);
            }, temporary, FileWaitReason.BackspaceFile, ct);
            FileAccessRetry.Run(() => File.Move(temporary, path, overwrite: true), path, ct: ct);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void Publish(string pending, string path, Action<string, string>? replace = null)
        => FileAccessRetry.Run(() => (replace ?? ((source, target) => File.Move(source, target, overwrite: true)))(pending, path), path);

    internal static void Validate(BackgroundNotification notice)
    {
        if (notice is null || !Guid.TryParseExact(notice.Id, "N", out _) || notice.CreatedUtc.Kind != DateTimeKind.Utc
            || notice.Title is null || notice.Title.Length > 1024 || notice.Detail is null || notice.Detail.Length > 8192
            || !Enum.IsDefined(notice.Severity) || (!notice.Toast && !notice.Sound)
            || notice.BackupId is { Length: > 256 } || (notice.BackupId is null) != (notice.RunId is null)
            || notice.RunId is not null && !Guid.TryParseExact(notice.RunId, "N", out _)) throw new InvalidDataException("Invalid background alert.");
    }
}
