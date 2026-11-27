using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// How many backups use a destination at once, and how many run in total
/// (U156, owner's request of 7 November 2026). Held for one part of a run (a
/// source to one destination) across every Backspace process: the window, a
/// scheduled run and each of the background service's workers.
///
/// <para>A destination has <see cref="BackupLimits.Maximum"/> slots, one
/// cross-process gate each (<c>{destination gate}</c>, <c>#1</c>, <c>#2</c>…).
/// A backup takes one free slot below "Backups at once per destination", then
/// one of the "Backups at once in total" slots. Restic lets backups share a
/// repository (shared locks), but cleanup (forget, prune) and checks need it
/// alone: before them the backup gives up its slots and takes every slot of
/// the destination at once, once the other backups there have finished.
/// Stand-alone cleanup and checks take the destination the same way, one at a
/// time (the destination's "exclusive" gate). Preparing the repository
/// (creating it when new) takes the destination's "setup" gate, one backup at
/// a time, until the engine says it is done (<see cref="SetupDone"/>).</para>
///
/// <para>Backups first (U267, owner 27 November 2026: "delaying a backup is
/// much worse than not checking"; "OK for 1-4"). A backup that has to wait
/// for the destination holds one of its "waiting" gates meanwhile. While one
/// is held, no cleanup or check starts there, and a running check gives way
/// (<see cref="GiveWayToBackups"/>): it is stopped, waits, and runs again
/// after the backup. A cleanup already running is not interrupted.</para>
///
/// <para>No deadlock: a part waits only while holding nothing (a waiting gate
/// aside, which nothing waits for), or holding its destination slot while
/// waiting for a total slot, or holding both while waiting for the setup gate.
/// Total slots and the setup gate are held only by parts actually working;
/// those finish or, for cleanup, let go of everything before waiting.</para>
/// </summary>
internal sealed class RepositoryAccess : IDisposable
{
    internal enum Kind
    {
        /// <summary>A backup: one destination slot and one total slot.</summary>
        Backup,
        /// <summary>Cleanup or checks: the destination alone, no total slot.</summary>
        Exclusive,
        /// <summary>A backup that also cleans up where this process can't
        /// take turns (an elevated helper runs it): alone on the destination,
        /// and one total slot.</summary>
        ExclusiveBackup,
    }

    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReasonRefresh = TimeSpan.FromSeconds(2);
    private static readonly AsyncLocal<RepositoryAccess?> CurrentAccess = new();

    private readonly string _gate;
    private readonly string _holder;
    private readonly string _destination;
    private readonly BackupLimits _limits;
    private readonly Action<string> _queued;
    private readonly SortedDictionary<int, CrossProcessGate> _slots = new();
    private CrossProcessGate? _intent, _total, _setup, _waiting;
    private bool _exclusive, _shownQueued, _disposed;
    /// <summary>Cleanup and checks give way to backups waiting here; a backup
    /// alone on the destination (an elevated helper's) does not.</summary>
    private bool _yieldsToBackups;

    private RepositoryAccess(string gate, string holder, string destination, BackupLimits limits, Action<string> queued)
    {
        _gate = gate; _holder = holder; _destination = destination; _limits = limits; _queued = queued;
    }

    /// <summary>The access of the part running in this flow, if any: the
    /// engine asks it for the destination alone before cleanup and checks.</summary>
    internal static RepositoryAccess? Current => CurrentAccess.Value;

    internal static string SlotName(string gate, int slot) => slot == 0 ? gate : gate + "#" + slot;
    internal static string ExclusiveName(string gate) => gate + "#exclusive";
    internal static string SetupName(string gate) => gate + "#setup";
    /// <summary>Held by a backup while it waits for this destination (U267).</summary>
    internal static string WaitingName(string gate, int slot) => gate + "#waiting#" + slot;
    /// <summary>Backups that can wait for one destination and be seen as
    /// waiting; one more still waits, unseen by cleanup and checks.</summary>
    internal const int WaitingSlots = 8;
    /// <summary>How often a running check looks for a backup waiting (U267).</summary>
    internal static TimeSpan GiveWayPoll = TimeSpan.FromSeconds(2);
    internal static string TotalName(int slot) => $@"Local\Backspace-Backups-{slot}";

