using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Quit stops Backspace completely, background service included (U100,
/// 2 November 2026): the owner may stop their service without a prompt. It
/// comes back when they open Backspace, at their next sign-in, or when Windows
/// starts again, including a start after "Shut down" with Fast Startup, which
/// resumes Windows' services as they were (<see cref="SystemServiceTriggers"/>).
/// Waking from sleep or hibernation is not a new start: the service then
/// stays stopped. The record of the Quit lives with the service's files.
/// </summary>
internal static class SystemServiceLifetime
{
    internal const string FileName = "quit.json";
    internal sealed record QuitRecord(int Version, int SessionId, long LogonTime, string Account, uint? BootId, DateTime QuitUtc);

    internal static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>The owner quit: remember the sign-in session, then stop the
    /// service, which ends any run it has, as Quit does in the window.</summary>
    internal static void Quit(SystemService.Registration registration, TimeSpan timeout)
    {
        var sessions = new WindowsInteractiveSessionReader();
        var session = sessions.Current();
        if (session is not null)
        {
            Directory.CreateDirectory(registration.DataDirectory);
            Write(PathFor(registration.DataDirectory), new(1, session.SessionId, session.LogonTime, session.Account,
                WindowsInteractiveSessionReader.CurrentBootId(), DateTime.UtcNow));
        }
        try { SystemService.StopAndWait(registration.ServiceName, timeout); }
        catch
        {
            // Backspace stays open; the service keeps running without a Quit.
            Forget(registration);
            throw;
        }
    }

    /// <summary>Opening Backspace, or signing in, starts it again.</summary>
    internal static void Resume(SystemService.Registration registration, TimeSpan timeout)
    {
        DeleteRecord(PathFor(registration.DataDirectory));
        SystemService.Start(registration.ServiceName, timeout);
    }

    /// <summary>A quit that a cancelled close must undo.</summary>
    internal static void Forget(SystemService.Registration registration) => DeleteRecord(PathFor(registration.DataDirectory));

    /// <summary>Deletes the record of a Quit, waiting out a reader: the
    /// service checking it as it starts, or Dropbox syncing it when the
    /// portable folder is inside Dropbox. Opening 1.1.4 found it "being
    /// used by another process" and said tasks stayed paused (U121,
    /// 4 November 2026). It waits as long as it takes (U135).</summary>
    internal static void DeleteRecord(string path, System.Threading.CancellationToken ct = default)
    {
        try { FileAccessRetry.Run(() => File.Delete(path), path, ct: ct); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Run by the service as it starts. True while the owner's Quit
    /// still holds: the same sign-in session, in the same Windows start.</summary>
    /// <param name="waiting">Called while another program holds the record,
    /// so the service can ask Windows for more time to start (U135).</param>
    internal static bool StaysQuit(string dataDirectory, out string reason, Action? waiting = null)
        => StaysQuit(dataDirectory, WindowsInteractiveSessionReader.CurrentBootId(),
            id => new WindowsInteractiveSessionReader().Read(id), out reason, waiting);

    internal static bool StaysQuit(string dataDirectory, uint? bootId, Func<int, InteractiveSessionIdentity?> readSession, out string reason, Action? waiting = null)
    {
        var path = PathFor(dataDirectory);
        if (!File.Exists(path)) { reason = "Windows or the owner started it"; return false; }
        QuitRecord? record;
        // Read without blocking its deletion: opening Backspace deletes it.
        // Another program holding it (Dropbox, a virus scanner) is waited
        // out, as long as it takes, instead of being read as "still quit"
        // (U135, 4 November 2026).
        while (true)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                record = JsonSerializer.Deserialize<QuitRecord>(file);
                break;
            }
            // Deleted since it was found: Backspace was just opened.
            catch (FileNotFoundException) { reason = "Windows or the owner started it"; return false; }
            catch (Exception ex) when (FileAccessRetry.IsLock(ex)) { waiting?.Invoke(); Thread.Sleep(250); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { record = null; break; }
        }
        if (record is null)
        {
            // Unreadable: keep the Quit. Opening Backspace or signing in
            // replaces it.
            reason = "Backspace was quit (its record could not be read)";
            return true;
        }
        if (record.BootId is { } saved && bootId is { } current && saved != current)
            return Ended(path, "Windows restarted since Backspace was quit", out reason);
        InteractiveSessionIdentity? session;
        try { session = readSession(record.SessionId); }
        catch (Exception) { reason = "Backspace was quit, and the sign-in session could not be checked"; return true; }
        if (session is null || session.LogonTime != record.LogonTime || !string.Equals(session.Account, record.Account, StringComparison.OrdinalIgnoreCase))
            return Ended(path, "the person who quit Backspace has signed out or Windows was shut down", out reason);
        reason = "Backspace was quit in this sign-in session";
        return true;
    }

    private static bool Ended(string path, string why, out string reason)
    {
        reason = why;
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private static void Write(string path, QuitRecord record)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, record); file.Flush(true); }
            FileAccessRetry.Run(() => File.Move(temporary, path, overwrite: true), path);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }
}
