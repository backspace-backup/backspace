using System;
using System.Collections.Generic;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U180 (owner go-ahead, 13 November 2026): the command line prints
/// what the service's run does, each line once, and its summary at the end.</summary>
public sealed class CommandLineTranscriptTests
{
    [Fact]
    public void The_command_line_prints_each_step_once_without_verbose_lines()
    {
        var started = DateTime.UtcNow;
        var printed = new List<string>();
        var transcript = new CommandLineTranscript("b1", started, printed.Add);
        var text = "";
        ServiceActivity.Snapshot Activity(string state = "running", RunRecord? result = null) =>
            new(1, Guid.NewGuid().ToString("N"), state, "b1", "check", started,
                new("b1", 0, "", [], [], result, TranscriptTail: text, TranscriptEnd: text.Length));
        string L(string line) => LogLineStamp.Stamp(line, DateTime.UtcNow) + "\n";

        text += L("[section] local_d · Data check (1 GiB sample)\tGoogle Drive");
        text += L("[restic] 00:39:09.512Z\tdebug\tcommand: restic check");
        text += L("[progress] 40% · Reading the data sample · Google Drive");
        transcript.Show(Activity());
        text += L("[recap] Data check (1 GiB sample) passed on «Google Drive» in 8 min 8 s.");
        transcript.Show(Activity());
        transcript.Show(Activity()); // nothing new: nothing printed twice
        var record = new RunRecord { BackupId = "b1", Status = BackupRunStatus.Success, Summary = "Data check (1 GiB sample) passed in 8 min 8 s.", StartedUtc = started, EndedUtc = DateTime.UtcNow };
        transcript.ShowSummary(Activity("completed", record));
        transcript.ShowSummary(Activity("completed", record));

        Assert.Equal(new[]
        {
            "local_d · Data check (1 GiB sample) · Google Drive",
            "  40% · Reading the data sample · Google Drive",
            "  Data check (1 GiB sample) passed on «Google Drive» in 8 min 8 s.",
            "=> Data check (1 GiB sample) passed in 8 min 8 s.",
        }, printed);
    }

    [Fact]
    public void A_run_that_ended_before_the_command_is_not_printed()
    {
        var printed = new List<string>();
        var transcript = new CommandLineTranscript("b1", DateTime.UtcNow, printed.Add);
        var old = "[recap] An older run.\n";
        transcript.Show(new ServiceActivity.Snapshot(1, Guid.NewGuid().ToString("N"), "completed", "b1", "check", DateTime.UtcNow.AddHours(-1),
            new("b1", 0, "", [], [], null, TranscriptTail: old, TranscriptEnd: old.Length)));
        Assert.Empty(printed);
    }
}
