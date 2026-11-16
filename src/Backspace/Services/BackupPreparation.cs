using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>
/// The start of a backup before restic reports anything (U252, owner
/// 14 November 2026: "arbitrarily allocate 1 or 2% of the overall progress to
/// this initial phase? So users doesn't see a stuck 0%"). Looking for the
/// previous backup and reading the backup's index can take minutes and restic
/// says nothing meanwhile. The first <see cref="Share"/> percent of the bar
/// fills over the time the last backup of this plan took for it (45 s when
/// never measured), never quite reaching the end until restic reports. The
/// card says only "Preparing the backup" and for how long so far (owner, 15
/// November 2026: "usually about 2 min 07 s" was misleading, since the steps
/// differ in length; "just show a more generic wording for the initial phase
/// (the details of each phase are in the logs)"). Each step is written to the
/// run's log as it starts.
/// Restic's own progress then fills the rest of the bar.
/// </summary>
internal sealed class BackupPreparation : IDisposable
{
    internal const double Share = 2;
    internal static readonly TimeSpan DefaultExpected = TimeSpan.FromSeconds(45);
    internal static TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly string _planId;
    private readonly Action<string> _announce;
    private readonly TimeSpan _expected;
    private readonly bool _measured;
    private readonly DateTime _started;
    private readonly Func<DateTime> _now;
    private readonly CancellationTokenSource _stop = new();
    private string _step = "";
    private int _done;

    internal BackupPreparation(string planId, Action<string> announce, double? lastSeconds, Func<DateTime>? now = null)
    {
        _planId = planId;
        _announce = announce;
        _measured = lastSeconds is > 0;
        _expected = _measured ? TimeSpan.FromSeconds(lastSeconds!.Value) : DefaultExpected;
        _now = now ?? (() => DateTime.UtcNow);
        _started = _now();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await Task.Delay(Tick, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                Report();
            }
        });
    }

    /// <summary>What happens now, said at once.</summary>
    internal void Step(string text)
    {
        _step = text;
        if (Volatile.Read(ref _done) == 0)
            try { _announce(text + "…"); } catch (Exception) { /* a closed log must not stop the backup */ }
        Report();
    }

    /// <summary>What the card says during the preparation.</summary>
    internal const string Wording = "Preparing the backup";

    /// <summary>The line for now: the share of the bar used so far, what
    /// happens, the time so far and the usual time.</summary>
    internal string Line()
    {
        var elapsed = _now() - _started;
        // Linear over the usual time, then slowing so it never reaches the share.
        var fraction = elapsed <= _expected
            ? 0.9 * elapsed.TotalSeconds / _expected.TotalSeconds
            : 1 - 0.1 * Math.Exp(-(elapsed - _expected).TotalSeconds / (3 * _expected.TotalSeconds));
        var percent = Share * Math.Clamp(fraction, 0, 0.99);
        return BackupLiveProgress.Prefix + percent.ToString("0.0", CultureInfo.InvariantCulture) + "% · "
            + Wording + " · " + Duration(elapsed);
    }

    private void Report()
    {
        if (Volatile.Read(ref _done) != 0 || _step.Length == 0) return;
        try { _announce(Line()); } catch (Exception) { /* a closed log must not stop the backup */ }
    }

    /// <summary>Restic reported: remember how long this took for next time,
    /// and stop.</summary>
    internal void Finished()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        _stop.Cancel();
        try { MaintenanceRecords.PreparationMeasured(_planId, (_now() - _started).TotalSeconds); }
        catch (Exception) { /* only an estimate for next time */ }
    }

    /// <summary>Restic's progress fills the bar after the preparation share.</summary>
    internal static string Rescale(string progressLine)
    {
        if (!progressLine.StartsWith(BackupLiveProgress.Prefix, StringComparison.Ordinal)) return progressLine;
        var rest = progressLine[BackupLiveProgress.Prefix.Length..];
        var end = rest.IndexOf('%');
        if (end <= 0 || !double.TryParse(rest[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return progressLine;
        var scaled = Share + Math.Clamp(value, 0, 100) * (100 - Share) / 100;
        return BackupLiveProgress.Prefix + scaled.ToString("0.0", CultureInfo.InvariantCulture) + rest[end..];
    }

    internal static string Duration(TimeSpan span) => HumanFormat.Duration(span < TimeSpan.Zero ? TimeSpan.Zero : span);

    public void Dispose()
    {
        Interlocked.Exchange(ref _done, 1);
        _stop.Cancel();
        _stop.Dispose();
    }
}
