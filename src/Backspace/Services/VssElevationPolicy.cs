using Backspace.Models;
using Backspace.Services.Platform;

namespace Backspace.Services;

/// <summary>
/// Decides whether a given backup needs the orchestrator to spawn an
/// elevated helper before running. Pure function: zero IO, zero state.
///
/// VSS (Volume Shadow Copy Service) is Windows-only — Backspace's
/// <see cref="Backup.UseVss"/> flag is the user's "use VSS for this
/// backup" toggle, and restic's <c>--use-fs-snapshot</c> implementation
/// requires the calling process to hold Administrator. On Unix the flag
/// is meaningless (no VSS), so we always say "doesn't need elevation"
/// regardless of the flag's value. On Windows the answer is "needs
/// elevation only if the current process isn't already elevated."
/// </summary>
public static class VssElevationPolicy
{
    public sealed record Decision(bool RequiresElevation, string Reason);

    public static Decision Evaluate(Backup backup)
        => Evaluate(backup, PlatformInfo.IsWindows, PlatformInfo.IsProcessElevated);

    /// <summary>Pure form for testability — the platform predicates
    /// are normally <see cref="PlatformInfo.IsWindows"/> +
    /// <see cref="PlatformInfo.IsProcessElevated"/>, but a test can
    /// drive them directly to cover the matrix without spinning up a
    /// new process.</summary>
    internal static Decision Evaluate(Backup backup, bool isWindows, bool isAlreadyElevated)
    {
        if (!backup.UseVss)
            return new Decision(false, "backup is not configured for VSS");
        if (!isWindows)
            return new Decision(false, "VSS is Windows-only; flag is ignored on this OS");
        if (isAlreadyElevated)
            return new Decision(false, "current process is already elevated; can run VSS in-process");
        return new Decision(true, "VSS is requested but the current process is not elevated");
    }
}
