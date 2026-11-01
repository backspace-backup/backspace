using System;
using System.ServiceProcess;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// The owner's tray app starts the background service for alerts while a
/// settings update may be starting it too. In the release VM check (31 October
/// 2026) the update lost that race, stopped before deleting its record, and
/// every later update was refused. Windows refuses the second start with an
/// InvalidOperationException; these replay that sequence.
/// </summary>
public sealed class ServiceStartRaceTests
{
    [Fact]
    public void Losing_the_start_race_to_another_starter_is_not_an_error()
    {
        var status = ServiceControllerStatus.Stopped; var starts = 0;
        ServiceRegistration.StartUnlessStarted(() => status, () =>
        {
            starts++;
            status = ServiceControllerStatus.StartPending; // the tray app got there first
            throw new InvalidOperationException("An instance of the service is already running.");
        });
        Assert.Equal(1, starts);
    }

    [Fact]
    public void A_start_that_really_fails_is_still_reported()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ServiceRegistration.StartUnlessStarted(
            () => ServiceControllerStatus.Stopped, () => throw new InvalidOperationException("The service is disabled.")));
        Assert.Contains("disabled", error.Message);
    }

    [Theory]
    [InlineData(ServiceControllerStatus.Running)]
    [InlineData(ServiceControllerStatus.StartPending)]
    public void A_running_or_starting_service_is_left_alone(ServiceControllerStatus current)
        => ServiceRegistration.StartUnlessStarted(() => current, () => throw new Exception("Must not start again"));

    [Fact]
    public void A_stopped_service_is_started_once()
    {
        var starts = 0;
        ServiceRegistration.StartUnlessStarted(() => ServiceControllerStatus.Stopped, () => starts++);
        Assert.Equal(1, starts);
    }
}
