using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Views;

/// <summary>
/// Add a backup, step 2 (U042, 30 October 2026): picking an existing
/// destination from the list and clicking Continue must move on. In the VM
/// it showed "Pick at least one destination" unless the small Add button was
/// clicked as well. These click the real controls.
/// </summary>
public sealed class OnboardingDestinationPickTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Continue_accepts_a_destination_picked_from_the_list(bool pick)
    {
        var source = Directory.CreateTempSubdirectory("bs-u042-").FullName;
        var destination = new Destination { Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"C:\BackspaceGuiRepo" };
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear(); cfg.Destinations.Clear();
            cfg.Backups.Add(new Backup { Name = "existing" });
            cfg.Destinations.Add(destination);
        });
        var window = new OnboardingWindow(seed: null, isFirstBackup: false) { Width = 900, Height = 760 };
        window.Show();
        try
        {
            var vm = (OnboardingViewModel)window.DataContext!;
            vm.SourceChips.Add(new SourceChipVm(source));
            Click(window, FindButton(window, "Continue"));
            Assert.Equal(2, vm.CurrentStep);

            if (pick)
            {
                var list = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible);
                list.SelectedItem = list.Items.Cast<object>().Single();
                window.UpdateLayout();
            }
            Click(window, FindButton(window, "Continue"));

            if (pick)
            {
                Assert.Equal(3, vm.CurrentStep);
                Assert.Null(vm.DestinationError);
                Assert.Equal(destination.Id, Assert.Single(vm.SelectedDestinations).Id);
                // Not the first backup: no "first backup" wording.
                Assert.Equal("Add a backup - Backspace", window.Title);
                Assert.NotNull(FindButton(window, "Save and run"));
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && (t.Text ?? "").Contains("first backup"));
            }
            else
            {
                // Negative: nothing picked still stops on step 2 with the error.
                Assert.Equal(2, vm.CurrentStep);
                Assert.Equal("Pick at least one destination, or set one up.", vm.DestinationError);
            }
        }
        finally
        {
            window.Close();
            ServiceLocator.Config.Update(cfg => { cfg.Backups.Clear(); cfg.Destinations.Clear(); });
            Directory.Delete(source, recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData(true, false, "Save and run my first backup")]
    [InlineData(false, false, "Save and run")]
    [InlineData(false, true, "Save and run")]
    public void Final_button_says_first_backup_only_for_the_first(bool first, bool editing, string expected)
    {
        var existing = new Backup { Name = "documents" };
        var window = new OnboardingWindow(seed: editing ? existing : null, isFirstBackup: first, isEdit: editing) { Width = 900, Height = 760 };
        window.Show();
        try
        {
            ((OnboardingViewModel)window.DataContext!).CurrentStep = 3;
            window.UpdateLayout();
            Assert.NotNull(FindButton(window, expected));
            if (!first) Assert.DoesNotContain("first", window.Title, StringComparison.OrdinalIgnoreCase);
        }
        finally { window.Close(); }
    }

    private static Button FindButton(Window window, string text) => window.GetVisualDescendants().OfType<Button>()
        .Single(b => b.IsEffectivelyVisible && (b.Content is string label ? label
            : string.Join(" ", b.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text))) == text);

    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); window.UpdateLayout(); Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        window.UpdateLayout();
    }
}
