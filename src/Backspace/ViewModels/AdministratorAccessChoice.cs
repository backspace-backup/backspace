using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Backspace.ViewModels;

/// <summary>
/// "Include files that are in use" inside the backup creation flow (U026,
/// 29 October 2026). Offered while creating a backup that uses VSS when
/// Backspace has no administrator access yet. Ticked by default and applied
/// when the person saves, before the first run, so Windows' prompt follows
/// the person's own choice instead of interrupting afterwards.
/// </summary>
public sealed partial class AdministratorAccessChoice : ObservableObject
{
    private readonly Func<BackgroundBackupViewModel> _create;
    private BackgroundBackupViewModel? _access;

    /// <summary>True when one Windows approval would turn access on.</summary>
    [ObservableProperty] private bool _offered;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Hint))] private bool _allow = true;

    public string Hint => Allow
        ? "Windows asks you to approve this once, when you save."
        : "Files that are open when a backup runs may be skipped. You can allow access later from Your backups.";

    public AdministratorAccessChoice() : this(() => new BackgroundBackupViewModel()) { }
    internal AdministratorAccessChoice(Func<BackgroundBackupViewModel> create) => _create = create;

    public async Task RefreshAsync()
    {
        if (!OperatingSystem.IsWindows()) { Offered = false; return; }
        try
        {
            _access ??= _create();
            await _access.RefreshAsync();
            Offered = _access.CanOffer;
        }
        catch (Exception) { Offered = false; }
    }

    public bool WillApply(bool usesVss) => Offered && Allow && usesVss && _access is not null;

    /// <summary>Turns access on if chosen. Returns the reason when setup
    /// failed after approval; null when not chosen, done, or the person
    /// cancelled Windows' prompt (the Backups page then keeps offering it).</summary>
    public async Task<string?> ApplyIfChosenAsync(bool usesVss)
    {
        if (!WillApply(usesVss)) return null;
        await _access!.ApplyCommand.ExecuteAsync(null);
        Offered = _access.CanOffer;
        return string.IsNullOrEmpty(_access.Error) || _access.LastApplyCancelled ? null : _access.Error;
    }
}
