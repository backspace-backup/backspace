using System;
using System.Collections.ObjectModel;
using System.Linq;
using Backspace.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Backspace.ViewModels;

/// <summary>
/// Backs the "Restores" left-nav cards view. One card per
/// in-flight restore session in
/// <see cref="ServiceLocator.ActiveRestores"/>; click a card →
/// jumps to the Restore wizard scope-targeted at that session
/// (today: open the wizard's progress screen; multi-restore
/// per-session VM state is a follow-up ROADMAP item once a real
/// parallel-restore case justifies the bookkeeping).
/// </summary>
public sealed partial class ActiveRestoresViewModel : ViewModelBase
{
    public ObservableCollection<ActiveRestoreSession> Sessions =>
        ServiceLocator.ActiveRestores.Sessions;

    public ActiveRestoresViewModel()
    {
        ServiceLocator.ActiveRestores.Changed += OnRegistryChanged;
        OnRegistryChanged();
    }

    [ObservableProperty] private bool _hasNone;

    private void OnRegistryChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            HasNone = Sessions.Count == 0;
        });
    }

    [RelayCommand]
    private void OpenSession(ActiveRestoreSession? session)
    {
        if (session is null) return;
        // Today's single-RestoreViewModel architecture surfaces the
        // most recent restore on the Restore tab. Routing back there
        // gives the user the live progress view they navigated away
        // from. Multi-restore distinct VM scoping is the next layer.
        AppNavigator.Go(NavItem.Restore);
    }
}
