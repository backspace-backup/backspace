using System;

namespace Backspace.Services;

/// <summary>Recovers only registered children of a confirmed dead owner. Older
/// unregistered rclone instances are left alone: their name, path or age cannot
/// prove ownership. Backrest's descendants stay in its kill-on-exit Windows job.</summary>
public static class RcloneOrphanReaper
{
    public static void Sweep()
    {
        try { OwnedProcessRegistry.Sweep(); }
        catch (Exception ex)
        {
            AppLogger.For("RcloneOrphanReaper").Warning(ex, "Owned-process recovery failed");
        }
    }
}