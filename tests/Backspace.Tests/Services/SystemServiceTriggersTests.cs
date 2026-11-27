using System;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U261 (owner, 26 November 2026: "If I'm logged in, I NEVER want to
/// see Backspace in the task manager if I have quit"; 27 November: "What did
/// we decide before?", B023: signing out ends a Quit). While quit, Windows
/// may start the service only at a sign-in, a sign-out or a start of Windows.</summary>
public sealed class SystemServiceTriggersTests
{
    /// <summary>Reads Winlogon's manifest on this computer: of all its events,
    /// the keywords chosen while quit select exactly 7001 (User Logon
    /// Notification) and 7002 (User Logoff Notification), not the events
    /// raised at lock, unlock, sleep or wake.</summary>
    [Fact]
    public void While_quit_only_Winlogon_sign_in_and_sign_out_events_start_it()
    {
        PlatformTest.WindowsOnly("reads Windows' Winlogon event manifest");
        var start = new ProcessStartInfo("wevtutil", "gp Microsoft-Windows-Winlogon /ge:true /gm:true /f:xml")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(start)!;
        var xml = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var events = XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "event")
            .Select(e => (Id: (string?)e.Attribute("value") ?? "", Keywords: Convert.ToUInt64(((string?)e.Attribute("keywords") ?? "0x0")[2..], 16)))
            .ToList();
        Assert.True(events.Count > 50, "Winlogon's manifest lists " + events.Count + " events");

        var signInOrOut = SystemServiceTriggers.SignInOrOut;
        Assert.Equal(SystemServiceTriggers.Providers[0], signInOrOut.Provider);
        var selected = events.Where(e => (e.Keywords & signInOrOut.KeywordAll) == signInOrOut.KeywordAll
            && (e.Keywords & signInOrOut.KeywordAny) != 0).Select(e => e.Id).Distinct().OrderBy(id => id).ToArray();
        Assert.Equal(["7001", "7002"], selected);
        // The negative case: the notification events Winlogon raises at lock and unlock exist and are left out.
        Assert.Contains(events, e => e.Id == "811");
        Assert.DoesNotContain("811", selected);

        // Kernel-Boot stays, unfiltered: a start of Windows ends a Quit.
        Assert.Equal([signInOrOut, new SystemServiceTriggers.Spec(SystemServiceTriggers.Providers[1])], SystemServiceTriggers.WhileQuit);
    }
}
