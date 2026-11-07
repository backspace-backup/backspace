using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U127 (4 November 2026): exceptions that can never bring anything
/// back, because the folder above them is excluded whole.</summary>
public sealed class DeadExceptionTests
{
    [Fact]
    public void An_exception_under_a_folder_excluded_whole_is_reported_with_the_fix()
    {
        var dead = Assert.Single(FilterTranslator.DeadExceptions("cache/\n!cache/keep/"));
        Assert.Equal("!cache/keep/ can never apply: the backup never looks inside cache, which cache/ excludes. "
            + "To keep it, exclude what cache contains (cache/*) rather than the folder itself.", dead);
        // With wildcards: the Code Cache folder is excluded wherever it is.
        Assert.Single(FilterTranslator.DeadExceptions("?:/Users/*/AppData/**/Code Cache/\n!?:/Users/*/AppData/Roaming/App/Code Cache/keep/"));
    }

    [Theory]
    [InlineData("cache/*\n!cache/keep/")]            // the contents are excluded, not the folder
    [InlineData("*.tmp\n!important.tmp")]             // a file exception at the top
    [InlineData("!?:/Program Files/")]                // nothing above it
    [InlineData("logs/\n!other/keep/")]               // unrelated folder
    [InlineData("")]
    public void Exceptions_that_can_apply_are_not_reported(string rules)
        => Assert.Empty(FilterTranslator.DeadExceptions(rules));

    [Fact]
    public void Converted_Duplicacy_includes_apply()
    {
        // local_c's Chrome rules, converted (U151): Chrome is excluded except its extension settings.
        const string imported = """
            i:(?i)^Users/[^/]+/AppData/[^/]+/Google/Chrome/$
            i:(?i)^Users/[^/]+/AppData/[^/]+/Google/Chrome/User Data/$
            i:(?i)^Users/[^/]+/AppData/[^/]+/Google/Chrome/User Data/[^/]+/$
            i:(?i)^Users/[^/]+/AppData/[^/]+/Google/Chrome/User Data/[^/]+/Local Extension Settings/
            e:(?i)^Users/[^/]+/AppData/[^/]+/Google/Chrome/
            i:(?i)^Program Files[^/]*/$
            i:(?i)^Program Files[^/]*/[^/]+/$
            e:(?i)^Program Files[^/]*/
            """;
        Assert.Empty(FilterTranslator.DeadExceptions(DuplicacyFilterConverter.Convert(imported, new[] { @"C:\" })));
        // The owner's converted local_c and local_d (U151) have none either.
        foreach (var name in new[] { "local_c-after.txt", "local_d-after.txt" })
            Assert.Empty(FilterTranslator.DeadExceptions(System.IO.File.ReadAllText(
                System.IO.Path.Combine(System.AppContext.BaseDirectory, "Services", "Fixtures", "U151", name))));
    }
}
