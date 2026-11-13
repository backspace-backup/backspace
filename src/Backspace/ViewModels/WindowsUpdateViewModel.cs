using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Backspace.Services;
using Backspace.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Backspace.ViewModels;

/// <summary>One update controller for the application. Automatic checks only
/// notify; download and restart always follow an explicit user action.</summary>
public sealed partial class WindowsUpdateViewModel : ViewModelBase, IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly HttpClient _http;
    private readonly Func<Action, bool> _requestRestart;

    /// <summary>What to do when work runs as the update is ready (U238).</summary>
    internal enum BusyChoice { NotNow, WhenFinished, StopNow }

    /// <summary>What runs now, in words ("«local_c» backup"); empty when nothing does. Replaceable in tests.</summary>
    internal Func<System.Collections.Generic.IReadOnlyList<string>> RunningWork { get; set; } = MainWindow.DescribeRunningWork;
    /// <summary>Stops what runs, for the update. Replaceable in tests.</summary>
    internal Func<Task> StopWork { get; set; } = MainWindow.StopWorkForUpdateAsync;
    /// <summary>Asks what to do while work runs. Replaceable in tests.</summary>
    internal Func<System.Collections.Generic.IReadOnlyList<string>, string, Task<BusyChoice>> AskWhenBusy { get; set; } = AskAsync;
    internal static TimeSpan WaitPoll = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? _waiting;

    /// <summary>The update waits for work to finish, then restarts by itself (U238).</summary>
    [ObservableProperty] private bool _isWaitingForWork;
    /// <summary>The status is something to act on: drawn as an amber notice.</summary>
    [ObservableProperty] private bool _statusIsWarning;
    private WindowsUpdatePackage.Release? _release;
    private WindowsUpdatePackage.Plan? _prepared;
    private CancellationTokenSource? _operation;
    private Timer? _timer;
    private bool _started;
    private bool _disposed;
    private bool _loading;
    private DateTime _lastAttempt;
    public bool Supported => OperatingSystem.IsWindows();
    public string CurrentVersionText => "Backspace " + WindowsUpdatePackage.CurrentVersion;
    public string[] Frequencies { get; } = { "Daily", "Weekly", "Monthly", "Never" };
    public bool InstallAvailable { get; }
    public string InstallExplanation { get; }
    [ObservableProperty] private string _frequency = "Monthly";
    [ObservableProperty] private string _status = "Check for a newer version whenever you like.";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _availableVersion = "";
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;
    public bool CanCheck => !IsBusy;

    // ---- Sidebar indicator (U088) ----
    // Like a browser's update badge: the version shows whether it is up to
    // date or a newer one is waiting, and opens App updates when clicked.
    /// <summary>"available", "current" or "unknown" (never checked, or the check failed).</summary>
    public string FooterState
    {
        get
        {
            if (HasUpdate || NewerKnown() is not null) return "available";
            var prefs = ServiceLocator.Config?.Current?.Preferences;
            return prefs is not null && prefs.LastUpdateCheckUtc > DateTime.UtcNow.AddDays(-45) ? "current" : "unknown";
        }
    }
    public bool FooterUpdateAvailable => FooterState == "available";
    public bool FooterUpToDate => FooterState == "current";
    public string FooterTooltip => FooterState switch
    {
        "available" => "Backspace " + (NewerKnown() ?? "") + " is available. Click to read what changed and update.",
        "current" => "Up to date. Last checked " + HumanFormat.Ago(ServiceLocator.Config.Current.Preferences.LastUpdateCheckUtc) + ". Click to check again.",
        _ => "Click to check for updates.",
    };

    private string? NewerKnown()
    {
        if (_release is { } release) return release.Version.ToString();
        var saved = ServiceLocator.Config?.Current?.Preferences.AvailableUpdateVersion;
        return Version.TryParse(saved, out var v) && v > WindowsUpdatePackage.CurrentVersion ? saved : null;
    }

    internal void RaiseFooter()
    {
        OnPropertyChanged(nameof(FooterState));
        OnPropertyChanged(nameof(FooterUpdateAvailable));
        OnPropertyChanged(nameof(FooterUpToDate));
        OnPropertyChanged(nameof(FooterTooltip));
    }

    public bool CanDownload => HasUpdate && !IsBusy && InstallAvailable && !IsWaitingForWork;
    public bool CanSkip => HasUpdate && !IsBusy;

    public WindowsUpdateViewModel() : this(Http, RequestRestart) { }
    internal WindowsUpdateViewModel(HttpClient http, Func<Action, bool> restart, bool? installAvailable = null)
    {
        _http = http; _requestRestart = restart;
        var available = WindowsUpdateRuntime.CanInstall(out var why);
        InstallAvailable = installAvailable ?? available;
        InstallExplanation = InstallAvailable ? "Your settings stay in place. Mounted snapshots close when Backspace restarts." : why;
        _loading = true;
        Frequency = NormalizeFrequency(ServiceLocator.Config.Current.Preferences.UpdateCheckFrequency);
        _loading = false;
        try
        {
            var latest = DateTime.MinValue;
            foreach (var parent in new[] { WindowsUpdateRuntime.UpdatesFolder(AppContext.BaseDirectory) })
            {
                var result = Path.Combine(parent, "result.json");
                if (!File.Exists(result) || new FileInfo(result).Length > 16384) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(result));
                if (!doc.RootElement.TryGetProperty("at", out var at) || !at.TryGetDateTime(out var date) || date < latest) continue;
                latest = date; Status = doc.RootElement.GetProperty("message").GetString() ?? Status;
            }
        }
        catch { }
    }
    internal static string NormalizeFrequency(string value) => value is "Daily" or "Weekly" or "Monthly" or "Never" ? value : "Monthly";
    internal static TimeSpan? Interval(string frequency) => frequency switch
    { "Daily" => TimeSpan.FromDays(1), "Weekly" => TimeSpan.FromDays(7), "Never" => null, _ => TimeSpan.FromDays(30) };
    partial void OnFrequencyChanged(string value)
    {
        if (_loading) return;
        ServiceLocator.Config.Update(c => c.Preferences.UpdateCheckFrequency = NormalizeFrequency(value));
        if (_started) ScheduleNext();
    }
    partial void OnIsBusyChanged(bool value) => UpdateButtons();
    partial void OnIsWaitingForWorkChanged(bool value) => UpdateButtons();
    partial void OnHasUpdateChanged(bool value) { UpdateButtons(); RaiseFooter(); }
    private void UpdateButtons()
    {
        OnPropertyChanged(nameof(CanCheck)); OnPropertyChanged(nameof(CanDownload)); OnPropertyChanged(nameof(CanSkip));
        CheckNowCommand.NotifyCanExecuteChanged(); DownloadAndRestartCommand.NotifyCanExecuteChanged(); SkipVersionCommand.NotifyCanExecuteChanged();
    }

    public void Begin()
    {
        if (_started || !Supported || Environment.GetEnvironmentVariable("BACKSPACE_NO_UPDATE_CHECK") == "1") return;
        _started = true; ScheduleNext();
    }
    private void ScheduleNext()
    {
        _timer?.Dispose(); _timer = null;
        if (_disposed || !_started || Interval(Frequency) is not { } interval) return;
        var lastCheck = ServiceLocator.Config.Current.Preferences.LastUpdateCheckUtc;
        if (lastCheck > DateTime.UtcNow) lastCheck = DateTime.UtcNow;
        var due = lastCheck + interval;
        if (_lastAttempt != default && due < _lastAttempt.AddHours(1)) due = _lastAttempt.AddHours(1);
        var wait = due - DateTime.UtcNow;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        if (wait > TimeSpan.FromDays(30)) wait = TimeSpan.FromDays(30);
        _timer = new Timer(_ => Dispatcher.UIThread.Post(async () => await CheckAsync(false)), null, wait, Timeout.InfiniteTimeSpan);
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNow() => CheckAsync(true);

    internal async Task CheckAsync(bool manual)
    {
        if (IsBusy || _disposed) return;
        IsBusy = true; Status = "Checking GitHub for updates…"; _lastAttempt = DateTime.UtcNow;
        HasUpdate = false; _release = null; Notes = ""; AvailableVersion = "";
        _operation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, WindowsUpdatePackage.ApiUrl);
            request.Headers.UserAgent.ParseAdd("Backspace-Updater");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, _operation.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            { _release = null; Status = "There are no published updates yet."; }
            else
            {
                response.EnsureSuccessStatusCode();
                _release = WindowsUpdatePackage.ParseRelease(await response.Content.ReadAsStringAsync(_operation.Token), WindowsUpdatePackage.CurrentVersion);
                Status = _release is null ? "You’re up to date." : "Backspace " + _release.Version + " is available.";
            }
            HasUpdate = _release is not null;
            Notes = _release?.Notes ?? ""; AvailableVersion = _release is null ? "" : "Update to " + _release.Version;
            ServiceLocator.Config.Update(c =>
            {
                c.Preferences.LastUpdateCheckUtc = DateTime.UtcNow;
                c.Preferences.AvailableUpdateVersion = _release?.Version.ToString() ?? "";
            });
            RaiseFooter();
            if (_release is { } release)
            {
                var skipped = ServiceLocator.Config.Current.Preferences.SkippedUpdateVersion == release.Version.ToString();
                if (skipped && !manual) Status += " You skipped this version; you can still install it here.";
                if (!manual && !skipped)
                    await ServiceLocator.Notifier.NotifyAsync("A Backspace update is available", "Version " + release.Version + ". Open Settings to read what changed and choose when to restart.", NotificationSeverity.Info,
                        onClick: () => Dispatcher.UIThread.Post(() => AppNavigator.Go(NavItem.Settings)));
            }
        }
        catch (OperationCanceledException) { Status = "The update check was cancelled or timed out. Try again when you’re ready."; }
        catch (Exception ex) { Status = "Couldn’t check for updates. " + ex.Message; }
        finally { _operation?.Dispose(); _operation = null; IsBusy = false; ScheduleNext(); }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAndRestart()
    {
        if (_release is not { } release) return;
        IsBusy = true; IsDownloading = true; Progress = 0;
        _operation = new CancellationTokenSource();
        try
        {
            var exe = Environment.ProcessPath!;
            if (_prepared is null || _prepared.Version != release.Version.ToString())
            {
                if (_prepared is { State: "Prepared" } prior) WindowsUpdatePackage.SafeRemoveWork(prior.Root, prior.Work);
                Status = "Downloading and checking the update. Your installed version stays untouched…";
                _prepared = await WindowsUpdatePackage.DownloadAsync(_http, release, exe,
                    new Progress<double>(p => Progress = p * 100), _operation.Token);
            }
            Status = "The update is ready. Checking that Backspace can restart safely…";
            StatusIsWarning = false;
            await RestartWhenPossibleAsync(release.Version.ToString());
        }
        catch (OperationCanceledException) { Status = "Download cancelled. Your installed version has not changed."; }
        catch (Exception ex) { Status = "The update was not installed. " + ex.Message; }
        finally { _operation?.Dispose(); _operation = null; IsDownloading = false; IsBusy = false; ScheduleNext(); }
    }
    /// <summary>Restarts into the downloaded update, or, while work runs, says
    /// what runs and offers to wait for it or stop it (U238).</summary>
    internal async Task RestartWhenPossibleAsync(string version)
    {
        if (!TryRestart())
        {
            // Something runs (U238): say what, and offer to wait for it or stop it.
            var running = RunningWork();
            if (running.Count == 0)
            {
                Status = $"Backspace {version} is downloaded, but Backspace can't restart yet: storage maintenance must finish first. Choose Download and restart again later; it won't download again.";
                StatusIsWarning = true;
            }
            else switch (await AskWhenBusy(running, version))
            {
                case BusyChoice.StopNow:
                    Status = $"Stopping {Join(running)}…";
                    await StopWork();
                    if (!TryRestart()) StartWaiting(version);
                    break;
                case BusyChoice.WhenFinished:
                    StartWaiting(version);
                    break;
                default:
                    Status = $"Backspace {version} is downloaded. {Capitalize(Join(running))} {(running.Count == 1 ? "is" : "are")} still running: choose Download and restart when you're ready; it won't download again.";
                    StatusIsWarning = true;
                    break;
            }
        }
    }

    /// <summary>Tests: an update already downloaded.</summary>
    internal void UsePrepared(WindowsUpdatePackage.Plan plan) => _prepared = plan;

    [RelayCommand] private void Cancel() => _operation?.Cancel();

    private bool TryRestart()
    {
        var plan = _prepared;
        return plan is not null && _requestRestart(() => WindowsUpdateRuntime.Pending = plan);
    }

    /// <summary>Waits for the work to finish, then restarts (U238).</summary>
    private void StartWaiting(string version)
    {
        _waiting?.Cancel();
        var waiting = _waiting = new CancellationTokenSource();
        IsWaitingForWork = true;
        StatusIsWarning = true;
        void Say()
        {
            var running = RunningWork();
            Status = running.Count == 0
                ? $"Restarting to Backspace {version}…"
                : $"Waiting for {Join(running)} to finish. Backspace then restarts to {version} by itself.";
        }
        Say();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!waiting.IsCancellationRequested)
                {
                    await Task.Delay(WaitPoll, waiting.Token);
                    var restarted = await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (waiting.IsCancellationRequested) return true;
                        if (TryRestart()) { IsWaitingForWork = false; return true; }
                        Say();
                        return false;
                    });
                    if (restarted) return;
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Stops waiting: the update stays downloaded.</summary>
    [RelayCommand]
    private void CancelWaiting()
    {
        _waiting?.Cancel();
        IsWaitingForWork = false;
        StatusIsWarning = false;
        Status = "The update stays downloaded. Choose Download and restart when you're ready; it won't download again.";
    }

    /// <summary>Stops what runs now and restarts (U238).</summary>
    [RelayCommand]
    private async Task StopAndUpdate()
    {
        if (_release is not { } release) return;
        Status = $"Stopping {Join(RunningWork())}…";
        await StopWork();
        if (TryRestart()) return;
        StartWaiting(release.Version.ToString());
    }

    internal static string Join(System.Collections.Generic.IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    private static string Capitalize(string text) => text.Length == 0 || char.IsUpper(text[0]) || text[0] == '«' ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static async Task<BusyChoice> AskAsync(System.Collections.Generic.IReadOnlyList<string> running, string version)
    {
        var owner = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window } ? window : null;
        var choice = await new UpdateBusyWindow(version, running).AskAsync(owner);
        return choice switch
        {
            UpdateBusyWindow.Choice.WhenDone => BusyChoice.WhenFinished,
            UpdateBusyWindow.Choice.StopNow => BusyChoice.StopNow,
            _ => BusyChoice.NotNow,
        };
    }
    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void SkipVersion()
    {
        if (_release is not { } release) return;
        ServiceLocator.Config.Update(c => c.Preferences.SkippedUpdateVersion = release.Version.ToString());
        Status = "Skipped version " + release.Version + ". Automatic checks won’t notify you about it again.";
    }
    [RelayCommand]
    private void OpenReleaseNotes()
    {
        var url = _release?.Url ?? WindowsUpdatePackage.ReleasesUrl;
        if (!WindowsUpdatePackage.IsReleaseUrl(url)) return;
        try { using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Status = "Couldn’t open the release page. " + ex.Message; }
    }
    private static bool RequestRestart(Action prepare) => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window }
        && window.TryQuitForUpdate(prepare);
    public void Dispose() { _disposed = true; _timer?.Dispose(); _operation?.Cancel(); _waiting?.Cancel(); }
}
