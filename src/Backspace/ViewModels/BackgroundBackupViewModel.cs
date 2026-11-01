using System;
using System.Threading.Tasks;
using Backspace.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Backspace.ViewModels;

public sealed partial class BackgroundBackupViewModel : ViewModelBase
{
    internal const string RunAllTheTime = "Allow administrator access";
    internal const string FinishSwitchingBack = "Finish removing access";
    private readonly IBackgroundBackupSetup _setup;
    private long _generation;
    public bool Supported => OperatingSystem.IsWindows();
    [ObservableProperty] private string _title = "Checking administrator access…";
    [ObservableProperty] private string _detail = "";
    /// <summary>"ok" once files in use are included, otherwise "warn".</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilesInUseIncluded)), NotifyPropertyChangedFor(nameof(NeedsAttention))]
    private string _statusClass = "warn";
    public bool FilesInUseIncluded => StatusClass == "ok";
    /// <summary>Settings opens its Administrator access section only while
    /// something needs doing or is in progress; once access is on it stays
    /// collapsed, with Remove access inside (U027, 29 October 2026).</summary>
    public bool NeedsAttention => StatusClass != "ok" || IsBusy || !string.IsNullOrEmpty(Error);
    [ObservableProperty] private string _actionText = RunAllTheTime;
    /// <summary>One line under the action button, for example that Windows
    /// will ask for approval. Empty hides it.</summary>
    [ObservableProperty] private string _actionHint = "";
    /// <summary>True once the background service is set up, whether or
    /// not its latest settings still need approval.</summary>
    [ObservableProperty] private bool _isOn;
    /// <summary>Why the last attempt to apply saved changes failed; null when
    /// none failed. Busy is not a failure: it is retried after the run.</summary>
    private string? _applyFailure;
    /// <summary>Outcome of the last quiet apply, for callers that retry.</summary>
    internal QuietApplyResult? LastQuietResult { get; private set; }
    /// <summary>Access is on but saved changes wait for one approval.</summary>
    [ObservableProperty] private bool _changesPending;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsAttention))] private string _error = "";
    /// <summary>True when the last Apply ended because the person cancelled
    /// Windows' prompt. Nothing failed, so callers need not show an error.</summary>
    [ObservableProperty] private bool _lastApplyCancelled;
    /// <summary>Access is off and one Windows approval would turn it on.</summary>
    public bool CanOffer => !IsOn && HasAction && ActionText == RunAllTheTime;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanApply)), NotifyPropertyChangedFor(nameof(NeedsAttention)), NotifyCanExecuteChangedFor(nameof(ApplyCommand)), NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanApply)), NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasAction;
    public bool CanApply => HasAction && !IsBusy;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RemoveCommand))] private bool _canRemove;
    private bool CanRemoveNow => CanRemove && !IsBusy;

    public BackgroundBackupViewModel() : this(DefaultSetup()) { }
    internal BackgroundBackupViewModel(IBackgroundBackupSetup setup) => _setup = setup;

    /// <summary>Test-only: the website screenshots run inside the test host,
    /// which is not a portable Backspace.exe and would otherwise show a setup
    /// error instead of the normal first-run state.</summary>
    internal static Func<IBackgroundBackupSetup>? SetupOverride { get; set; }

    private static IBackgroundBackupSetup DefaultSetup()
    {
        if (SetupOverride is { } setupOverride) return setupOverride();
#if WINDOWS
        return new Services.Platform.Windows.WindowsBackgroundBackupSetup();
#else
        return new UnavailableBackgroundBackupSetup();
#endif
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        try
        {
            var state = await _setup.InspectAsync();
            if (generation != _generation) return;
            // Three things a person needs to know: when schedules run, what
            // to do if something is unfinished, and why this folder cannot
            // do more. Everything else stays out of this card.
            // Say what it means for the person's files, not which permission
            // is set (U027, 29 October 2026).
            // Saved changes are applied without a prompt (U043): only a
            // failure to apply them needs the person's attention.
            var applying = state.State == BackgroundBackupState.ChangesPending && _applyFailure is null;
            Title = state.State switch
            {
                BackgroundBackupState.Ready => "Files in use are included",
                BackgroundBackupState.ChangesPending => applying ? "Files in use are included" : "Your latest changes aren't applied yet",
                BackgroundBackupState.SetupPending => "Setup isn't finished",
                BackgroundBackupState.RemovalPending => "Removing access isn't finished",
                _ => "Files that are open may be skipped",
            };
            StatusClass = state.State == BackgroundBackupState.Ready || applying ? "ok" : "warn";
            IsOn = state.State is BackgroundBackupState.Ready or BackgroundBackupState.ChangesPending;
            ChangesPending = state.State == BackgroundBackupState.ChangesPending;
            Detail = applying ? "Applying your latest changes to the background service…"
                : state.State == BackgroundBackupState.ChangesPending ? _applyFailure! : state.Detail;
            if (state.State == BackgroundBackupState.Ready) Error = "";
            CanRemove = state.CanRemove;
            HasAction = state.State is BackgroundBackupState.Off or BackgroundBackupState.SetupPending or BackgroundBackupState.RemovalPending
                || (state.State == BackgroundBackupState.ChangesPending && !applying);
            ActionText = state.State switch
            {
                BackgroundBackupState.ChangesPending => "Try again",
                BackgroundBackupState.SetupPending => "Finish setup",
                BackgroundBackupState.RemovalPending => FinishSwitchingBack,
                _ => RunAllTheTime,
            };
            ActionHint = state.State == BackgroundBackupState.Off
                ? "Windows asks you to approve this once."
                : HasAction && state.State != BackgroundBackupState.ChangesPending ? "Windows asks for administrator approval." : "";
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            Title = "Files that are open may be skipped";
            StatusClass = "warn";
            Detail = "Backspace could not check its administrator access: " + ex.Message;
            HasAction = false;
            ChangesPending = false;
            CanRemove = false;
            ActionHint = "";
        }
    }

    /// <summary>Applies saved changes through the background service's
    /// settings task: no Windows prompt (U043). Only a setup made by an
    /// earlier version, which lacks that task, asks Windows once.</summary>
    internal Task ApplyChangesQuietlyAsync()
    {
        // Saving, a debounced change and Try again can overlap: they share one attempt.
        if (_quiet is { IsCompleted: false }) return _quiet;
        return _quiet = ApplyChangesQuietlyCoreAsync();
    }
    private Task? _quiet;

    /// <summary>Asks the setup itself, not this view's last refresh, which can
    /// lag behind a save, whether saved changes are waiting; applies them if so.</summary>
    internal async Task ApplyPendingChangesAsync()
    {
        BackgroundBackupStatus state;
        try { state = await _setup.InspectAsync(); }
        catch (Exception) { return; }
        if (state.State == BackgroundBackupState.ChangesPending) await ApplyChangesQuietlyAsync();
    }

    private async Task ApplyChangesQuietlyCoreAsync()
    {
        if (IsBusy) { AppLogger.For<BackgroundBackupViewModel>().Information("Applying saved settings waits for the current access step"); return; }
        IsBusy = true; ++_generation;
        var outcome = new QuietApplyOutcome(QuietApplyResult.Failed);
        try
        {
            outcome = await _setup.ApplyChangesQuietlyAsync();
            _applyFailure = outcome.Result == QuietApplyResult.Failed
                ? outcome.Reason ?? "Your saved settings are kept. Try again." : null;
        }
        catch (Exception ex) { _applyFailure = ex.Message; AppLogger.For<BackgroundBackupViewModel>().Warning(ex, "Applying saved settings to the background service failed"); }
        finally { LastQuietResult = outcome.Result; IsBusy = false; await RefreshAsync(); }
        AppLogger.For<BackgroundBackupViewModel>().Information("Applying saved settings to the background service: {Result} {Reason}", outcome.Result, outcome.Reason ?? "");
        if (outcome.Result == QuietApplyResult.NeedsApproval) await ElevatedApplyAsync();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (ChangesPending) { await ApplyChangesQuietlyAsync(); return; }
        await ElevatedApplyAsync();
    }

    private async Task ElevatedApplyAsync()
    {
        var finishingRemoval = ActionText == FinishSwitchingBack;
        IsBusy = true; Error = ""; ActionHint = ""; LastApplyCancelled = false; ++_generation;
        Title = finishingRemoval ? "Removing access…" : "Setting up…";
        Detail = "Approve the Windows prompt to continue.";
        try { await _setup.ApplyAsync(); }
        catch (OperationCanceledException ex) { Error = ex.Message; LastApplyCancelled = true; }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; await RefreshAsync(); }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveNow))]
    private async Task RemoveAsync()
    {
        IsBusy = true; Error = ""; ++_generation;
        Title = "Removing access…";
        Detail = "Approve the Windows prompt. Your backups, history and sign-ins are kept.";
        try { await _setup.RemoveAsync(); }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; await RefreshAsync(); }
    }
}
