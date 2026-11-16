using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace Backspace.Views;

/// <summary>
/// One shared frame clock for everything that moves in a window (U239): the
/// turning icons and the "Backups" sweep. It follows the frames Avalonia
/// draws, so each move lands on a frame, at even intervals; the earlier
/// timers fired between frames at uneven moments, which made motion jerky.
///
/// Backspace draws on the processor (U109). With Avalonia's own Windows
/// surface any change redraws and resends the whole window: moving on every
/// frame cost a whole processor core (measured 14 November 2026: 108%
/// against 8% without motion, in a 2088x1342 window). With the window's kept
/// frame (RetainedFrames, 15 November 2026) only the changed rectangle is
/// redrawn and sent, so it moves on every frame (the frames follow the
/// screen's refresh, 60 to 110 a second: DisplayClock); without it (another
/// platform, or Avalonia changed) about 8 times a second, on every Nth frame.
/// Moving on every second frame stuttered badly (owner, 15 November 2026:
/// "spinning and animating text while stuttering"): Avalonia pauses its
/// frames when one changes nothing, and restarting them came late, so the
/// moves landed 60 to 250 ms apart (measured: 8 moves a second instead of
/// 40). On every frame they land 12 ms apart, for about 22% of one core
/// against 7-13% without motion (82 frames a second, 165 Hz screen). Every mover steps
/// on the same frames, so the window is redrawn once for all of them. It
/// stops by itself on the first frame with nothing left to move.
/// </summary>
internal static class FrameLoop
{
    /// <summary>About 8 moves a second, when each redraws the whole window.</summary>
    internal const double TargetGapMs = 125;
    /// <summary>Every frame, when the window keeps its frame.</summary>
    internal const double SmoothGapMs = 0;
    /// <summary>For measurements: BACKSPACE_MOTION_GAP_MS overrides it.</summary>
    private static readonly double? GapMs = double.TryParse(Environment.GetEnvironmentVariable("BACKSPACE_MOTION_GAP_MS"), out var g) ? g : null;
    /// <summary>Frames measured before choosing how many to skip.</summary>
    private const int Measured = 12;

    private sealed class Chain
    {
        public int Frames;
        public TimeSpan? Last;
        public double GapSum;
        public int Gaps;
        public int Every = 1;
        public double? LastMove;
    }

    private static readonly List<(Func<TopLevel, bool> Wanted, Action<TopLevel> Step)> Movers = new();
    private static readonly Dictionary<TopLevel, Chain> Chains = new();

    /// <summary>Adds a mover: <paramref name="wanted"/> says whether it has
    /// something to move in that window, <paramref name="step"/> moves it.</summary>
    public static void Register(Func<TopLevel, bool> wanted, Action<TopLevel> step) => Movers.Add((wanted, step));

    /// <summary>Makes sure the window's frames move its movers.</summary>
    public static void Ensure(TopLevel top)
    {
        if (Chains.ContainsKey(top)) return;
        Chains[top] = new Chain();
        top.RequestAnimationFrame(now => Frame(top, now));
    }

#if WINDOWS
    private static bool KeepsFrame(TopLevel top) => RetainedFrames.IsActive(top);
#else
    private static bool KeepsFrame(TopLevel top) => false;
#endif

    /// <summary>How many frames apart the moves are, for a wanted gap and the screen's frame interval.</summary>
    internal static int EveryFor(double wantedGapMs, double frameMs) => Math.Max(1, (int)Math.Round(wantedGapMs / frameMs));

    /// <summary>For tests: how many frames apart the window's moves are now.</summary>
    internal static int? EveryOf(TopLevel top) => Chains.TryGetValue(top, out var c) ? c.Every : null;

    private static void Frame(TopLevel top, TimeSpan now)
    {
        if (!Chains.TryGetValue(top, out var chain)) return;
#if WINDOWS
        // A window whose render target was recreated gets its kept frame back.
        RetainedFrames.Ensure(top);
#endif
        var any = false;
        foreach (var mover in Movers) if (mover.Wanted(top)) { any = true; break; }
        if (!any) { Chains.Remove(top); return; }

        // Learn the screen's frame interval from the first frames. The spacing
        // is chosen again on every frame: the window may start keeping its
        // frame after the motion began (a backup already running when the
        // window opens), and 1.1.51 then kept the slow spacing for the whole run.
        if (chain.Gaps < Measured && chain.Last is { } last)
        {
            var gap = (now - last).TotalMilliseconds;
            if (gap is > 1 and < 100) { chain.GapSum += gap; chain.Gaps++; }
        }
        chain.Last = now;
        if (chain.Gaps == Measured)
        {
            var every = EveryFor(GapMs ?? (KeepsFrame(top) ? SmoothGapMs : TargetGapMs), chain.GapSum / chain.Gaps);
            if (every != chain.Every) { chain.Frames = 0; chain.Every = every; }
        }

        if (chain.Frames++ % chain.Every == 0)
        {
            foreach (var mover in Movers) if (mover.Wanted(top)) mover.Step(top);
            var at = MoveClock.Elapsed.TotalMilliseconds;
            if (chain.LastMove is { } previous)
            {
                var gap = at - previous;
                _moves++; _gapSum += gap; _gapMax = Math.Max(_gapMax, gap);
                if (gap > 1.5 * chain.Every * (chain.GapSum / Math.Max(1, chain.Gaps))) _late++;
            }
            chain.LastMove = at;
        }
        top.RequestAnimationFrame(next => Frame(top, next));
    }

    private static readonly System.Diagnostics.Stopwatch MoveClock = System.Diagnostics.Stopwatch.StartNew();
    private static long _moves, _late;
    private static double _gapSum, _gapMax;

    /// <summary>For measurements: moves since the last call, their mean and
    /// longest gap, and how many came at least half a gap late.</summary>
    internal static (long Moves, double MeanGapMs, double MaxGapMs, long Late, int Every) TakeMoveStats(TopLevel top)
    {
        var r = (_moves, _moves == 0 ? 0 : _gapSum / _moves, _gapMax, _late, EveryOf(top) ?? 0);
        _moves = 0; _late = 0; _gapSum = 0; _gapMax = 0;
        return r;
    }
}
