using System;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>
/// What the app does with the saved passwords when it starts (U153). It
/// never asks for the vault password here: a locked vault shows a banner
/// (<c>VaultBannerViewModel</c>) and the password is asked when an action
/// needs it. An open vault gives the background service this computer's key.
/// </summary>
public static class VaultStartup
{
    public static async Task RunAsync(SecretsStore secrets)
    {
        try
        {
            var state = await Task.Run(secrets.Refresh).ConfigureAwait(false);
            if (state != VaultState.Unlocked) return;
            await Task.Run(() => VaultGate.AfterUnlock(secrets)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.For(nameof(VaultStartup)).Warning(ex, "The saved passwords could not be checked at start");
        }
    }
}
