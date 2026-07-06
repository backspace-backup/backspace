using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Settings view in its full populated state. Each accent variant
/// merges its own resource dictionary at runtime (Accent_Ocean,
/// Accent_Plum, etc.) — the snapshot-per-accent matrix below is a
/// quick way to spot any accent that breaks contrast against the
/// cream background or that doesn't propagate to its hover/tint
/// derivatives.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S12_SettingsAndAccents : ScenarioBase
{
    protected override string ScenarioName => "S12-settings-and-accents";

    [AvaloniaFact] public async Task Run_S12() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var vm = new SettingsViewModel();
        var view = new SettingsView { DataContext = vm };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 820, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "settings default ocean",
            "Settings — Appearance, Logs & diagnostics, Notifications etc. Default Ocean accent.");

        // Cycle every accent variant — Settings.Accent flips the
        // app-wide resource dictionary at runtime, so the snapshot
        // captures whether the new accent's tint/hover derivatives
        // actually reach the bound controls. finally-restore the
        // original: the accent is process-global and used to leak the
        // LAST cycled accent into every subsequent test in the run
        // (S49's screenshots came out Terracotta/Ocean tinted).
        var originalAccent = vm.Accent;
        try
        {
            foreach (var accent in vm.AvailableAccents)
            {
                vm.Accent = accent;
                ctx.Driver.Pump();
                ctx.Snapshot(host, $"settings accent {accent}",
                    $"Accent={accent} — primary buttons + active nav + status highlights tinted.");
            }
        }
        finally
        {
            vm.Accent = originalAccent;
            ctx.Driver.Pump();
        }

        host.Close();
        return Task.CompletedTask;
    }
}
