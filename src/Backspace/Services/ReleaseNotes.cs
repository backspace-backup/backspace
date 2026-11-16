using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Backspace.Services;

/// <summary>
/// The release notes shown in App updates (U253, owner 16 November 2026:
/// "Why does it show release notes 'since XXXX' from all versions and not
/// just from the version actually installed?"). Each release's notes start
/// with its own changes ("## What's new in 1.1.54") followed by the earlier
/// ones ("### From 1.1.53", "### From 1.1.52", …), so someone several
/// versions behind sees everything they'd get. Only the versions newer than
/// the installed one are kept.
/// </summary>
internal static partial class ReleaseNotes
{
    [GeneratedRegex(@"^#{1,4}\s+(?:What's new in|What’s new in|From)\s+v?(?<v>\d+\.\d+\.\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionHeading();

    /// <summary>The notes of the versions newer than <paramref name="installed"/>;
    /// notes without version headings are returned as they are.</summary>
    internal static string Since(string notes, Version installed)
    {
        if (string.IsNullOrEmpty(notes)) return notes;
        var lines = notes.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var sawVersion = false;
        var keep = true; // text before the first version heading
        foreach (var line in lines)
        {
            if (VersionHeading().Match(line) is { Success: true } m && Version.TryParse(m.Groups["v"].Value, out var v))
            {
                sawVersion = true;
                keep = Normalize(v) > Normalize(installed);
            }
            if (keep) kept.Add(line);
        }
        if (!sawVersion) return notes;
        return string.Join("\n", kept).TrimEnd();
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
