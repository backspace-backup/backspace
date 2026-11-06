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
        if (!ServicePortableLayout.IsPortableConfig(_root, _configuration)
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
                    "Setup stopped before the background service could open your saved passwords. Finish setup to give it this computer's key; your backups and passwords are unchanged.", true);
            return new BackgroundBackupStatus(BackgroundBackupState.Ready,
                "Backups also run on schedule when nobody is signed in. Quit stops them until you next sign in or restart.", true);
        }
        return OffOrUnsuitable();
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

    private void Apply()
    {
        RequirePortableCopy();
        ApplySystem();
    }

    private string EngineRoot => EngineInstallation.Root;

    /// <summary>Version 3 (U100): one Windows approval creates the service,
    /// then it gets this computer's vault key without another prompt (U153).</summary>
    private void ApplySystem()
    {
        var owner = SystemService.CurrentOwnerSid();
        // Before asking Windows: the vault must be open (the window asks for
        // the password first) and in its portable form.
        var secrets = new SecretsStore();
        if (secrets.Refresh() is VaultState.Locked or VaultState.Unreadable) throw new VaultLockedException(secrets.State, secrets.LoadErrorMessage);
        if (SystemService.ForCopy(_root) is null)
        {
            if (!File.Exists(Path.Combine(_configuration, "config.json"))) ServiceLocator.Config.Save();
            ValidateNewServiceLocation();
            Elevated("--system-service-setup", owner, EngineRoot);
        }
        GiveServiceTheVaultKey();
        TaskSchedulerService.NotifyChanged();
    }

    /// <summary>Gives the new service this computer's vault key (U153) and
    /// waits until it confirms it opens the portable vault; from then on it
    /// counts as set up. Nothing is copied: the service reads the same
    /// vault.bin. The caller unlocks the vault first.</summary>
    private void GiveServiceTheVaultKey()
    {
        var system = SystemService.ForCopy(_root) ?? throw new IOException("The background service was not set up. Try again.");
        var secrets = new SecretsStore();
        var state = secrets.Refresh();
        if (state is VaultState.Locked or VaultState.Unreadable) throw new VaultLockedException(state, secrets.LoadErrorMessage);
        if (state == VaultState.NoVault && File.Exists(Path.Combine(_configuration, "secrets.bin")))
            throw new InvalidOperationException("Your saved passwords are still in their earlier form. Restart Backspace and finish moving them, then try again.");
        SystemService.Start(system.ServiceName, TimeSpan.FromSeconds(60));
        var error = ServiceVaultUnlock.Push(system, secrets);
        if (error is not null)
            throw new IOException(error == "conversion-needed"
                ? "The background service still holds your earlier saved passwords. Restart Backspace and finish moving them, then try again."
                : "The background service could not open your saved passwords. Try again; your settings are unchanged.");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (SystemService.ForCopy(_root) is not { Activated: true })
        {
            if (DateTime.UtcNow > deadline) throw new IOException("The background service did not confirm your saved passwords. Try again.");
            Thread.Sleep(200);
        }
    }

    /// <summary>Turning administrator access off. With the portable vault
    /// (U153) there is nothing to hand back: the service's key copy is deleted
    /// and one Windows approval removes it. A service not yet converted still
    /// holds the authoritative sign-ins in its earlier vault: it returns them
    /// first, as before. Its history stays in Backspace.config.</summary>
    private void RemoveSystem(SystemService.Registration system)
    {
        var owner = SystemService.CurrentOwnerSid();
        var ownerPath = Path.Combine(_configuration, "secrets.bin");
        var unconverted = system.Activated && !File.Exists(Path.Combine(_configuration, VaultFile.FileName))
            && File.Exists(ServiceVaultUnlock.LegacyVaultPath(system.DataDirectory));
        if (unconverted)
        {
            var vault = new LegacySecretsVault(new DpapiSecretsEncryption(), ownerPath);
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
        ForgetServiceKeys();
        TaskSchedulerService.NotifyChanged();
    }

    /// <summary>The removed service's copies of vault keys are of no further use.</summary>
    private static void ForgetServiceKeys()
    {
        var keys = EngineWorkPaths.KeysDirectory;
        if (!Directory.Exists(keys)) return;
        foreach (var file in Directory.EnumerateFiles(keys, "*.service.bin"))
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { AppLogger.For<WindowsBackgroundBackupSetup>().Warning(ex, "A removed service's key copy could not be deleted"); }
    }

    public async Task<QuietApplyOutcome> ApplyChangesQuietlyAsync()
    {
        if (!await Gate.WaitAsync(0).ConfigureAwait(false)) return new(QuietApplyResult.Busy);
        try
        {
            return await Task.Run(() =>
            {
                RequirePortableCopy();
                // The service reads the saved settings itself, within a minute.
                return SystemService.ForCopy(_root) is { Activated: true }
                    ? new QuietApplyOutcome(QuietApplyResult.Applied)
                    : new QuietApplyOutcome(QuietApplyResult.NeedsApproval);
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
        if (SystemService.ForCopy(_root) is { } system) RemoveSystem(system);
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
