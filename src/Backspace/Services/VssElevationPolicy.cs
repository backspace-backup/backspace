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
/// requires an elevated administrator or the verified service's existing
/// Backup Operators identity and backup privileges. On Unix the flag
/// is meaningless (no VSS), so we always say "doesn't need elevation"
/// regardless of the flag's value. On Windows the answer is "needs
/// elevation only if the current process doesn't already have a verified
/// execution context for VSS."
/// </summary>
public static class VssElevationPolicy
{
    public sealed record Decision(bool RequiresElevation, string Reason);

    /// <summary>Whether a run may ask Windows for approval to start the
    /// elevated VSS helper. Off since 29 October 2026 (U026): administrator
    /// access is a one-time grant through the background service, which also
    /// runs Run now, so neither desktop nor scheduled runs prompt each time.
    /// Without that access a run backs up without VSS and says so. The
    /// per-run helper remains behind this switch until that change is
    /// validated; then it can be removed.</summary>
    public static bool PromptAllowed { get; set; }

    internal const string SkippedWithoutAccessWarning =
        "Files that other apps had open may have been skipped, because Backspace doesn't have administrator access. To include them, click the orange badge at the bottom of the sidebar and choose Allow administrator access.";

    internal const string SkippedPendingApprovalWarning =
        "Files that other apps had open may have been skipped, because your latest changes weren't applied to the background service yet. Open Settings → Administrator access and choose Try again to include them.";

    /// <summary>The warning for a run that could not use VSS, naming the fix
    /// that applies to this copy.</summary>
    internal static string SkippedWarning()
    {
#if WINDOWS
        if (Platform.Windows.ServiceCommandLineRun.IsActive(System.AppContext.BaseDirectory.TrimEnd('\\')))
            return SkippedPendingApprovalWarning;
#endif
        return SkippedWithoutAccessWarning;
    }

    /// <summary>The same backup for one run with VSS off. A copy, so the
    /// saved setting is untouched.</summary>
    internal static Backup WithoutVss(Backup backup)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<Backup>(System.Text.Json.JsonSerializer.Serialize(backup))!;
        copy.UseVss = false;
        return copy;
    }

    public static Decision Evaluate(Backup backup)
    {
        var capable = PlatformInfo.IsProcessElevated;
#if WINDOWS
        // Only the protected worker can establish this after checking its
        // virtual identity, Backup Operators membership and actual privileges.
        capable |= Platform.Windows.ServiceApprovedWorker.HasVerifiedVssIdentity;
#endif
        return Evaluate(backup, PlatformInfo.IsWindows, capable);
    }

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
