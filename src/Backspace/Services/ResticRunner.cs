using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>The repository restic works on: its address, password, the
/// destination's environment (rclone configuration, S3 keys) and the options
/// every command gets (bandwidth limits, no password). Built from Backspace's
/// destination and vault for each operation (U175); nothing is stored.</summary>
internal sealed record ResticRepository(string Uri, string Password, IReadOnlyList<string> Env, IReadOnlyList<string> Flags);

/// <summary>One restic command. Its command line is
/// <c>restic &lt;Arguments&gt; &lt;repository options&gt; -o sftp.args=-oBatchMode=yes &lt;Trailing&gt;</c>:
/// the order Backrest used, so new command lines read like the old ones.</summary>
internal sealed record ResticCommand(string Binary, ResticRepository Repository, IReadOnlyList<string> Arguments,
    IReadOnlyList<string>? Trailing = null)
{
    /// <summary>Status lines per second (restic's RESTIC_PROGRESS_FPS): 2 for
    /// a backup's live progress, 0 (none) for everything else.</summary>
    public double ProgressFps { get; init; }
    /// <summary>restic's plain output (not JSON) is written to the run's log,
    /// as detail shown with Verbose: forget, prune, check and friends.</summary>
    public bool OutputToTranscript { get; init; }
    /// <summary>How much of standard output the result keeps (characters);
    /// 0 keeps none.</summary>
    public int CaptureOutput { get; init; }
    /// <summary>The longest line read whole; longer lines are cut there.</summary>
    public int MaximumLine { get; init; } = ResticRunner.MaximumLineCharacters;
    /// <summary>The command's name in the log: "backup", "forget"…</summary>
    public string Name => Arguments.Count == 0 ? "restic" : Arguments[0] == "cat" && Arguments.Count > 1 ? "cat " + Arguments[1] : Arguments[0];
}

/// <summary>One line restic wrote. <see cref="Json"/> is valid only during the
/// callback that receives it.</summary>
internal readonly record struct ResticLine(string Text, bool StandardError, string? MessageType, JsonElement Json, bool Truncated)
{
    public bool IsJson => Json.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
}

/// <summary>How a run of restic stopped when asked to.</summary>
internal enum ResticStop
{
    /// <summary>It ended without being asked.</summary>
    None,
    /// <summary>Asked with Ctrl+Break (SIGINT elsewhere), it ended by itself
    /// (and removed its lock).</summary>
    Gentle,
    /// <summary>It did not end in time and was ended with everything it started.</summary>
    Ended,
}

/// <summary>What a run of restic returned. <see cref="ErrorText"/> is bounded:
/// restic's own closing message and the start of its error output.</summary>
internal sealed record ResticResult(int ExitCode, bool Cancelled, ResticStop Stop, TimeSpan Elapsed, string? ExitMessage,
    string ErrorText, string Output, int FileErrors, string StandardError = "")
{
    /// <summary>restic said everything worked.</summary>
    public bool Succeeded => ExitCode == 0 && !Cancelled;
}

