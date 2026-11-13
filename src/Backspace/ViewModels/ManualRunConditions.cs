using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Views;

namespace Backspace.ViewModels;

/// <summary>
/// A run you start yourself while its conditions aren't met (U226, owner,
/// 13 November 2026: "manual runs should never be stopped if user has
/// specifically confirmed at the start he wanted to bypass the conditions").
/// Backspace says which condition isn't met and asks: run when it is (the
/// default, queued on the card), run now anyway (never stopped by its
/// conditions), or not now. A backup whose network destinations would wait
/// while its local ones run asks whether to back up the rest now.
/// </summary>
internal static class ManualRunConditions
{
    internal enum Choice { NotNow, Run, WhenAllowed, RunAnyway }

    internal sealed record Question(string Title, string Body, string WaitLabel, string? AnywayLabel, bool Partial);

    internal static Func<bool> OnBattery = ScheduleConditions.OnBattery;
    internal static Func<bool> Metered = ScheduleConditions.Metered;
    internal static Func<bool> Online = ScheduleConditions.NetworkAvailable;

    /// <summary>Replaces the dialog in tests.</summary>
    internal static Func<Question, Task<Choice>>? AskOverride;

    /// <summary>What holds this backup's run back now; null when nothing does.</summary>
    internal static string? Unmet(Backup backup, IReadOnlyList<Destination> destinations) =>
        ScheduleConditions.Blocked(new SchedulePlan(backup, ScheduledTaskKind.Backup, backup.Schedule), destinations, OnBattery, Online, Metered);

    /// <summary>What holds these checks back now: the first that is held back.</summary>
    internal static string? UnmetForChecks(Backup backup, IEnumerable<BackupCheck> checks, IReadOnlyList<Destination> destinations) =>
        checks.Select(c => ScheduleConditions.Blocked(new SchedulePlan(backup, ScheduledTaskKind.Check, c.Schedule ?? backup.Schedule, c),
            destinations, OnBattery, Online, Metered)).FirstOrDefault(r => r is not null);

    /// <summary>The network destinations a backup started now would leave
    /// waiting for an unmetered connection while the others run.</summary>
    internal static IReadOnlyList<string> WaitingNetworkDestinations(Backup backup, IReadOnlyList<Destination> destinations)
    {
        if (!backup.WaitForUnmeteredNetwork || !Metered() || !Online()) return [];
        return backup.Targets.Where(t => Backup.TargetNeedsNetwork(t, destinations))
            .Select(t => destinations.FirstOrDefault(d => d.Id == t.DestinationId)?.Name).OfType<string>().Distinct().ToList();
    }

    /// <summary>What to wait for, in the card's words: "AC power".</summary>
    internal static string Awaited(string unmet) => unmet switch
    {
        "the computer is on battery" => "AC power",
        "the connection is metered" => "an unmetered connection",
        "no network connection" => "a network connection",
        _ => unmet,
    };

    internal static Question Ask(string name, string unmet, bool checks)
    {
        var what = checks ? $"The checks of «{name}»" : $"«{name}»";
        var (condition, now, wait) = unmet switch
        {
            "the computer is on battery" => ("run only on AC power", "This computer is on battery.", "Run when plugged in"),
            "the connection is metered" => ("run only on an unmetered connection", "This computer is on a metered connection.", "Run when unmetered"),
            _ => ("need a network connection", "This computer has no network connection.", "Run when connected"),
        };
        if (unmet == "no network connection")
            return new Question($"{what} {(checks ? "need" : "needs")} a network connection",
                now + " Backspace can start as soon as one is back.", wait, null, false);
        return new Question($"{what} {(checks ? condition : condition.Replace("run only", "runs only"))}",
            now + " Start as soon as that changes, or now anyway. A run started anyway is not stopped if the computer is "
                + "unplugged or the connection becomes metered while it runs.",
            wait, "Run now anyway", false);
    }

    internal static Question AskPartial(string name, IReadOnlyList<string> waiting) => new(
        $"{Names(waiting)} {(waiting.Count == 1 ? "waits" : "wait")} for an unmetered connection",
        $"This computer is on a metered connection. «{name}» can back up its other destinations now; {Names(waiting)} "
            + "then waits for its next backup on an unmetered connection. Or back up everything now anyway: it is not stopped "
            + "if the connection stays metered.",
        "Back up the others now", "Back up everything now", true);

    private static string Names(IReadOnlyList<string> names) => string.Join(", ", names.Select(n => "«" + n + "»"));

    internal static async Task<Choice> AskAsync(Question question, Avalonia.Controls.Window? owner)
    {
        if (AskOverride is not null) return await AskOverride(question);
        var choice = await new DriveChoiceWindow(question.Title, question.Body, question.WaitLabel, question.AnywayLabel).AskAsync(owner);
        return choice switch
        {
            DriveChoiceWindow.Choice.UseDrive => question.Partial ? Choice.Run : Choice.WhenAllowed,
            DriveChoiceWindow.Choice.Decline => Choice.RunAnyway,
            _ => Choice.NotNow,
        };
    }

    /// <summary>Waits until <paramref name="unmet"/> says nothing holds the
    /// run back: on a power or network change, or every 15 seconds.</summary>
    internal static async Task WaitAsync(Func<string?> unmet, CancellationToken ct)
    {
        while (unmet() is not null)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Woken() => changed.TrySetResult();
#if WINDOWS
            Services.Platform.Windows.ScheduleSignals.Changed += Woken;
            Services.Platform.Windows.ScheduleSignals.NetworkChanged += Woken;
#endif
            try { await Task.WhenAny(changed.Task, Task.Delay(Poll, ct)).ConfigureAwait(false); }
            finally
            {
#if WINDOWS
                Services.Platform.Windows.ScheduleSignals.Changed -= Woken;
                Services.Platform.Windows.ScheduleSignals.NetworkChanged -= Woken;
#endif
            }
            ct.ThrowIfCancellationRequested();
        }
    }

    internal static TimeSpan Poll = TimeSpan.FromSeconds(15);
}
