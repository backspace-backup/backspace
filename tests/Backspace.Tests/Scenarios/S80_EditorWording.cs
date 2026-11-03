using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U104: both stop conditions read "Stop mid-run if …". U107: the storage
/// password text says what ON and OFF mean. U108: a destination's Options
/// button lists only its own options, so Google Drive no longer mentions an
/// S3 region.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S80_EditorWording : ScenarioBase
{
    protected override string ScenarioName => "S80-editor-wording";

    [AvaloniaFact] public async Task Run_S80() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        Options(ctx, DestinationKind.GoogleDrive, "(name)", "google drive options");
        Options(ctx, DestinationKind.S3Compatible, "(name, region, bucket prefix)", "s3 options");

        var backup = new Backup { Name = "local_c" };
        backup.SourcePaths.Add(ctx.MakeSource("src", targetBytes: 10_000));
        var vm = new BackupEditorViewModel(backup, isNew: false);
        var win = new BackupEditorWindow { DataContext = vm, Width = 1000, Height = 980 };
        ctx.Driver.Show(win);
        Assert.True(ctx.Driver.WaitFor(() => win.GetVisualDescendants().OfType<TabControl>().Any()));
        win.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
        ctx.Driver.Pump();
        var stops = win.GetVisualDescendants().OfType<CheckBox>()
            .Where(c => c.Content is string s && s.StartsWith("Stop mid-run if ")).ToList();
        Assert.Equal(new[] { "Stop mid-run if the computer switches to battery", "Stop mid-run if the connection becomes metered" },
            stops.Select(c => (string)c.Content!));
        stops.Last().BringIntoView();
        ctx.Driver.Pump();
        ctx.Snapshot(win, "stop conditions", "Schedule: the two stopping rules read 'Stop mid-run if the computer switches to battery' and 'Stop mid-run if the connection becomes metered'.");
        win.Close();
        vm.Dispose();
        return Task.CompletedTask;
    }

    private static void Options(ScenarioContext ctx, DestinationKind kind, string hint, string shot)
    {
        var vm = new DestinationEditorViewModel(new Destination { Kind = kind, Encrypted = true }, false);
        var win = new DestinationEditorWindow { DataContext = vm, Width = 760, Height = 760 };
        ctx.Driver.Show(win);
        ctx.Driver.Pump();
        Assert.Equal(hint, vm.OptionsHint);
        var toggle = win.FindControl<Button>("OptionsToggleButton")!;
        toggle.BringIntoView();
        ctx.Driver.Pump();
        Assert.Contains(toggle.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == hint);
        ctx.Snapshot(win, shot, $"{kind}: the Options button reads '{hint}', with the storage password explanation above it.");
        win.Close();
    }
}
