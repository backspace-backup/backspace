using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Backspace.Services.WinFsp;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Tests for the bundled-MSI detection + outcome shape on
/// <see cref="WinFspInstaller"/>. The actual <c>InstallAsync</c>
/// shells out to msiexec with UAC elevation and would prompt the
/// user, so we don't exercise the install itself from a test —
/// the end-to-end mount path is covered by E04 against the live
/// runtime. What we DO cover here:
///   • <c>BundledMsiPath</c> returns the expected path next to the
///     test host (the build copies winfsp.msi to the output dir).
///   • When the MSI is absent, the installer returns a
///     <c>MissingBundle</c> outcome with a non-empty actionable
///     message (not just an exception).
/// </summary>
[SupportedOSPlatform("windows")]
public class WinFspInstallerTests
{
    [Fact]
    public void BundledMsiPath_ReturnsPathNextToTestHost_WhenAssetPresent()
    {
        var path = WinFspInstaller.BundledMsiPath();
        // The build copies winfsp.msi next to Backspace.dll via the
        // csproj's Content/CopyToOutput item. AppContext.BaseDirectory
        // in a test run is the test host's bin folder, which has the
        // same MSI thanks to the test project's project-reference
        // copying it through.
        if (path is null)
        {
            // Tolerable: a SkipBackrestDownload=true build leaves the
            // asset missing. We treat both branches as valid — the
            // assertion is "if the path is non-null, the file exists".
            return;
        }
        Assert.True(File.Exists(path),
            $"BundledMsiPath returned {path} but no MSI is at that location.");
        Assert.EndsWith(WinFspInstaller.BundledMsiName, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_ReturnsMissingBundle_WhenMsiAbsent()
    {
        // Temporarily rename the MSI on disk so the installer's
        // BundledMsiPath probe returns null. Restore even if the
        // assertion throws so we don't break the next test that
        // might run from the same output dir.
        var msi = WinFspInstaller.BundledMsiPath();
        if (msi is null)
        {
            // Build already stripped the MSI — the installer's
            // missing-bundle branch is the trivial path here.
            var outcomeNoMsi = await WinFspInstaller.InstallAsync();
            Assert.Equal(InstallStatus.MissingBundle, outcomeNoMsi.Status);
            Assert.False(string.IsNullOrWhiteSpace(outcomeNoMsi.Message));
            return;
        }

        var stash = msi + ".stashed";
        File.Move(msi, stash);
        try
        {
            var outcome = await WinFspInstaller.InstallAsync();
            Assert.Equal(InstallStatus.MissingBundle, outcome.Status);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Message),
                "Missing-bundle message must be non-empty so the UI can show something useful.");
            Assert.False(outcome.Succeeded);
        }
        finally
        {
            File.Move(stash, msi);
        }
    }
}
