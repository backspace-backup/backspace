using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Verifies that a just-captured Dropbox OAuth token is live and tells
/// the user what it maps to. The Restic-side incarnation drives rclone
/// against a tiny probe: <c>rclone lsd dropbox:</c> succeeds iff the
/// token can list the user's Dropbox root, fails with a humanised
/// error otherwise.
///
/// Architecturally this service does very little: it materialises a
/// throwaway rclone.conf with one section for the candidate token,
/// shells out to the bundled rclone binary, and surfaces the exit
/// code + redacted stderr tail. Path resolution to the rclone binary
/// goes through <see cref="AppPaths.RcloneBinary"/>; FetchBackrest.targets
/// drops it next to the app at build time. If the binary isn't on disk
/// the probe returns a clear "rclone missing" error so the user knows
/// to repair the install rather than re-paste their token thinking it
/// was the cause.
/// </summary>
public sealed class DropboxAuthProbe
{
    private static ILogger _log => AppLogger.For<DropboxAuthProbe>();
    private readonly SecretsStore _secrets;

    public DropboxAuthProbe(SecretsStore secrets) => _secrets = secrets;

    /// <summary>
    /// Runs a fast, side-effect-free check against Dropbox using the
    /// provided refresh-token JSON blob (the same shape rclone's
    /// interactive <c>rclone config</c> stores). Writes a throwaway
    /// rclone.conf in a per-probe temp directory and points rclone at
    /// it via <c>RCLONE_CONFIG</c>, so the user's real config is
    /// untouched.
    /// </summary>
    public async Task<ProbeResult> HealthCheckAsync(string tokenJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tokenJson))
            return ProbeResult.Fail("Paste a token from the Dropbox helper first.");

        if (!File.Exists(AppPaths.RcloneBinary))
            return ProbeResult.Fail(
                "Backspace can't find the bundled rclone binary. Run a non-skip build " +
                "(drop -p:SkipBackrestDownload) or drop rclone(.exe) next to the Backspace exe.");

        _log.Information("Dropbox token health-check starting (token blob length={Len})", tokenJson.Length);

        var probeDir = Path.Combine(Path.GetTempPath(), $"backspace-dbx-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeDir);
        try
        {
            var rcloneConf = Path.Combine(probeDir, "rclone.conf");
            // Create the file with 0600 atomically on Unix. The prior
            // write-then-chmod sequence had a race window where the
            // file sat at the user's umask (typically 0644) between
            // File.WriteAllText returning and File.SetUnixFileMode
            // executing — long enough for another local user to read
            // the OAuth refresh token. UnixCreateMode sets the mode at
            // open(2) time, eliminating the window.
            var probeOpts = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                probeOpts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(rcloneConf, probeOpts))
            using (var sw = new StreamWriter(fs))
                sw.Write(BuildProbeConfig(tokenJson));

            var (exit, output) = await RunRcloneAsync(
                AppPaths.RcloneBinary,
                args: new[] { "lsd", $"{ProbeRemoteName}:" },
                rcloneConfPath: rcloneConf,
                ct: ct).ConfigureAwait(false);

            if (exit == 0)
            {
                _log.Information("Dropbox token validated through rclone lsd");
                return ProbeResult.Ok("Connected to Dropbox. Token is valid.");
            }

            _log.Warning("Dropbox probe failed (exit {Exit}): {Tail}", exit, Tail(output));
            return ProbeResult.Fail(SummarizeError(output, tokenJson,
                "Dropbox refused the token. It may have been revoked, expired, or truncated in paste."));
        }
        finally
        {
            try { Directory.Delete(probeDir, recursive: true); }
            catch (Exception ex) { _log.Warning(ex, "Couldn't clean Dropbox probe dir {Path}", probeDir); }
        }
    }

    /// <summary>Fixed remote name for the probe's rclone.conf section
    /// — there's only one section in the throwaway config, so any
    /// valid identifier works, but pinning it makes the diagnostic
    /// stderr easier to read in logs.</summary>
    internal const string ProbeRemoteName = "backspace-dropbox-probe";

    internal static string BuildProbeConfig(string tokenJson) =>
        $"[{ProbeRemoteName}]{Environment.NewLine}" +
        $"type = dropbox{Environment.NewLine}" +
        $"token = {tokenJson}{Environment.NewLine}";

    private static async Task<(int exit, string output)> RunRcloneAsync(
        string rcloneExe, string[] args, string rcloneConfPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = rcloneExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["RCLONE_CONFIG"] = rcloneConfPath;

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var buf = new StringBuilder();
        var gate = new object();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) buf.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (gate) buf.AppendLine("[err] " + e.Data); };

        p.Start();
        KillOnExitJobObject.TryAssign(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using (ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }))
        {
            await p.WaitForExitAsync(ct);
        }
        return (p.ExitCode, buf.ToString());
    }

    /// <summary>
    /// Pull a one-line user-facing cause out of rclone's stderr,
    /// redacting any token-shaped string that survived. rclone prints
    /// "Failed to ... : <reason>" and "couldn't get account info: ..."
    /// — both forms are useful; we surface the first non-empty
    /// error-tagged line we find.
    /// </summary>
    internal static string SummarizeError(string raw, string secret, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // rclone tags errors with "ERROR" or "Failed to". Both
            // patterns carry the human-readable reason on the same
            // line.
            if (line.Contains(" ERROR ", StringComparison.Ordinal)
                || line.Contains("Failed to", StringComparison.Ordinal)
                || line.StartsWith("[err]", StringComparison.Ordinal))
            {
                var msg = Redact(line, secret);
                return msg.Length > 240 ? msg[..240] + "…" : msg;
            }
        }
        return Redact(Tail(raw), secret);
    }

    /// <summary>Replace any embedded secret (or a token-shaped substring)
    /// with a sentinel so summarised error text is safe to log or
    /// display. Exposed for tests.</summary>
    internal static string Redact(string text, string secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 8)
            text = text.Replace(secret, "[REDACTED]");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\bsl\.[A-Za-z0-9_\-]{20,}\b", "[REDACTED]");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?i)\b(bearer|token|access_token|refresh_token)\s*[:=\s]\s*[A-Za-z0-9_\-\.]{16,}",
            m => m.Groups[1].Value + "=[REDACTED]");
        return text;
    }

    private static string Tail(string raw, int maxChars = 240)
    {
        var trimmed = raw?.Trim() ?? "";
        return trimmed.Length <= maxChars ? trimmed : "…" + trimmed[^maxChars..];
    }
}

public readonly record struct ProbeResult(bool Healthy, string Message)
{
    public static ProbeResult Ok(string msg)   => new(true, msg);
    public static ProbeResult Fail(string msg) => new(false, msg);
}
