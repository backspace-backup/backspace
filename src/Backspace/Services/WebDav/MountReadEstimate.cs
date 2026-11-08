using System;
using System.Collections.Generic;
using System.Linq;

namespace Backspace.Services.WebDav;

/// <summary>
/// How far reading a mount's trees has got, as a percentage, at no extra cost
/// (U166). Each revision's file count is already known from restic's snapshot
/// summary (the picker shows it); the listing counts the files (not folders)
/// it has read, which matches that summary: the owner's 486,506-entry
/// snapshot was 415,256 files and 71,249 folders, and its summary said
/// 415,256 files.
///
/// <para>Over several revisions each counts by its file count, so the bar runs
/// once from 0 to 100% over the whole mount. A revision without a summary
/// (older snapshots) is estimated from another chosen revision of the same
/// source, usually much the same size; with none, the bar is indeterminate
/// while that revision is read and it does not count towards the total.</para>
///
/// <para>The estimate never goes backwards and stays at 99% at most until the
/// last revision has been read: the summary can count a few files the listing
/// leaves out (links), or the other way round.</para>
/// </summary>
public sealed class MountReadEstimate
{
    private readonly int?[] _expected;
    private readonly long _total;
    private int _shown;

    /// <param name="revisions">Each revision in reading order: its source
    /// (to borrow a neighbour's size) and its file count, 0 when unknown.</param>
    public MountReadEstimate(IReadOnlyList<(string Source, int Files)> revisions)
    {
        _expected = new int?[revisions.Count];
        for (var i = 0; i < revisions.Count; i++)
        {
            if (revisions[i].Files > 0) { _expected[i] = revisions[i].Files; continue; }
            // The nearest chosen revision of the same source with a summary,
            // the one read just before first.
            for (var distance = 1; distance < revisions.Count && _expected[i] is null; distance++)
                foreach (var j in new[] { i - distance, i + distance })
                    if (j >= 0 && j < revisions.Count && revisions[j].Files > 0
                        && string.Equals(revisions[j].Source, revisions[i].Source, StringComparison.Ordinal))
                    { _expected[i] = revisions[j].Files; break; }
        }
        _total = _expected.Sum(e => (long)(e ?? 0));
    }

    /// <summary>The whole mount's percentage while revision
    /// <paramref name="index"/> (0-based) has read <paramref name="files"/>
    /// files; <paramref name="complete"/> once it has been read in full. Null
    /// while nothing can be estimated for this revision.</summary>
    public int? Percent(int index, int files, bool complete)
    {
        if (_total == 0 || index < 0 || index >= _expected.Length) return null;
        long done = 0;
        for (var i = 0; i < index; i++) done += _expected[i] ?? 0;
        if (_expected[index] is not { } expected)
        {
            if (!complete) return null;
        }
        else done += complete ? expected : Math.Clamp(files, 0, expected);
        var last = index == _expected.Length - 1;
        var percent = last && complete ? 100 : (int)Math.Min(99, done * 100 / _total);
        _shown = Math.Max(_shown, percent);
        return _shown;
    }
}
