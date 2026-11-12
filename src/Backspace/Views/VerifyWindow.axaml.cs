using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Backspace.Models;
using Backspace.ViewModels;

namespace Backspace.Views;

/// <summary>The card's Verify window (U213): the checks to run now.</summary>
public partial class VerifyWindow : Window
{
    public VerifyWindow() : this(new VerifyViewModel(new Backup { Name = "backup" }, [], System.DateTime.UtcNow)) { }

    public VerifyWindow(VerifyViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; Close(null); } };
    }

    private VerifyViewModel Model => (VerifyViewModel)DataContext!;
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnRun(object? sender, RoutedEventArgs e) => Close((IReadOnlyList<BackupCheck>)Model.Selected());
}
