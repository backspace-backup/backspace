using System;
using System.Collections.Generic;

namespace Backspace.Models;

/// <summary>
/// Persisted to config.json. Holds all backups, destinations, and app-level settings.
/// Secrets (API keys, OAuth tokens, SMTP passwords) live in a separate DPAPI-encrypted
/// file referenced by *Ref suffixes.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Reserved for future schema migrations. Currently always
    /// 1 — System.Text.Json's missing-property tolerance + the defaulted
    /// collection properties below cover backward compat for the changes
    /// we've needed so far (added fields default sanely, renamed fields
    /// migrate at-read in their model class). When a future change
    /// requires touching prior versions destructively (renamed enum
    /// values, normalisation), ConfigStore.Load should branch on this
    /// version, mutate the deserialized tree, then bump SchemaVersion
    /// at save-time.</summary>
    public int SchemaVersion { get; set; } = 1;

    public List<Destination> Destinations { get; set; } = new();

    public List<Backup> Backups { get; set; } = new();

    public AppPreferences Preferences { get; set; } = new();

    public MonitoringSettings Monitoring { get; set; } = new();

    public NotificationSettings Notifications { get; set; } = new();

    public MailSettings Mail { get; set; } = new();

    public RestoreSettings Restore { get; set; } = new();

    public LoggingSettings Logging { get; set; } = new();

    /// <summary>
    /// Per-provider OAuth app credentials for rclone. Default empty:
    /// rclone falls back to its built-in shared client_ids, which are
    /// pooled across every rclone user worldwide and routinely hit
    /// rate limits on Dropbox / Google Drive / OneDrive. A user with
    /// their own registered OAuth app on a provider gets their own
    /// quota, and dropping the (client_id, client_secret) in here
    /// makes Backspace honor it in the generated rclone.conf.
    /// </summary>
    public List<RcloneOAuthApp> RcloneOAuthApps { get; set; } = new();
}

/// <summary>
/// Per-provider OAuth app credentials for rclone. One entry per
/// <see cref="DestinationKind"/> that uses rclone (Dropbox / OneDrive
/// / Google Drive). When a destination of the matching kind goes
/// through <see cref="Services.RcloneConfigBuilder"/>, the entry's
/// client_id + (resolved-from-secrets) client_secret land on the
/// rclone.conf section for that destination, replacing the shared
/// default.
/// </summary>
public sealed class RcloneOAuthApp
{
    /// <summary>The provider this app's credentials apply to. We key by
    /// <see cref="DestinationKind"/> rather than a free-text provider
    /// name so the wiring is strongly typed end-to-end.</summary>
    public DestinationKind Provider { get; set; }

    /// <summary>OAuth client_id. Stored in plaintext config — not a
    /// secret per any provider's TOS (the client_id is the public
    /// half of the registered app pair).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>SecretsStore ref for the client_secret. Empty when the
    /// user only registered a public/installed-app client (no secret).
    /// rclone tolerates the missing secret in that case.</summary>
    public string? ClientSecretRef { get; set; }
}

public sealed class LoggingSettings
{
    /// <summary>How many days of rolling log files to retain. Default 30.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Minimum level written to the file sink. One of:
    /// Verbose, Debug, Information, Warning, Error, Fatal.
    /// Default Information in release; Verbose is forced on when the app
    /// is launched with BACKSPACE_DEBUG=1 (see launch-debug.bat).
    /// </summary>
    public string MinimumLevel { get; set; } = "Information";
}

public sealed class RestoreSettings
{
    /// <summary>
    /// Default parallelism for bulk restore and per-file retry pool. 4 is a
    /// reasonable balance for cloud destinations (which bottleneck on auth)
    /// and local ones (which can handle much more).
    /// </summary>
    public int DefaultThreads { get; set; } = 4;

    /// <summary>
    /// Retry delays applied in order to a failed file. Exhausting this list
    /// marks the file as permanently failed for this pass (the user can
    /// trigger "retry all failed" from the summary screen, which runs the
    /// full cascade again).
    /// </summary>
    public List<int> RetryDelaysSeconds { get; set; } = new() { 1, 5, 10, 30, 60 };

    /// <summary>Default for the "preserve directory structure" option in the restore dialog.</summary>
    public bool PreserveStructureByDefault { get; set; } = true;

    /// <summary>Default for the "overwrite existing files" option in the restore dialog.</summary>
    public bool OverwriteByDefault { get; set; } = true;
}

public sealed class AppPreferences
{
    /// <summary>Default: follow the OS appearance setting. Matches what
    /// every modern desktop app does — most users have already picked
    /// dark vs light at the OS level and expect apps to honor it.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    public AccentColor Accent { get; set; } = AccentColor.Ocean;

