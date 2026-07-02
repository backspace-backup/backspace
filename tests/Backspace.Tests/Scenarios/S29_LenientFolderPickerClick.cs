using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP P2 "folder-picker rows should toggle expand
/// on click anywhere left of the checkbox" — narrowed in scope to
/// "anywhere on the row that ISN'T the checkbox". User reported the
/// chevron hit area was too small. The fix added a
/// <c>PointerPressed</c> handler on the row Grid that flips
/// <see cref="FileSystemNode.IsExpanded"/> for non-checkbox clicks
/// (left-button only — right-click stays available for OS context
/// menus).
///
/// The unit-test layer was inadequate here: the algorithm was a
/// one-line setter on the node. What needed testing was the
/// XAML+code-behind wiring: do clicks on the row TEXT actually reach
/// the handler? Do checkbox clicks STILL toggle selection without
/// also expanding? This scenario asserts both halves via real
/// headless mouse events.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S29_LenientFolderPickerClick : ScenarioBase
{
    protected override string ScenarioName => "S29-lenient-folder-picker-click";

    [AvaloniaFact] public async Task Run_S29() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        await Task.Yield();

        // Build a 2-level synthetic tree directly on the VM.
        var vm = new SourceTreePickerViewModel(System.Array.Empty<string>());
        var root = new FileSystemNode(path: "C:\\", displayName: "C:", isDirectory: true);
        var subfolder = new FileSystemNode(path: "C:\\Documents", displayName: "Documents", isDirectory: true);
        subfolder.Parent = root;
        root.Children.Clear(); // drop the lazy placeholder
        root.Children.Add(subfolder);
        vm.Roots.Add(root);

        var win = new SourceTreePickerWindow();
        win.DataContext = vm;
        win.Width = 600; win.Height = 480;
        ctx.Driver.Show(win);
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        // -------- Click on the row's text area should expand --------
        Assert.False(root.IsExpanded, "Root starts collapsed.");

        var rowText = win.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => string.Equals(t.Text, "C:", StringComparison.Ordinal));
        Assert.NotNull(rowText);

        ClickAt(win, rowText!);

        Assert.True(root.IsExpanded,
            "Clicking the row text should toggle expand. If this fails the lenient " +
            "click handler regressed.");

        // -------- Click on the checkbox should NOT also expand --------
        root.IsExpanded = false;
        for (int i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();

        // Re-find: the visual tree may have re-instantiated controls
        // after the expand-collapse cycle.
        var rowCheckbox = win.GetVisualDescendants()
            .OfType<CheckBox>()
            .FirstOrDefault(c => ReferenceEquals(c.DataContext, root));
        Assert.NotNull(rowCheckbox);

        ClickAt(win, rowCheckbox!);

        Assert.False(root.IsExpanded,
            "Clicking the checkbox must NOT expand the row — selection and " +
            "expansion are distinct gestures.");

        ctx.Snapshot(win, "after-lenient-clicks",
            "Folder-picker lenient click semantics verified: text clicks expand, " +
            "checkbox clicks leave expansion alone.");
        win.Close();
    }

    private static void ClickAt(Window window, Visual element)
    {
        var b = element.Bounds;
        var pt = element.TranslatePoint(new Point(b.Width / 2, b.Height / 2), window) ?? default;
        window.MouseDown(pt, MouseButton.Left);
        window.MouseUp(pt, MouseButton.Left);
        for (int i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
    }
}
