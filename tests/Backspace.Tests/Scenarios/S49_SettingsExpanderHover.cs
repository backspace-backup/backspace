using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP P1 "Collapsible-section hover styling has
/// weird stripe" (re-opened 2026-07-03 for the Settings page).
///
/// FluentTheme's Expander ControlTheme paints its hover brushes
/// DIRECTLY onto the template parts (Border#ToggleButtonBackground:
/// an 80%-alpha white/black band with ~3px corners; a second 10%-alpha
/// square behind the chevron). Element-level "Background=Transparent"
/// overrides can't stop that: a StyleTrigger on the part outranks the
/// TemplateBinding. The fix pierces to the same parts with app-frame
/// styles: NeutralHover wash, 8px corners, chevron pill removed.
///
/// This scenario forces the :pointerover pseudo-class on a section
/// header (the same class real mouse hover sets) and asserts the two
/// transition-free style outcomes, plus captures the washed header for
/// eyes-on validation.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S49_SettingsExpanderHover : ScenarioBase
{
    protected override string ScenarioName => "S49-settings-expander-hover";

    [AvaloniaFact] public async Task Run_S49() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var vm = new SettingsViewModel();
        var view = new SettingsView { DataContext = vm };
        var host = new Window { Width = 1100, Height = 820, Content = view };
        ctx.Driver.Show(host);

        var expanders = host.GetVisualDescendants().OfType<Expander>().ToList();
        Assert.True(expanders.Count >= 2,
            $"Expected the Settings sections to render as Expanders; found {expanders.Count}.");

        // Second card ("Window behavior") is collapsed by default, so
        // the hover wash is the only thing that changes visually.
        var header = expanders[1].GetVisualDescendants()
            .OfType<ToggleButton>()
            .FirstOrDefault(t => t.Name == "ExpanderHeader");
        Assert.NotNull(header);

        ((IPseudoClasses)header!.Classes).Add(":pointerover");
        ctx.Driver.Pump();

        var bg = header.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "ToggleButtonBackground");
        Assert.NotNull(bg);

        // Corner radius is set by the pierced :pointerover style with
        // no transition, so it's deterministic: 8px (BS.CornerRadiusSmall)
        // instead of Fluent's default ~3px band corners.
        Assert.Equal(8, bg!.CornerRadius.TopLeft);

        // The separate chevron pill (Fluent's 10%-alpha square) must be
        // gone — the header reads as ONE hover surface.
        var chevron = header.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "ExpandCollapseChevronBorder");
        if (chevron?.Background is Avalonia.Media.ISolidColorBrush chevronBrush)
        {
            Assert.Equal(0, chevronBrush.Color.A);
        }

        ctx.Snapshot(host, "hover wash on section header",
            "Window behavior header carries a soft rounded neutral wash (same family as " +
            "nav-row hover). Before the fix this was a hard-edged 80%-alpha band with " +
            "square corners plus a darker mini-square around the chevron.");

        ((IPseudoClasses)header.Classes).Remove(":pointerover");
        ctx.Driver.Pump();
        ctx.Snapshot(host, "header at rest",
            "Same header after the pointer leaves: flat card again, no residue.");

        host.Close();
        return Task.CompletedTask;
    }
}
