using System.Linq;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// 4 November 2026, testing the owner's local_c: every run ended with 92
/// "unsupported file type" errors, all from Windows' app execution aliases in
/// AppData\Local\Microsoft\WindowsApps (wt.exe, winget.exe…), links the engine
/// can't store and no restore can use. They are now always left out, for a
/// whole drive and for a profile folder alike.
/// </summary>
public sealed class WindowsAppsAliasTests
{
    [Theory]
    [InlineData(@"C:\", "Users/erwin/AppData/Local/Microsoft/WindowsApps/wt.exe")]
    [InlineData(@"C:\", "Users/erwin/AppData/Local/Microsoft/WindowsApps/Claude_pzs8sxrjxfjjc/claude.exe")]
    [InlineData(@"C:\Users\erwin", "AppData/Local/Microsoft/WindowsApps/winget.exe")]
    public void App_execution_aliases_are_always_left_out(string source, string path)
    {
        var rules = FilterTranslator.CompileRules(FilterTranslator.ScopeToSource(RecommendedFilters.WithAlwaysExcluded(""), source));
        Assert.True(FilterTranslator.Evaluate(rules, path).Excluded, path);
        // The rest of AppData stays in.
        Assert.False(FilterTranslator.Evaluate(rules, path.Replace("Microsoft/WindowsApps", "Microsoft/Windows")).Excluded);
    }

    [Fact]
    public void The_plan_binds_the_rule_to_the_source()
    {
        var plan = ResticBackrestEngine.BuildPlanFromBackup("plan", "repo", new Backspace.Models.Backup { FiltersText = "" }, @"C:\Users\erwin");
        // Restic gets it as "AppData/Local/Microsoft/WindowsApps/", proven to
        // exclude exactly what "C:/Users/erwin/**/AppData/..." does under the source (U133).
        var rule = Assert.Single(plan.IExcludes.Concat(plan.Excludes), p => p.EndsWith("AppData/Local/Microsoft/WindowsApps/")).ToLowerInvariant();
        Assert.True(ResticFilterReference.List([rule], @"c:\users\erwin\appdata\local\microsoft\windowsapps"));
        Assert.True(ResticFilterReference.List([rule], @"c:\users\erwin\x\appdata\local\microsoft\windowsapps\y"));
        Assert.False(ResticFilterReference.List([rule], @"c:\users\erwin\appdata\local\microsoft\windows"));
    }
}
