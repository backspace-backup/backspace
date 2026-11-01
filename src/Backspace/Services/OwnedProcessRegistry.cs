using System;
using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services;

/// <summary>Persists identities only for processes we actually started. A binary
/// path is not ownership: another running Backspace, or the user, may use it too.
/// PID and exact creation time protect against process-ID reuse. An unreadable
/// identity is deliberately different from a confirmed exited process.</summary>
internal static class OwnedProcessRegistry
{
    internal sealed record Identity(int Pid, long StartedUtcTicks, string BinaryPath);
    internal sealed record Ownership(Identity Owner, Identity Child);
    internal enum Presence { Missing, Matching, Different, Unknown }

    internal static Identity Capture(Process process) => new(process.Id,
        process.StartTime.ToUniversalTime().Ticks,
        Path.GetFullPath(GetExecutablePath(process)));

    private static string GetExecutablePath(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return process.MainModule?.FileName
                ?? throw new InvalidOperationException("Cannot identify the child executable.");

        // MainModule can be absent before the child's loader has run. Query the
        // OS image identity through the process handle instead; never substitute
        // StartInfo.FileName, which describes our request rather than the process.
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (!QueryFullProcessImageName(process.SafeHandle, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify the child executable.");
        return path.ToString();
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags,
        StringBuilder path, ref int size);

    internal static Ownership CaptureOwnership(Process child)
    {
        using var owner = Process.GetCurrentProcess();
        return new(Capture(owner), Capture(child));
    }

    internal static bool CanReap(Ownership ownership, Func<Identity, Presence> inspect)
        => inspect(ownership.Owner) is Presence.Missing or Presence.Different
            && inspect(ownership.Child) == Presence.Matching;

    internal static Presence Inspect(Identity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            if (process.HasExited) return Presence.Missing;
            var live = Capture(process);
            return Matches(identity, live) ? Presence.Matching : Presence.Different;
        }
        catch (ArgumentException) { return Presence.Missing; }
        catch { return Presence.Unknown; }
    }

    internal static bool Matches(Identity expected, Identity actual)
        => expected.Pid == actual.Pid && expected.StartedUtcTicks == actual.StartedUtcTicks
            && string.Equals(expected.BinaryPath, actual.BinaryPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static void Write(string path, Ownership ownership)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(ownership));
            File.Move(temp, path, overwrite: true);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    internal static Ownership? Read(string path)
    {
        try { return JsonSerializer.Deserialize<Ownership>(File.ReadAllText(path)); }
        catch { return null; }
    }

    internal static void Register(Process child)
    {
        try
        {
            var ownership = CaptureOwnership(child);
            Write(Path.Combine(AppPaths.ConfigRoot, "process-owners",
                $"{ownership.Child.Pid}-{ownership.Child.StartedUtcTicks}.json"), ownership);
        }
        catch (Exception ex)
        {
            AppLogger.For("ProcessOwnership").Warning(ex,
                "Could not persist child identity; job-object lifetime protection remains active");
        }
    }

    internal static void Reap(string path, Func<Identity, Presence>? inspect = null,
        Action<Identity>? terminate = null)
    {
        var ownership = Read(path);
        if (ownership is null) return; // Legacy/unreadable records are not authority to kill.
        inspect ??= Inspect;
        if (CanReap(ownership, inspect))
        {
            if (terminate is not null) terminate(ownership.Child);
            else
            {
                using var child = Process.GetProcessById(ownership.Child.Pid);
                // Inspect and act on the same process handle, closing the PID-reuse race.
                if (!Matches(ownership.Child, Capture(child))) return;
                child.Kill(entireProcessTree: true);
                child.WaitForExit(5000);
            }
        }
        if (inspect(ownership.Child) is Presence.Missing or Presence.Different)
            File.Delete(path);
    }

    internal static void Sweep()
    {
        var directory = Path.Combine(AppPaths.ConfigRoot, "process-owners");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try { Reap(path); }
            catch (Exception ex) { AppLogger.For("ProcessOwnership").Debug(ex, "Could not reap recorded child"); }
        }
    }
}
