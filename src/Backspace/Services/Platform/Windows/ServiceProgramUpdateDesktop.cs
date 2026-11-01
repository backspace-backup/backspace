using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace Backspace.Services.Platform.Windows;

/// <summary>The ordinary desktop stages the public release and closes before
/// asking Windows to elevate the trusted portable executable. Only that helper
/// authorizes and replaces service code; the new GUI starts unelevated.</summary>
internal static class ServiceProgramUpdateDesktop
{
    internal sealed record Request(string Root, string Tag, string StagingId);
    internal sealed record Outcome(bool Success, string Message, DateTime At, string StagingId);
    internal const string OutcomeFile = "program-update-result.json";
    internal static Request? Pending { get; set; }

    internal static bool Applies(string root)
    {
        if (ServiceRetirement.Read(root) is { Stage: "complete" }) return false;
        if (!File.Exists(ServicePortableLayout.Record(root, ServiceWorkerBinding.FileName))) return false;
        var binding = ServiceWorkerBinding.LoadForOwner(root);
        if (!binding.Activated) throw new IOException("Finish background backup setup before updating Backspace.");
        ServiceRetirement.RequireNotRetiring(root);
        return true;
    }

    internal static Request Prepare(string root, string tag, WindowsUpdatePackage.Plan staged)
    {
        if (!Applies(root)) throw new IOException("Background backup setup changed during the download. Check for updates again.");
        return ValidateStaging(root, tag, staged);
    }

    internal static Request ValidateStaging(string root, string tag, WindowsUpdatePackage.Plan staged)
    {
        WindowsUpdatePackage.ValidateLocation(staged);
        if (staged.ServiceUpdate || !staged.Root.Equals(ServicePortableLayout.Config(root), StringComparison.OrdinalIgnoreCase) || staged.State != "Prepared")
            throw new IOException("Background backup setup changed during the download. Check for updates again.");
        var id = Path.GetFileName(staged.Work);
        ServiceProgramUpdate.ValidateRequest(tag, id);
        return new(root, tag, id);
    }

    internal static void ApplyAfterExit(Request request, bool enginesStopped)
    {
        var root = request.Root;
        var message = "The update was not installed."; var success = false;
        try
        {
            if (!enginesStopped) throw new IOException("The backup engine could not stop safely. The update was not installed.");
            if (!Applies(root)) throw new IOException("Background backup setup changed. Check for updates again.");
            var exe = Path.Combine(root, "Backspace.exe");
            ServiceInstallationSecurity.ValidateCodeFile(exe, root);
            var result = Elevated(exe, "--service-update", request.Tag, request.StagingId);
            var outcome = ReadOutcome(root);
            var matches = MatchesOutcome(request, outcome, result);
            success = result == 0 && matches;
            message = matches ? outcome!.Message : "The update helper did not report a completed update. Your recovery files were retained.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { message = "Windows approval was cancelled. The update was not installed."; }
        catch (Exception ex) { message = "The update could not finish. " + ex.Message; }
        // This is only the public download, never the protected previous
        // version or its recovery launcher. The helper has exited before we
        // remove it, including when Windows approval was cancelled.
        CleanupDownload(request);
        WindowsUpdateRuntime.WriteResult(root, message, success, portableData: true);
        if (ServiceProgramUpdate.IsPending(root))
        {
            ShowRecoveryNeeded(root, message);
            return; // Never launch a possibly incomplete bundle.
        }
        try { WindowsUpdateRuntime.Launch(Path.Combine(root, "Backspace.exe")); }
        catch (Exception ex) { ShowRecoveryNeeded(root, "Backspace could not reopen. " + ex.Message); }
    }

    internal static bool MatchesOutcome(Request request, Outcome? outcome, int exitCode)
        => outcome?.StagingId == request.StagingId && outcome.Success == (exitCode == 0);

    internal static void CleanupDownload(Request request)
    {
        ServiceProgramUpdate.ValidateRequest(request.Tag, request.StagingId);
        var config = ServicePortableLayout.Config(request.Root);
        WindowsUpdatePackage.SafeRemoveWork(config, Path.Combine(config, ".backspace-updates", request.StagingId));
    }

    internal static bool RecoverPendingBeforeStartup(string root, string[] args)
    {
        if (!ServiceProgramUpdate.IsPending(root)) return false;
        try
        {
            var plan = ServiceProgramUpdate.PendingPlan(root);
            var runner = Path.Combine(plan.Work, "recovery-runner.exe");
            ServiceInstallationSecurity.ValidateProtectedRecord(runner, root);
            if (Elevated(runner, "--service-recover-update", Path.Combine(plan.Work, "plan.json"), "--finish-pending") != 0)
                throw new IOException("The pending update could not be recovered. Its previous files were retained.");
            WindowsUpdateRuntime.WriteResult(root, "The interrupted update was recovered. Your settings and backups were preserved.", false, portableData: true);
            WindowsUpdateRuntime.Launch(Path.Combine(root, "Backspace.exe"), args);
        }
        catch (Exception ex) { ShowRecoveryNeeded(root, ex.Message); }
        return true;
    }

    internal static int RunRecovery(string planPath, bool finishPending)
    {
        var plan = WindowsUpdateRuntime.ReadPlan(planPath);
        ServiceProgramUpdate.ValidatePlan(plan, plan.Root);
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            ServiceProgramUpdate.RecoverFromPlan(planPath, restoreCompleted: !finishPending);
            return 0;
        }
        _ = ServiceWorkerBinding.LoadForOwner(plan.Root);
        // The batch launcher starts this ordinary process and waits for it.
        // Modern .NET handles the elevation; PowerShell need not launch a long
        // executable pathname through its older .NET Framework implementation.
        var runner = Path.Combine(plan.Work, "recovery-runner.exe");
        ServiceInstallationSecurity.ValidateProtectedRecord(runner, plan.Root);
        var code = finishPending ? Elevated(runner, "--service-recover-update", planPath, "--finish-pending")
            : Elevated(runner, "--service-recover-update", planPath);
        if (code == 0) WindowsUpdateRuntime.Launch(Path.Combine(plan.Root, plan.ExeName), separateFromRecoveryConsole: true);
        return code;
    }

    internal static void WriteOutcome(string root, bool success, string message, string stagingId)
    {
        ServiceRegistration.RequireAdministrator();
        _ = ServiceWorkerBinding.Load(root);
        ServiceSetup.WriteRecord(root, OutcomeFile, JsonSerializer.SerializeToUtf8Bytes(new Outcome(success, message, DateTime.UtcNow, stagingId), ServiceRunAuthorization.Json));
    }

    private static Outcome? ReadOutcome(string root)
    {
        var path = ServicePortableLayout.Record(root, OutcomeFile);
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ServiceInstallationSecurity.ValidateProtectedRecord(path, root);
        if (file.Length is <= 0 or > 16384) throw new InvalidDataException("The update result could not be read.");
        return JsonSerializer.Deserialize<Outcome>(file, ServiceRunAuthorization.Json);
    }

    private static int Elevated(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows did not start the update helper.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void ShowRecoveryNeeded(string root, string reason)
    {
        var location = Path.Combine(ServicePortableLayout.Control(root), "updates");
        MessageBoxW(IntPtr.Zero, reason + "\n\nBackspace has kept the previous application files. Open the newest update folder in:\n"
            + location + "\n\nand run Recover Backspace.cmd. Your backup data has not been removed.", "Backspace update needs recovery", 0x10);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}
