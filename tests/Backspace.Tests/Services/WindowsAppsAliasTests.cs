using System;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// 4 November 2026, testing the owner's local_c: every run ended with 92
/// "unsupported file type" errors, all from Windows' app execution aliases in
/// AppData\Local\Microsoft\WindowsApps (wt.exe, winget.exe…), links the engine
/// can't store and no restore can use. Since U165 the rule is one of the
/// Essentials, ticked by default, and matches in every profile,
/// for a whole drive and for a profile folder alike.
/// </summary>
public sealed class WindowsAppsAliasTests
{
    [Theory]
    [InlineData("C:/Users/erwin/AppData/Local/Microsoft/WindowsApps/wt.exe")]
    [InlineData("C:/Users/erwin/AppData/Local/Microsoft/WindowsApps/Claude_pzs8sxrjxfjjc/claude.exe")]
    [InlineData("D:/Users/other/AppData/Local/Microsoft/WindowsApps/winget.exe")]
    public void App_execution_aliases_are_left_out_by_the_essentials(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var rules = FilterTranslator.CompileRules(RecommendedFilters.EssentialsText());
        Assert.True(FilterTranslator.Evaluate(rules, path).Excluded, path);
        // The rest of AppData stays in, and so does a copied profile deeper down.
        Assert.False(FilterTranslator.Evaluate(rules, path.Replace("Microsoft/WindowsApps", "Microsoft/Windows")).Excluded);
        Assert.False(FilterTranslator.Evaluate(rules, path.Replace(":/Users/", ":/Backups/Users/")).Excluded);
    }
}