/// <summary>
/// Runs restic itself (U175, step 0), in place of the Backrest daemon that
/// ran it before. Grown from <see cref="StreamingRestoreProcess"/>:
/// <list type="bullet">
/// <item>The password goes only in restic's environment, never on its command
/// line; an inherited password file, password command or repository file is
/// removed so it cannot override it.</item>
/// <item>The bundled rclone is first on restic's PATH.</item>
/// <item>restic and everything it starts sit in a job of their own inside
/// Backspace's kill-on-close job: ending the job ends rclone too.</item>
/// <item>Each line is read as it arrives, with a size limit (a longer line is
/// cut, never kept whole); JSON messages (status, summary, error,
/// exit_error, verbose_status) go to the caller, parsed once.</item>
/// <item>The error text is bounded (restic's closing message and the first
/// 1,000 characters of its error output); the whole output goes to the log.</item>
/// <item>The log gets the command line (no password: it is never on it), restic's
/// errors and warnings, and how it ended.</item>
/// <item>Stopping: Ctrl+Break first on Windows, SIGINT elsewhere (restic then
/// removes its own lock), up to <see cref="GentleStopWait"/>, then the whole
/// job (the whole process tree elsewhere); the call returns only once restic
/// has ended.</item>
/// </list>
/// restic's exit codes and JSON messages are documented in its
/// <c>doc/075_scripting.rst</c>.
/// </summary>
internal static class ResticRunner
{
    internal const int MaximumLineCharacters = 128 * 1024;
    /// <summary>How much of restic's error output an error message carries.</summary>
    internal const int ErrorTextLimit = 1000;
    /// <summary>Per-file errors written to the log; the rest are counted.</summary>
    internal const int TranscriptFileErrors = 1000;
    /// <summary>How long restic gets to end after Ctrl+Break before it is ended.</summary>
    internal static TimeSpan GentleStopWait = TimeSpan.FromSeconds(3);
    /// <summary>The exit code a process ended by Backspace gets.</summary>
    internal const int EndedExitCode = 1;

    /// <summary>Exit codes restic documents (doc/075_scripting.rst).</summary>
    internal static string Meaning(int code, string command = "") => code switch
    {
        0 => "success",
        1 => "failed",
        2 => "Go runtime error",
        3 => command == "forget" ? "some snapshots could not be removed" : "some source data could not be read",
        10 => "no repository at this address",
        11 => "the repository is locked",
        12 => "wrong password",
        130 => "cancelled",
        _ => "unknown exit code",
    };

    private static readonly ConcurrentDictionary<int, Action> Running = new();
    private static int _runs;

    /// <summary>Ends every restic this process runs, at once, without asking
    /// (Force quit). Each run still returns only once its restic has ended.</summary>
    internal static void EndAll()
    {
        foreach (var end in Running.Values)
            try { end(); } catch (Exception ex) { AppLogger.For(nameof(ResticRunner)).Debug(ex, "Could not end a restic run"); }
    }

    /// <summary>The start information for <paramref name="command"/>: its
    /// arguments, and its environment built from this process's.</summary>
    internal static ProcessStartInfo StartInfo(ResticCommand command)
    {
        var start = new ProcessStartInfo(command.Binary);
        foreach (var argument in Arguments(command)) start.ArgumentList.Add(argument);
        // Whatever this process inherited must not choose the repository or
        // its password: restic would prefer a password file or command.
        foreach (var name in new[] { "RESTIC_PASSWORD", "RESTIC_PASSWORD_FILE", "RESTIC_PASSWORD_COMMAND", "RESTIC_REPOSITORY", "RESTIC_REPOSITORY_FILE" })
            start.Environment.Remove(name);
        foreach (var entry in command.Repository.Env)
        {
            var equals = entry.IndexOf('=');
            if (equals > 0) start.Environment[entry[..equals]] = entry[(equals + 1)..];
        }
        start.Environment["RESTIC_REPOSITORY"] = command.Repository.Uri;
        if (command.Repository.Password.Length > 0) start.Environment["RESTIC_PASSWORD"] = command.Repository.Password;
        // A positive inherited value would bring status lines to every command.
        start.Environment["RESTIC_PROGRESS_FPS"] = command.ProgressFps.ToString(CultureInfo.InvariantCulture);
        BundledEngineEnvironment.IncludeRclone(start);
        return start;
    }

    internal static IReadOnlyList<string> Arguments(ResticCommand command)
    {
        var arguments = new List<string>(command.Arguments);
        arguments.AddRange(command.Repository.Flags);
        arguments.Add("-o");
        arguments.Add("sftp.args=-oBatchMode=yes");
        if (command.Trailing is not null) arguments.AddRange(command.Trailing);
        return arguments;
    }