    /// <summary>"Queued: 2 backups already running".</summary>
    internal static string TotalReason(int limit) => limit == 1 ? "1 backup already running" : $"{limit} backups already running";

    /// <summary>"waiting for local_c (same destination)".</summary>
    internal static string DestinationReason(IReadOnlyCollection<string> holders, string destination, bool cleanup)
    {
        var who = holders.Count == 0 ? $"another backup to «{destination}»"
            : holders.Count == 1 ? holders.First()
            : string.Join(", ", holders.Take(holders.Count - 1)) + " and " + holders.Last();
        var text = holders.Count == 0 ? "waiting for " + who : $"waiting for {who} (same destination)";
        return cleanup ? text + " before cleanup and checks" : text;
    }

    /// <summary>Waits for the destination (and, for a backup, a total slot).
    /// <paramref name="queued"/> hears the reason while it waits, then "".</summary>
    internal static async Task<RepositoryAccess> AcquireAsync(Kind kind, string gate, string holder, string destination,
        BackupLimits limits, Action<string> queued, CancellationToken ct)
    {
        var access = new RepositoryAccess(gate, holder, destination, limits, queued) { _yieldsToBackups = kind == Kind.Exclusive };
        try
        {
            if (kind == Kind.Backup) await access.TakeSharedAsync(ct).ConfigureAwait(false);
            else
            {
                // A backup alone on the destination is still a backup: cleanup
                // and checks let it go first.
                if (kind == Kind.ExclusiveBackup) access.MarkWaiting();
                try { await access.TakeExclusiveAsync(cleanup: kind == Kind.Exclusive, ct).ConfigureAwait(false); }
                finally { access.UnmarkWaiting(); }
            }
            if (kind != Kind.Exclusive) await access.TakeTotalAsync(ct).ConfigureAwait(false);
            // One backup at a time prepares the repository: two engines
            // creating a new one at once each wrote their own key and left it
            // unreadable ("config or key ... is damaged", E64, 7 November 2026).
            if (kind == Kind.Backup) await access.TakeSetupAsync(ct).ConfigureAwait(false);
            access.ClearQueued();
            return access;
        }
        catch { access.Dispose(); throw; }
    }

    /// <summary>Makes this access the current one for the engine calls that
    /// follow in this flow, until the returned scope is disposed.</summary>
    internal IDisposable Enter()
    {
        var previous = CurrentAccess.Value;
        CurrentAccess.Value = this;
        return new Scope(() => CurrentAccess.Value = previous);
    }

    /// <summary>Before cleanup and checks: the destination alone. Called by
    /// the engine; nothing to do outside a gated part.</summary>
    internal static Task ExclusiveAsync(CancellationToken ct) => Current is { } access ? access.UpgradeAsync(ct) : Task.CompletedTask;

    /// <summary>After the engine has prepared the repository (created it if
    /// new): another backup to it may prepare now. A part that never says so
    /// keeps the setup gate until its cleanup or its end.</summary>
    internal static void SetupDone() { if (Current is { } access) access.ReleaseSetup(); }

    private void ReleaseSetup() { _setup?.Dispose(); _setup = null; }

    /// <summary>The engine holds this part back for another task (one task
    /// per repository in a Backspace process).</summary>
    internal const string EngineRepositoryReason = "waiting for the backup engine to finish another task on this destination";
    internal const string EngineQueueReason = "waiting for the backup engine to finish another backup";
    internal const string SnapshotReason = "waiting for another backup's Windows snapshot";

    /// <summary>The engine says the part waits, though it has its gates: the
    /// card says "Queued: …" instead of "Running" (U156).</summary>
    internal static void Waiting(string reason)
    {
        if (Current is not { _disposed: false } access) return;
        access._shownQueued = true;
        access._queued(reason);
    }

    /// <summary>The engine runs the part again.</summary>
    internal static void NotWaiting() { if (Current is { _disposed: false } access) access.ClearQueued(); }

