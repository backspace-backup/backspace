using System;
using System.ComponentModel;
using System.Collections.Generic;
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
        if (SystemService.ForCopy(_root) is { } system)
        {
            if (!system.Activated)
                return new BackgroundBackupStatus(BackgroundBackupState.SetupPending,
                    "Setup stopped before your saved sign-ins were handed over. Your backups and sign-ins are unchanged.", true);
            return new BackgroundBackupStatus(BackgroundBackupState.Ready,
                "Backups also run on schedule when nobody is signed in. Quit stops them until you next sign in or restart.", true);
        }
        if (LegacyServiceActive)
            return new BackgroundBackupStatus(BackgroundBackupState.SetupPending,
                "The background service needs a one-time upgrade: it will no longer change any folder's permissions, and Quit will stop it completely. Your backups, history and sign-ins are kept.", true);
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
        // The engines live in %LOCALAPPDATA%\Backspace\bin now (U100), not
        // beside Backspace.exe: only the folder itself can rule access out.
        try { ServiceInstallationSecurity.ValidateServiceLocation(_root); }
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

    /// <summary>The background service of Backspace 1.0 (a virtual account
    /// that changed folder permissions) is set up for this copy.</summary>
    private bool LegacyServiceActive => File.Exists(ServicePortableLayout.Record(_root, ServiceWorkerBinding.FileName))
        && ServiceRetirement.Read(_root) is not { Stage: "complete" };

    private void Apply()
    {
        RequirePortableCopy();
        if (LegacyServiceActive && ServiceRetirement.Read(_root) is null && ServiceWorkerBinding.LoadForOwner(_root) is { Activated: true })
        { Upgrade(); return; }
        if (!LegacyServiceActive) { ApplySystem(); return; }
        if (ServiceRetirement.Read(_root) is { Stage: not "complete" }) { Remove(); return; }
        // An earlier setup that never finished: take it back, then set up anew.
        Elevated("--service-abort-prepare");
        ApplySystem();
    }

    private string EngineRoot => EngineInstallation.Root;

    /// <summary>Version 3 (U100): one Windows approval creates the service,
    /// then the saved sign-ins are handed over without another prompt.</summary>
    private void ApplySystem()
    {
        var owner = SystemService.CurrentOwnerSid();
        if (SystemService.ForCopy(_root) is null)
        {
            if (!File.Exists(Path.Combine(_configuration, "config.json"))) ServiceLocator.Config.Save();
            ValidateNewServiceLocation();
            Elevated("--system-service-setup", owner, EngineRoot);
        }
        Handoff();
        TaskSchedulerService.NotifyChanged();
    }

    /// <summary>Moves Backspace 1.0's service to version 3 with one Windows
    /// approval: it returns the saved sign-ins, restores every permission it
    /// changed and goes; the new service is created and receives them.</summary>
    private void Upgrade()
    {
        var owner = SystemService.CurrentOwnerSid();
        var ownerPath = Path.Combine(_configuration, "secrets.bin");
        var vault = new SecretsStore(new DpapiSecretsEncryption(), ownerPath);
        vault.WithMigrationSnapshot(_ =>
        {
            var upgrade = StartApprovedAndWait(ServiceRetirement.PreparedHintFile, "--system-service-upgrade", owner, EngineRoot);
            try
            {
                var retirement = ServiceRetirement.Read(_root) ?? throw new IOException("The earlier background service was not prepared for the upgrade.");
                var binding = ServiceWorkerBinding.LoadForOwner(_root);
                ServiceVaultReturn.Transfer(vault, ownerPath, retirement.ReturnId,
                    request => ServiceVaultReturnProtocol.Exchange(binding, request),
                    () =>
                    {
                        var returned = Path.Combine(_configuration, ServiceRetirement.ReturnedHintFile);
                        FileAccessRetry.Run(() => File.WriteAllText(returned, ""), returned);
                        WaitForElevated(upgrade);
                    });
            }
            finally
            {
                FileAccessRetry.DeleteIfExists(Path.Combine(_configuration, ServiceRetirement.PreparedHintFile));
                upgrade.Dispose();
            }
            return true;
        }, requireExisting: true);
        Handoff();
        TaskSchedulerService.NotifyChanged();
    }

    /// <summary>Copies this copy's saved sign-ins to the new service and
    /// waits until it has verified them; from then on it keeps them.</summary>
    private void Handoff()
    {
        var system = SystemService.ForCopy(_root) ?? throw new IOException("The background service was not set up. Try again.");
        if (system.Activated) return;
        var vaultPath = Path.Combine(_configuration, "secrets.bin");
        if (!File.Exists(vaultPath) && !File.Exists(vaultPath + ".bak"))
            new SecretsStore(new DpapiSecretsEncryption(), vaultPath).ImportIntoNewVault(new Dictionary<string, string>());
        SystemService.Start(system.ServiceName, TimeSpan.FromSeconds(60));
        var source = new SecretsStore(new DpapiSecretsEncryption(), vaultPath);
        ServiceVaultHandoff.TransferVerifyAndActivate(source, system.ServiceName + "-migration", SystemService.LocalSystemSid,
            system.MigrationId, () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (SystemService.ForCopy(_root) is not { Activated: true })
                {
                    if (DateTime.UtcNow > deadline) throw new IOException("The background service did not confirm your saved sign-ins. Try again.");
                    Thread.Sleep(200);
                }
            });
    }

    /// <summary>Turning administrator access off: the service hands the saved
    /// sign-ins back and stops changing them, then one Windows approval
    /// deletes it. Its history stays in Backspace.config.</summary>
    private void RemoveSystem(SystemService.Registration system)
    {
        var owner = SystemService.CurrentOwnerSid();
        var ownerPath = Path.Combine(_configuration, "secrets.bin");
        var vault = new SecretsStore(new DpapiSecretsEncryption(), ownerPath);
        if (system.Activated)
        {
            SystemService.Start(system.ServiceName, TimeSpan.FromSeconds(60));
            var returnId = SystemServiceClient.Freeze(system);
            try
            {
                vault.WithMigrationSnapshot(_ =>
                {
                    ServiceVaultReturn.Transfer(vault, ownerPath, returnId,
                        request => ServiceVaultReturnProtocol.Exchange(system.Binding, request),
                        () => Elevated("--system-service-remove", owner));
                    return true;
                }, requireExisting: true);
            }
            catch
            {
                try { SystemServiceClient.Thaw(system); } catch (Exception) { }
                throw;
            }
        }
        else Elevated("--system-service-remove", owner);
        SystemServiceClient.ForgetVault(system.DataDirectory);
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
                // Version 3 reads the saved settings itself, within a minute.
                if (SystemService.ForCopy(_root) is { Activated: true }) return new QuietApplyOutcome(QuietApplyResult.Applied);
                if (SystemService.ForCopy(_root) is not null || LegacyServiceActive && ServiceRetirement.Read(_root) is null)
                    return new QuietApplyOutcome(QuietApplyResult.NeedsApproval);
                var binding = ServiceWorkerBinding.LoadForOwner(_root);
                if (!binding.Activated) return new QuietApplyOutcome(QuietApplyResult.NeedsApproval);
                var failure = Path.Combine(_configuration, Program.SetupFailureFile);
                FileAccessRetry.DeleteIfExists(failure);
                var code = await ServiceSettingsTask.RunAsync(binding, Executable, CancellationToken.None).ConfigureAwait(false);
                if (code is null) return new QuietApplyOutcome(QuietApplyResult.NeedsApproval);
                if (code == ServiceSettingsTask.BusyExitCode) return new QuietApplyOutcome(QuietApplyResult.Busy);
                if (code == 0) { TaskSchedulerService.NotifyChanged(); return new QuietApplyOutcome(QuietApplyResult.Applied); }
                string? reason = null;
                reason = FileAccessRetry.ReadNote(failure);
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
        if (SystemService.ForCopy(_root) is { } system) { RemoveSystem(system); return; }
        var ownerPath = Path.Combine(_configuration, "secrets.bin");
        var owner = new SecretsStore(new DpapiSecretsEncryption(), ownerPath);
        owner.WithMigrationSnapshot(_ =>
        {
            var retirement = ServiceRetirement.Read(_root);
            if (retirement?.Stage == "complete") return true;
            Process? removal = null;
            try
            {
                if (retirement?.Stage is not ("removing" or "removed"))
                {
                    // One approval covers freezing the service and finishing (U050).
                    if (_elevatedForTest is not null) Elevated("--service-retire-prepare");
                    else removal = StartApprovedAndWait(ServiceRetirement.PreparedHintFile, "--service-retire");
                }
                retirement = ServiceRetirement.Read(_root) ?? throw new IOException("Background service removal was not prepared.");
                if (retirement.Stage is "removing" or "removed")
                {
                    if (removal is not null) WaitForElevated(removal);
                    else Elevated("--service-retire-complete");
                }
                else
                {
                    var binding = ServiceWorkerBinding.LoadForOwner(_root);
                    ServiceVaultReturn.Transfer(owner, ownerPath, retirement.ReturnId,
                        request => ServiceVaultReturnProtocol.Exchange(binding, request),
                        () =>
                        {
                            if (removal is null) { Elevated("--service-retire-complete"); return; }
                            var returned = Path.Combine(_configuration, ServiceRetirement.ReturnedHintFile);
                            FileAccessRetry.Run(() => File.WriteAllText(returned, ""), returned);
                            WaitForElevated(removal);
                        });
                }
            }
            finally
            {
                // Release a removal process still waiting for the return.
                FileAccessRetry.DeleteIfExists(Path.Combine(_configuration, ServiceRetirement.PreparedHintFile));
                removal?.Dispose();
            }
            return true;
        }, requireExisting: true);
        // The tray app keeps the schedule again, while the owner is signed in.
        TaskSchedulerService.NotifyChanged();
    }

    private string FailurePath => Path.Combine(_configuration, Program.SetupFailureFile);

    /// <summary>Starts an approved process and returns once it leaves
    /// <paramref name="hintFile"/>; it then waits for the desktop's part.</summary>
    private Process StartApprovedAndWait(string hintFile, params string[] args)
    {
        var hint = Path.Combine(_configuration, hintFile);
        FileAccessRetry.DeleteIfExists(hint);
        FileAccessRetry.DeleteIfExists(FailurePath);
        var start = new ProcessStartInfo(Executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = _root };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("Windows did not start background backup setup."); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new OperationCanceledException("Windows approval was cancelled. Your saved settings and credentials have been preserved.", ex); }
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (!File.Exists(hint))
        {
            if (process.HasExited) { WaitForElevated(process); throw new IOException("The approved Windows process stopped before the background service was ready."); }
            if (DateTime.UtcNow > deadline) throw new System.TimeoutException("The background service is taking unusually long to get ready.");
            Thread.Sleep(250);
        }
        return process;
    }

    private void WaitForElevated(Process process)
    {
        process.WaitForExit();
        if (process.ExitCode == 0) return;
        string? reason = null;
        reason = FileAccessRetry.ReadNote(FailurePath, delete: true);
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
        FileAccessRetry.DeleteIfExists(failure);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Windows did not start background backup setup.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                string? reason = null;
                reason = FileAccessRetry.ReadNote(failure, delete: true);
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