    /// <summary>The command line as the log shows it.</summary>
    internal static string Render(string binary, IEnumerable<string> arguments)
    {
        static string Quote(string value) => value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || c == '"')
            ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
        return "\"" + binary + "\" " + string.Join(' ', arguments.Select(Quote));
    }

    /// <summary>A log line in the same shape as the engine's other records:
    /// <c>[restic] 06:10:43.026Z&lt;TAB&gt;info&lt;TAB&gt;…</c>. Activity &amp;
    /// logs shows debug lines with Verbose only.</summary>
    internal static string TranscriptLine(string level, string message, DateTime? utc = null)
        => "[restic] " + (utc ?? DateTime.UtcNow).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z\t" + level + "\t" + message;

    /// <summary>Removes the repository password from <paramref name="text"/>,
    /// should restic or rclone ever print it.</summary>
    internal static string Redact(string text, string password)
        => password.Length >= 4 && text.Contains(password, StringComparison.Ordinal) ? text.Replace(password, "***", StringComparison.Ordinal) : text;

    /// <summary>Runs <paramref name="command"/> to its end. <paramref name="onLine"/>
    /// hears every line (on the reading thread); <paramref name="transcript"/>
    /// gets the lines for the run's log. Cancelling stops restic (see the class
    /// remarks); the call still returns only after it has ended.</summary>
    internal static Task<ResticResult> RunAsync(ResticCommand command, Action<ResticLine>? onLine,
        Action<string>? transcript, CancellationToken ct, Action<int>? onStarted = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(command.Binary))
            throw new EngineBinaryMissingException("A required backup component is missing. Restart Backspace while online to download it again. " +
                "Your Backspace.config folder is not affected.", command.Binary);
        return RunStartAsync(StartInfo(command), command, onLine, transcript, ct, onStarted);
    }

    /// <summary>The process part of <see cref="RunAsync"/>, for a prepared
    /// start (tests start other console programs this way).</summary>
    internal static async Task<ResticResult> RunStartAsync(ProcessStartInfo start, ResticCommand command, Action<ResticLine>? onLine,
        Action<string>? transcript, CancellationToken ct, Action<int>? onStarted = null)
    {
        ct.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        var password = command.Repository.Password;
        void Write(string line) { try { transcript?.Invoke(Redact(line, password)); } catch { /* a log writer must not stop restic */ } }

        using var process = new Process { StartInfo = start };
        var output = new ResticOutput(command);
        var elapsed = Stopwatch.StartNew();
        var sharedConsole = false;
        var started = false;
        var forced = false;
        var runId = Interlocked.Increment(ref _runs);
        Task? stdout = null, stderr = null;
#if WINDOWS
        Platform.Windows.ProcessTreeJob? job = null;
#endif
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => cancellation.TrySetResult());
        var stop = ResticStop.None;
        try
        {
#if WINDOWS
            // restic in a process group of its own, so Ctrl+Break reaches it
            // alone; on this process's console when it has one.
            lock (Platform.Windows.ConsoleBreak.Gate)
            {
                sharedConsole = Platform.Windows.ConsoleBreak.HasConsole();
                start.CreateNoWindow = !sharedConsole;
                start.CreateNewProcessGroup = true;
                if (!process.Start()) throw new IOException($"restic ({command.Name}) could not start.");
            }
#else
            start.CreateNoWindow = true;
            if (!process.Start()) throw new IOException($"restic ({command.Name}) could not start.");
#endif
            started = true;
            // Backspace's own job first, then this run's: the run's job then
            // sits inside Backspace's (see ProcessTreeJob).
            if (!KillOnExitJobObject.TryAssign(process) && !process.HasExited)
                throw new IOException("Windows could not attach restic to Backspace's lifetime. The operation was stopped.");
#if WINDOWS
            job = Platform.Windows.ProcessTreeJob.TryCreate();
            if (job is not null && !job.TryAssign(process)) { job.Dispose(); job = null; }
            var endJob = job;
#endif
            Running[runId] = () =>
            {
                forced = true;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
#if WINDOWS
                endJob?.Terminate(EndedExitCode);
#endif
            };
            // restic never reads its input here: give it none.
            try { process.StandardInput.Close(); } catch (IOException) { }
            try { onStarted?.Invoke(process.Id); } catch (Exception ex) { AppLogger.For(nameof(ResticRunner)).Debug(ex, "restic start observer failed"); }
            Write(TranscriptLine("info", "command: " + Render(start.FileName, start.ArgumentList)));

            async Task ReadAsync(TextReader reader, bool standardError)
            {
                try { await ReadLinesAsync(reader, command.MaximumLine, (text, cut) => output.Accept(text, cut, standardError, onLine, Write)).ConfigureAwait(false); }
                catch (Exception ex) { failure.TrySetResult(); AppLogger.For(nameof(ResticRunner)).Warning(ex, "Reading restic's output failed"); throw; }
            }
            stdout = ReadAsync(process.StandardOutput, standardError: false);
            stderr = ReadAsync(process.StandardError, standardError: true);
            var exited = process.WaitForExitAsync(CancellationToken.None);
            var first = await Task.WhenAny(exited, cancellation.Task, failure.Task).ConfigureAwait(false);
            if (first != exited)
            {
#if WINDOWS
                stop = await StopAsync(process, job, sharedConsole, gentle: first == cancellation.Task).ConfigureAwait(false);
#else
                stop = await StopAsync(process, gentle: first == cancellation.Task).ConfigureAwait(false);
#endif
            }
            await WaitForEndAsync(process).ConfigureAwait(false);
#if WINDOWS
            // Anything restic left running ends with it: an rclone it started
            // shares restic's error output, which would keep the reading open.
            job?.Terminate(EndedExitCode);
#endif
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            var exitCode = process.ExitCode;
            var cancelled = ct.IsCancellationRequested;
            var meaning = Meaning(exitCode, command.Name);
            var took = Seconds(elapsed.Elapsed);
            if (stop == ResticStop.Gentle)
                Write(TranscriptLine("warn", $"{command.Name} stopped on request after {took}: restic ended by itself (exit code {exitCode})"));
            else if (stop == ResticStop.Ended || forced)
                Write(TranscriptLine("warn", forced && stop == ResticStop.None
                    ? $"{command.Name} ended by Force quit after {took}, with everything restic started"
                    : cancelled
                    ? $"{command.Name} stopped on request after {took}: restic did not end within {Seconds(GentleStopWait)} and was ended with everything it started"
                    : $"{command.Name} ended after {took}: its output could not be read"));
            else
                Write(TranscriptLine(exitCode == 0 ? "info" : exitCode == 3 ? "warn" : "error",
                    $"{command.Name} finished: exit code {exitCode} ({meaning}) after {took}" + output.FileErrorNote()
                    + (exitCode == 0 ? "" : output.Reason() is { Length: > 0 } reason ? ": " + reason : "")));
            AppLogger.For("EngineTiming").Debug("restic {Command} finished in {ElapsedMs} ms; exit={ExitCode}; stop={Stop}",
                command.Name, elapsed.ElapsedMilliseconds, exitCode, stop);
            return new ResticResult(exitCode, cancelled, stop, elapsed.Elapsed, output.ExitMessage,
                Redact(output.ErrorText(exitCode, meaning), password), output.Output, output.FileErrors, Redact(output.StandardError, password));
        }
        finally
        {
            Running.TryRemove(runId, out _);
            if (started)
            {
                // Never return while restic may still write to the repository:
                // the caller's repository turn ends when this returns.
                await WaitForEndAsync(process, terminate: true).ConfigureAwait(false);
                if (stdout is not null) try { await stdout.ConfigureAwait(false); } catch { }
                if (stderr is not null) try { await stderr.ConfigureAwait(false); } catch { }
            }
#if WINDOWS
            // Closing the job ends anything restic left behind.
            job?.Dispose();
#endif
        }
    }

