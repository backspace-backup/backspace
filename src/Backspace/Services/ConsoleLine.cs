using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Backspace.Services;

/// <summary>What a line in the Activity &amp; logs console is, which sets its
/// colour (U176). Derived from the real log formats by
/// <see cref="ConsoleLineClassifier"/>.</summary>
public enum ConsoleLineKind
{
    /// <summary>Ordinary output.</summary>
    Plain,
    /// <summary>Setup and state lines: dim.</summary>
    Setup,
    /// <summary>A log file's header and outcome fields: dim.</summary>
    Meta,
    /// <summary>A progress line: muted, its percentage highlighted.</summary>
    Progress,
    /// <summary>Amber.</summary>
    Warning,
    /// <summary>Red.</summary>
    Error,
    /// <summary>The recap and summaries (online-only files skipped, cleanup): accent.</summary>
    Summary,
    /// <summary>Engine debug detail, shown with Verbose only: dim.</summary>
    Detail,
    /// <summary>A changed file (<c>[file]</c>), shown with Verbose only.</summary>
    File,
    /// <summary>Where a run, a check or a cleanup starts: a rule and a bold header.</summary>
    Section,
    /// <summary>A note from Backspace about the view itself (log truncated).</summary>
    Notice,
    /// <summary>An empty line.</summary>
    Blank,
}

/// <summary>One line in the console: its text without the stamp, the local
/// time it was written when known, and its kind. Selection and the brief
/// highlight after an error jump are view state kept on the line, so a row
/// recycled by the virtualizing list shows them correctly.</summary>
public sealed partial class ConsoleLine : ObservableObject
{
    public ConsoleLine(string text, ConsoleLineKind kind, DateTime? time = null)
    {
        Text = text; Kind = kind; Time = time;
    }

    /// <summary>The line as written, without its stamp. For a section, its title.</summary>
    public string Text { get; internal set; }
    public ConsoleLineKind Kind { get; internal set; }
    /// <summary>Local time, or null when the log did not record it.</summary>
    public DateTime? Time { get; internal set; }
    /// <summary>A section's secondary text (where it runs).</summary>
    public string? Subtitle { get; internal set; }
    /// <summary>A section made from a log file's header block rather than a
    /// <c>[section]</c> line; a marker line right after it replaces its title.</summary>
    internal bool FromHeader { get; set; }
    /// <summary>False for the outcome block's status and summary: red with a
    /// failed run, but the error list names the error itself.</summary>
    internal bool ListAsError { get; set; } = true;

    /// <summary>Shown only with Verbose.</summary>
    public bool IsDetail => Kind is ConsoleLineKind.Detail or ConsoleLineKind.File;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isFlashing;

    /// <summary>The line's message, as the error list shows it.</summary>
    public string Message => ConsoleLineClassifier.Message(Text);

    public string TimeLabel => Time?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

    /// <summary>The section header's text: "local_c · backup · 20:00".</summary>
    public string SectionTitle => Time is { } t ? Text + " · " + t.ToString("HH:mm", CultureInfo.InvariantCulture) : Text;

    /// <summary>The line as copied: what is shown, with its time when Timestamps is on.</summary>
    public string CopyText(bool withTime)
    {
        var text = Kind == ConsoleLineKind.Section
            ? "── " + SectionTitle + (string.IsNullOrEmpty(Subtitle) ? "" : " · " + Subtitle)
            : Text;
        return withTime && Time is not null ? TimeLabel + "  " + text : text;
    }

    public override string ToString() => Text;
}
