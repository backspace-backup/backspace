using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>
/// Runs an owned restore process while consuming progress without accumulating
/// it. The caller must hold its repository read lease until this method returns.
/// Cancellation requests termination; it never returns while the child might
/// still be writing into the caller's staging directory.
/// </summary>
internal static class StreamingRestoreProcess
{
    internal sealed record Result(int ExitCode, bool Cancelled, string Output, bool HadErrors);
    internal const int MaximumLineCharacters = 128 * 1024;

    internal static async Task<Result> RunAsync(ProcessStartInfo start,
        Action<string>? onLine, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        using var process = new Process { StartInfo = start };
        var started = false;
        Task? stdout = null, stderr = null;
        var output = new OutputSummary();
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => cancellation.TrySetResult());

        async Task ReadAsync(TextReader reader)
        {
            try
            {
                await ReadLinesAsync(reader, line =>
                {
                    output.Accept(line);
                    onLine?.Invoke(line);
                }).ConfigureAwait(false);
            }
            catch (Exception ex) { failure.TrySetResult(ex); throw; }
        }

        try
        {
            if (!process.Start()) throw new IOException("The restore process could not start.");
            started = true;
            // A very short-lived process may have already exited successfully.
            if (!KillOnExitJobObject.TryAssign(process) && !process.HasExited)
                throw new IOException("Windows could not attach the restore process to Backspace's lifetime. Restore was stopped.");
            stdout = ReadAsync(process.StandardOutput);
            stderr = ReadAsync(process.StandardError);
            var exited = process.WaitForExitAsync(CancellationToken.None);
            var completed = await Task.WhenAny(exited, cancellation.Task, failure.Task).ConfigureAwait(false);
            await StopAndWaitAsync(process, terminate: completed != exited).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new Result(process.ExitCode, ct.IsCancellationRequested, output.Text, output.HadErrors);
        }
        finally
        {
            if (started)
            {
                // Do not put a cancellation token or timeout on this wait.
                // Returning early would release the repository lease and allow
                // maintenance to race a restore that is still alive.
                await StopAndWaitAsync(process, terminate: true).ConfigureAwait(false);
                if (stdout is not null) try { await stdout.ConfigureAwait(false); } catch { }
                if (stderr is not null) try { await stderr.ConfigureAwait(false); } catch { }
            }
        }
    }

    private static async Task StopAndWaitAsync(Process process, bool terminate)
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
                AppLogger.For("RestoreProcess").Error(ex,
                    "Restore process exit is not confirmed. Keeping its repository lease and temporary output.");
                terminate = true;
                await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal static async Task ReadLinesAsync(TextReader reader, Action<string> accept)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n')
                {
                    accept(line.ToString().TrimEnd('\r'));
                    line.Clear();
                }
                else
                {
                    if (line.Length >= MaximumLineCharacters)
                        throw new IOException("The restore process returned an unexpectedly large output line.");
                    line.Append(buffer[i]);
                }
            }
        }
        if (line.Length > 0) accept(line.ToString().TrimEnd('\r'));
    }

    /// <summary>Latest summary plus a bounded diagnostic tail; progress is transient.</summary>
    internal sealed class OutputSummary
    {
        internal const int DiagnosticLimit = 32 * 1024;
        private readonly object _gate = new();
        private readonly StringBuilder _diagnostics = new();
        private string _summary = "";
        private bool _hadErrors;
        internal bool HadErrors { get { lock (_gate) return _hadErrors; } }
        internal string Text { get { lock (_gate) return _diagnostics + "\n" + _summary; } }

        internal void Accept(string line)
        {
            string? type = null;
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("message_type", out var message)
                    && message.ValueKind == JsonValueKind.String) type = message.GetString();
            }
            catch (JsonException) { }
            lock (_gate)
            {
                if (type == "summary") { _summary = line; return; }
                if (type is "status" or "verbose_status") return;
                if (type == "error") _hadErrors = true;
                if (line.Length > DiagnosticLimit) line = line[^DiagnosticLimit..];
                _diagnostics.AppendLine(line);
                if (_diagnostics.Length > DiagnosticLimit)
                    _diagnostics.Remove(0, _diagnostics.Length - DiagnosticLimit);
            }
        }
    }
}
