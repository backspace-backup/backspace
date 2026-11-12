using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U179 (10 November 2026): the backup editor's Checks are a list, one entry
/// per check, each with its own frequency and conditions. Opens local_d as the
/// owner saved it (a monthly data sample and test restore converted from the
/// old single check), clicks Add a check › Structure check, changes it to
/// weekly, removes the data sample, saves, and reads the result back. Then the
/// Easy mode sentence for a new backup, and the same list in dark mode.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S99_ChecksList : ScenarioBase
{
    protected override string ScenarioName => "S99-checks-list";

    [AvaloniaFact] public async Task Run_S99() => await Run();

    private static void Click(Window host, Control target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), host)!.Value;
        host.MouseDown(at, MouseButton.Left); host.MouseUp(at, MouseButton.Left);
    }

    private static async Task ShowChecks(ScenarioContext ctx, Window win, Control target)
    {
        target.BringIntoView();
        await ctx.Driver.PumpAsync(120);
        var scroll = target.GetVisualAncestors().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, scroll.Offset.Y + target.TranslatePoint(default, scroll)!.Value.Y - 8);
        await ctx.Driver.PumpAsync(120);
    }

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Google Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("gdrive") };
        // local_d as saved before U179: monthly at 21:00, data sample and test restore.
        var json = """
            {"name":"local_d","sourcePaths":["D:\\"],"checkSchedule":{"frequency":"Monthly","timeOfDay":"21:00:00","weeklyDaysBitmask":127,"hourlyInterval":24,"skipOnBattery":true,"stopOnBattery":true,"requireNetwork":true,"catchUpMissedRuns":true},
             "checkReadDataSample":true,"checkRestoreFiles":true,"restoreCheckOptions":{"full":false,"samplePercent":1,"maximumSampleBytes":1073741824,"compareOriginals":false,"label":"Restore check (1% of files)"}}
            """;
        var backup = System.Text.Json.JsonSerializer.Deserialize<Backup>(json, new System.Text.Json.JsonSerializerOptions
            { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id });
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(backup); });

        var win = new BackupEditorWindow(ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id), isNew: false) { Width = 1100, Height = 900 };
        ctx.Driver.Show(win);
        var vm = (BackupEditorViewModel)win.DataContext!;
        win.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
        await ctx.Driver.PumpAsync(150);
        Assert.Equal(new[] { CheckKind.DataSample, CheckKind.TestRestore }, vm.Checks.Select(c => c.Kind));
        var card = win.FindControl<Border>("ChecksCard")!;
        await ShowChecks(ctx, win, card);
        ctx.Snapshot(win, "local_d converted", "local_d's old monthly check at 21:00 is now two entries: Data sample (1 GiB) and Restore check (1% of files, at most 1 GiB), each with its own How often / Time / limits and conditions, and a plain sentence on top.");

        // Add a check › Structure check, by clicking.
        var add = win.FindControl<Button>("AddStructureCheck")!;
        await ShowChecks(ctx, win, add);
        Click(win, add);
        await ctx.Driver.PumpAsync(150);
        var structure = Assert.Single(vm.Checks, c => c.Kind == CheckKind.Structure);
        Assert.Equal(MaintenanceCadence.Weekly, structure.Cadence);
        // Edit it: daily at 02:30.
        structure.Cadence = MaintenanceCadence.Daily;
        structure.TimeOfDay = new TimeSpan(2, 30, 0);
        await ctx.Driver.PumpAsync(100);
        Assert.Contains("; every day it checks that the backup is complete", vm.ChecksSentence);
        await ShowChecks(ctx, win, win.GetVisualDescendants().OfType<TextBlock>().Last(t => t.Text == "Structure check"));
        ctx.Snapshot(win, "structure check added", "After clicking Add a check › Structure check and choosing Daily at 02:30: a third entry, Structure check, explains that it reads no file contents; the sentence on top now says every day it checks that the backup is complete.");

        // Remove the data sample, by clicking its Remove button.
        var sampleRemove = win.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Content as string == "Remove" && b.DataContext is CheckEntryViewModel { Kind: CheckKind.DataSample });
        await ShowChecks(ctx, win, sampleRemove);
        Click(win, sampleRemove);
        await ctx.Driver.PumpAsync(150);
        Assert.DoesNotContain(vm.Checks, c => c.Kind == CheckKind.DataSample);
        Assert.DoesNotContain(win.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.StartsWith("Data check (") == true);

        // An invalid limit can't be saved.
        var restore = vm.Checks.Single(c => c.Kind == CheckKind.TestRestore);
        restore.SamplePercent = 0;
        Assert.False(vm.ValidateForm());
        restore.SamplePercent = 2;
        Assert.True(vm.ValidateForm());
        var saved = vm.CommitToWorking()!;
        Assert.Equal(new[] { CheckKind.TestRestore, CheckKind.Structure }, saved.Checks.Select(c => c.Kind));
        Assert.Equal(new TimeSpan(2, 30, 0), saved.Checks[1].Schedule!.TimeOfDay);
        Assert.Equal(ScheduleFrequency.Daily, saved.Checks[1].Schedule!.Frequency);
        Assert.Equal(2, saved.Checks[0].SamplePercent);
        Assert.Equal(TimeSpan.FromHours(21), saved.Checks[0].Schedule!.TimeOfDay); // Untouched entries keep their time.
        await ShowChecks(ctx, win, card);
        ctx.Snapshot(win, "data sample removed", "After clicking Remove on the data sample: the test restore (now 2%) and the daily structure check remain; the sentence on top says so.");

        // The explanations the owner asked for.
        var texts = win.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains(texts, t => t.Contains("never tested first, then those tested longest ago"));
        Assert.DoesNotContain(texts, t => t.Contains("guards the"));
        win.Close();

        // Dark mode: the converted list again.
        try
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            var dark = new BackupEditorWindow(ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id), isNew: false) { Width = 1100, Height = 900 };
            ctx.Driver.Show(dark);
            dark.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
            await ctx.Driver.PumpAsync(150);
            await ShowChecks(ctx, dark, dark.FindControl<Border>("ChecksCard")!);
            ctx.Snapshot(dark, "checks dark", "The same Checks list in dark mode: entries, sentence and Add a check buttons stay readable.");
            dark.Close();
        }
        finally { Application.Current!.RequestedThemeVariant = ThemeVariant.Light; }

        // Easy mode: one plain sentence, no settings.
        var easy = new OnboardingWindow(seed: null, isFirstBackup: false) { Width = 900, Height = 820 };
        ctx.Driver.Show(easy);
        var easyVm = (OnboardingViewModel)easy.DataContext!;
        easyVm.CurrentStep = 3;
        await ctx.Driver.PumpAsync(150);
        var sentence = easy.FindControl<TextBlock>("EasyChecksSentence")!;
        Assert.True(sentence.IsEffectivelyVisible);
        Assert.Equal("Every week Backspace removes old copies it no longer needs to keep and checks that the backup is complete, "
            + "reading back 1 GiB of the stored data to make sure it is undamaged; every month it restores some files to a temporary folder "
            + "to prove they come back.", sentence.Text);
        Assert.DoesNotContain(easy.GetVisualDescendants().OfType<Button>(), b => b.Name?.StartsWith("Add") == true && b.Name.Contains("Check"));
        ctx.Snapshot(easy, "easy mode sentence", "Easy mode, step 3: under the schedule, one plain sentence says what Backspace does by itself every week and every month. No check settings.");
        easy.Close();
    }
}