    private async Task TakeSetupAsync(CancellationToken ct)
    {
        var setup = new CrossProcessGate(SetupName(_gate), _holder);
        try
        {
            await WaitAsync(() => TryTake(setup), () =>
            {
                var who = CrossProcessGate.Holders.Read(SetupName(_gate)) is { } name && name != _holder ? [name] : new List<string>();
                return DestinationReason(who, _destination, cleanup: false);
            }, ct).ConfigureAwait(false);
        }
        catch { setup.Dispose(); throw; }
        _setup = setup;
    }

    private async Task UpgradeAsync(CancellationToken ct)
    {
        if (_exclusive || _disposed) return;
        // Never wait while holding anything (see the class remarks): the
        // backup's own slot too, which a backup waiting here may take first
        // (U267). The cleanup then takes the destination as a whole.
        _total?.Dispose(); _total = null;
        ReleaseSetup();
        ReleaseSlots();
        _yieldsToBackups = true;
        await TakeExclusiveAsync(cleanup: true, ct).ConfigureAwait(false);
        ClearQueued();
    }

    private async Task TakeSharedAsync(CancellationToken ct)
    {
        var limit = _limits.PerDestinationClamped;
        bool TryShared()
        {
            for (var slot = 0; slot < limit; slot++)
            {
                var gate = new CrossProcessGate(SlotName(_gate, slot), _holder);
                if (TryTake(gate)) { _slots[slot] = gate; return true; }
            }
            return false;
        }
        if (TryShared()) return;
        // Backups first (U267): while this one waits, no cleanup or check
        // starts here, and a running check gives way.
        MarkWaiting();
        try { await WaitAsync(TryShared, () => Holders(includeExclusive: false, cleanup: false), ct).ConfigureAwait(false); }
        finally { UnmarkWaiting(); }
    }

    private async Task TakeExclusiveAsync(bool cleanup, CancellationToken ct)
    {
        // One cleanup or check at a time here.
        if (_intent is null)
        {
            var intent = new CrossProcessGate(ExclusiveName(_gate), _holder);
            try
            {
                await WaitAsync(() => TryTake(intent), () => Holders(includeExclusive: true, cleanup), ct).ConfigureAwait(false);
            }
            catch { intent.Dispose(); throw; }
            _intent = intent;
        }
        // Every slot at once, or none: holding some while waiting for the
        // rest would hold back the backups that go first (U267).
        await WaitAsync(() =>
        {
            if (_yieldsToBackups && WaitingBackup() is not null) return false;
            var taken = new List<(int Slot, CrossProcessGate Gate)>();
            for (var slot = 0; slot < BackupLimits.Maximum; slot++)
            {
                if (_slots.ContainsKey(slot)) continue;
                var gate = new CrossProcessGate(SlotName(_gate, slot), _holder);
                if (TryTake(gate)) { taken.Add((slot, gate)); continue; }
                foreach (var (_, held) in taken) held.Dispose();
                return false;
            }
            foreach (var (slot, gate) in taken) _slots[slot] = gate;
            return true;
        }, () => _yieldsToBackups && WaitingBackup() is { } who
            ? BackupFirstReason(who, cleanup)
            : Holders(includeExclusive: false, cleanup), ct).ConfigureAwait(false);
        _exclusive = true;
    }

    /// <summary>"waiting for local_d to back up first (same destination)".</summary>
    internal static string BackupFirstReason(string backup, bool cleanup)
        => $"waiting for {backup} to back up first (same destination)" + (cleanup ? " before cleanup and checks" : "");

    /// <summary>This backup waits here: noted for cleanup and checks (U267).</summary>
    private void MarkWaiting()
    {
        if (_waiting is not null) return;
        for (var slot = 0; slot < WaitingSlots; slot++)
        {
            var gate = new CrossProcessGate(WaitingName(_gate, slot), _holder);
            if (TryTake(gate)) { _waiting = gate; return; }
        }
        AppLogger.For<RepositoryAccess>().Debug("{Holder} waits for «{Destination}» unseen: every waiting gate is taken", _holder, _destination);
    }

    private void UnmarkWaiting() { _waiting?.Dispose(); _waiting = null; }

