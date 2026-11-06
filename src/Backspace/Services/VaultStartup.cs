using System;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>
/// What the app does with the saved passwords when it starts (U153). It
/// never asks for the vault password here: a locked vault shows a banner
/// (<c>VaultBannerViewModel</c>) and the password is asked when an action
/// needs it. The only question at start is the one-time move of the earlier
/// per-computer files, asked when they are found and the portable vault is
/// not there yet. An open vault resumes an unfinished move and gives the
/// background service this computer's key.
/// </summary>
public static class VaultStartup
{
    public static async Task RunAsync(SecretsStore secrets, bool askToConvert)
    {
        try
        {
            var state = await Task.Run(secrets.Refresh).ConfigureAwait(false);
            var host = await Task.Run(VaultConversion.Host).ConfigureAwait(false);
            if (state == VaultState.NoVault && VaultConversion.Needed(AppPaths.ConfigRoot, host))
            {
                if (askToConvert) await VaultGate.ConvertAsync().ConfigureAwait(false);
                return;
            }
            if (state != VaultState.Unlocked) return;
            if (VaultConversion.Read(AppPaths.ConfigRoot) is not null)
            {
                var error = await Task.Run(() => VaultConversion.Continue(secrets, host)).ConfigureAwait(false);
                if (error is not null) AppLogger.For(nameof(VaultStartup)).Warning("The saved-password move continues later: {Reason}", error);
                return;
            }
            await Task.Run(() => VaultGate.AfterUnlock(secrets)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.For(nameof(VaultStartup)).Warning(ex, "The saved passwords could not be checked at start");
        }
    }
}
