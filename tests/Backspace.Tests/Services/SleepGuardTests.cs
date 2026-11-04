using System;
using System.Runtime.InteropServices;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U132 (4 November 2026): the PC went into standby, then down,
/// during a backup in the night. While a backup runs, Windows is now asked to
/// stay on.</summary>
public sealed class SleepGuardTests
{
    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, out uint output, uint outputLength);

    private static uint SystemExecutionState()
    {
        Assert.Equal(0u, CallNtPowerInformation(16, IntPtr.Zero, 0, out var state, sizeof(uint)));
        return state;
    }

    [Fact]
    public void While_a_backup_runs_Windows_is_asked_to_stay_on_and_released_after()
    {
        Assert.False(SleepGuard.Holding);
        using (SleepGuard.Hold())
        using (SleepGuard.Hold()) // two backups at once share it
        {
            Assert.True(SleepGuard.Holding);
            if (OperatingSystem.IsWindows())
                Assert.NotEqual(0u, SystemExecutionState() & 0x1); // ES_SYSTEM_REQUIRED
        }
        Assert.False(SleepGuard.Holding);
    }
}
