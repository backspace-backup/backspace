using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>Desktop coordinator. Windows changes happen only after an explicit
/// button press and the normal administrator approval dialog.</summary>
internal sealed class WindowsBackgroundBackupSetup : IBackgroundBackupSetup
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _root;
    private readonly string _configuration;
    private readonly Action<string[]>? _elevatedForTest;
    private string Executable => Path.Combine(_root, "Backspace.exe");

    internal WindowsBackgroundBackupSetup() : this(AppContext.BaseDirectory.TrimEnd('\\'), AppPaths.ConfigRoot, null) { }
    internal WindowsBackgroundBackupSetup(string installation, string configuration, Action<string[]>? elevatedForTest)
    { _root = installation; _configuration = configuration; _elevatedForTest = elevatedForTest; }

    private void RequirePortableCopy()
    {
        if (!ServiceOwnerSecrets.Applies(_root, _configuration)
            || (_elevatedForTest is null && !string.Equals(Environment.ProcessPath, Executable, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Open the portable Backspace.exe with its Backspace.config folder beside it to set up automatic backups.");
    }

    public Task<BackgroundBackupStatus> InspectAsync() => Task.Run(() =>
    {
        RequirePortableCopy();
        if (ServiceRetirement.Read(_root) is { } retirement)
            return retirement.Stage == "complete"
                ? OffOrUnsuitable()
                : new BackgroundBackupStatus(BackgroundBackupState.RemovalPending, "Scheduled backups are paused until this finishes. Your backups and sign-ins are kept.");
        var path = ServicePortableLayout.Record(_root, ServiceWorkerBinding.FileName);
        if (!File.Exists(path)) return OffOrUnsuitable();
        var binding = ServiceWorkerBinding.LoadForOwner(_root);
        if (!binding.Activated)
            return new BackgroundBackupStatus(BackgroundBackupState.SetupPending,
                "Setup stopped before it finished. Your backups and sign-ins are unchanged.");
        var approval = ServiceRunAuthorization.Load(ServicePortableLayout.Record(_root, "service-approval.json"), _root, binding.OwnerSid).Snapshot();
        if (File.Exists(ServicePortableLayout.Record(_root, ServiceConfigurationUpdate.FileName))
            || !ServiceConfigurationComparison.Equivalent(approval.Configuration, ServiceConfigurationComparison.ReadDesktop(_root)))
            return new BackgroundBackupStatus(BackgroundBackupState.ChangesPending,
                "Until you approve your latest changes, scheduled backups wait and Run now can't include files that are in use.", true);
        using var service = new ServiceController(binding.ServiceName);
        if (service.StartType != ServiceStartMode.Automatic)
            return new BackgroundBackupStatus(BackgroundBackupState.SetupPending,
                "Windows is no longer set to start Backspace's background service. Finish setup to restore it.", true);
        if (ServiceLegacyTasks.HasRemaining(_root, binding))
            return new BackgroundBackupStatus(BackgroundBackupState.SetupPending,
                "Your earlier signed-in schedules are still registered. Finish setup to replace them, so nothing runs twice.", true);
        // Moving an earlier version's Windows tasks into the service is an
        // ordinary settings update, applied without a prompt (U043).
        if (ServiceScheduleRegistration.HasLegacyTasks(binding))
            return new BackgroundBackupStatus(BackgroundBackupState.ChangesPending,
                "Backspace is moving your schedules into the background service.", true);
        foreach (var backup in approval.Configuration.Backups.Where(b => b.Enabled))
        {
            var schedule = ServiceScheduleRegistration.Inspect(_root, Executable, backup.Id);
            if (schedule.State != ScheduleReadiness.Ready)
                return new BackgroundBackupStatus(BackgroundBackupState.SetupPending, schedule.Detail, true);
        }
        return new BackgroundBackupStatus(BackgroundBackupState.Ready,
            "Backups also run on schedule when nobody is signed in.", true);
    });

    /// <summary>Off means schedules run as the signed-in person. Say up front
    /// when this folder cannot run them all the time, instead of after the
    /// approval prompt.</summary>
    private BackgroundBackupStatus OffOrUnsuitable()
    {
        const string off = "Without administrator access, backups can't include files other apps have open. They run on schedule only while you're signed in.";
        try { ServiceInstallationSecurity.ValidateServiceLocation(_root); ServiceSetup.ValidateContents(_root); }
        catch (Exception ex) when (ex is SecurityException or IOException)
        {
            return new BackgroundBackupStatus(BackgroundBackupState.Unavailable, off + "\n" + ex.Message);
        }
        return new BackgroundBackupStatus(BackgroundBackupState.Off, off);
    }


    public async Task ApplyAsync()
    {
        if (!await Gate.WaitAsync(0).ConfigureAwait(false)) throw new IOException("Background backup setup is already running.");
        try { await Task.Run(Apply).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    private void Apply()
    {
        RequirePortableCopy();
        if (ServiceRetirement.Read(_root) is { Stage: not "complete" }) { Remove(); return; }
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User?.Value ?? throw new IOException("Windows could not identify this account.");
        var bindingPath = ServicePortableLayout.Record(_root, ServiceWorkerBinding.FileName);
        Process? setup = null;
        if (!File.Exists(bindingPath) || ServiceRetirement.Read(_root) is { Stage: "complete" })
        {
            if (ServiceConfigurationComparison.ReadDesktop(_root).Backups.Count == 0)
                throw new InvalidOperationException("Save a backup first, then turn this on.");
            ValidateNewServiceLocation();
            // One approval covers preparing and activating (U050).
            if (_elevatedForTest is not null) Elevated("--service-prepare", owner);
            else setup = StartSetupAndWaitUntilPrepared(owner);
        }
        var binding = ServiceWorkerBinding.LoadForOwner(_root);
        if (!binding.Activated)
        {
            using var service = new ServiceController(binding.ServiceName);
            ServiceRegistration.EnsureRunning(service, TimeSpan.FromSeconds(20));
            var path = ServicePortableLayout.Record(_root, ServiceMigrationProtocol.GrantFileName);
            ServiceInstallationSecurity.ValidateProtectedRecord(path, _root);
            var grant = JsonSerializer.Deserialize<ServiceMigrationProtocol.Grant>(File.ReadAllBytes(path), ServiceRunAuthorization.Json)
                ?? throw new IOException("The credential handoff record is missing.");
            if (grant.OwnerSid != owner) throw new IOException("This setup belongs to another Windows account.");
            var source = new SecretsStore(new DpapiSecretsEncryption(), Path.Combine(ServicePortableLayout.Config(_root), "secrets.bin"));
            try
            {
                ServiceVaultHandoff.TransferVerifyAndActivate(source, binding.ServiceName + "-migration", binding.ServiceSid,
                    grant.MigrationId, () => { if (setup is not null) WaitForElevated(setup); else Elevated("--service-activate"); });
            }
            finally
            {
                // Release a setup process still waiting for the handoff.
                try { File.Delete(Path.Combine(_configuration, ServiceActivation.PreparedHintFile)); } catch (Exception) { }
                setup?.Dispose();
            }
        }
        else
        {
            // Finish a saved update before approving any newer edits.
            if (File.Exists(ServicePortableLayout.Record(_root, ServiceConfigurationUpdate.FileName))) Elevated("--service-refresh");
            var current = ServiceConfigurationComparison.ReadDesktop(_root);
            var approval = ServiceRunAuthorization.Load(ServicePortableLayout.Record(_root, "service-approval.json"), _root, binding.OwnerSid).Snapshot();
            if (!ServiceConfigurationComparison.Equivalent(approval.Configuration, current))
                Elevated("--service-refresh", ServiceRunAuthorization.Digest(current));
            else Elevated("--service-activate");
        }
        TaskSchedulerService.NotifyChanged();
    }

    public async Task<QuietApplyOutcome> ApplyChangesQuietlyAsync()
    {
        if (!await Gate.WaitAsync(0).ConfigureAwait(false)) return new(QuietApplyResult.Busy);
        try
        {
            return await Task.Run(async () =>
            {
                RequirePortableCopy();
                var binding = ServiceWorkerBinding.LoadForOwner(_root);
                if (!binding.Activated) return new QuietApplyOutcome(QuietApplyResult.NeedsApproval);
                var failure = Path.Combine(_configuration, Program.SetupFailureFile);
                try { File.Delete(failure); } catch (Exception) { }
                var code = await ServiceSettingsTask.RunAsync(binding, Executable, CancellationToken.None).ConfigureAwait(false);
                if (code is null) return new QuietApplyOutcome(QuietApplyResult.NeedsApproval);
                if (code == ServiceSettingsTask.BusyExitCode) return new QuietApplyOutcome(QuietApplyResult.Busy);
                if (code == 0) { TaskSchedulerService.NotifyChanged(); return new QuietApplyOutcome(QuietApplyResult.Applied); }
                string? reason = null;
                try { if (new FileInfo(failure).Length is > 0 and < 8192) reason = File.ReadAllText(failure).Trim(); } catch (Exception) { }
                return new QuietApplyOutcome(QuietApplyResult.Failed, reason ?? $"Windows reported code {code}. Your saved settings are kept.");
            }).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task RemoveAsync()
    {
        if (!await Gate.WaitAsync(0).ConfigureAwait(false)) throw new IOException("Background backup setup is already running.");
        try { await Task.Run(Remove).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    private void Remove()
    {
        RequirePortableCopy();
        var ownerPath = Path.Combine(_configuration, "secrets.bin");
        var owner = new SecretsStore(new DpapiSecretsEncryption(), ownerPath);
        owner.WithMigrationSnapshot(_ =>
        {
            var retirement = ServiceRetirement.Read(_root);
            if (retirement?.Stage == "complete") return true;
            if (retirement?.Stage is not ("removing" or "removed")) Elevated("--service-retire-prepare");
            retirement = ServiceRetirement.Read(_root) ?? throw new IOException("Background service removal was not prepared.");
            if (retirement.Stage is "removing" or "removed") Elevated("--service-retire-complete");
            else
            {
                var binding = ServiceWorkerBinding.LoadForOwner(_root);
                ServiceVaultReturn.Transfer(owner, ownerPath, retirement.ReturnId,
                    request => ServiceVaultReturnProtocol.Exchange(binding, request),
                    () => Elevated("--service-retire-complete"));
            }
            return true;
        }, requireExisting: true);
        // The tray app keeps the schedule again, while the owner is signed in.
        TaskSchedulerService.NotifyChanged();
    }

    private string FailurePath => Path.Combine(_configuration, Program.SetupFailureFile);

    /// <summary>Starts the approved setup process and returns once it has
    /// prepared the service; it then waits for the sign-in handoff.</summary>
    private Process StartSetupAndWaitUntilPrepared(string owner)
    {
        var hint = Path.Combine(_configuration, ServiceActivation.PreparedHintFile);
        try { File.Delete(hint); } catch (Exception) { }
        try { File.Delete(FailurePath); } catch (Exception) { }
        var start = new ProcessStartInfo(Executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = _root };
        start.ArgumentList.Add("--service-setup");
        start.ArgumentList.Add(owner);
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("Windows did not start background backup setup."); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new OperationCanceledException("Windows approval was cancelled. Your saved settings and credentials have been preserved.", ex); }
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (!File.Exists(hint))
        {
            if (process.HasExited) { WaitForElevated(process); throw new IOException("Background backup setup stopped before the service was ready."); }
            if (DateTime.UtcNow > deadline) throw new System.TimeoutException("Preparing the background service is taking unusually long.");
            Thread.Sleep(250);
        }
        return process;
    }

    private void WaitForElevated(Process process)
    {
        process.WaitForExit();
        if (process.ExitCode == 0) return;
        string? reason = null;
        try { if (new FileInfo(FailurePath).Length is > 0 and < 8192) reason = File.ReadAllText(FailurePath).Trim(); File.Delete(FailurePath); }
        catch (Exception) { }
        throw new IOException("Scheduled backups could not be set up. Your backups and settings are unchanged."
            + (string.IsNullOrEmpty(reason) ? " Try again; if it keeps failing, copy this message for support." : " Reason: " + reason));
    }

    private void Elevated(params string[] args)
    {
        if (_elevatedForTest is not null) { _elevatedForTest(args); return; }
        // Arguments contain only fixed verbs, a SID or a hex digest. No
        // credential value, user command, or alternate executable is accepted.
        var start = new ProcessStartInfo(Executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = _root };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // The approved process cannot return its output through the prompt;
        // it leaves its reason in this file instead (Program.ReportSetupFailure).
        var failure = Path.Combine(_configuration, Program.SetupFailureFile);
        try { File.Delete(failure); } catch (Exception) { }
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Windows did not start background backup setup.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                string? reason = null;
                try { if (new FileInfo(failure).Length is > 0 and < 8192) reason = File.ReadAllText(failure).Trim(); File.Delete(failure); }
                catch (Exception) { }
                throw new IOException("Scheduled backups could not be set up. Your backups and settings are unchanged."
                    + (string.IsNullOrEmpty(reason) ? " Try again; if it keeps failing, copy this message for support." : " Reason: " + reason));
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new OperationCanceledException("Windows approval was cancelled. Your saved settings and credentials have been preserved.", ex); }
    }

    private void ValidateNewServiceLocation()
    {
        // Where Backspace lives is the owner's choice. Only places the service
        // cannot work from are refused, each with its own plain reason.
        try { ServiceInstallationSecurity.ValidateServiceLocation(_root); }
        catch (SecurityException ex) { throw new InvalidOperationException(ex.Message, ex); }
        var probe = Path.Combine(_configuration, ".write-check-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllBytes(probe, Array.Empty<byte>()); File.Delete(probe); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException("Backspace's folder is read-only, so its settings cannot be saved. Move the whole Backspace folder somewhere you can save files, then try again.", ex);
        }
    }
}
