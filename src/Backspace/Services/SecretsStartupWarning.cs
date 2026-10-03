using System;
using System.IO;
using System.Security;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>Startup validation shared by the actual dialog and its regression
/// tests. Checking the vault runs off the UI thread and never returns secrets.</summary>
public sealed record SecretsStartupWarning(string Title, string Message)
{
    public static Task<SecretsStartupWarning?> CheckAsync(SecretsStore secrets) => Task.Run(() =>
    {
        try
        {
            secrets.ValidateReadable();
            return string.IsNullOrEmpty(secrets.LoadErrorMessage) ? null
                : new SecretsStartupWarning("Saved tokens couldn't be opened", secrets.LoadErrorMessage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            AppLogger.For<SecretsStartupWarning>().Warning(ex, "Saved secrets are temporarily inaccessible at startup");
            return new SecretsStartupWarning("Saved tokens are temporarily unavailable",
                "Backspace couldn't read your saved cloud tokens and storage passwords. " +
                "The file may be busy or access may be restricted. Your saved credentials have not been changed. " +
                "Close any program holding the file, check access to your Backspace configuration folder, " +
                "then try again. Backups that need those credentials may fail until access is restored.");
        }
    });
}
