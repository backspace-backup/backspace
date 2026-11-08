using System;
using System.Linq;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>U156: Run now and Save and run sent to a busy background service
/// show "Queued" on the card, not "Running" and not nothing.</summary>
public sealed class ServiceQueuedCardTests
{
    [AvaloniaFact]
    public void A_run_the_service_cannot_start_yet_shows_queued_even_when_the_card_was_idle()
    {
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
        ServiceLocator.Config.Update(cfg => { cfg.Backups.Clear(); cfg.Backups.Add(new Backup { Id = "save-and-run", Name = "local_d" }); });
        using var vm = new BackupsViewModel();
        var card = Assert.Single(vm.Cards);
        Assert.False(card.IsRunning); // Save and run: nothing set the card running

        vm.ShowServiceQueued(card.Id, "waiting for the background service to start it");
        Assert.True(card.IsRunning);
        Assert.True(card.IsQueued);
        Assert.False(card.IsActuallyRunning);
        Assert.Equal("Queued", card.LastRunLabel); Assert.StartsWith("Queued: waiting for the background service to start it.", card.LastRunDetail);

        vm.ShowServiceQueued(card.Id, ""); // accepted: the service's activity takes over
        Assert.False(card.IsQueued);
        Assert.Equal("Running…", card.LastRunLabel);

        vm.ShowServiceQueued("someone-else", "waiting"); // another backup's: nothing changes
        Assert.False(card.IsQueued);
    }
}
