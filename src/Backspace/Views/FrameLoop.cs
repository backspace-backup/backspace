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
/// redrawn and sent, so it moves about 30 times a second; without it (another
/// platform, or Avalonia changed) about 8 times. It moves on every Nth frame,
/// chosen once per window so the moves are evenly spaced. Every mover steps
/// on the same frames, so the window is redrawn once for all of them. It
/// stops by itself on the first frame with nothing left to move.
/// </summary>
internal static class FrameLoop
{
    /// <summary>About 8 moves a second, when each redraws the whole window.</summary>
    internal const double TargetGapMs = 125;
    /// <summary>About 30 moves a second, when the window keeps its frame.</summary>
    internal const double SmoothGapMs = 33;
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

        // Learn the screen's frame interval from the first frames, then keep
        // the same spacing for as long as this window moves.
        if (chain.Gaps < Measured && chain.Last is { } last)
        {
            var gap = (now - last).TotalMilliseconds;
            if (gap is > 1 and < 100) { chain.GapSum += gap; chain.Gaps++; }
            if (chain.Gaps == Measured)
                chain.Every = Math.Max(1, (int)Math.Round((GapMs ?? (KeepsFrame(top) ? SmoothGapMs : TargetGapMs)) / (chain.GapSum / chain.Gaps)));
        }
        chain.Last = now;

        if (chain.Frames++ % chain.Every == 0)
            foreach (var mover in Movers) if (mover.Wanted(top)) mover.Step(top);
        top.RequestAnimationFrame(next => Frame(top, next));
    }
}
