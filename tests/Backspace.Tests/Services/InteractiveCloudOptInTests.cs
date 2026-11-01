using System;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class InteractiveCloudOptInTests
{
    [Theory]
    [InlineData(null, null, false)] [InlineData("", null, false)]
    [InlineData("true", null, false)] [InlineData("1", null, true)]
    [InlineData("1", "1", false)] [InlineData(null, "0", false)]
    public void MissingCredentialsNeverOpenConsentWithoutExplicitOptIn(string? optIn, string? noninteractive, bool expected)
    {
        var beforeOptIn = Environment.GetEnvironmentVariable("BACKSPACE_TEST_INTERACTIVE");
        var beforeNoninteractive = Environment.GetEnvironmentVariable("BACKSPACE_E2E_NONINTERACTIVE");
        try
        {
            Environment.SetEnvironmentVariable("BACKSPACE_TEST_INTERACTIVE", optIn);
            Environment.SetEnvironmentVariable("BACKSPACE_E2E_NONINTERACTIVE", noninteractive);
            Assert.Equal(expected, InteractiveTokenAcquirer.CanAcquireInteractively);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKSPACE_TEST_INTERACTIVE", beforeOptIn);
            Environment.SetEnvironmentVariable("BACKSPACE_E2E_NONINTERACTIVE", beforeNoninteractive);
        }
    }
}
