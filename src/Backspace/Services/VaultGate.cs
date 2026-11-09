using System;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>What the password window is for (U153).</summary>
public enum VaultPromptMode
{
    /// <summary>The first credential is about to be saved: choose a password.</summary>
    Create,
    /// <summary>This computer has no key yet: enter the password once.</summary>
    Unlock,
    /// <summary>Settings → Saved passwords: a new password.</summary>
    Change,
    /// <summary>The vault can't be opened; start a new, empty one.</summary>
    StartOver,
}

/// <summary>One request to the password window. <see cref="Submit"/> runs the
/// action with what was typed and returns null when done, or a message to
/// show (wrong password, a file in use) while the window stays open.</summary>
public sealed record VaultPromptRequest(VaultPromptMode Mode, string Reason, Func<string, Task<string?>> Submit,
    string? Detail = null);

/// <summary>Shows the password window. Tests replace it with a script.</summary>
public interface IVaultPrompt
{
    /// <summary>True when <see cref="VaultPromptRequest.Submit"/> succeeded;
    /// false when the person cancelled.</summary>
    Task<bool> AskAsync(VaultPromptRequest request);
}

/// <summary>
/// Asks for the vault password only when it is needed (U153): before a
/// credential is saved (<see cref="EnsureWritableAsync"/>) and before an
/// action reads one (<see cref="EnsureReadableAsync"/>). Without a vault, the
/// first save asks to choose a password (an empty one is allowed); reading
/// with no vault needs nothing. A cancelled prompt returns false and the
/// caller saves or runs nothing.
/// </summary>
public static class VaultGate
{
    /// <summary>The window that asks; set by the app at start, replaced by tests.</summary>
    public static IVaultPrompt? Prompt { get; set; }

    internal const string WrongPassword = "That password doesn't open your saved passwords. Check it and try again.";

    public static Task<bool> EnsureWritableAsync(string reason, SecretsStore? secrets = null)
        => EnsureAsync(reason, secrets ?? ServiceLocator.Secrets, write: true);

    public static Task<bool> EnsureReadableAsync(string reason, SecretsStore? secrets = null)
        => EnsureAsync(reason, secrets ?? ServiceLocator.Secrets, write: false);

    private static async Task<bool> EnsureAsync(string reason, SecretsStore secrets, bool write)
    {
        var state = await Task.Run(secrets.Refresh).ConfigureAwait(false);
        switch (state)
        {
            case VaultState.Unlocked: return true;
            case VaultState.NoVault when !write: return true;
            case VaultState.NoVault: return await AskAsync(VaultPromptMode.Create, reason, secrets).ConfigureAwait(false);
            case VaultState.Locked: return await AskAsync(VaultPromptMode.Unlock, reason, secrets).ConfigureAwait(false);
            default: return await AskAsync(VaultPromptMode.StartOver, reason, secrets).ConfigureAwait(false);
        }
    }

    /// <summary>Settings → Saved passwords → Forgot the password? Only for a
    /// vault locked on this computer: the old file is kept and a new, empty
    /// vault starts.</summary>
    public static Task<bool> StartOverForgottenAsync(SecretsStore? secrets = null)
    {
        secrets ??= ServiceLocator.Secrets;
        return AskAsync(VaultPromptMode.StartOver, "", secrets, ForgottenDetail);
    }

    internal const string ForgottenDetail = "Without the password, nobody can open your saved passwords, including Backspace.";

    /// <summary>Settings → Saved passwords → Change password.</summary>
    public static async Task<bool> ChangePasswordAsync(SecretsStore? secrets = null)
    {
        secrets ??= ServiceLocator.Secrets;
        if (!await EnsureWritableAsync("Enter your current password first.", secrets).ConfigureAwait(false)) return false;
        return await AskAsync(VaultPromptMode.Change, "", secrets).ConfigureAwait(false);
    }

    private static async Task<bool> AskAsync(VaultPromptMode mode, string reason, SecretsStore secrets, string? startOverDetail = null)
    {
        var prompt = Prompt;
        if (prompt is null)
        {
            AppLogger.For(nameof(VaultGate)).Warning("The saved passwords are {State} and no window can ask; nothing was saved", secrets.State);
            return false;
        }
        var detail = mode == VaultPromptMode.StartOver ? startOverDetail ?? secrets.LoadErrorMessage : null;
        var done = await prompt.AskAsync(new VaultPromptRequest(mode, reason, password => Task.Run(() => Submit(mode, secrets, password)), detail))
            .ConfigureAwait(false);
        if (done && mode != VaultPromptMode.Change) _ = Task.Run(() => AfterUnlock(secrets));
        return done;
    }

    private static string? Submit(VaultPromptMode mode, SecretsStore secrets, string password)
    {
        try
        {
            switch (mode)
            {
                case VaultPromptMode.Create:
                    if (secrets.Refresh() != VaultState.NoVault)
                        return "Saved passwords were just created by another Backspace window. Close this one and try again.";
                    secrets.Create(password);
                    return null;
                case VaultPromptMode.Unlock:
                    if (secrets.TryUnlock(password)) return null;
                    return secrets.State == VaultState.Unreadable ? secrets.LoadErrorMessage : WrongPassword;
                case VaultPromptMode.Change:
                    secrets.ChangePassword(password);
                    return null;
                case VaultPromptMode.StartOver:
                    secrets.StartOver(password);
                    return null;
                default:
                    return "This window can't do that.";
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Security.Cryptography.CryptographicException)
        {
            AppLogger.For(nameof(VaultGate)).Warning(ex, "The saved passwords could not be {Mode}", mode);
            return ex.Message;
        }
    }

    /// <summary>The background service gets this computer's key too.</summary>
    internal static void AfterUnlock(SecretsStore secrets)
    {
#if WINDOWS
        Platform.Windows.ServiceVaultUnlock.PushIfServiceSetUp(secrets);
#endif
    }
}