    /// <summary>If false, app starts minimized to the tray on launch.</summary>
    public bool OpenToMainWindow { get; set; } = true;

    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// When true (default), clicking the close (X) button parks the
    /// main window in the system tray instead of quitting the
    /// process. Idle CPU is essentially zero; the choice between
    /// tray-on-close and full-quit is purely about reopen speed +
    /// at-a-glance status, NOT about whether scheduled backups
    /// keep running — those are registered with the OS scheduler
    /// (Task Scheduler / launchd / systemd) and fire on time
    /// whether the app process is open, tray-parked, or fully
    /// quit. The settings copy spells this out so the user doesn't
    /// have to worry about "did I disable my backups by closing
    /// the window."
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Where the app persists its own data. Default: %APPDATA%\Backspace\.</summary>
    public string? DataRootOverride { get; set; }
}

public enum AppTheme
{
    Dark,
    Light,
    System,
}

public enum AccentColor
{
    /// <summary>Default — deep saturated blue; reads as trustworthy and calm.</summary>
    Ocean,
    /// <summary>Calm, natural, green.</summary>
    Emerald,
    /// <summary>Rich, creative, purple.</summary>
    Plum,
    /// <summary>Neutral, quiet, near-black.</summary>
    Graphite,
    /// <summary>Warm, considered, earthy.</summary>
    Terracotta,
}

public sealed class MonitoringSettings
{
    // ---- Healthchecks.io ----

    public bool HealthchecksEnabled { get; set; }

    /// <summary>User's personal Healthchecks.io API key. Stored in secrets file.</summary>
    public string? HealthchecksApiKeyRef { get; set; }

    /// <summary>Self-hosted instance URL, or null for the hosted service.</summary>
    public string HealthchecksBaseUrl { get; set; } = "https://healthchecks.io";

    /// <summary>Project ID or "" for personal project.</summary>
    public string HealthchecksProject { get; set; } = "";

    // ---- local watchdog ----

    /// <summary>
    /// Parse stdout of each run for these substrings (case-insensitive) and raise
    /// a warning immediately. Matches "ERROR", "Failed to", "access denied" by default.
    /// </summary>
    public List<string> LocalErrorPatterns { get; set; } = new()
    {
        "ERROR",
        "Failed to",
        "access is denied",
        "access denied",
    };

    /// <summary>
    /// If the last successful run is older than this, the dashboard nags the user
    /// even if no scheduled run has officially failed. Belt and braces.
    /// </summary>
    public TimeSpan StaleBackupThreshold { get; set; } = TimeSpan.FromDays(3);
}

/// <summary>
/// Per-event-class notification matrix. A backup run ends in one of
/// three user-relevant verdicts (Success / Skipped / Failure) and each
/// verdict has its own choice of channels — Windows toast, sound, and
/// email. Replaced the older flat ToastsEnabled / PlaySound / DND-
/// bypass toggles, which couldn't express "ping me on failure but
/// stay quiet on success."
///
/// <para>Failure additionally pops as a topmost sticky alert rather
/// than a tray toast: that escalation is a fixed property of the
/// severity (a failed backup is the one notification the user can't
/// afford to miss), not a separate toggle.</para>
///
/// <para>The legacy fields below are kept solely for read-time
/// migration of older config.json files — <see cref="MigrateLegacy"/>
/// folds them into the new matrix on first load and then they stop
/// being written (JSON ignores null-on-write via the store's
/// <c>WhenWritingNull</c> policy).</para>
/// </summary>
public sealed class NotificationSettings
{
    public NotificationChannels OnSuccess { get; set; } = new() { Toast = true, Sound = true };
    public NotificationChannels OnSkipped { get; set; } = new() { Toast = true, Sound = false };
    public NotificationChannels OnFailure { get; set; } = new() { Toast = true, Sound = true };

    // ---- legacy fields (read-only migration) ----

    /// <summary>Legacy global toast toggle. Folded into the per-event
    /// matrix by <see cref="MigrateLegacy"/>; nulled out after.</summary>
    public bool? ToastsEnabled { get; set; }

    /// <summary>Legacy DND-bypass-on-failure toggle. The bypass is now
    /// implicit in the Failure severity itself, so this only feeds
    /// the legacy migration (a user who explicitly turned it OFF is
    /// honored by leaving Failure.Toast=false).</summary>
    public bool? BypassDndOnFailure { get; set; }

    /// <summary>Legacy DND-bypass-on-success toggle. Migrated to
    /// Success.Toast (the practical behavior the user opted into).</summary>
    public bool? BypassDndOnSuccess { get; set; }

    /// <summary>Legacy global sound toggle. Migrated into per-event Sound.</summary>
    public bool? PlaySound { get; set; }

