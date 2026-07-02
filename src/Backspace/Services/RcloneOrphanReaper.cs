using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Backspace.Services.Platform;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// On startup, scan for orphaned <c>rclone.exe</c> processes from prior
/// Backspace sessions and terminate them. Backspace launches Backrest
/// via <see cref="WindowsKillOnExitJobObject"/> which kills the entire
/// process tree when Backspace dies — but corner cases survive: a
/// Backrest crash that doesn't take its rclone child with it, a
/// detached subprocess from before the job object was attached, or a
/// user who killed Backspace at the right millisecond.
///
/// Real-world consequence: rclone.exe lingers in the background,
/// holding open a network handle on a 4G/metered connection — exactly
/// the bug surfaced in <c>BACKSPACE_PORT_DESIGN.md § 6.3</c>.
///
/// Heuristic for "is this rclone ours?":
///   1. Process name = "rclone" (handles .exe / no-extension on Unix).
///   2. <see cref="Process.MainModule"/> resolves to our bundled
///      <see cref="AppPaths.RcloneBinary"/> path. Other rclone
///      installations on PATH stay untouched.
///   3. Process start time is BEFORE the current Backspace's start
///      time. The orphan is by definition a survivor from a prior
///      session; a sibling rclone Backrest spawns DURING this session
///      starts after us.
///
/// Triple guard makes false positives essentially impossible: a user
/// who happens to be running our exact bundled rclone.exe from an
/// independent shell would have to have done so BEFORE Backspace
/// launched, which makes them indistinguishable from an orphan — and
/// killing in that case is the correct behaviour anyway (the user
/// pointed our bundled binary at their own task, with no Backrest
/// parent in sight). Net effect: zero collateral damage in practice.
/// </summary>
public static class RcloneOrphanReaper
{
    private static ILogger _log => AppLogger.For("RcloneOrphanReaper");

    /// <summary>
    /// Sweep once. Call early in Program.cs main, before launching the
    /// Backrest sidecar — that way the new sidecar's rclone children
    /// (started AFTER our process) are safely outside the reaper's
    /// "started before us" filter.
    /// </summary>
    public static void Sweep()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            SweepWindows();
        }
        catch (Exception ex)
        {
            // Reaper failure must never block app startup. Log + carry
            // on; the worst case is one more orphan stays in flight.
            _log.Warning(ex, "Rclone orphan reaper sweep threw");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SweepWindows()
    {
        var ourPath = TryNormalisePath(AppPaths.RcloneBinary);
        if (ourPath is null)
        {
            _log.Debug("Skipping orphan sweep: rclone binary path didn't resolve");
            return;
        }

        var us = Process.GetCurrentProcess();
        var usStartTime = us.StartTime;

        int killed = 0;
        int inspected = 0;
        // Process.GetProcessesByName matches the image name without
        // extension; "rclone" covers rclone.exe on Windows.
        foreach (var proc in Process.GetProcessesByName("rclone"))
        {
            try
            {
                if (proc.Id == us.Id) continue;
                inspected++;

                string? procPath;
                try { procPath = proc.MainModule?.FileName; }
                catch
                {
                    // MainModule access can fail with "Access is denied"
                    // for processes owned by another user / elevated.
                    // Those aren't ours by definition — skip.
                    continue;
                }

                var normalised = TryNormalisePath(procPath);
                if (normalised is null) continue;
                if (!string.Equals(normalised, ourPath, StringComparison.OrdinalIgnoreCase)) continue;

                // Started after us → it's a sibling from THIS session.
                // Backrest spawned it; don't reap.
                if (proc.StartTime > usStartTime) continue;

                _log.Warning(
                    "Reaping orphan rclone PID {Pid} (started {Started:O}, " +
                    "Backspace started {Now:O}, image {Path})",
                    proc.Id, proc.StartTime, usStartTime, normalised);

                try { proc.Kill(entireProcessTree: true); }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Kill failed for orphan rclone PID {Pid}", proc.Id);
                    continue;
                }
                killed++;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Couldn't inspect rclone PID {Pid}", proc.Id);
            }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }

        if (inspected == 0)
            _log.Debug("Rclone orphan sweep: no rclone processes seen");
        else
            _log.Information("Rclone orphan sweep: inspected {Inspected}, reaped {Killed}", inspected, killed);
    }

    private static string? TryNormalisePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }
}
