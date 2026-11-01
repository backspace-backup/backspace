using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
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
    private WindowsUpdatePackage.Release? _release;
    private WindowsUpdatePackage.Plan? _prepared;
    private bool _preparedForService;
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
    public bool CanDownload => HasUpdate && !IsBusy && InstallAvailable;
    public bool CanSkip => HasUpdate && !IsBusy;

    public WindowsUpdateViewModel() : this(Http, RequestRestart) { }
    internal WindowsUpdateViewModel(HttpClient http, Func<Action, bool> restart, bool? installAvailable = null)
    {
        _http = http; _requestRestart = restart;
        var available = WindowsUpdateRuntime.CanInstall(out var why);
        InstallAvailable = installAvailable ?? available;
        InstallExplanation = InstallAvailable ? "Your settings stay in place. Active backups and restores must finish first; mounted snapshots will close when the app restarts." : why;
#if WINDOWS
        try
        {
            if (InstallAvailable && Services.Platform.Windows.ServiceProgramUpdateDesktop.Applies(AppContext.BaseDirectory.TrimEnd('\\')))
                InstallExplanation += " Windows will ask for administrator approval because this copy also runs background backups.";
        }
        catch (Exception ex) { InstallAvailable = false; InstallExplanation = "Background backup setup needs attention before updating. " + ex.Message; }
#endif
        _loading = true;
        Frequency = NormalizeFrequency(ServiceLocator.Config.Current.Preferences.UpdateCheckFrequency);
        _loading = false;
        try
        {
            var latest = DateTime.MinValue;
            foreach (var parent in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, AppPaths.AppFolderName) })
            {
                var result = Path.Combine(parent, ".backspace-updates", "result.json");
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
    partial void OnHasUpdateChanged(bool value) => UpdateButtons();
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
            ServiceLocator.Config.Update(c => c.Preferences.LastUpdateCheckUtc = DateTime.UtcNow);
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
            var root = Path.GetDirectoryName(exe)!;
            var forService = false;
#if WINDOWS
            forService = Services.Platform.Windows.ServiceProgramUpdateDesktop.Applies(root);
#endif
            if (_prepared is null || _prepared.Version != release.Version.ToString() || _preparedForService != forService)
            {
                if (_prepared is { State: "Prepared" } prior) WindowsUpdatePackage.SafeRemoveWork(prior.Root, prior.Work);
                Status = "Downloading and checking the update. Your installed version stays untouched…";
                var downloadLocation = forService ? Path.Combine(root, AppPaths.AppFolderName, Path.GetFileName(exe)) : exe;
                _prepared = await WindowsUpdatePackage.DownloadAsync(_http, release, downloadLocation,
                    new Progress<double>(p => Progress = p * 100), _operation.Token);
                _preparedForService = forService;
            }
            Status = forService ? "The download is ready. Windows will ask for approval to update this portable copy after Backspace closes."
                : "The update is ready. Checking that Backspace can restart safely…";
            var plan = _prepared;
#if WINDOWS
            var serviceRequest = forService ? Services.Platform.Windows.ServiceProgramUpdateDesktop.Prepare(root, release.Tag, plan) : null;
#endif
            if (!_requestRestart(() =>
            {
#if WINDOWS
                if (serviceRequest is not null) { Services.Platform.Windows.ServiceProgramUpdateDesktop.Pending = serviceRequest; return; }
#endif
                WindowsUpdateRuntime.Pending = plan;
            }))
                Status = "The download is ready. Finish active backups, restores or maintenance, then choose Download and restart again. It won’t download twice.";
        }
        catch (OperationCanceledException) { Status = "Download cancelled. Your installed version has not changed."; }
        catch (Exception ex) { Status = "The update was not installed. " + ex.Message; }
        finally { _operation?.Dispose(); _operation = null; IsDownloading = false; IsBusy = false; ScheduleNext(); }
    }
    [RelayCommand] private void Cancel() => _operation?.Cancel();
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
    public void Dispose() { _disposed = true; _timer?.Dispose(); _operation?.Cancel(); }
}