    /// <summary>A backup waiting for this destination, by name, or null.</summary>
    internal string? WaitingBackup()
    {
        for (var slot = 0; slot < WaitingSlots; slot++)
        {
            var name = WaitingName(_gate, slot);
            if (_waiting?.Name == name) continue;
            var probe = new CrossProcessGate(name);
            if (TryTake(probe)) { probe.Dispose(); continue; }
            return CrossProcessGate.Holders.Read(name) ?? "another backup";
        }
        return null;
    }

    /// <summary>The backup this check gave way to, once it has (U267).</summary>
    internal string? GaveWayTo { get; private set; }

    /// <summary>A running check gives way to a backup that waits for this
    /// destination (U267): <paramref name="run"/> is cancelled, and
    /// <see cref="GaveWayTo"/> names the backup. The check then lets go of
    /// the destination and waits for it again, after the backup. Until the
    /// returned scope is disposed.</summary>
    internal IDisposable GiveWayToBackups(CancellationTokenSource run)
    {
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(GiveWayPoll, stop.Token).ConfigureAwait(false);
                    if (_disposed || stop.IsCancellationRequested || WaitingBackup() is not { } who) continue;
                    GaveWayTo = who;
                    AppLogger.For<RepositoryAccess>().Information("{Holder} gives way to {Backup}'s backup on «{Destination}»", _holder, who, _destination);
                    try { run.Cancel(); } catch (ObjectDisposedException) { }
                    return;
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
        return new Scope(() => stop.Cancel());
    }

    private async Task TakeTotalAsync(CancellationToken ct)
    {
        var limit = _limits.TotalClamped;
        await WaitAsync(() =>
        {
            for (var slot = 0; slot < limit; slot++)
            {
                var gate = new CrossProcessGate(TotalName(slot), _holder);
                if (TryTake(gate)) { _total = gate; return true; }
            }
            return false;
        }, () => TotalReason(limit), ct).ConfigureAwait(false);
    }

    private static bool TryTake(CrossProcessGate gate)
    {
        bool taken;
        try { taken = gate.TryAcquire(); }
        catch { gate.Dispose(); throw; }
        if (!taken) gate.Dispose();
        return taken;
    }

    private string Holders(bool includeExclusive, bool cleanup)
    {
        var names = new List<string>();
        void Add(string gate)
        {
            if (CrossProcessGate.Holders.Read(gate) is { } who && who != _holder && !names.Contains(who)) names.Add(who);
        }
        if (includeExclusive) Add(ExclusiveName(_gate));
        for (var slot = 0; slot < BackupLimits.Maximum; slot++)
            if (!_slots.ContainsKey(slot)) Add(SlotName(_gate, slot));
        return DestinationReason(names, _destination, cleanup);
    }

    private async Task WaitAsync(Func<bool> tryTake, Func<string> reason, CancellationToken ct)
    {
        string? shown = null;
        var refreshed = DateTime.MinValue;
        var started = DateTime.UtcNow;
        var reported = TimeSpan.Zero;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (tryTake()) return;
            // A wait that lasts says who holds what, and whether that process
            // still runs (U264: on 24 November a cleanup and a backup each said
            // they waited for the other, and nothing showed why).
            var waited = DateTime.UtcNow - started;
            if (waited - reported >= TimeSpan.FromMinutes(2))
            {
                reported = waited;
                AppLogger.For<RepositoryAccess>().Information("{Holder} has waited {Minutes:F0} min for «{Destination}»: {Gates}",
                    _holder, waited.TotalMinutes, _destination, CrossProcessGate.Holders.Describe(_gate, BackupLimits.Maximum));
            }
            if (shown is null || DateTime.UtcNow - refreshed >= ReasonRefresh)
            {
                var now = reason();
                refreshed = DateTime.UtcNow;
                if (now != shown) { shown = now; _shownQueued = true; _queued(now); }
            }
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private void ClearQueued()
    {
        if (!_shownQueued) return;
        _shownQueued = false;
        _queued("");
    }

    private void ReleaseSlots()
    {
        foreach (var gate in _slots.Values.Reverse()) gate.Dispose();
        _slots.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnmarkWaiting();
        ReleaseSetup();
        _total?.Dispose(); _total = null;
        ReleaseSlots();
        _intent?.Dispose(); _intent = null;
        ClearQueued();
    }

    private sealed class Scope(Action exit) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) exit(); }
    }
}