    /// <summary>
    /// One-shot migration from the pre-matrix fields. Called by
    /// ConfigStore.Load after deserialization. Returns true if any
    /// legacy field was present (caller persists the migrated state).
    /// </summary>
    public bool MigrateLegacy()
    {
        var hadLegacy =
            ToastsEnabled.HasValue || BypassDndOnFailure.HasValue ||
            BypassDndOnSuccess.HasValue || PlaySound.HasValue;
        if (!hadLegacy) return false;

        var toast = ToastsEnabled ?? true;
        var sound = PlaySound ?? true;
        var bypassFail = BypassDndOnFailure ?? true;
        var bypassSuccess = BypassDndOnSuccess ?? false;

        // Per-event channels reflect what the legacy switches actually
        // did on disk: toast OR bypass-DND → user wanted to see it;
        // sound applied uniformly across events.
        OnSuccess = new NotificationChannels { Toast = toast || bypassSuccess, Sound = sound };
        OnSkipped = new NotificationChannels { Toast = toast, Sound = false };
        OnFailure = new NotificationChannels { Toast = toast || bypassFail, Sound = sound };

        ToastsEnabled = null;
        BypassDndOnFailure = null;
        BypassDndOnSuccess = null;
        PlaySound = null;
        return true;
    }
}

/// <summary>
/// Three independent notification channels per event class. Each
/// channel fires if its toggle is on; otherwise the event is silent
/// on that channel.
/// </summary>
public sealed class NotificationChannels
{
    /// <summary>Windows toast / tray pop-up (or, for Failure events, the topmost sticky alert).</summary>
    public bool Toast { get; set; }

    /// <summary>Short system sound. Best-effort on non-Windows where there's no portable BCL surface.</summary>
    public bool Sound { get; set; }

    /// <summary>Send an email through the configured provider. Requires a working Mail provider — the
    /// orchestrator falls back to silent when Email=true but no provider is configured.</summary>
    public bool Email { get; set; }
}

public enum MailProvider
{
    Disabled,
    Resend,
    Mailgun,
}

/// <summary>
/// Mailgun has separate API hosts for US and EU customers. Picking the
/// wrong one returns "domain not found" — so we surface this in the UI
/// rather than silently defaulting US-only.
/// </summary>
public enum MailgunRegion
{
    US,
    EU,
}

public sealed class MailSettings
{
    /// <summary>Hard cap on simultaneous recipients. The UI enforces
    /// this, the sender enforces this, and the Mailgun-sandbox case
    /// already caps at 5 anyway. Five is also Resend's batched-send
    /// best-practice for transactional notifications — beyond that
    /// it's a mailing list, not a backup notification.</summary>
    public const int MaxRecipients = 5;

    public MailProvider Provider { get; set; } = MailProvider.Disabled;

    public string FromAddress { get; set; } = "";

    /// <summary>
    /// Legacy single-address field, kept on disk for migration from
    /// older configs. New code reads / writes <see cref="ToAddresses"/>;
    /// on load, a non-empty <c>ToAddress</c> is appended to
    /// <c>ToAddresses</c> if the list is empty (one-shot migration).
    /// </summary>
    public string ToAddress { get; set; } = "";

    /// <summary>
    /// Up to <see cref="MaxRecipients"/> destination addresses. The UI
    /// renders these as removable pills above the entry input.
    /// </summary>
    public List<string> ToAddresses { get; set; } = new();

    // ---- legacy per-event email toggles ----
    //
    // Moved into NotificationSettings.OnSuccess/OnSkipped/OnFailure.Email
    // so the user sees all three channels (toast / sound / email) lined
    // up under the same event row in Settings. Kept here as nullable
    // so older configs still deserialize cleanly; ConfigStore.Load
    // forwards the values into the new matrix on first read.

    public bool? OnSuccess { get; set; }
    public bool? OnFailure { get; set; }
    public bool? OnSkip { get; set; }

    // ---- Resend (API key only) ----

    /// <summary>Stored in secrets file.</summary>
    public string? ResendApiKeyRef { get; set; }

    // ---- Mailgun (HTTP API, NOT SMTP) ----
    //
    // Mailgun's HTTP API is the modern path: single API key, no SMTP
    // ports / credentials / TLS dance. .NET's System.Net.Mail.SmtpClient
    // is also obsolete per Microsoft. Sending is a POST to
    //   https://api.mailgun.net/v3/{domain}/messages    (US)
    //   https://api.eu.mailgun.net/v3/{domain}/messages (EU)
    // with HTTP Basic auth (username "api", password = API key).

    /// <summary>
    /// The verified sending domain on the Mailgun account, e.g.
    /// <c>mg.example.com</c>. Required: Mailgun's API URL embeds this.
    /// </summary>
    public string MailgunDomain { get; set; } = "";

    /// <summary>US (default) or EU. Picks the API host.</summary>
    public MailgunRegion MailgunRegion { get; set; } = MailgunRegion.US;

    /// <summary>Stored in secrets file.</summary>
    public string? MailgunApiKeyRef { get; set; }
}
