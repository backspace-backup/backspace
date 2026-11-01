using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Backspace.Services;

namespace Backspace.ViewModels;

/// <summary>
/// Hosts the four navigation-target view-models. <see cref="Backups"/>
/// is the landing page so it's eagerly constructed; the rest are
/// lazy-initialised on first navigation. Lazy-init is meaningful for
/// LogsViewModel especially — it subscribes to
/// <see cref="IBackupEngine.LineWritten"/> on construction and starts
/// buffering every stdout byte from any in-flight backup, even when
/// the user is sitting on the Backups tab and never opens the Logs
/// view. For multi-GB backups that's MBs of throwaway buffer growth.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private NavItem _selectedNav = NavItem.Backups;
    [ObservableProperty] private ViewModelBase? _currentPage;

    /// <summary>True when at least one restore is currently in flight.
    /// Drives the conditional visibility of the "Restores" left-nav
    /// item and its animated download-tray icon. Today's single-VM
    /// architecture supports one restore at a time, so this mirrors
    /// the RestoreViewModel's IsRunning state directly. Multi-
    /// restore evolution (parallel restores, per-card view) is
    /// ROADMAP item 8's follow-up.</summary>
    [ObservableProperty] private bool _hasActiveRestores;
    [ObservableProperty] private bool _hasRunningRestores;
    [ObservableProperty] private bool _hasLimitedProtection;

    private void OnProtectionChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) RefreshProtection();
        });

    private void RefreshProtection() => HasLimitedProtection = OperatingSystem.IsWindows()
        && ServiceLocator.Config.Current.Backups.Any(b => !b.UseVss);

    public BackupsViewModel Backups { get; }

    /// <summary>One administrator-access state for the whole window: the
    /// Backups warning, the Settings card and the sidebar badge read it.</summary>
    public BackgroundBackupViewModel AdministratorAccess { get; } = new();

    // Lazy-init: constructed on first access. Accessing these getters
    // is thread-safe via LazyInitializer.EnsureInitialized — without
    // it, two concurrent accesses (e.g. nav from a hotkey while a
    // Config.Changed handler also touches the property) could each
    // create a fresh VM, with the loser silently discarded after
    // having already subscribed to ServiceLocator events. The
    // discarded VM would leak its handlers for the rest of the
    // process lifetime.
    private DestinationsViewModel? _destinations;
    private LogsViewModel? _logs;
    private RestoreViewModel? _restore;
    private SettingsViewModel? _settings;
    private ActiveRestoresViewModel? _activeRestores;
    private readonly System.Collections.Generic.HashSet<RestoreViewModel> _sessionPages = new();
    private readonly CancellationTokenSource _refreshLifetime = new();
    private Task? _externalRefreshTask;
    private bool _disposed;

    public DestinationsViewModel Destinations =>
        System.Threading.LazyInitializer.EnsureInitialized(ref _destinations, () => new DestinationsViewModel());
    public LogsViewModel Logs =>
        System.Threading.LazyInitializer.EnsureInitialized(ref _logs, () => new LogsViewModel());
    public RestoreViewModel Restore =>
        System.Threading.LazyInitializer.EnsureInitialized(ref _restore, () => new RestoreViewModel());
    public SettingsViewModel Settings =>
        System.Threading.LazyInitializer.EnsureInitialized(ref _settings, () => new SettingsViewModel(null, AdministratorAccess));
    public ActiveRestoresViewModel ActiveRestores =>
        System.Threading.LazyInitializer.EnsureInitialized(ref _activeRestores, () => new ActiveRestoresViewModel());

    public MainWindowViewModel()
    {
        // Backups is the landing page so it's worth the up-front cost —
        // anything else has to wait for first nav.
        Backups = new BackupsViewModel(AdministratorAccess);
        CurrentPage = Backups;

        // Eager-init LogsViewModel so it subscribes to
        // Orchestrator.RunStarted / Runner.LineWritten /
        // AppNavigator.LiveActivityStarted from app startup. The
        // user reported live logs not appearing for a fast-fail
        // backup run kicked off from the Backups page — root cause
        // was that LogsViewModel was lazily created on first nav to
        // the Logs tab, so any run that started before that nav
        // happened was missed by every event handler. Discarding
        // the result is fine; the property setter caches it via
        // LazyInitializer.
        _ = Logs;

        AppNavigator.Requested += OnNavRequested;

        // Active-restore indicator: bound to the registry's session
        // count rather than the Restore VM's IsRunning. The registry
        // is the source of truth for "is anything restoring right
        // now" because (a) it survives multi-restore parallelism
        // and (b) the user-facing "Restores" cards view reads from
        // the same collection.
        ServiceLocator.ActiveRestores.Changed += OnActiveRestoresChanged;
        ServiceLocator.ActiveRestores.SessionOpened += OnRestoreSessionOpened;
        HasActiveRestores = ServiceLocator.ActiveRestores.HasAny;
        HasRunningRestores = ServiceLocator.ActiveRestores.HasRunning;
        ServiceLocator.Config.Changed += OnProtectionChanged;
        RefreshProtection();
    }

    private void OnActiveRestoresChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            HasActiveRestores = ServiceLocator.ActiveRestores.HasAny;
            HasRunningRestores = ServiceLocator.ActiveRestores.HasRunning;
        });
    }

    private void OnNavRequested(NavItem target)
    {
        if (target == NavItem.Restore && _restore?.IsStepProgressOrSummary == true)
        {
            _sessionPages.Add(_restore);
            _restore = new RestoreViewModel();
        }
        var alreadySelected = SelectedNav == target;
        SelectedNav = target;
        if (target == NavItem.Restore) CurrentPage = Restore;
        if (target == NavItem.ActiveRestores)
        {
            ActiveRestores.SelectedSession = null;
            CurrentPage = ActiveRestores;
        }
        if (alreadySelected && target is NavItem.Backups or NavItem.Logs)
            _ = RefreshExternalStateAsync();
    }

    private void OnRestoreSessionOpened(ActiveRestoreSession session)
    {
        SelectedNav = NavItem.ActiveRestores;
        if (session.Owner is RestoreViewModel page && !page.MultiSourceMode)
        {
            _sessionPages.Add(page);
            CurrentPage = page;
        }
        else
        {
            ActiveRestores.SelectedSession = session;
            CurrentPage = ActiveRestores;
        }
    }

    partial void OnSelectedNavChanged(NavItem value)
    {
        CurrentPage = value switch
        {
            NavItem.Backups        => Backups,
            NavItem.Destinations   => Destinations,
            NavItem.Logs           => Logs,
            NavItem.Restore        => Restore,
            NavItem.Settings       => Settings,
            NavItem.ActiveRestores => ActiveRestores,
            _ => Backups,
        };
        if (value is NavItem.Backups or NavItem.Logs)
            _ = RefreshExternalStateAsync();

        // Restore is a step-by-step wizard, not a stateful workspace.
        // A fresh nav back to it should restart from step 1 — otherwise
        // a user who half-finished a restore yesterday lands back in
        // the middle of the prior flow with stale Selected* values
        // (and that prior flow may have referenced a backup or
        // destination they've since deleted, leaving the view in a
        // broken state). The PreselectRestoreBackup signal — which
        // fires AFTER nav — re-applies whichever backup the BackupCard
        // was clicked from, so this reset doesn't conflict with the
        // "Restore from this card" path.
        //
        // Exception: an actively-running restore must NOT be reset on
        // nav-back. The user reported "if I start a restore and click
        // Backups, I lose access to the progress view forever." The
        // ROADMAP "Restores left-nav" entry below sketches the broader
        // active-restores cards feature; this guard is the minimal
        // fix that solves the most painful symptom — clicking back to
        // Restore mid-flight returns the user to the live progress
        // view instead of resetting the wizard.
        if (value == NavItem.Restore && _restore is not null && !_restore.IsRunning)
        {
            try { _restore.RestartWizard(); }
            catch { /* defensive — never block nav */ }
        }
    }

    /// <summary>
    /// Read externally scheduled results when the user returns to the app or
    /// visits Backups/Logs. The disk fingerprint runs off the UI thread; there
    /// is no watcher or idle polling, and overlapping requests share one read.
    /// Editors keep their own drafts; only the persisted snapshot is replaced.
    /// </summary>
    public Task RefreshExternalStateAsync()
    {
        if (_disposed) return Task.CompletedTask;
        // A request during a read runs once more after it: the read in flight
        // may already have refreshed the page the person has just left.
        if (_externalRefreshTask is { IsCompleted: false }) { _refreshAgain = true; return _externalRefreshTask; }
        return _externalRefreshTask = RefreshExternalStateCoreAsync();
    }

    private bool _refreshAgain;

    private async Task RefreshExternalStateCoreAsync()
    {
        do { _refreshAgain = false; await RefreshExternalStateOnceAsync().ConfigureAwait(false); }
        while (_refreshAgain && !_disposed);
    }

    private async Task RefreshExternalStateOnceAsync()
    {
        try
        {
            await ServiceLocator.Config.ReloadIfChangedAsync(_refreshLifetime.Token).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                if (SelectedNav == NavItem.Backups) Backups.Refresh();
                // History may change without config changing, and the Logs VM
                // survives navigation. Always reload its selected timeline.
                if (SelectedNav == NavItem.Logs) Logs.Refresh();
            });
        }
        catch (OperationCanceledException) when (_disposed || _refreshLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogger.For<MainWindowViewModel>().Warning(ex, "Could not refresh externally updated backup state");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshLifetime.Cancel();
        _refreshLifetime.Dispose();
        AppNavigator.Requested -= OnNavRequested;
        ServiceLocator.Config.Changed -= OnProtectionChanged;
        ServiceLocator.ActiveRestores.Changed -= OnActiveRestoresChanged;
        ServiceLocator.ActiveRestores.SessionOpened -= OnRestoreSessionOpened;
        Backups.Dispose();
        _destinations?.Dispose();
        _logs?.Dispose();
        _restore?.Dispose();
        foreach (var page in _sessionPages) if (!ReferenceEquals(page, _restore)) page.Dispose();
        _settings?.Dispose();
        _activeRestores?.Dispose();
    }
}

/// <summary>
/// Sidebar navigation slots. Dashboard removed — it's been merged into
/// Backups (see BackupsView). The enum value is kept around as an alias
/// (Backups) at the route layer so any old persisted state or code path
/// passing NavItem.Dashboard still lands on Backups.
/// </summary>
public enum NavItem { Backups, Destinations, Logs, Restore, Settings, ActiveRestores }
