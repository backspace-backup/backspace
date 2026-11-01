using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Loads and saves config.json. Every writer holds a shared file lock across
/// reading the latest state, applying a mutation and atomically replacing it.
/// Independent GUI, scheduler and elevated-helper instances preserve edits.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static ILogger _log => AppLogger.For<ConfigStore>();
    private readonly object _gate = new();
    private readonly string _path = AppPaths.ConfigFile;
    private AppConfig _current = new();
    private byte[]? _lastKnownFileHash;
    /// <summary>Prevents recursive Changed broadcasts when a subscriber saves
    /// another change. The outer save is already committed before notification.</summary>
    [ThreadStatic] private static int _updateDepth;

    /// <summary>True once <see cref="Load"/> has either deserialised
    /// an existing config or persisted a fresh-defaults file. False
    /// while Load is still racing a transient IO error — Update +
    /// Save refuse to write in that state so an unread on-disk
    /// config never gets overwritten with the empty default
    /// AppConfig that was field-initialised at construction.</summary>
    private bool _loaded;

    /// <summary>True once <see cref="Load"/> has either deserialised
    /// an existing config or written fresh defaults. False if Load
    /// hit a transient IO error or a permission denial — callers
    /// in the headless run path should treat that case as an
    /// "abort + non-zero exit" rather than "no backups configured"
    /// to avoid a silent scheduled-task miss.</summary>
    public bool IsLoaded { get { lock (_gate) return _loaded; } }

    /// <summary>
    /// CAUTION: returns a live reference to the in-memory <see cref="AppConfig"/>,
    /// NOT a snapshot. Reads of immutable fields (strings, enums, scalars)
    /// are safe; reading collections is safe under the assumption that
    /// no caller mutates them outside <see cref="Update"/>. Mutating any
    /// field on the returned object directly is a bug — those changes
    /// would not be saved to disk and would race with concurrent
    /// <see cref="Update"/> calls. Always go through <see cref="Update"/>
    /// for writes.
    /// </summary>
    public AppConfig Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler? Changed;

    public void Load()
    {
        AppPaths.EnsureAll();
        bool changed = false;
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(_path);
            _loaded = false;
            if (!File.Exists(_path))
            {
                if (File.Exists(_path + ".bak"))
                {
                    _log.Error("Settings file is missing but its backup remains at {Path}. Refusing to create empty settings", _path + ".bak");
                    return;
                }
                _log.Information("No config file at {Path} — initializing defaults", _path);
                _current = new AppConfig();
                // Set _loaded BEFORE Save_NoLock so the guard inside
                // doesn't refuse the legitimate first-run write.
                _loaded = true;
                Save_NoLock();
                changed = true;
            }
            else
            {
                try
                {
                    // FileShare.ReadWrite | Delete so a concurrent atomic
                    // Save_NoLock (write-tmp + File.Replace) in another
                    // process or thread doesn't surface as a sharing
                    // violation that the catch below would misreport as
                    // "Corrupt config file" — and then overwrite it.
                    string text;
                    using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs))
                        text = sr.ReadToEnd();
                    _current = JsonSerializer.Deserialize<AppConfig>(text, Json)
                        ?? throw new JsonException("Settings file contains no configuration.");
                    _lastKnownFileHash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                    _log.Information(
                        "Loaded config: {BackupCount} backup(s), {DestCount} destination(s)",
                        _current.Backups.Count, _current.Destinations.Count);
                    _loaded = true;
                    changed = true;

                    // One-shot migration from pre-matrix Notification +
                    // Mail event toggles. Each model returns true when
                    // it consumed a legacy field; persist immediately
                    // so the next load deserializes the canonical shape.
                    var migrated = MigrateLegacyNotificationModel(_current);
                    if (migrated)
                    {
                        try { Save_NoLock(); }
                        catch (Exception ex) { _log.Warning(ex, "Persisting notification-model migration failed (will retry next save)"); }
                    }
                }
                catch (JsonException ex)
                {
                    // Never crash on a bad config — preserve it for forensics, start fresh.
                    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
                    var backup = _path + $".broken.{stamp}";
                    // Recovery may replace a corrupt file only AFTER preserving
                    // the original. A failed copy must never authorize data loss.
                    File.Copy(_path, backup, overwrite: false);
                    _log.Error(ex, "Corrupt config file — preserved at {Backup}, starting fresh", backup);
                    _current = new AppConfig();
                    // _loaded BEFORE Save so the guard doesn't block
                    // the fresh-defaults write after corruption recovery.
                    _loaded = true;
                    Save_NoLock();
                    changed = true;
                }
                catch (IOException ex)
                {
                    // Transient IO (sharing violation from a concurrent
                    // rewrite, antivirus-held file, etc.) — keep the
                    // previous in-memory state and try again next time.
                    // Crucially, do NOT overwrite the on-disk config —
                    // that would discard the user's data on a transient
                    // blip. _loaded stays false so Update + Save refuse
                    // to write until a successful Load.
                    _log.Warning(ex, "Couldn't read config file (transient IO); keeping in-memory state");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // Locked-down corp Windows ACLs, AV-quarantined
                    // config — neither warrants a startup crash.
                    // Keep in-memory defaults, log the cause, and
                    // refuse to write until the next launch (when the
                    // user has presumably fixed the permissions /
                    // restored the file from AV quarantine).
                    //
                    // PathTooLongException is NOT in this clause —
                    // it's a sibling of IOException in .NET 6+ so the
                    // earlier `catch (IOException)` handles it
                    // already. Listing it here was dead code.
                    _log.Error(ex, "Permission issue reading config file; running with defaults until next launch");
                }
            }
        }
        if (changed) RaiseChanged();
    }

    /// <summary>Refreshes externally written state on demand without polling or
    /// blocking the UI. Unchanged content emits no event. Failed reads retain the
    /// last known-good snapshot and can be retried on the next activation.</summary>
    public Task<bool> ReloadIfChangedAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        bool changed;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            using var transaction = StateFileLock.Acquire(_path);
            var bytes = File.ReadAllBytes(_path);
            var hash = SHA256.HashData(bytes);
            if (_loaded && _lastKnownFileHash is not null && hash.AsSpan().SequenceEqual(_lastKnownFileHash))
                return false;
            var latest = JsonSerializer.Deserialize<AppConfig>(bytes, Json)
                ?? throw new JsonException("Settings file contains no configuration.");
            MigrateLegacyNotificationModel(latest);
            ct.ThrowIfCancellationRequested();
            _current = latest;
            _loaded = true;
            _lastKnownFileHash = hash;
            changed = true;
        }
        if (changed) RaiseChanged();
        return changed;
    }, ct);

    /// <summary>
    /// Fold the pre-matrix notification + per-event email toggles into
    /// the new <see cref="NotificationChannels"/>-keyed model. Two
    /// migrations happen in lock-step here:
    ///
    ///   1. NotificationSettings.MigrateLegacy() consumes the flat
    ///      ToastsEnabled / PlaySound / BypassDnd-on-* fields.
    ///   2. MailSettings.OnSuccess / OnFailure / OnSkip flow into
    ///      NotificationSettings.OnSuccess|OnSkipped|OnFailure.Email,
    ///      then the legacy mail fields are nulled out so they stop
    ///      being persisted (DefaultIgnoreCondition=WhenWritingNull
    ///      drops nulls from the JSON output).
    ///
    /// Returns true if either migration touched the config — caller
    /// persists immediately so the on-disk shape converges.
    /// </summary>
    private static bool MigrateLegacyNotificationModel(AppConfig cfg)
    {
        var notif = cfg.Notifications.MigrateLegacy();

        bool mail = false;
        var m = cfg.Mail;
        if (m.OnSuccess.HasValue) { cfg.Notifications.OnSuccess.Email = m.OnSuccess.Value; m.OnSuccess = null; mail = true; }
        if (m.OnFailure.HasValue) { cfg.Notifications.OnFailure.Email = m.OnFailure.Value; m.OnFailure = null; mail = true; }
        if (m.OnSkip.HasValue)    { cfg.Notifications.OnSkipped.Email = m.OnSkip.Value;    m.OnSkip    = null; mail = true; }

        if (notif || mail)
            _log.Information("Migrated notification model to per-event channels (notif={Notif}, mail={Mail})", notif, mail);
        return notif || mail;
    }

    /// <summary>
    /// Read-modify-save under the mutex. The callback receives a mutable reference
    /// to the live config; changes are saved atomically on return.
    /// Re-entrant: a Changed-handler that calls Update again is fine; only
    /// the OUTERMOST Update raises Changed (the inner already
    /// represented the latest state, and the outer's mutator has
    /// already completed by the time the handler ran).
    /// </summary>
    public void Update(Action<AppConfig> mutate)
    {
        _updateDepth++;
        try
        {
            MutateAndSave(mutate);
            if (_updateDepth == 1) RaiseChanged();
        }
        finally { _updateDepth--; }
    }

    // There are no supported out-of-transaction mutations of Current. Save
    // therefore refreshes and persists the latest disk state, never a stale
    // instance snapshot. Writers must express changes through Update.
    public void Save()
    {
        MutateAndSave(_ => { });
        RaiseChanged();
    }

    public void UpdateQuiet(Action<AppConfig> mutate) => MutateAndSave(mutate);

    /// <summary>Commits an editor draft while retaining operation results which
    /// may have changed since the dialog opened, including scheduled-process writes.
    /// Erase/reset commands update those results explicitly before calling here.</summary>
    public void SaveBackupEdit(Backup draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var edited = JsonSerializer.Deserialize<Backup>(JsonSerializer.Serialize(draft, Json), Json)!;
        Update(config =>
        {
            var index = config.Backups.FindIndex(b => b.Id == edited.Id);
            if (index < 0) { config.Backups.Add(edited); return; }
            var latest = config.Backups[index];
            PreserveBackupOutcomes(edited, latest);
            config.Backups[index] = edited;
        });
    }

    /// <summary>Apply an administrator-approved service configuration while
    /// retaining newer operation results in the service's private state. This
    /// is internal: a desktop request cannot supply configuration bytes.</summary>
    internal void ApplyServiceApproval(AppConfig approved)
    {
        var next = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(approved, Json), Json)!;
        Update(current =>
        {
            foreach (var backup in next.Backups)
            {
                var previous = current.Backups.SingleOrDefault(b => b.Id == backup.Id);
                if (previous is not null) PreserveBackupOutcomes(backup, previous);
            }
            current.SchemaVersion = next.SchemaVersion;
            current.Backups = next.Backups;
            current.Destinations = next.Destinations;
            current.Preferences = next.Preferences;
            current.Monitoring = next.Monitoring;
            current.Notifications = next.Notifications;
            current.Mail = next.Mail;
            current.Restore = next.Restore;
            current.Logging = next.Logging;
            current.RcloneOAuthApps = next.RcloneOAuthApps;
        });
    }

    private static void PreserveBackupOutcomes(Backup edited, Backup latest)
    {
            edited.CreatedUtc = latest.CreatedUtc;
            if (!string.IsNullOrEmpty(latest.SnapshotIdSeed)) edited.SnapshotIdSeed = latest.SnapshotIdSeed;
            edited.LastRunStartUtc = latest.LastRunStartUtc;
            edited.LastRunEndUtc = latest.LastRunEndUtc;
            edited.LastRunStatus = latest.LastRunStatus;
            edited.LastRunSummary = latest.LastRunSummary;
            edited.LastSuccessUtc = latest.LastSuccessUtc;
            edited.LastNotificationFiredUtc = latest.LastNotificationFiredUtc;
            edited.LastNotifiedStatus = latest.LastNotifiedStatus;
            edited.LastVerifyUtc = latest.LastVerifyUtc;
            edited.LastVerifyPass = latest.LastVerifyPass;
            edited.LastVerifySummary = latest.LastVerifySummary;
            edited.LastVerifyWasSample = latest.LastVerifyWasSample;
            var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var sources = new HashSet<string>(edited.SourcePaths, comparison);
            edited.SourceLastSizeBytes = new Dictionary<string, long>(comparison);
            foreach (var pair in latest.SourceLastSizeBytes)
                if (sources.Contains(pair.Key)) edited.SourceLastSizeBytes[pair.Key] = pair.Value;
            // A new manual grace reset survives editing. A completed successful
            // run supersedes older resets; another newer manual reset also wins.
            if (latest.SkipNotificationGraceResetUtc is { } reset &&
                (edited.SkipNotificationGraceResetUtc is null || reset > edited.SkipNotificationGraceResetUtc))
                edited.SkipNotificationGraceResetUtc = reset;
            if (edited.SkipNotificationGraceResetUtc <= latest.LastSuccessUtc)
                edited.SkipNotificationGraceResetUtc = null;
    }

    private void MutateAndSave(Action<AppConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            if (!_loaded)
                throw new InvalidOperationException("Settings have not been loaded successfully. Reload them before saving changes.");
            using var transaction = StateFileLock.Acquire(_path);
            // Refresh INSIDE the inter-process transaction. A scheduled run
            // may have persisted status while the GUI held an older snapshot.
            // A read failure must propagate; falling back would lose its edits.
            var latest = ReadCurrentFile();
            MigrateLegacyNotificationModel(latest);
            var prior = _current;
            try
            {
                mutate(latest);
                _current = latest;
                Save_NoLock();
            }
            catch
            {
                _current = prior;
                throw;
            }
        }
    }

    private AppConfig ReadCurrentFile()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<AppConfig>(stream, Json)
            ?? throw new JsonException("Settings file contains no configuration.");
    }

    /// <summary>
    /// Fires <see cref="Changed"/> outside the gate (subscribers might
    /// call back into <see cref="Update"/>; firing inside the lock would
    /// nest reentrantly — currently safe with `lock` semantics, but
    /// fragile). Catches subscriber exceptions individually so one
    /// throwing handler doesn't starve the rest.
    /// </summary>
    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler)d).Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _log.Warning(ex, "Config.Changed subscriber threw"); }
        }
    }

    private void Save_NoLock()
    {
        // Refuse to write while the on-disk config is still unread (a
        // transient IOException or permission issue during Load left
        // _loaded=false; _current is the field-initialised empty
        // default). Persisting that empty default would silently
        // discard the user's actual config the moment any Update
        // triggers — exactly the regression the audit flagged.
        if (!_loaded)
        {
            _log.Warning("Save refused — initial Load has not completed; existing settings were left untouched");
            throw new InvalidOperationException("Settings have not been loaded successfully.");
        }
        AppPaths.EnsureAll();
        var text = JsonSerializer.Serialize(_current, Json);
        var tmp = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            // Pre-clear any stale .tmp left over from a prior crashed
            // save — File.WriteAllText overwrites fine, but AV scanners
            // sometimes hold a transient handle on a freshly-created
            // .tmp that makes the subsequent File.Replace fail with
            // sharing violation. Best-effort delete here narrows that
            // window.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

            using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(Encoding.UTF8.GetBytes(text));
                output.Flush(flushToDisk: true);
            }
            // A security scanner or sync client can briefly hold either
            // filename during an atomic replace. Retry only Windows errors
            // whose documented outcome leaves both filenames intact. Keep
            // the bounded backoff aligned with other durable-file writers;
            // the maximum wait is 1.55 seconds, and persistent locks still
            // surface as a save failure instead of risking a partial write.
            const int maxAttempts = 6;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(_path))
                        File.Replace(tmp, _path, destinationBackupFileName: _path + ".bak");
                    else
                        File.Move(tmp, _path);
                    _lastKnownFileHash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                    break;
                }
                catch (Exception ex) when (attempt < maxAttempts && FileReplacementErrors.CanRetry(ex))
                {
                    System.Threading.Thread.Sleep(50 << (attempt - 1));
                }
            }
            _log.Debug("Saved config: {BackupCount} backup(s), {DestCount} destination(s)",
                _current.Backups.Count, _current.Destinations.Count);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to save config to {Path}", _path);
            // An uncertain Windows replacement may already have renamed the
            // original. Preserve both the pending settings and the .bak.
            throw;
        }
    }
}
