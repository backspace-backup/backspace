using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Loads, saves, and watches config.json. Writes are atomic (write tmp, replace).
/// A single instance is shared app-wide; readers get a stable snapshot, writers
/// hold a mutex across load-modify-save.
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
    private AppConfig _current = new();
    /// <summary>
    /// Per-thread depth counter for re-entrant <see cref="Update"/>
    /// calls. A handler subscribed to <see cref="Changed"/> that calls
    /// <see cref="Update"/> again would otherwise fire two Save_NoLock
    /// rounds and two RaiseChanged events for one logical mutation —
    /// subscribers see the same state twice in close succession, and
    /// any in-between observer can read a still-saving file. We
    /// suppress the OUTER RaiseChanged when an inner Update has
    /// already raised one for the latest state; the inner save+raise
    /// is sufficient because it ran AFTER all the outer mutator's
    /// changes (the outer was already past its mutate(_current)
    /// statement when the handler-triggered inner Update started).
    /// </summary>
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
            if (!File.Exists(AppPaths.ConfigFile))
            {
                _log.Information("No config file at {Path} — initializing defaults", AppPaths.ConfigFile);
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
                    using (var fs = new FileStream(AppPaths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs))
                        text = sr.ReadToEnd();
                    _current = JsonSerializer.Deserialize<AppConfig>(text, Json) ?? new AppConfig();
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
                    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    var backup = AppPaths.ConfigFile + $".broken.{stamp}";
                    try { File.Copy(AppPaths.ConfigFile, backup, overwrite: true); } catch { }
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
            lock (_gate)
            {
                // Snapshot BEFORE mutate so a Save_NoLock throw can roll
                // back the in-memory mutation. Without this, a failed
                // save left _current diverged from the on-disk file: the
                // user saw "save failed" but the in-memory state was
                // still mutated, and the next Update would then persist
                // that change atomically with whatever it was doing —
                // silent persistence of a change that surfaced as
                // "failed" to the caller.
                //
                // EVERY frame takes its own snapshot AND rolls back its
                // own mutation if its Save throws — regardless of
                // depth. The two earlier attempts at depth-conditional
                // rollback both produced silent in-memory-vs-disk
                // divergence:
                //
                //  • Round-3 ("snapshot only when depth==1"): a
                //    handler-triggered inner Update whose Save threw
                //    had no snapshot and no rollback; the inner's
                //    mutation stuck in _current while disk was
                //    untouched by the failed inner save.
                //  • Round-4 ("snapshot every frame, restore only on
                //    outermost catch"): only fixed the case where the
                //    inner is nested INSIDE the outer's try block. The
                //    common case — inner Update fired from a Changed
                //    handler running INSIDE RaiseChanged, which is
                //    OUTSIDE the outer's try block — has no outer
                //    catch to perform the rewind. Same divergence as
                //    round-3.
                //
                // The correct semantic: each Update is atomic w.r.t.
                // its own (mutate + Save) pair. If Save throws, that
                // frame's mutation is unwound to its own pre-mutate
                // snapshot; the throw then propagates so the caller
                // sees failure. An outer's success is committed both
                // in-memory AND on disk before the outer exits its try
                // block; a later inner failure can only roll back its
                // own changes, leaving the outer's successful state
                // intact. This matches the atomic-write-tmp + Replace
                // semantics already at the file level.
                string snapshot = JsonSerializer.Serialize(_current, Json);
                mutate(_current);
                try
                {
                    Save_NoLock();
                }
                catch
                {
                    var rolled = JsonSerializer.Deserialize<AppConfig>(snapshot, Json);
                    if (rolled is not null) _current = rolled;
                    throw;
                }
            }
            // Only the outermost Update fires Changed. Inner re-entrant
            // calls (handler → Update → handler again) have already
            // saved their state under the same lock; firing twice would
            // double-broadcast.
            if (_updateDepth == 1) RaiseChanged();
        }
        finally
        {
            _updateDepth--;
        }
    }

    public void Save()
    {
        lock (_gate) Save_NoLock();
        RaiseChanged();
    }

    /// <summary>
    /// Mutate-and-save WITHOUT raising <see cref="Changed"/>. Use ONLY
    /// for state updates that have a more direct UI signalling path
    /// (e.g. orchestrator's per-run status updates already broadcast
    /// via <see cref="BackupOrchestrator.RunEnded"/>; firing Changed
    /// too would trigger every subscribed VM's Refresh redundantly,
    /// which the debouncer mitigates but doesn't eliminate).
    /// Save still runs atomically under the mutex; the only thing
    /// suppressed is the broadcast.
    /// </summary>
    public void UpdateQuiet(Action<AppConfig> mutate)
    {
        lock (_gate)
        {
            mutate(_current);
            Save_NoLock();
        }
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
            _log.Warning("Save_NoLock skipped — initial Load hasn't completed (transient IO or permission denied at startup); refusing to persist empty defaults over the real on-disk config");
            return;
        }
        AppPaths.EnsureAll();
        var text = JsonSerializer.Serialize(_current, Json);
        var tmp = AppPaths.ConfigFile + ".tmp";
        try
        {
            // Pre-clear any stale .tmp left over from a prior crashed
            // save — File.WriteAllText overwrites fine, but AV scanners
            // sometimes hold a transient handle on a freshly-created
            // .tmp that makes the subsequent File.Replace fail with
            // sharing violation. Best-effort delete here narrows that
            // window.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

            File.WriteAllText(tmp, text);
            // Replace/Move with a tiny retry loop. Real-world cause:
            // anti-virus on-access scanning briefly opens the .tmp file
            // we just wrote, returning IOException(SharingViolation) on
            // the Replace call. A single 50ms retry past the AV's read
            // window resolves it without bothering the user with a
            // failed-save toast.
            const int maxAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(AppPaths.ConfigFile))
                        File.Replace(tmp, AppPaths.ConfigFile, destinationBackupFileName: null);
                    else
                        File.Move(tmp, AppPaths.ConfigFile);
                    break;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    System.Threading.Thread.Sleep(50 * attempt);
                }
            }
            _log.Debug("Saved config: {BackupCount} backup(s), {DestCount} destination(s)",
                _current.Backups.Count, _current.Destinations.Count);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to save config to {Path}", AppPaths.ConfigFile);
            // Best-effort cleanup of the orphaned .tmp so the next save
            // attempt isn't blocked by it. We've already failed; no
            // point making it worse by leaving litter.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
