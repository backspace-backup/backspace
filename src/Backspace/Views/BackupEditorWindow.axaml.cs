using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;

namespace Backspace.Views;

public partial class BackupEditorWindow : Window
{
    private readonly BackupEditorViewModel _vm;

    public BackupEditorWindow() : this(new Backup(), true) { }

    public BackupEditorWindow(Backup working, bool isNew)
        : this(working, isNew, isFirstBackup: false) { }

    public BackupEditorWindow(Backup working, bool isNew, bool isFirstBackup, bool openedFromEasy = false)
    {
        _vm = new BackupEditorViewModel(working, isNew, openedFromEasy);
        // Header copy mirrors the Easy mode wizard so a mid-flow toggle
        // lands the user on a familiar phrasing rather than "Backup
        // settings".
        if (!isNew)               _vm.HeaderTitle = $"Edit «{working.Name}»";
        else if (isFirstBackup)   _vm.HeaderTitle = "Let's get your first backup running";
        else                      _vm.HeaderTitle = "Let's add a new backup";
        DataContext = _vm;
        InitializeComponent();
        WindowPlacement.Attach(this);

        // Escape closes without saving — matches OS dialog convention.
        // Enter is intentionally NOT bound to Save here: the editor
        // contains free-form multi-line text fields (filters, extra
        // args), so binding Enter globally would corrupt typing.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
        };
        // Dispose the VM on close so its final CancellationTokenSource
        // (the live savings-estimate ticket) is reclaimed instead of
        // waiting for GC. Matches DestinationEditorWindow.
        Closed += (_, _) => _vm.Dispose();
        // Closing with unsaved changes asks first (U206). What was there on
        // opening is compared with what the editor would save now.
        // Once the window has settled: a new backup's recommended filters are
        // written into its rules just after it opens, which is not a change.
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _opened ??= Fingerprint(),
            Avalonia.Threading.DispatcherPriority.ApplicationIdle);
        UnsavedChangesWindow.Guard(this, isNew ? "this new backup" : "this backup's settings",
            () => _opened is not null && Fingerprint() != _opened, () => OnSave(this, new RoutedEventArgs()), () => SwitchToEasy);
    }

    private string? _opened;
    /// <summary>What the person entered. A blank name is completed with a
    /// generated one (random suffix) when the snapshot is built, so the typed
    /// name is compared instead, and the storage id seed is left out.</summary>
    private string Fingerprint()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(_vm.BuildSnapshot()))!.AsObject();
        node["Name"] = _vm.Name ?? "";
        node.Remove("SnapshotIdSeed");
        return node.ToJsonString();
    }

    /// <summary>True when the user confirmed via "Save &amp; Run" rather
    /// than plain "Save". The parent <see cref="ViewModels.BackupsViewModel"/>
    /// reads this on dialog close so it can fire the Orchestrator after
    /// persisting.</summary>
    public bool RunAfterSave { get; private set; }

    /// <summary>True iff the user clicked the Easy toggle. Caller reads
    /// <see cref="SwitchSnapshot"/> for the state to seed the wizard.</summary>
    public bool SwitchToEasy { get; private set; }
    internal AdministratorAccessChoice AdministratorAccess => _vm.AdministratorAccess;
    public Backup? SwitchSnapshot { get; private set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    // The summary's lines open their tab (U228).
    private void OnSummaryBackups(object? sender, RoutedEventArgs e) => ScheduleSubTabs.SelectedItem = ScheduleBackupsTab;
    private void OnSummaryCleanup(object? sender, RoutedEventArgs e) => ScheduleSubTabs.SelectedItem = ScheduleCleanupTab;
    private void OnSummaryChecks(object? sender, RoutedEventArgs e) => ScheduleSubTabs.SelectedItem = ScheduleChecksTab;

    // Left out's rule links (U177) select the rule on the Rules tab.
    private BackupEditorViewModel? _ruleSource;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_ruleSource is not null) _ruleSource.RuleHighlightRequested -= ShowRule;
        _ruleSource = DataContext as BackupEditorViewModel;
        if (_ruleSource is not null) _ruleSource.RuleHighlightRequested += ShowRule;
    }

    /// <summary>Opens Filters › Advanced › Rules with line <paramref name="line"/>
    /// (from 0) selected and scrolled into view.</summary>
    internal void ShowRule(int line)
    {
        EditorTabs.SelectedItem = FiltersTab;
        FiltersSubTabs.SelectedItem = AdvancedFiltersTab;
        AdvancedSubTabs.SelectedItem = RulesTab;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var text = RulesEditor.Text ?? "";
            var start = 0;
            for (var i = 0; i < line && start >= 0; i++)
                start = text.IndexOf('\n', start) is var n and >= 0 ? n + 1 : -1;
            if (start < 0) return;
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            if (end > start && text[end - 1] == '\r') end--;
            RulesEditor.Focus();
            RulesEditor.SelectionStart = start;
            RulesEditor.SelectionEnd = end;
            RulesEditor.ScrollToLine(line);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Opens Filters > Recommended at the new filter suggestions:
    /// the card's "New filter suggestions: Review" (U165).</summary>
    public void ShowFilterSuggestions()
    {
        EditorTabs.SelectedItem = FiltersTab;
        FiltersSubTabs.SelectedItem = RecommendedFiltersTab;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => FilterSuggestions.BringIntoView(),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        // async void exception trap: SaveAsync runs remote probes and
        // can throw on network errors that bubble out of the regular
        // validation path. Without this catch the exception goes to
        // Avalonia's unobserved-async-void path and the user sees the
        // Save button do nothing.
        try
        {
            _vm.RunAfterSaveRequested = false;
            var saved = await _vm.SaveAsync(CancellationToken.None);
            if (saved is null) return;   // validation failed or user cancelled
            UnsavedChangesWindow.MarkSaved(this);
            Close(saved);
        }
        catch (Exception ex)
        {
            AppLogger.For<BackupEditorWindow>().Error(ex, "OnSave threw");
            _vm.SummaryError = "Save failed: " + ex.Message;
        }
    }

    private async void OnSaveAndRun(object? sender, RoutedEventArgs e)
    {
        try
        {
            _vm.RunAfterSaveRequested = true;
            var saved = await _vm.SaveAsync(CancellationToken.None);
            if (saved is null) return;
            RunAfterSave = true;
            UnsavedChangesWindow.MarkSaved(this);
            Close(saved);
        }
        catch (Exception ex)
        {
            AppLogger.For<BackupEditorWindow>().Error(ex, "OnSaveAndRun threw");
            _vm.SummaryError = "Save failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Advanced → Easy toggle. Capture current editor state into a
    /// Backup snapshot, surface it via <see cref="SwitchSnapshot"/>, and
    /// close — the parent reopens the wizard with the snapshot. We
    /// don't validate before closing: the user is mid-edit and switching
    /// modes shouldn't be gated by Advanced-only required fields.
    /// </summary>
    private void OnSwitchToEasy(object? sender, RoutedEventArgs e)
    {
        SwitchSnapshot = _vm.BuildSnapshot();
        SwitchToEasy = true;
        Close(null);
    }
}
