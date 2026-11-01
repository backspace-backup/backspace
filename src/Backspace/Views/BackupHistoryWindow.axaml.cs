using Avalonia.Controls;
using Avalonia.Interactivity;
using Backspace.Services;
using Backspace.ViewModels;

namespace Backspace.Views;

public partial class BackupHistoryWindow : Window
{
    public BackupHistoryWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; Close(); } };
    }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void OnOpenLog(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: BackupHistoryRow row }) return;
        Close();
        AppNavigator.OpenRun(row.BackupId, row.RunId);
    }
}
