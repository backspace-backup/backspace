using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Views;

namespace Backspace.ViewModels;

/// <summary>
/// Before a backup the person starts (Run now, Save &amp; Run), settles any
/// external drive that lacks this backup (U117). The first backup to a
/// drive needs no question: starting it is the confirmation. A drive that
/// should already hold the backup, or that was declined, is asked about.
/// </summary>
internal static class DriveConfirmation
{
    internal sealed record Question(string Title, string Body, string UseLabel, string? DeclineLabel);

    /// <summary>Replaces the dialog in tests.</summary>
    internal static Func<Question, Task<DriveChoiceWindow.Choice>>? AskOverride;

    /// <summary>False when the person chose Not now: nothing runs.</summary>
    internal static async Task<bool> ConfirmAsync(Backup backup, Avalonia.Controls.Window? owner)
    {
        foreach (var target in backup.Targets)
        {
            if (ServiceLocator.Config.Current.Destinations.FirstOrDefault(d => d.Id == target.DestinationId) is not { } destination) continue;
            var state = DriveRepositoryGuard.Check(backup, destination);
            if (state is DriveRepositoryGuard.State.NotApplicable or DriveRepositoryGuard.State.Present or DriveRepositoryGuard.State.Accepted) continue;

            if (state == DriveRepositoryGuard.State.Missing && !DriveRepositoryGuard.SucceededBefore(backup, destination, ServiceLocator.Logs))
            {
                Accept(backup, destination);
                continue;
            }

            var where = DriveRepositoryGuard.RepositoryPath(destination) ?? destination.PathOrSubpath;
            var question = state == DriveRepositoryGuard.State.Declined
                ? new Question("This drive is set aside for this backup",
                    $"You chose not to back up «{backup.Name}» to the drive attached for «{destination.Name}». Use it for this backup now? "
                    + $"Backspace will start a new backup at {where}.",
                    "Use this drive", null)
                : new Question("This drive doesn't have this backup",
                    $"«{backup.Name}» has backed up to «{destination.Name}» before, but the drive attached now has no backup at {where}. "
                    + "It may be a different drive with the same name, or the drive was erased.\n\n"
                    + "Start a new backup on this drive, or set this drive aside for this backup: Backspace then skips it for "
                    + "«" + backup.Name + "» and doesn't ask again. Other backups and other computers decide for themselves.",
                    "Start a new backup here", "Don't use this drive");
            var choice = AskOverride is not null
                ? await AskOverride(question)
                : await new DriveChoiceWindow(question.Title, question.Body, question.UseLabel, question.DeclineLabel).AskAsync(owner);
            switch (choice)
            {
                case DriveChoiceWindow.Choice.UseDrive: Accept(backup, destination); break;
                case DriveChoiceWindow.Choice.Decline:
                    // If the marker can't be written the run still skips this
                    // drive (no repository, no go-ahead); it just asks again later.
                    try { DriveRepositoryGuard.Decline(backup, destination); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        AppLogger.For(nameof(DriveConfirmation)).Warning(ex, "Could not set the drive aside for {Destination}", destination.Name);
                    }
                    break;
                default: return false;
            }
        }
        return true;
    }

    private static void Accept(Backup backup, Destination destination)
    {
        try { DriveRepositoryGuard.Accept(backup, destination); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The run then stops at the guard and says why.
            AppLogger.For(nameof(DriveConfirmation)).Warning(ex, "Could not record the go-ahead on the drive for {Destination}", destination.Name);
        }
    }
}
