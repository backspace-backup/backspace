using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// One scenario's run record. Built up step-by-step by
/// <see cref="ScenarioContext"/> and serialised into the
/// <see cref="ScenarioRunSummary"/> markdown at suite end.
/// </summary>
public sealed class ScenarioReport
{
    public string Name { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; private set; }
    public ScenarioStatus Status { get; private set; } = ScenarioStatus.Pending;
    public string? StatusReason { get; private set; }
    public string ScreenshotDir { get; }
    public List<ScenarioStep> Steps { get; } = new();
    public List<string> Anomalies { get; } = new();

    public ScenarioReport(string name, string screenshotDir)
    {
        Name = name;
        ScreenshotDir = screenshotDir;
    }

    public void RecordStep(string label, string screenshotPath, string? note = null) =>
        Steps.Add(new ScenarioStep(label, screenshotPath, note));

    /// <summary>Record a step that has no screenshot — e.g. an
    /// assertion result, a byte-equality verdict, or any "we just
    /// want this to show up as a row in SUMMARY.md without a
    /// broken image link" moment.</summary>
    public void RecordNote(string label, string? note = null) =>
        Steps.Add(new ScenarioStep(label, ScreenshotPath: null, note));

    public void RecordAnomaly(string message) => Anomalies.Add(message);

    public void MarkPassed()                  { Status = ScenarioStatus.Passed; EndedUtc = DateTime.UtcNow; }
    public void MarkSkipped(string reason)    { Status = ScenarioStatus.Skipped; StatusReason = reason; EndedUtc = DateTime.UtcNow; }
    public void MarkFailed (string reason)    { Status = ScenarioStatus.Failed;  StatusReason = reason; EndedUtc = DateTime.UtcNow; }
}

public enum ScenarioStatus { Pending, Passed, Skipped, Failed }

public sealed record ScenarioStep(string Label, string? ScreenshotPath, string? Note);

/// <summary>
/// Suite-level aggregator. One static instance for the whole xUnit
/// assembly; every scenario calls <see cref="Register"/> with its
/// report. <see cref="WriteSummary"/> is invoked from the assembly
/// fixture's Dispose so a SUMMARY.md is rebuilt every test run with
/// the current pass/skip/fail breakdown plus the missing-secrets
/// list the user needs to act on.
/// </summary>
public static class ScenarioRunSummary
{
    private static readonly object _lock = new();
    private static readonly List<ScenarioReport> _reports = new();
    private static SecretsLoader? _secrets;

    public static void Register(ScenarioReport report)
    {
        lock (_lock) _reports.Add(report);
    }

    public static void AttachSecrets(SecretsLoader secrets) => _secrets = secrets;

    public static string WriteSummary(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var summaryPath = Path.Combine(outputDir, "SUMMARY.md");
        var sb = new StringBuilder();

        IReadOnlyList<ScenarioReport> snapshot;
        lock (_lock) snapshot = _reports.OrderBy(r => r.Name).ToArray();

        var passed   = snapshot.Count(r => r.Status == ScenarioStatus.Passed);
        var skipped  = snapshot.Count(r => r.Status == ScenarioStatus.Skipped);
        var failed   = snapshot.Count(r => r.Status == ScenarioStatus.Failed);

        sb.AppendLine("# E2E scenario run summary");
        sb.AppendLine();
        sb.AppendLine($"_Generated {DateTime.Now:yyyy-MM-dd HH:mm}_  ");
        sb.AppendLine($"**{passed} step sequences completed · {skipped} skipped · {failed} failed** out of {snapshot.Count} scenarios.");
        sb.AppendLine();

        // Missing secrets table — first thing the user should see if they
        // want to unlock more coverage. Each row tells them exactly which
        // file to drop in.
        if (_secrets is not null && _secrets.MissingSecretsByName.Count > 0)
        {
            sb.AppendLine("## Missing secrets");
            sb.AppendLine();
            sb.AppendLine("Scenarios were skipped because these credentials weren't on disk. Drop the missing file at the listed path to unlock them on the next run:");
            sb.AppendLine();
            sb.AppendLine("| Secret | Where to put it |");
            sb.AppendLine("|--------|-----------------|");
            foreach (var (name, path) in _secrets.MissingSecretsByName.OrderBy(kv => kv.Key))
                sb.AppendLine($"| `{name}` | `{path}` |");
            sb.AppendLine();
        }

        // Per-scenario breakdown.
        sb.AppendLine("> Scope: scenario steps and their cleanup only. This report cannot observe the test runner’s later teardown. A completed sequence is not a passing test result. Use the test runner result/TRX for the final verdict.");
        sb.AppendLine();
        sb.AppendLine("## Scenarios");
        sb.AppendLine();
        foreach (var r in snapshot)
        {
            var statusBadge = r.Status switch
            {
                ScenarioStatus.Passed  => "✓ STEPS COMPLETED",
                ScenarioStatus.Skipped => "⊘ SKIP",
                ScenarioStatus.Failed  => "❌ FAIL",
                _                      => "… running",
            };
            var dur = r.EndedUtc is { } end ? (end - r.StartedUtc).TotalSeconds.ToString("F1") + "s" : "—";
            sb.AppendLine($"### {r.Name} — {statusBadge} ({dur})");
            if (!string.IsNullOrEmpty(r.StatusReason))
                sb.AppendLine($"_Reason:_ {r.StatusReason}");
            sb.AppendLine();
            if (r.Steps.Count > 0)
            {
                sb.AppendLine("| # | Step | Screenshot | Notes |");
                sb.AppendLine("|---|------|------------|-------|");
                for (int i = 0; i < r.Steps.Count; i++)
                {
                    var s = r.Steps[i];
                    // A no-screenshot step renders an em-dash in the
                    // Screenshot column so the row stays legible
                    // without a broken markdown link.
                    string screenshotCell;
                    if (string.IsNullOrEmpty(s.ScreenshotPath))
                    {
                        screenshotCell = "—";
                    }
                    else
                    {
                        var rel = Path.GetRelativePath(outputDir, s.ScreenshotPath).Replace('\\', '/');
                        screenshotCell = $"[{Path.GetFileName(s.ScreenshotPath)}]({rel})";
                    }
                    sb.AppendLine($"| {i + 1} | {s.Label} | {screenshotCell} | {s.Note ?? ""} |");
                }
                sb.AppendLine();
            }
            if (r.Anomalies.Count > 0)
            {
                sb.AppendLine("**Anomalies:**");
                foreach (var a in r.Anomalies) sb.AppendLine($"- {a}");
                sb.AppendLine();
            }
        }

        File.WriteAllText(summaryPath, sb.ToString());

        // Also emit a richer HTML report — easier to share with
        // reviewers and clickable into screenshots inline. The HTML
        // sits next to SUMMARY.md so both formats stay in sync.
        WriteHtmlSummary(outputDir, snapshot, passed, skipped, failed);

        return summaryPath;
    }