#if WINDOWS
    private static async Task<ResticStop> StopAsync(Process process, Platform.Windows.ProcessTreeJob? job, bool sharedConsole, bool gentle)
    {
        if (process.HasExited) return ResticStop.None;
        if (gentle && Platform.Windows.ConsoleBreak.Send(process.Id, sharedConsole))
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(GentleStopWait).ConfigureAwait(false);
                return ResticStop.Gentle;
            }
            catch (TimeoutException) { }
        }
        // The tree first, while restic still says which processes are its
        // children, then the job, for anything that left the tree.
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) { AppLogger.For(nameof(ResticRunner)).Debug(ex, "Killing restic's tree failed"); }
        job?.Terminate(EndedExitCode);
        return ResticStop.Ended;
    }
#else
    /// <summary>SIGINT first, as Ctrl+C in a terminal (restic removes its
    /// lock and stops), then the whole process tree.</summary>
    private static async Task<ResticStop> StopAsync(Process process, bool gentle)
    {
        if (process.HasExited) return ResticStop.None;
        if (gentle && Interrupt(process.Id))
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(GentleStopWait).ConfigureAwait(false);
                return ResticStop.Gentle;
            }
            catch (TimeoutException) { }
        }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) { AppLogger.For(nameof(ResticRunner)).Debug(ex, "Killing restic's tree failed"); }
        return ResticStop.Ended;
    }

    /// <summary>Sends SIGINT with the system's own <c>kill</c> command.</summary>
    private static bool Interrupt(int pid)
    {
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", ["-INT", pid.ToString(CultureInfo.InvariantCulture)])
                { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
            if (kill is null || !kill.WaitForExit(2000)) return false;
            return kill.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLogger.For(nameof(ResticRunner)).Debug(ex, "Could not interrupt restic");
            return false;
        }
    }
