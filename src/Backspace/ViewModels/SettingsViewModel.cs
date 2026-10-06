using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Backspace.Models;
using Backspace.Services;

namespace Backspace.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private static Serilog.ILogger _log => AppLogger.For<SettingsViewModel>();

    // System first so the default sits at the top of the dropdown.
    public AppTheme[] AvailableThemes { get; } = new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark };
    public AccentColor[] AvailableAccents { get; } =
        new[] { AccentColor.Ocean, AccentColor.Emerald, AccentColor.Plum, AccentColor.Graphite, AccentColor.Terracotta };
    public MailProvider[] AvailableMailProviders { get; } =
        new[] { MailProvider.Disabled, MailProvider.Resend, MailProvider.Mailgun };

    public MailgunRegion[] AvailableMailgunRegions { get; } =
        new[] { MailgunRegion.US, MailgunRegion.EU };

    // Auto-save bridge for every "regular" field — wires the
    // ObservableProperty's OnChanged into the debounced save. Theme,
    // Accent, MailProvider, MailgunDomain, LogRetentionDays,
    // LogsVerbose already do their own immediate ConfigStore.Update
    // inside their OnChanged partials (live-theme swaps need
    // immediate effect); the rest are covered here.
    partial void OnHealthchecksEnabledChanged(bool value)     => ScheduleAutoSave();
    partial void OnHealthchecksApiKeyChanged(string value)    => ScheduleAutoSave();
    partial void OnHealthchecksBaseUrlChanged(string value)   => ScheduleAutoSave();
    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        if (_suspendAutoSave) return;
        // Closing immediately after the click must see the new preference.
        // Keep other in-progress settings edits pending through the Changed event.
        ScheduleAutoSave();
        ServiceLocator.Config.Update(c =>
        {
            c.Preferences.MinimizeToTrayOnClose = value;
            if (value) c.Preferences.ShowTrayIcon = true;
        });
    }
    partial void OnMailFromChanged(string value)              { RecomputeEmailGate(); ScheduleAutoSave(); }
    // Per-event notification matrix. Each OnEmail* additionally
    // re-derives the AnyEmailEnabled aggregate that gates the nested
    // email-provider subsection's visibility.
    partial void OnSuccessToastChanged(bool value)            => ScheduleAutoSave();
    partial void OnSuccessSoundChanged(bool value)            => ScheduleAutoSave();
    partial void OnSuccessEmailChanged(bool value)            { RecomputeEmailGate(); ScheduleAutoSave(); }
    partial void OnSkippedToastChanged(bool value)            => ScheduleAutoSave();
    partial void OnSkippedSoundChanged(bool value)            => ScheduleAutoSave();
    partial void OnSkippedEmailChanged(bool value)            { RecomputeEmailGate(); ScheduleAutoSave(); }
    partial void OnFailureToastChanged(bool value)            => ScheduleAutoSave();
    partial void OnFailureSoundChanged(bool value)            => ScheduleAutoSave();
    partial void OnFailureEmailChanged(bool value)            { RecomputeEmailGate(); ScheduleAutoSave(); }
    partial void OnResendApiKeyChanged(string value)          { RecomputeEmailGate(); ScheduleAutoSave(); }
    partial void OnMailgunApiKeyChanged(string value)         { RecomputeEmailGate(); ScheduleAutoSave(); }
    partial void OnDropboxClientIdChanged(string value)       => ScheduleAutoSave();
    partial void OnDropboxClientSecretChanged(string value)   => ScheduleAutoSave();
    partial void OnOneDriveClientIdChanged(string value)      => ScheduleAutoSave();
    partial void OnOneDriveClientSecretChanged(string value)  => ScheduleAutoSave();
    partial void OnGoogleDriveClientIdChanged(string value)   => ScheduleAutoSave();
    partial void OnGoogleDriveClientSecretChanged(string value) => ScheduleAutoSave();
    partial void OnMailgunRegionChanged(MailgunRegion value)  => ScheduleAutoSave();

    // ---- appearance ----
    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private AccentColor _accent;

    partial void OnThemeChanged(AppTheme value)
    {
        ServiceLocator.Config.Update(cfg => cfg.Preferences.Theme = value);
    }

    partial void OnAccentChanged(AccentColor value)
    {
        ServiceLocator.Config.Update(cfg => cfg.Preferences.Accent = value);
    }

    // ---- custom rclone OAuth apps (per-provider client_id + secret) ----
    //
    // One pair per family: Dropbox covers AppScoped + FullAccess,
    // OneDrive covers Personal + Business, Google Drive is its own.
    // RcloneConfigBuilder.FindMatchingOAuthApp does the family
    // fall-through at write time. Blank values mean "fall through
    // to rclone's built-in shared default."
    [ObservableProperty] private string _dropboxClientId = "";
    [ObservableProperty] private string _dropboxClientSecret = "";
    [ObservableProperty] private string _oneDriveClientId = "";
    [ObservableProperty] private string _oneDriveClientSecret = "";
    [ObservableProperty] private string _googleDriveClientId = "";
    [ObservableProperty] private string _googleDriveClientSecret = "";

    // ---- window behavior ----
    /// <summary>If true, closing the X button hides the window to
    /// the tray instead of quitting. Default true — matches what
    /// Slack / Discord / Signal etc. do, and is the right default
    /// for a backup app that needs to stay running for scheduled
    /// jobs to fire.</summary>
    [ObservableProperty] private bool _minimizeToTrayOnClose;

    // ---- monitoring ----
    [ObservableProperty] private bool _healthchecksEnabled;
    [ObservableProperty] private string _healthchecksApiKey = "";
    [ObservableProperty] private string _healthchecksBaseUrl = "https://healthchecks.io";

    // ---- notifications ----
    //
    // Per-event matrix. Three event classes (Success / Skipped /
    // Failure) × three channels (Toast / Sound / Email). Each class's
    // Email toggle also opts the user into the nested provider sub-
    // section below — at least one Email=true forces the user to fill
    // out provider + key + FROM + at least one TO.
    [ObservableProperty] private bool _successToast;
    [ObservableProperty] private bool _successSound;
    [ObservableProperty] private bool _successEmail;
    [ObservableProperty] private bool _skippedToast;
    [ObservableProperty] private bool _skippedSound;
    [ObservableProperty] private bool _skippedEmail;
    [ObservableProperty] private bool _failureToast;
    [ObservableProperty] private bool _failureSound;
    [ObservableProperty] private bool _failureEmail;

    /// <summary>True when ANY event has Email enabled — gates the
    /// nested email-provider configuration block. While false the
    /// provider/key/from/to fields are hidden, mirroring the user's
    /// "I don't want emails" intent and stopping the section from
    /// asking for fields that aren't currently needed.</summary>
    public bool AnyEmailEnabled => SuccessEmail || SkippedEmail || FailureEmail;

    private void RecomputeEmailGate()
    {
        OnPropertyChanged(nameof(AnyEmailEnabled));
        OnPropertyChanged(nameof(EmailConfigComplete));
        OnPropertyChanged(nameof(EmailConfigError));
    }

    // ---- mail ----
    [ObservableProperty] private MailProvider _mailProvider;
    [ObservableProperty] private string _mailFrom = "";

    /// <summary>Text the user is typing in the "add a recipient" entry box;
    /// empties on each successful Add.</summary>
    [ObservableProperty] private string _mailToInput = "";

    /// <summary>Live list of TO addresses, rendered as pills in the UI.
    /// Capped at <see cref="MailSettings.MaxRecipients"/> by the Add
    /// command. Persisted into <see cref="MailSettings.ToAddresses"/>.</summary>
    public ObservableCollection<string> MailToList { get; } = new();
    public int MailToCount => MailToList.Count;
    public bool CanAddMoreRecipients => MailToList.Count < MailSettings.MaxRecipients;

    /// <summary>Status banner shown under the "Send test email" button —
    /// success or the specific error returned by the provider.</summary>
    [ObservableProperty] private string _testEmailStatus = "";
    [ObservableProperty] private bool _testEmailFailed;
    [ObservableProperty] private bool _testEmailBusy;

    [RelayCommand]
    private void AddRecipient()
    {
        var raw = (MailToInput ?? "").Trim();
        if (string.IsNullOrEmpty(raw)) return;
        if (MailToList.Count >= MailSettings.MaxRecipients) return;
        // Naive shape check — let the provider do the real validation.
        // We just block obvious typos like "no-at-sign" and dupes.
        if (!raw.Contains('@') || raw.Contains(' '))
        {
            TestEmailStatus = $"That doesn't look like an email address: {raw}";
            TestEmailFailed = true;
            return;
        }
        if (MailToList.Any(x => string.Equals(x, raw, StringComparison.OrdinalIgnoreCase)))
        {
            MailToInput = "";
            return;
        }
        MailToList.Add(raw);
        MailToInput = "";
        OnPropertyChanged(nameof(MailToCount));
        OnPropertyChanged(nameof(CanAddMoreRecipients));
        RecomputeEmailGate();
        TestEmailStatus = "";
    }

    [RelayCommand]
    private void RemoveRecipient(string address)
    {
        MailToList.Remove(address);
        OnPropertyChanged(nameof(MailToCount));
        OnPropertyChanged(nameof(CanAddMoreRecipients));
        RecomputeEmailGate();
    }

    [RelayCommand]
    private async Task SendTestEmail()
    {
        // Use sentinel "-test" ref names so the test never persists a
        // possibly-wrong key into the production ref that real backup
        // emails read. Earlier we wrote directly to the production refs;
        // a failed test followed by closing Settings without Save left
        // the bad key live in secrets, and the next real backup email
        // picked it up.
        const string testResendRef = "mail:resendApiKey-test";
        const string testMailgunRef = "mail:mailgunApiKey-test";

        var test = new MailSettings
        {
            Provider = MailProvider,
            FromAddress = MailFrom,
            ResendApiKeyRef = testResendRef,
            MailgunApiKeyRef = testMailgunRef,
            MailgunDomain = MailgunDomain,
            MailgunRegion = MailgunRegion,
        };

        if (!string.IsNullOrWhiteSpace(ResendApiKey))
            ServiceLocator.Secrets.Set(testResendRef, ResendApiKey);
        if (!string.IsNullOrWhiteSpace(MailgunApiKey))
            ServiceLocator.Secrets.Set(testMailgunRef, MailgunApiKey);

        var to = MailToList.ToList();
        if (to.Count == 0 && !string.IsNullOrWhiteSpace(MailToInput))
        {
            // Be forgiving: the user typed an address but didn't click
            // Add. Use it for the test anyway.
            to.Add(MailToInput.Trim());
        }

        TestEmailBusy = true;
        TestEmailStatus = "Sending…";
        TestEmailFailed = false;
        try
        {
            var (ok, detail) = await ServiceLocator.Mail.SendTestAsync(
                test, to, System.Threading.CancellationToken.None);
            // MailService formats failures as "Send failed: <reason>".
            // Inside the error callout the bold red line should show
            // just the actual reason — the surrounding Border + the
            // "fix and retry" follow-up already convey "this failed".
            // Trim the prefix so "Send failed: Resend HTTP 401: …"
            // becomes "Resend HTTP 401: …" in the bold line.
            const string failPrefix = "Send failed: ";
            TestEmailStatus = !ok && detail.StartsWith(failPrefix, StringComparison.Ordinal)
                ? detail[failPrefix.Length..]
                : detail;
            TestEmailFailed = !ok;
        }
        finally
        {
            TestEmailBusy = false;
            // Always clean up the test-only refs so they can't be read
            // by anything later (the production code paths use the
            // production refs anyway).
            // If Remove throws, the test-only API key persists in the
            // encrypted vault past the test send — log so support can
            // see the leak instead of it silently sticking around.
            try { ServiceLocator.Secrets.Remove(testResendRef); }
            catch (Exception ex) { AppLogger.For<SettingsViewModel>().Warning(ex, "Failed to remove test-only Resend secret {Ref}", testResendRef); }
            try { ServiceLocator.Secrets.Remove(testMailgunRef); }
            catch (Exception ex) { AppLogger.For<SettingsViewModel>().Warning(ex, "Failed to remove test-only Mailgun secret {Ref}", testMailgunRef); }
        }
    }

    /// <summary>True when the user has picked a real provider — drives
    /// visibility of the From/To fields and the Send-test button.
    /// Hidden when Disabled so users aren't asked for fields they
    /// can't use yet.</summary>
    public bool IsMailEnabled => MailProvider != MailProvider.Disabled;

    /// <summary>
    /// True when the user opted into Email on at least one event AND
    /// the mail provider + key + a FROM + at least one TO are all in
    /// place. Drives the "complete the required fields" callout in
    /// the nested email subsection. Used by the view to flag the
    /// missing piece while the user is filling things out.
    /// </summary>
    public bool EmailConfigComplete
    {
        get
        {
            if (!AnyEmailEnabled) return true; // not asked for any
            if (MailProvider == MailProvider.Disabled) return false;
            if (string.IsNullOrWhiteSpace(MailFrom)) return false;
            if (MailToList.Count == 0) return false;
            if (MailProvider == MailProvider.Resend && string.IsNullOrWhiteSpace(ResendApiKey)) return false;
            if (MailProvider == MailProvider.Mailgun)
            {
                if (string.IsNullOrWhiteSpace(MailgunDomain)) return false;
                if (string.IsNullOrWhiteSpace(MailgunApiKey)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Concrete next-step text shown in the email-required callout
    /// when the user has opted into Email but hasn't finished
    /// configuring the provider. Empty when EmailConfigComplete is
    /// true. Lists the SINGLE most-likely missing piece so the user
    /// has one thing to fix, not a wall of red checklist items.
    /// </summary>
    public string EmailConfigError
    {
        get
        {
            if (!AnyEmailEnabled || EmailConfigComplete) return "";
            if (MailProvider == MailProvider.Disabled)
                return "Pick a provider (Resend or Mailgun) so Backspace knows where to send from.";
            if (MailProvider == MailProvider.Resend && string.IsNullOrWhiteSpace(ResendApiKey))
                return "Paste your Resend API key so the emails can actually send.";
            if (MailProvider == MailProvider.Mailgun)
            {
                if (string.IsNullOrWhiteSpace(MailgunDomain))
                    return "Enter your verified Mailgun sending domain.";
                if (string.IsNullOrWhiteSpace(MailgunApiKey))
                    return "Paste your Mailgun private API key.";
            }
            if (string.IsNullOrWhiteSpace(MailFrom))
                return "Enter the FROM address you'd like Backspace to send from.";
            if (MailToList.Count == 0)
                return "Add at least one recipient address.";
            return "";
        }
    }

    /// <summary>True only for Resend — drives the helper text under
    /// FROM that explains the verified-domain requirement.</summary>
    public bool IsResendProvider => MailProvider == MailProvider.Resend;

    /// <summary>
    /// Hand-roll the partial: when the provider switches, raise the
    /// IsMailEnabled / IsResendProvider notifications, AND auto-fill
    /// the FROM with onboarding@resend.dev when Resend is selected
    /// and the user hasn't typed their own sender yet (it's the only
    /// FROM Resend accepts until you verify a domain).
    /// </summary>
    partial void OnMailProviderChanged(MailProvider value)
    {
        OnPropertyChanged(nameof(IsMailEnabled));
        OnPropertyChanged(nameof(IsResendProvider));
        RecomputeEmailGate();
        if (value == MailProvider.Resend && string.IsNullOrWhiteSpace(MailFrom))
            MailFrom = "onboarding@resend.dev";
    }
    [ObservableProperty] private string _resendApiKey = "";

    // Mailgun via HTTP API (NOT SMTP). One key + verified sending
    // domain + region selector. The username/password/host/port fields
    // from earlier versions are gone.
    [ObservableProperty] private string _mailgunApiKey = "";
    [ObservableProperty] private string _mailgunDomain = "";
    [ObservableProperty] private MailgunRegion _mailgunRegion = MailgunRegion.US;

    /// <summary>
    /// Recognises Mailgun's sandbox domains: <c>sandbox{hex}.mailgun.org</c>.
    /// Sandbox accounts have strict limitations (5 authorized recipients
    /// total, sender locked to the sandbox) so we auto-populate FROM and
    /// surface the constraints in the UI when detected.
    /// </summary>
    public bool IsMailgunSandbox
    {
        get
        {
            var d = Services.MailService.NormaliseMailgunDomain(MailgunDomain);
            if (string.IsNullOrEmpty(d)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                d, @"^sandbox[0-9a-f]+\.mailgun\.org$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
    }

    /// <summary>True once the user has entered any sending domain — the
    /// FROM field is hidden until then, since Mailgun's API URL embeds
    /// the domain and there's nothing useful to type before that.</summary>
    public bool IsMailgunFromVisible =>
        !string.IsNullOrWhiteSpace(MailgunDomain);

    partial void OnMailgunDomainChanged(string value)
    {
        OnPropertyChanged(nameof(IsMailgunSandbox));
        OnPropertyChanged(nameof(IsMailgunFromVisible));
        RecomputeEmailGate();
        // Auto-populate (and lock; see XAML IsReadOnly binding) the FROM
        // for sandbox domains — Mailgun rejects any other sender on a
        // sandbox account, so there's nothing for the user to choose
        // here. We use backspace@<sandbox> as a stable, recognizable
        // local-part. For custom domains we leave whatever the user
        // typed alone; the note in the UI tells them what to enter.
        if (IsMailgunSandbox)
        {
            var d = Services.MailService.NormaliseMailgunDomain(value);
            MailFrom = $"backspace@{d}";
        }
    }

    // ---- logging ----
    [ObservableProperty] private int _logRetentionDays;
    [ObservableProperty] private bool _logsVerbose;

    partial void OnLogRetentionDaysChanged(int value)
    {
        if (value < 1) value = 1;
        ServiceLocator.Config.Update(cfg => cfg.Logging.RetentionDays = value);
    }

    partial void OnLogsVerboseChanged(bool value)
    {
        ServiceLocator.Config.Update(cfg => cfg.Logging.MinimumLevel = value ? "Verbose" : "Information");
    }

    [RelayCommand]
    private void OpenAppLogFolder()
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(AppPaths.AppLogsDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.For<SettingsViewModel>().Warning(ex, "Open app log folder failed");
        }
    }

    [RelayCommand]
    private void OpenEngineLogFolder()
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(AppPaths.EngineLogsRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.For<SettingsViewModel>().Warning(ex, "Open engine log folder failed");
        }
    }

    /// <summary>Auto-save debouncer. Every settings field's OnChanged
    /// partial schedules this; the actual Save runs once 800 ms after
    /// the last edit so a user racing through five fields gets ONE
    /// disk write, not five. Theme + Accent + a few others already
    /// save eagerly inside their OnChanged (they need immediate effect
    /// for the live theme swap); the debounced auto-save covers the
    /// long tail. The visible Save button still works for users who
    /// want immediate persistence.</summary>
    private readonly UiThreadDebouncer _autoSaveDebouncer;
    /// <summary>Set by Load() to suppress auto-save while the VM is
    /// initialising properties from config — without this, the very
    /// first hydration would schedule a redundant save round-trip.</summary>
    private bool _suspendAutoSave;

    public SettingsViewModel()
    {
        // Auto-save runs the persist logic SILENTLY — without flipping
        // SaveStatus to "Saved." each time. Otherwise typing into a
        // text field with 800ms debounce would re-render "Saved."
        // every time the user paused, which read as "the app is
        // doing something I should care about" rather than the
        // intended invisible-by-default behaviour. The manual Save
        // button (RelayCommand below) explicitly opts into the
        // visible status line because that click is a user gesture
        // they want feedback on.
        _autoSaveDebouncer = new UiThreadDebouncer(() => SaveCore(silent: true), debounceMs: 800);
        Load();
        // Marshal to UI thread — Config.Changed fires synchronously on
        // whatever thread mutated config; setting [ObservableProperty]
        // values from a background thread would push UI updates from
        // the wrong thread. Named handler so Dispose can unsubscribe;
        // a captured-lambda subscription would be unremovable and
        // would root this VM for the rest of the process.
        ServiceLocator.Config.Changed += OnConfigChanged;
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        // Skip Load when the user has unsaved typing in flight — a
        // Load mid-debounce would re-assign every property from the
        // pre-save config snapshot, wiping whatever the user just
        // typed. The outstanding save will run shortly and produce a
        // fresh Changed event the user's input has already informed.
        if (_autoSaveDebouncer.HasPendingFlush) return;
        OnUiThread(Load);
    }

    public void Dispose()
    {
        ServiceLocator.Config.Changed -= OnConfigChanged;
        _autoSaveDebouncer.Dispose();
    }

    /// <summary>Hooked into every settings field's OnChanged partial.
    /// Skipped during Load() so initial hydration doesn't trigger a
    /// redundant save round-trip.</summary>
    private void ScheduleAutoSave()
    {
        if (_suspendAutoSave) return;
        _autoSaveDebouncer.Schedule();
    }

    private void Load()
    {
        // Suppress auto-save during hydration: assigning Theme = X
        // would otherwise trigger OnThemeChanged → ScheduleAutoSave even
        // though we just READ the value from disk.
        _suspendAutoSave = true;
        try
        {
        var cfg = ServiceLocator.Config.Current;
        Theme = cfg.Preferences.Theme;
        Accent = cfg.Preferences.Accent;
        MinimizeToTrayOnClose = cfg.Preferences.MinimizeToTrayOnClose;
        LogRetentionDays = Math.Max(1, cfg.Logging.RetentionDays);
        LogsVerbose = string.Equals(cfg.Logging.MinimumLevel, "Verbose", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(cfg.Logging.MinimumLevel, "Debug",   StringComparison.OrdinalIgnoreCase);

        HealthchecksEnabled = cfg.Monitoring.HealthchecksEnabled;
        HealthchecksBaseUrl = cfg.Monitoring.HealthchecksBaseUrl;
        HealthchecksApiKey = cfg.Monitoring.HealthchecksApiKeyRef is { } r && ServiceLocator.Secrets.TryGet(r, out var k) ? k : "";

        SuccessToast  = cfg.Notifications.OnSuccess.Toast;
        SuccessSound  = cfg.Notifications.OnSuccess.Sound;
        SuccessEmail  = cfg.Notifications.OnSuccess.Email;
        SkippedToast  = cfg.Notifications.OnSkipped.Toast;
        SkippedSound  = cfg.Notifications.OnSkipped.Sound;
        SkippedEmail  = cfg.Notifications.OnSkipped.Email;
        FailureToast  = cfg.Notifications.OnFailure.Toast;
        FailureSound  = cfg.Notifications.OnFailure.Sound;
        FailureEmail  = cfg.Notifications.OnFailure.Email;
        RecomputeEmailGate();

        MailProvider = cfg.Mail.Provider;
        MailFrom = cfg.Mail.FromAddress;

        // Populate the recipient pills. Prefer the new ToAddresses
        // list; for older configs that only have the legacy
        // ToAddress single field, migrate it forward into the list.
        MailToList.Clear();
        var loaded = cfg.Mail.ToAddresses ?? new System.Collections.Generic.List<string>();
        foreach (var addr in loaded.Where(a => !string.IsNullOrWhiteSpace(a))
                                   .Take(MailSettings.MaxRecipients))
            MailToList.Add(addr);
        if (MailToList.Count == 0 && !string.IsNullOrWhiteSpace(cfg.Mail.ToAddress))
            MailToList.Add(cfg.Mail.ToAddress);
        OnPropertyChanged(nameof(MailToCount));
        OnPropertyChanged(nameof(CanAddMoreRecipients));

        if (cfg.Mail.ResendApiKeyRef is { } rr && ServiceLocator.Secrets.TryGet(rr, out var rk)) ResendApiKey = rk;
        MailgunDomain = cfg.Mail.MailgunDomain;
        MailgunRegion = cfg.Mail.MailgunRegion;
        if (cfg.Mail.MailgunApiKeyRef is { } mak && ServiceLocator.Secrets.TryGet(mak, out var mk)) MailgunApiKey = mk;

        // Hydrate the OAuth client_id pairs from the saved
        // RcloneOAuthApps list. Each provider family has at most one
        // entry; missing entries are treated as "use rclone's
        // built-in default".
        HydrateOAuthApp(cfg, DestinationKind.DropboxAppScoped,
            cid => DropboxClientId = cid, sec => DropboxClientSecret = sec);
        HydrateOAuthApp(cfg, DestinationKind.OneDrivePersonal,
            cid => OneDriveClientId = cid, sec => OneDriveClientSecret = sec);
        HydrateOAuthApp(cfg, DestinationKind.GoogleDrive,
            cid => GoogleDriveClientId = cid, sec => GoogleDriveClientSecret = sec);
        }
        finally { _suspendAutoSave = false; }
    }

    private static void HydrateOAuthApp(
        AppConfig cfg, DestinationKind family,
        Action<string> setClientId, Action<string> setClientSecret)
    {
        var entry = cfg.RcloneOAuthApps.FirstOrDefault(a => a.Provider == family);
        if (entry is null) return;
        setClientId(entry.ClientId);
        if (entry.ClientSecretRef is { } r && ServiceLocator.Secrets.TryGet(r, out var s))
            setClientSecret(s);
    }

    /// <summary>Reconcile the RcloneOAuthApps list with the current
    /// (clientId, clientSecret) pair for a provider family. Empty
    /// client_id means "remove any entry for this family"; non-empty
    /// upserts the entry and writes the secret into the store.</summary>
    private static void PersistOAuthApp(
        AppConfig cfg, SecretsStore secrets, DestinationKind family,
        string clientId, string clientSecret, string secretKey)
    {
        var existing = cfg.RcloneOAuthApps.FirstOrDefault(a => a.Provider == family);

        if (string.IsNullOrWhiteSpace(clientId))
        {
            // User cleared the field: drop the entry so the config
            // doesn't carry a dead {provider, ClientId=""} stub that
            // RcloneConfigBuilder's "Where ClientId not empty" filter
            // would skip anyway. Don't touch the secret -- secrets are
            // append-only, removing a single value would shake other
            // refs.
            if (existing is not null) cfg.RcloneOAuthApps.Remove(existing);
            return;
        }

        if (existing is null)
        {
            existing = new RcloneOAuthApp { Provider = family };
            cfg.RcloneOAuthApps.Add(existing);
        }
        existing.ClientId = clientId.Trim();
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            existing.ClientSecretRef ??= secretKey;
            secrets.Set(existing.ClientSecretRef, clientSecret);
        }
        else
        {
            // Blank secret means "public client, no secret". Clear the
            // ref so RcloneConfigBuilder doesn't emit a stale value.
            existing.ClientSecretRef = null;
        }
    }

    /// <summary>One-shot status line surfaced after a Save click. Empty
    /// while idle, "Saved." on success, or a short error description if
    /// the save threw. The view binds this to a TextBlock so the user
    /// has visible confirmation that their click did something.</summary>
    [ObservableProperty] private string _saveStatus = "";

    [RelayCommand]
    private void Save() => SaveCore(silent: false);

    private void SaveCore(bool silent)
    {
        try
        {
            var secrets = ServiceLocator.Secrets;
            ServiceLocator.Config.Update(cfg =>
            {
                cfg.Preferences.Theme = Theme;
                cfg.Preferences.MinimizeToTrayOnClose = MinimizeToTrayOnClose;

                cfg.Monitoring.HealthchecksEnabled = HealthchecksEnabled;
                cfg.Monitoring.HealthchecksBaseUrl = HealthchecksBaseUrl;
                if (!string.IsNullOrWhiteSpace(HealthchecksApiKey))
                {
                    cfg.Monitoring.HealthchecksApiKeyRef ??= "monitoring:hcApiKey";
                    secrets.Set(cfg.Monitoring.HealthchecksApiKeyRef, HealthchecksApiKey);
                }

                cfg.Notifications.OnSuccess.Toast = SuccessToast;
                cfg.Notifications.OnSuccess.Sound = SuccessSound;
                cfg.Notifications.OnSuccess.Email = SuccessEmail;
                cfg.Notifications.OnSkipped.Toast = SkippedToast;
                cfg.Notifications.OnSkipped.Sound = SkippedSound;
                cfg.Notifications.OnSkipped.Email = SkippedEmail;
                cfg.Notifications.OnFailure.Toast = FailureToast;
                cfg.Notifications.OnFailure.Sound = FailureSound;
                cfg.Notifications.OnFailure.Email = FailureEmail;

                cfg.Mail.Provider = MailProvider;
                cfg.Mail.FromAddress = MailFrom;
                // Multi-recipient: persist the pill list. Keep the legacy
                // ToAddress in sync (first pill) so older code paths that
                // still read it don't break, but new code reads ToAddresses.
                cfg.Mail.ToAddresses = MailToList.ToList();
                cfg.Mail.ToAddress = MailToList.FirstOrDefault() ?? "";
                // Legacy mail-event toggles are migrated forward into
                // NotificationSettings.OnXxx.Email at load time and
                // never written back — null them so the WhenWritingNull
                // policy strips them from the JSON output.
                cfg.Mail.OnSuccess = null;
                cfg.Mail.OnFailure = null;
                cfg.Mail.OnSkip = null;

                if (!string.IsNullOrWhiteSpace(ResendApiKey))
                {
                    cfg.Mail.ResendApiKeyRef ??= "mail:resendApiKey";
                    secrets.Set(cfg.Mail.ResendApiKeyRef, ResendApiKey);
                }
                cfg.Mail.MailgunDomain = MailgunDomain.Trim();
                cfg.Mail.MailgunRegion = MailgunRegion;
                if (!string.IsNullOrWhiteSpace(MailgunApiKey))
                {
                    cfg.Mail.MailgunApiKeyRef ??= "mail:mailgunApiKey";
                    secrets.Set(cfg.Mail.MailgunApiKeyRef, MailgunApiKey);
                }

                PersistOAuthApp(cfg, secrets, DestinationKind.DropboxAppScoped,
                    DropboxClientId, DropboxClientSecret, "rclone:dropbox:client_secret");
                PersistOAuthApp(cfg, secrets, DestinationKind.OneDrivePersonal,
                    OneDriveClientId, OneDriveClientSecret, "rclone:onedrive:client_secret");
                PersistOAuthApp(cfg, secrets, DestinationKind.GoogleDrive,
                    GoogleDriveClientId, GoogleDriveClientSecret, "rclone:googledrive:client_secret");
            });

            // Log a redacted summary so we have a record of what changed
            // without ever writing secret values. Booleans + provider names
            // + presence-of-key are all safe.
            _log.Information(
                "Settings saved: theme={Theme} accent={Accent} healthchecks(enabled={HcEnabled}, hasKey={HasHcKey}, baseUrl={HcUrl}) " +
                "notifications(success=[t={ST},s={SS},e={SE}] skipped=[t={KT},s={KS},e={KE}] failure=[t={FT},s={FS},e={FE}]) " +
                "mail(provider={MailProvider}, from={From}, recipients={ToCount}, " +
                "hasResendKey={HasResendKey}, hasMailgunKey={HasMailgunKey}, mailgunDomain={MgDomain}, mailgunRegion={MgRegion})",
                Theme, Accent, HealthchecksEnabled, !string.IsNullOrWhiteSpace(HealthchecksApiKey), HealthchecksBaseUrl,
                SuccessToast, SuccessSound, SuccessEmail,
                SkippedToast, SkippedSound, SkippedEmail,
                FailureToast, FailureSound, FailureEmail,
                MailProvider, MailFrom, MailToCount,
                !string.IsNullOrWhiteSpace(ResendApiKey), !string.IsNullOrWhiteSpace(MailgunApiKey),
                MailgunDomain, MailgunRegion);
            // Only flip SaveStatus on a manual click — auto-save is
            // invisible by design. A failure ALWAYS surfaces because
            // silent failure is worse than a bit of UI noise.
            // For silent saves we ALSO clear any previous status —
            // otherwise a stale "Save failed: …" from an earlier
            // manual click would persist forever even after the next
            // auto-save fixed the underlying issue.
            if (!silent) SaveStatus = "Saved.";
            else if (!string.IsNullOrEmpty(SaveStatus)) SaveStatus = "";
        }
        catch (Exception ex)
        {
            // Persistence can throw on disk-full, file-locked, or
            // DPAPI-protect failures (e.g., user profile being torn
            // down). Surface the error to the user instead of letting
            // the click look like a no-op. Memory state may be partly
            // updated; we deliberately don't try to roll back since
            // ConfigStore.Update has already either committed or
            // failed atomically per its own contract.
            _log.Error(ex, "Settings save failed");
            SaveStatus = "Save failed: " + ex.Message;
        }
    }

    // ---- legacy import ----

    /// <summary>
    /// Status / result line for the legacy-migration row. One-shot text,
    /// set after the user clicks import. Not persisted.
    /// </summary>
    [ObservableProperty] private string _migrationStatus = "";

    /// <summary>
    /// One-click import from an existing Duplicacy setup. Same code path as
    /// <c>--migrate</c> on the CLI. Two layouts supported: the picked folder
    /// can either be a single Duplicacy repository (contains <c>.duplicacy/</c>)
    /// or a parent folder that contains many such repos (the duplicacy-util
    /// convention).
    /// </summary>
    [RelayCommand]
    private async Task ImportLegacy()
    {
        var window = MainWindowOrNull();
        if (window is null)
        {
            MigrationStatus = "Couldn't open a folder picker; run Backspace.exe --migrate <path> from a terminal instead.";
            return;
        }

        var picked = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick a Duplicacy repository (contains .duplicacy) or a parent folder containing several",
            AllowMultiple = false,
        });
        if (picked.Count == 0) { MigrationStatus = ""; return; }

        var path = picked[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            MigrationStatus = "That folder isn't a local path. Pick a folder on this PC.";
            return;
        }

        var report = ServiceLocator.Migrator.MigrateFrom(path);
        if (report.Skipped)
        {
            MigrationStatus = $"Skipped: {report.Reason}";
            return;
        }

        // Surface BOTH the success count AND the partial-failure detail.
        // Earlier this only reported the success count, so a 1-of-3
        // import looked identical to a 1-of-1 — the user had no idea
        // anything went wrong unless they happened to grep the app log.
        var sb = new System.Text.StringBuilder();
        if (report.ImportedBackups.Count > 0)
            sb.Append($"Imported {report.ImportedBackups.Count} backup(s) from {path}.");
        else
            sb.Append($"No Duplicacy repositories found under {path}.");

        if (report.Errors.Count > 0)
        {
            sb.Append($" {report.Errors.Count} import warning(s):");
            // Cap inline error display to the first three so the status
            // line stays readable; the full set lands in the app log.
            foreach (var err in report.Errors.Take(3))
                sb.Append($" • {err}");
            if (report.Errors.Count > 3)
                sb.Append($" (and {report.Errors.Count - 3} more; see Activity & logs)");
        }
        MigrationStatus = sb.ToString();
    }

    private static Avalonia.Controls.Window? MainWindowOrNull() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l
            ? l.MainWindow
            : null;

    // External URLs (verified by HTTP probe 2026-04-27):
    //   • https://resend.com/signup        — direct signup form (HTTP 200)
    //   • https://healthchecks.io/         — Healthchecks signs people up
    //     via a modal on the homepage, hooked to the "Sign Up" button
    //     (href="#"). Both /signup/ and /accounts/signup/ return 405,
    //     so the homepage is the only sane destination.

    [RelayCommand]
    private void OpenResendSignup() => SafeOpen("https://resend.com/signup");

    [RelayCommand]
    private void OpenHealthchecksSignup() => SafeOpen("https://healthchecks.io/");

    private static void SafeOpen(string url)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.For<SettingsViewModel>().Warning(ex, "Open URL failed: {Url}", url);
        }
    }
}
