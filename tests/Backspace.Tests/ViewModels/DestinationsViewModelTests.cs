using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

public class DestinationsViewModelTests
{
    [AvaloniaFact]
    public void NewViewModel_WithEmptyConfig_IsEmpty()
    {
        ResetConfig();

        using var vm = new DestinationsViewModel();

        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Rows);
    }

    [AvaloniaFact]
    public void AddingDestination_UpdatesRows()
    {
        ResetConfig();

        using var vm = new DestinationsViewModel();

        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(new Destination
        {
            Name = "Local backup drive",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = @"D:\backups",
        }));

        Assert.False(vm.IsEmpty);
        Assert.Single(vm.Rows);
        Assert.Equal("Local backup drive", vm.Rows[0].Name);
    }

    private static void ResetConfig()
    {
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
    }
}
