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
/// returns to that session's existing wizard, or its dedicated
/// result panel when the wizard contains multiple source restores.
/// </summary>
public sealed partial class ActiveRestoresViewModel : ViewModelBase, IDisposable
{
    public ObservableCollection<ActiveRestoreSession> Sessions =>
        ServiceLocator.ActiveRestores.Sessions;

    public ActiveRestoresViewModel()
    {
        ServiceLocator.ActiveRestores.Changed += OnRegistryChanged;
        OnRegistryChanged();
    }

    /// <summary>Unhook from the process-lifetime registry. Without
    /// this every instance ever navigated to stayed subscribed (and
    /// rooted) forever; MainWindowViewModel.Dispose calls it.</summary>
    public void Dispose() => ServiceLocator.ActiveRestores.Changed -= OnRegistryChanged;

    [ObservableProperty] private bool _hasNone;
    [ObservableProperty] private ActiveRestoreSession? _selectedSession;
    public bool HasSelectedSession => SelectedSession is not null;
    partial void OnSelectedSessionChanged(ActiveRestoreSession? value) => OnPropertyChanged(nameof(HasSelectedSession));

    [RelayCommand]
    private void BackToList() => SelectedSession = null;

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
        ServiceLocator.ActiveRestores.Open(session);
    }

    [RelayCommand]
    private void CancelSession(ActiveRestoreSession? session) => session?.Cancel();

    [RelayCommand]
    private void DismissSession(ActiveRestoreSession? session)
    {
        if (session is not null)
        {
            ServiceLocator.ActiveRestores.Dismiss(session);
            if (ReferenceEquals(SelectedSession, session) && !session.IsRunning) SelectedSession = null;
        }
    }
}
