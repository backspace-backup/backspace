using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Backspace.Services;

namespace Backspace.ViewModels;

/// <summary>
/// The saved-passwords banner on the Backups page (U153). Shown, instead of a
/// dialog at start, while the vault is locked on this computer, can't be
/// opened, or still waits for the one-time move from the earlier files. Its
/// button asks for the password (or starts the move).
/// </summary>
public sealed partial class VaultBannerViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _actionLabel = "";
    [ObservableProperty] private string _tone = "warn";
    private readonly SecretsStore _secrets;

    public VaultBannerViewModel() : this(ServiceLocator.Secrets) { }

    internal VaultBannerViewModel(SecretsStore secrets)
    {
        _secrets = secrets;
        SecretsStore.StateChanged += OnStateChanged;
        _ = RefreshAsync();
    }

    private void OnStateChanged(string path)
    {
        // Only the vault this banner shows.
        if (string.Equals(path, _secrets.FilePath, StringComparison.OrdinalIgnoreCase)) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        VaultState state;
        bool pending;
        try
        {
            state = await Task.Run(_secrets.Refresh).ConfigureAwait(false);
            pending = _secrets.ConversionPending;
        }
        catch (Exception ex)
        {
            AppLogger.For<VaultBannerViewModel>().Warning(ex, "The saved passwords' state could not be read for the banner");
            return;
        }
        var message = _secrets.LoadErrorMessage;
        await Dispatcher.UIThread.InvokeAsync(() => Apply(state, pending, message));
    }

    internal void Apply(VaultState state, bool conversionPending, string? loadError)
    {
        switch (state)
        {
            case VaultState.Locked:
                Show("warn", "Your saved passwords are locked on this computer",
                    "Backups that need a storage password or cloud sign-in won't run until you enter your saved-passwords password once.",
                    "Enter password");
                break;
            case VaultState.NoVault when conversionPending:
                Show("warn", "Your saved passwords need one more step",
                    "Choose a password so they can travel with Backspace. Backups that need them wait until then.",
                    "Choose a password");
                break;
            case VaultState.Unreadable:
                Show("fail", "Backspace can't open your saved passwords",
                    FirstParagraph(loadError) ?? "The file holding them is damaged. It was kept as it is.",
                    "See what to do");
                break;
            default:
                IsVisible = false;
                break;
        }
    }

    private static string? FirstParagraph(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Split("\n\n")[0];

    private void Show(string tone, string headline, string detail, string action)
    {
        Tone = tone; Headline = headline; Detail = detail; ActionLabel = action;
        IsVisible = true;
    }

    [RelayCommand]
    private async Task ActAsync()
    {
        var state = await Task.Run(_secrets.Refresh);
        if (state == VaultState.NoVault && _secrets.ConversionPending) await VaultGate.ConvertAsync();
        else await VaultGate.EnsureReadableAsync("", _secrets);
        await RefreshAsync();
    }

    public void Dispose() => SecretsStore.StateChanged -= OnStateChanged;
}
