using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Backspace.Services.WinFsp;

/// <summary>
/// Runs the bundled WinFSP MSI installer with admin elevation. The
/// MSI lives next to Backspace.exe as <c>winfsp.msi</c> (placed there
/// by <c>FetchWinFsp.targets</c> at build time + a Content/CopyToOutput
/// item in Backspace.csproj). At runtime we spawn
/// <c>msiexec /i &lt;path&gt; /qb /norestart</c> via
/// <see cref="ProcessStartInfo.UseShellExecute"/> + <c>Verb="runas"</c>,
/// which triggers the standard Windows UAC prompt; the user picks
/// Yes once, the kernel driver lands, and every subsequent mount is
/// unprivileged.
///
/// Why bundle and auto-install rather than asking the user to download:
///   • The Mount feature is unusable without the WinFSP runtime — a
///     bare "go install this" message means the user has to leave the
///     app, hunt for the right MSI, install it, and come back. That's
///     the kind of friction that turns a feature into a feature flag.
///   • The MSI is signed by upstream (Bill Zissimopoulos / WinFSP
///     project), 2.2 MB, and ships in the Backspace dist anyway —
///     might as well run it for the user.
///   • The user already consents via the UAC prompt; we don't
///     silently install (no <c>/qn</c>).
/// </summary>
[SupportedOSPlatform("windows")]
public static class WinFspInstaller
{
    private static ILogger _log => AppLogger.For<WinFspMountCoordinator>();

    /// <summary>Filename the build target writes next to Backspace.exe.
    /// Matches the <c>Link</c> in the csproj's Content item — anything
    /// fancier (per-version names) just makes the runtime path harder
    /// to reason about with no payoff.</summary>
    public const string BundledMsiName = "winfsp.msi";

    /// <summary>Path to the WinFSP MSI. The installer is embedded
    /// in Backspace.exe as a resource and extracted to
    /// <see cref="WinFspNativeLoader.ExtractRoot"/> on startup; this
    /// returns the extracted path when present. Returns null when
    /// EnsureRegistered hasn't run yet or the extraction failed
    /// (offline build with <c>-p:SkipBackrestDownload=true</c>,
    /// stripped resource). Callers fall back to the "install
    /// manually" message in that case.</summary>
    public static string? BundledMsiPath()
    {
        try
        {
            var msi = Path.Combine(WinFspNativeLoader.ExtractRoot, BundledMsiName);
            return File.Exists(msi) ? msi : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Spawn msiexec to install the bundled WinFSP MSI. Returns
    /// <c>InstallOutcome.Installed</c> if the runtime is detected
    /// after the installer exits, <c>InstallOutcome.UserDeclined</c>
    /// if the UAC prompt was dismissed (msiexec returns 1602),
    /// <c>InstallOutcome.MissingBundle</c> if there's no MSI on disk
    /// to install, or <c>InstallOutcome.Failed</c> with the exit code
    /// for anything else.
    ///
    /// Synchronous up to the `Start` call (UAC blocks until the user
    /// picks); after that we wait for msiexec to exit on a thread-pool
    /// thread so the caller can <c>await</c> without blocking the
    /// dispatcher.
    /// </summary>
    public static async Task<InstallOutcome> InstallAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return new InstallOutcome(InstallStatus.Failed, ExitCode: -1, Message: "WinFSP install is Windows-only.");

        var msi = BundledMsiPath();
        if (msi is null)
        {
            return new InstallOutcome(InstallStatus.MissingBundle, ExitCode: -1,
                Message: $"Backspace couldn't find the bundled WinFSP installer " +
                         $"({BundledMsiName}) next to the app. Reinstall Backspace " +
                         $"or download WinFSP manually from {WinFspRuntimeDetector.InstallUrl}.");
        }

        try
        {
            // /i  install
            // /qb basic UI (progress bar, no questions) — friendlier
            //     than /qn (silent) because the user sees something
            //     happen, but still hands-off.
            // /norestart never reboot for any reason (WinFSP doesn't
            //     need a reboot but msiexec's default is to ask).
            //
            // UseShellExecute=true + Verb="runas" → UAC prompt. Without
            // ShellExecute we'd bypass the verb and msiexec would
            // launch unelevated, fail with ERROR_INSTALL_FAILURE.
            var psi = new ProcessStartInfo("msiexec.exe")
            {
                Arguments = $"/i \"{msi}\" /qb /norestart",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new InstallOutcome(InstallStatus.Failed, ExitCode: -1,
                    Message: "msiexec didn't start. Try installing WinFSP manually from " + WinFspRuntimeDetector.InstallUrl + ".");
            }

            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller cancelled — leave the installer running; it's
                // already past the UAC prompt and a kill mid-MSI is
                // worse than letting it finish.
                throw;
            }

            var exit = proc.ExitCode;
            _log.Information("WinFSP msiexec returned {Code}", exit);

            // ERROR_INSTALL_USEREXIT = 1602 — UAC declined or user
            // cancelled the visible installer UI. Distinguish from a
            // genuine failure so the caller can show "try again later"
            // instead of "something broke".
            if (exit == 1602)
                return new InstallOutcome(InstallStatus.UserDeclined, exit,
                    "Install cancelled. Click Mount again whenever you're ready.");

            // 0 = success, 3010 = success-with-pending-reboot (we passed
            //     /norestart so the system flagged the request but
            //     didn't reboot; WinFSP loads on next mount regardless).
            if (exit == 0 || exit == 3010)
            {
                if (WinFspRuntimeDetector.IsInstalled())
                    return new InstallOutcome(InstallStatus.Installed, exit, "WinFSP installed.");

                // msiexec claimed success but our detector can't find
                // the install. Very unusual — corrupted MSI or
                // antivirus quarantine. Treat as failure so the UI
                // surfaces "something went wrong" rather than a silent
                // retry that's just going to fail again.
                return new InstallOutcome(InstallStatus.Failed, exit,
                    "msiexec exited 0 but the runtime isn't detected. Reinstall WinFSP manually from " + WinFspRuntimeDetector.InstallUrl + ".");
            }

            return new InstallOutcome(InstallStatus.Failed, exit,
                $"WinFSP installer returned exit code {exit}. Install it manually from {WinFspRuntimeDetector.InstallUrl}.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED = UAC prompt dismissed before msiexec
            // even ran. Treat exactly like /qb's 1602.
            return new InstallOutcome(InstallStatus.UserDeclined, ExitCode: 1223,
                Message: "Install cancelled at the UAC prompt. Click Mount again whenever you're ready.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "WinFSP install threw");
            return new InstallOutcome(InstallStatus.Failed, ExitCode: -1, Message: "Install failed: " + ex.Message);
        }
    }
}

public enum InstallStatus
{
    Installed,
    UserDeclined,
    MissingBundle,
    Failed,
}

public readonly record struct InstallOutcome(InstallStatus Status, int ExitCode, string Message)
{
    public bool Succeeded => Status == InstallStatus.Installed;
}
