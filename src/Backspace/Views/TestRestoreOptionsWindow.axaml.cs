using Avalonia.Controls;
using Avalonia.Interactivity;
using Backspace.Services;
using Backspace.ViewModels;

namespace Backspace.Views;

public partial class TestRestoreOptionsWindow : Window
{
    public TestRestoreOptionsWindow() : this("backup") { }
    private readonly RestoreCheckOptionsViewModel _options;
    public TestRestoreOptionsWindow(string backupName, TestRestoreOptions? options = null)
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; Close(null); } };
        Heading.Text = "Test “" + backupName + "”";
        _options = new(options);
        OptionsUi.DataContext = _options;
        void UpdateLabel() => StartButton.Content = _options.Full ? "Start full check" : "Start sample check";
        _options.PropertyChanged += (_, _) =>
        {
            UpdateLabel();
        };
        UpdateLabel();
    }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnStart(object? sender, RoutedEventArgs e) => Close(_options.ToOptions());
}
