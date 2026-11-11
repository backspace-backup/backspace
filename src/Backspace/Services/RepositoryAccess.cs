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
/// alone: before them the backup gives up its total slot and takes every slot
/// of the destination, waiting for the other backups there to finish. A
/// cleanup waiting for that takes the destination's "exclusive" gate first, so
/// no new backup starts there meanwhile. Stand-alone cleanup and checks take
/// the destination the same way. Preparing the repository (creating it when
/// new) takes the destination's "setup" gate, one backup at a time, until the
/// engine says it is done (<see cref="SetupDone"/>).</para>
///
/// <para>No deadlock: a part waits only while holding nothing, or holding its
/// destination slot while waiting for a total slot, or holding both while
/// waiting for the setup gate. Total slots and the setup gate are held only
/// by parts actually working; those finish or, for cleanup, let go of them
/// before waiting for anything.</para>
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
    private CrossProcessGate? _intent, _total, _setup;
    private bool _exclusive, _shownQueued, _disposed;

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
        var access = new RepositoryAccess(gate, holder, destination, limits, queued);
        try
        {
            if (kind == Kind.Backup) await access.TakeSharedAsync(ct).ConfigureAwait(false);
            else await access.TakeExclusiveAsync(cleanup: kind == Kind.Exclusive, ct).ConfigureAwait(false);
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
        // Never wait while holding a total slot or the setup gate (see the
        // class remarks).
        _total?.Dispose(); _total = null;
        ReleaseSetup();
        var intent = new CrossProcessGate(ExclusiveName(_gate), _holder);
        if (intent.TryAcquire()) _intent = intent;
        else
        {
            intent.Dispose();
            // Someone else cleans up here first: hold nothing while waiting.
            ReleaseSlots();
        }
        await TakeExclusiveAsync(cleanup: true, ct).ConfigureAwait(false);
        ClearQueued();
    }

    private async Task TakeSharedAsync(CancellationToken ct)
    {
        var limit = _limits.PerDestinationClamped;
        await WaitAsync(() =>
        {
            // A cleanup waiting for this destination goes first.
            using var intent = new CrossProcessGate(ExclusiveName(_gate));
            if (!intent.TryAcquire()) return false;
            for (var slot = 0; slot < limit; slot++)
            {
                var gate = new CrossProcessGate(SlotName(_gate, slot), _holder);
                if (TryTake(gate)) { _slots[slot] = gate; return true; }
            }
            return false;
        }, () => Holders(includeExclusive: true, cleanup: false), ct).ConfigureAwait(false);
    }

    private async Task TakeExclusiveAsync(bool cleanup, CancellationToken ct)
    {
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
        // In order, so two cleanups can't each hold half of the slots.
        for (var slot = 0; slot < BackupLimits.Maximum; slot++)
        {
            if (_slots.ContainsKey(slot)) continue;
            var gate = new CrossProcessGate(SlotName(_gate, slot), _holder);
            try
            {
                await WaitAsync(() => TryTake(gate), () => Holders(includeExclusive: false, cleanup), ct).ConfigureAwait(false);
            }
            catch { gate.Dispose(); throw; }
            _slots[slot] = gate;
        }
        _exclusive = true;
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
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (tryTake()) return;
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
