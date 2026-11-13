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
/// 13 November 2026). Each condition is decided on its own: "Run on battery
/// anyway" ignores the battery until the run ends and nothing else; "Use this
/// metered connection anyway" ignores the connection until the run ends.
/// What is not ignored holds the run back until it is met (queued on the
/// card), and pauses it if it stops holding while the run goes; Backspace
/// then says so in a notification, where you can continue anyway. A backup
/// whose drive and cloud destinations meet a metered connection backs up the
/// drive now either way; only the cloud part waits.
/// </summary>
internal static class ManualRunConditions
{
    internal enum Condition { Battery, Metered, Offline }

    /// <summary>What isn't met now, and what the run would do meanwhile.</summary>
    /// <param name="LocalAlsoRuns">The run has destinations on this computer:
    /// they go on whatever the connection.</param>
    /// <param name="NetworkDestinations">Names of the destinations that need the network.</param>
    internal sealed record Assessment(IReadOnlyList<Condition> Unmet, bool LocalAlsoRuns, IReadOnlyList<string> NetworkDestinations);

    /// <summary>The conditions ignored for this run, until it ends.</summary>
    internal sealed record Decision(bool WaiveBattery, bool WaiveMetered)
    {
        internal static readonly Decision None = new(false, false);
    }

    internal static Func<bool> OnBattery = ScheduleConditions.OnBattery;
    internal static Func<bool> Metered = ScheduleConditions.Metered;
    internal static Func<bool> Online = ScheduleConditions.NetworkAvailable;

    /// <summary>Replaces the question in tests.</summary>
    internal static Func<string, bool, Assessment, Task<Decision?>>? AskOverride;

    internal static Assessment Assess(Backup backup, IReadOnlyList<Destination> destinations, IReadOnlyList<BackupCheck>? checks = null)
    {
        SchedulePlan Plan(BackupCheck check) => new(backup, ScheduledTaskKind.Check, check.Schedule ?? backup.Schedule, check);
        var acOnly = checks is null ? backup.Schedule.SkipOnBattery : checks.Any(c => ScheduleConditions.OnlyOnAcPower(Plan(c)));
        var unmeteredOnly = checks is null ? backup.WaitForUnmeteredNetwork : checks.Any(c => ScheduleConditions.UnmeteredOnly(Plan(c)));
        var network = backup.Targets.Where(t => Backup.TargetNeedsNetwork(t, destinations)).ToList();
        var names = network.Select(t => destinations.FirstOrDefault(d => d.Id == t.DestinationId)?.Name).OfType<string>().Distinct().ToList();
        var onlyNetwork = network.Count > 0 && network.Count == backup.Targets.Count;
        var unmet = new List<Condition>();
        if (acOnly && OnBattery()) unmet.Add(Condition.Battery);
        if (network.Count > 0)
        {
            if (!Online()) { if (onlyNetwork) unmet.Add(Condition.Offline); }
            else if (unmeteredOnly && Metered()) unmet.Add(Condition.Metered);
        }
        return new(unmet, !onlyNetwork, names);
    }

    /// <summary>What still holds the run back once <paramref name="decision"/>
    /// is applied: a metered connection doesn't when the drive part can go on.</summary>
    internal static IReadOnlyList<Condition> Blocking(Assessment assessment, Decision decision) =>
        assessment.Unmet.Where(c => c switch
        {
            Condition.Battery => !decision.WaiveBattery,
            Condition.Metered => !decision.WaiveMetered && !assessment.LocalAlsoRuns,
            _ => true,
        }).ToList();

    /// <summary>What to wait for, in the card's words: "AC power".</summary>
    internal static string Awaited(Condition condition) => condition switch
    {
        Condition.Battery => "AC power",
        Condition.Metered => "an unmetered connection",
        _ => "a network connection",
    };

    internal static async Task<Decision?> AskAsync(string name, bool checks, Assessment assessment, Avalonia.Controls.Window? owner)
    {
        if (AskOverride is not null) return await AskOverride(name, checks, assessment);
        return await new RunConditionsWindow(name, checks, assessment).AskAsync(owner);
    }

    /// <summary>Waits until nothing in <paramref name="blocking"/> holds the
    /// run back: on a power or network change, or every 15 seconds.</summary>
    internal static async Task WaitAsync(Func<IReadOnlyList<Condition>> blocking, CancellationToken ct)
    {
        while (blocking().Count > 0)
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