#endif

    private static async Task WaitForEndAsync(Process process, bool terminate = false)
    {
        while (true)
        {
            try
            {
                if (process.HasExited) return;
                if (terminate) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                AppLogger.For(nameof(ResticRunner)).Error(ex, "restic's end is not confirmed. Keeping its repository turn.");
                terminate = true;
                await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString(span.TotalSeconds < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";

    /// <summary>Reads lines as they arrive. A line longer than
    /// <paramref name="limit"/> is cut there and the rest of it skipped, so
    /// memory stays bounded; <c>cut</c> says so.</summary>
    internal static async Task ReadLinesAsync(TextReader reader, int limit, Action<string, bool> accept)
    {
        var buffer = new char[8192];
        var line = new StringBuilder();
        var cut = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\n')
                {
                    accept(TrimCarriageReturn(line), cut);
                    line.Clear();
                    cut = false;
                }
                else if (line.Length < limit) line.Append(c);
                else cut = true;
            }
        }
        if (line.Length > 0 || cut) accept(TrimCarriageReturn(line), cut);
    }

    private static string TrimCarriageReturn(StringBuilder line)
    {
        var length = line.Length;
        if (length > 0 && line[length - 1] == '\r') length--;
        return line.ToString(0, length);
    }

    /// <summary>What a run collects from restic's output, from both streams.</summary>
    internal sealed class ResticOutput(ResticCommand command)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _errors = new();
        private readonly StringBuilder _output = new();
        private int _droppedErrorCharacters;
        private int _fileErrors;
        private string? _exitMessage;

        internal string? ExitMessage { get { lock (_gate) return _exitMessage; } }
        internal string Output { get { lock (_gate) return _output.ToString(); } }
        /// <summary>restic's plain error output, bounded (see <see cref="ErrorTextLimit"/>).</summary>
        internal string StandardError { get { lock (_gate) return _errors.ToString(); } }
        internal int FileErrors { get { lock (_gate) return _fileErrors; } }

        internal string FileErrorNote()
        {
            lock (_gate)
                return _fileErrors > TranscriptFileErrors
                    ? $"; {_fileErrors:N0} file errors, the first {TranscriptFileErrors:N0} are listed above"
                    : "";
        }

        internal void Accept(string text, bool cut, bool standardError, Action<ResticLine>? onLine, Action<string> write)
        {
            JsonDocument? document = null;
            string? type = null;
            if (!cut && text.Length > 1 && text[0] is '{' or '[')
            {
                try
                {
                    document = JsonDocument.Parse(text);
                    if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("message_type", out var kind)
                        && kind.ValueKind == JsonValueKind.String) type = kind.GetString();
                }
                catch (JsonException) { document?.Dispose(); document = null; }
            }
            using (document)
            {
                var json = document?.RootElement ?? default;
                // Plain text as a terminal shows it: restic starts some lines
                // with a carriage return to overwrite the one before.
                var shown = document is null ? Shown(text) : text;
                lock (_gate)
                {
                    if (!standardError)
                    {
                        if (command.CaptureOutput > 0 && _output.Length < command.CaptureOutput)
                            _output.Append(text.AsSpan(0, Math.Min(text.Length, command.CaptureOutput - _output.Length))).Append('\n');
                    }
                    else if (type == "exit_error")
                    {
                        var message = json.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                        _exitMessage = Bound(message.TrimEnd());
                    }
                    else if (type == "error") _fileErrors++;
                    else if (document is null && shown.Length > 0)
                    {
                        var room = ErrorTextLimit - _errors.Length;
                        if (room > 0)
                        {
                            if (_errors.Length > 0) _errors.Append('\n');
                            _errors.Append(shown.AsSpan(0, Math.Min(shown.Length, room)));
                            _droppedErrorCharacters += Math.Max(0, shown.Length - room);
                        }
                        else _droppedErrorCharacters += shown.Length;
                    }
                }
                // The log: restic's JSON errors as they are (shown, like the
                // file errors Backrest kept), its plain output as detail
                // (shown with Verbose, as Backrest's command logs were); the
                // line that says how it ended carries the reason.
                if (standardError)
                {
                    if (type == "error") { if (FileErrors <= TranscriptFileErrors) write("[restic] " + text); }
                    else if (document is not null) write("[restic] " + text);
                    else if (shown.Length > 0) write(TranscriptLine("debug", shown));
                }
                else if (command.OutputToTranscript && document is null && shown.Length > 0)
                    write(TranscriptLine("debug", shown));
                onLine?.Invoke(new ResticLine(text, standardError, type, json, cut));
            }
        }

        /// <summary>What a terminal would show of a line: the text after its
        /// last carriage return, without trailing spaces.</summary>
        internal static string Shown(string text)
        {
            var cr = text.LastIndexOf('\r');
            return (cr < 0 ? text : text[(cr + 1)..]).TrimEnd();
        }

        /// <summary>The first line of restic's reason, for the log's end line.</summary>
        internal string Reason()
        {
            lock (_gate)
            {
                var text = _exitMessage is { Length: > 0 } exit ? exit : _errors.ToString().Trim();
                var end = text.IndexOf('\n');
                return end < 0 ? text : text[..end].TrimEnd('\r');
            }
        }

        /// <summary>restic's reason, bounded: its closing message, then the
        /// start of its error output, then the exit code.</summary>
        internal string ErrorText(int exitCode, string meaning)
        {
            lock (_gate)
            {
                var errors = _errors.ToString().Trim();
                var text = _exitMessage is { Length: > 0 } exit
                    ? errors.Length == 0 || errors == exit ? exit : exit + "\nOutput: " + errors
                    : errors.Length > 0 ? errors : $"restic ended without saying why ({meaning})";
                if (_droppedErrorCharacters > 0)
                    text += $"\n… {_droppedErrorCharacters:N0} more characters of restic's output are in the run's log.";
                return text + $" (restic exit code {exitCode.ToString(CultureInfo.InvariantCulture)})";
            }
        }

        private static string Bound(string text) => text.Length <= ErrorTextLimit ? text : text[..ErrorTextLimit] + "…";
    }
}