    /// <summary>
    /// Build a self-contained HTML report of every scenario, with
    /// inline thumbnail-style screenshots that click through to the
    /// full PNG. The intent is "open this in a browser and you can
    /// audit every UI path the suite exercised without running the
    /// app yourself". Sits at outputDir/index.html alongside
    /// SUMMARY.md so both renderings live in the same artifact dir.
    /// </summary>
    private static void WriteHtmlSummary(
        string outputDir,
        IReadOnlyList<ScenarioReport> snapshot,
        int passed, int skipped, int failed)
    {
        var path = Path.Combine(outputDir, "index.html");
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<title>Backspace E2E scenario report</title>");
        html.AppendLine("<style>");
        html.AppendLine(@"
:root { color-scheme: light dark; }
body { font: 14px/1.5 -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif; max-width: 1200px; margin: 24px auto; padding: 0 16px; }
h1 { margin: 0 0 4px; }
.summary { color: #555; margin: 0 0 24px; }
.summary .pass { color: #2a7d2a; font-weight: 600; }
.summary .skip { color: #b48e00; font-weight: 600; }
.summary .fail { color: #c33; font-weight: 600; }
.scenario { border: 1px solid #ddd; border-radius: 8px; margin: 16px 0; padding: 16px; }
.scenario h2 { margin: 0 0 6px; font-size: 18px; }
.scenario .reason { color: #c33; margin: 0 0 8px; font-style: italic; }
.scenario .meta { color: #888; font-size: 12px; margin: 0 0 12px; }
.badge { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 12px; font-weight: 600; vertical-align: middle; margin-left: 8px; }
.badge.pass { background: #e3f5e3; color: #2a7d2a; }
.badge.skip { background: #fff3cd; color: #856404; }
.badge.fail { background: #f8d7da; color: #c33; }
.steps { width: 100%; border-collapse: collapse; }
.steps th, .steps td { text-align: left; vertical-align: top; padding: 6px 8px; border-bottom: 1px solid #eee; }
.steps th { color: #555; font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; }
.steps img { max-width: 240px; max-height: 160px; border: 1px solid #ddd; border-radius: 4px; }
.steps a { display: inline-block; }
.scenario { scroll-margin-top: 16px; }
.scenario:target { box-shadow: 0 0 0 2px #2a7d2a55; }
.anchor { color: #aaa; text-decoration: none; font-weight: 400; margin-right: 8px; }
.scenario h2:hover .anchor { color: #2a7d2a; }
.anomalies { background: #fff3cd; border-left: 4px solid #f0ad4e; padding: 8px 12px; margin-top: 10px; }
.anomalies ul { margin: 4px 0 0 18px; padding: 0; }
.missing-secrets { background: #f4f6f8; border-left: 4px solid #6c757d; padding: 12px 16px; margin: 16px 0; }
.missing-secrets table { width: 100%; border-collapse: collapse; margin-top: 8px; }
.missing-secrets td { padding: 4px 8px; border-bottom: 1px solid #e0e3e6; font-family: ui-monospace, monospace; font-size: 12px; }
@media (prefers-color-scheme: dark) {
  body { background: #1c1c1e; color: #eee; }
  .scenario { border-color: #333; }
  .steps th, .steps td { border-color: #333; color: #ddd; }
  .badge.pass { background: rgba(42,125,42,0.2); }
  .badge.skip { background: rgba(180,142,0,0.2); }
  .badge.fail { background: rgba(204,51,51,0.2); }
  .missing-secrets { background: #2a2a2c; }
}
");
        html.AppendLine("</style></head><body>");

        html.AppendLine($"<h1>Backspace E2E scenario report</h1>");
        html.AppendLine($"<p class=\"summary\">Generated {DateTime.Now:yyyy-MM-dd HH:mm} · ");
        html.AppendLine($"<span class=\"pass\">{passed} step sequences completed</span> · ");
        html.AppendLine($"<span class=\"skip\">{skipped} skipped</span> · ");
        html.AppendLine($"<span class=\"fail\">{failed} failed</span> ");
        html.AppendLine($"out of {snapshot.Count} scenarios.</p>");
        html.AppendLine("<p class=\"anomalies\"><strong>Evidence scope:</strong> scenario steps and their cleanup only. Later test-runner teardown can still fail. STEPS COMPLETED is not a passing test verdict; use the test runner result/TRX for that.</p>");

        if (_secrets is not null && _secrets.MissingSecretsByName.Count > 0)
        {
            html.AppendLine("<div class=\"missing-secrets\"><strong>Missing secrets</strong> — drop the listed files to unlock these scenarios on the next run.<table>");
            foreach (var (name, p) in _secrets.MissingSecretsByName.OrderBy(kv => kv.Key))
                html.AppendLine($"<tr><td>{HtmlEncode(name)}</td><td>{HtmlEncode(p)}</td></tr>");
            html.AppendLine("</table></div>");
        }

        foreach (var r in snapshot)
        {
            var statusClass = r.Status switch
            {
                ScenarioStatus.Passed  => "pass",
                ScenarioStatus.Skipped => "skip",
                ScenarioStatus.Failed  => "fail",
                _ => "skip",
            };
            var statusLabel = r.Status switch
            {
                ScenarioStatus.Passed  => "STEPS COMPLETED",
                ScenarioStatus.Skipped => "SKIP",
                ScenarioStatus.Failed  => "FAIL",
                _ => "—",
            };
            var dur = r.EndedUtc is { } end ? (end - r.StartedUtc).TotalSeconds.ToString("F1") + "s" : "—";
            // Stable per-scenario anchor so ROADMAP and any other doc can
            // deep-link to the rendered proof via `index.html#<scenario>`.
            // The scenario name is already URL-safe (kebab-case, ASCII)
            // for the same reason it's used as a directory name.
            html.AppendLine($"<section class=\"scenario\" id=\"{HtmlEncode(r.Name)}\">");
            html.AppendLine($"<h2><a href=\"#{HtmlEncode(r.Name)}\" class=\"anchor\">#</a>{HtmlEncode(r.Name)} <span class=\"badge {statusClass}\">{statusLabel}</span></h2>");
            html.AppendLine($"<div class=\"meta\">Duration: {dur}</div>");
            if (!string.IsNullOrEmpty(r.StatusReason))
                html.AppendLine($"<p class=\"reason\">{HtmlEncode(r.StatusReason)}</p>");

            if (r.Steps.Count > 0)
            {
                html.AppendLine("<table class=\"steps\"><thead><tr><th>#</th><th>Step</th><th>Screenshot</th><th>Notes</th></tr></thead><tbody>");
                for (int i = 0; i < r.Steps.Count; i++)
                {
                    var s = r.Steps[i];
                    string shotCell;
                    if (string.IsNullOrEmpty(s.ScreenshotPath))
                    {
                        shotCell = "—";
                    }
                    else
                    {
                        var rel = Path.GetRelativePath(outputDir, s.ScreenshotPath).Replace('\\', '/');
                        shotCell = $"<a href=\"{HtmlEncode(rel)}\" target=\"_blank\"><img src=\"{HtmlEncode(rel)}\" alt=\"{HtmlEncode(s.Label)}\"></a>";
                    }
                    html.AppendLine($"<tr><td>{i + 1}</td><td>{HtmlEncode(s.Label)}</td><td>{shotCell}</td><td>{HtmlEncode(s.Note ?? "")}</td></tr>");
                }
                html.AppendLine("</tbody></table>");
            }

            if (r.Anomalies.Count > 0)
            {
                html.AppendLine("<div class=\"anomalies\"><strong>Anomalies</strong><ul>");
                foreach (var a in r.Anomalies) html.AppendLine($"<li>{HtmlEncode(a)}</li>");
                html.AppendLine("</ul></div>");
            }
            html.AppendLine("</section>");
        }

        html.AppendLine("</body></html>");
        File.WriteAllText(path, html.ToString());
    }

    private static string HtmlEncode(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>Reset state between independent suite runs (used by the
    /// fixture's constructor so re-running the suite doesn't accumulate
    /// stale entries).</summary>
    public static void Reset()
    {
        lock (_lock) _reports.Clear();
    }
}
