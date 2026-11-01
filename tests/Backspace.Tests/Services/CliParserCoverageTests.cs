using System;
using System.Reflection;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class CliParserCoverageTests
{
    [Theory]
    [InlineData("--not-a-backspace-option")]
    [InlineData("unknown-positional-argument")]
    [InlineData("--unknown=value")]
    [InlineData("--run")]
    [InlineData("--prune")]
    [InlineData("--check")]
    [InlineData("--migrate")]
    [InlineData("--restore-prompt")]
    public void Invalid_or_missing_required_argument_selects_usage_error_without_GUI(string argument)
        => Assert.Equal("UsageError", Mode(Parse(argument)));

    [Theory]
    [InlineData("--typo", "--run", "fixture")]
    [InlineData("--run", "fixture", "--typo")]
    [InlineData("--prune", "fixture", "extra")]
    [InlineData("--check", "fixture", "extra")]
    [InlineData("--migrate", "fixture", "extra")]
    [InlineData("--restore-prompt", "fixture", "extra")]
    [InlineData("--run-all", "extra", null)]
    [InlineData("--help", "extra", null)]
    [InlineData("--version", "extra", null)]
    [InlineData("--migrate", "", null)]
    public void Mixed_unknown_trailing_or_missing_value_arguments_fail_before_restore_state_changes(string first, string second, string? third)
    {
        var original = Backspace.Program.PendingRestorePath;
        var parsed = third is null ? Parse(first, second) : Parse(first, second, third);
        Assert.Equal("UsageError", Mode(parsed));
        Assert.Equal(original, Backspace.Program.PendingRestorePath);
    }

    [Theory]
    [InlineData("--help", "Help")]
    [InlineData("-h", "Help")]
    [InlineData("/?", "Help")]
    [InlineData("--version", "Version")]
    [InlineData("-v", "Version")]
    [InlineData("--run-all", "RunAll")]
    public void Supported_no_value_commands_keep_their_mode(string argument, string mode)
        => Assert.Equal(mode, Mode(Parse(argument)));

    [Theory]
    [InlineData("--run", "RunOne", "BackupName")]
    [InlineData("--prune", "PruneOne", "BackupName")]
    [InlineData("--check", "CheckOne", "BackupName")]
    [InlineData("--migrate", "Migrate", "Path")]
    public void Supported_value_commands_keep_their_argument(string argument, string mode, string property)
    {
        var parsed = Parse(argument, "fixture with spaces");
        Assert.Equal(mode, Mode(parsed));
        Assert.Equal("fixture with spaces", parsed.GetType().GetProperty(property)!.GetValue(parsed));
    }

    [Theory]
    [InlineData("--run", "-h", "RunOne")]
    [InlineData("--prune", "--archive", "PruneOne")]
    [InlineData("--check", "--help", "CheckOne")]
    public void Existing_option_shaped_backup_names_are_preserved_as_positional_data(string command, string name, string mode)
    {
        Assert.True(Backspace.Models.Backup.IsValidName(name));
        var parsed = Parse(command, name);
        Assert.Equal(mode, Mode(parsed));
        Assert.Equal(name, parsed.GetType().GetProperty("BackupName")!.GetValue(parsed));
    }

    [Fact]
    public void Empty_arguments_restore_prompt_and_elevated_helper_keep_supported_behavior()
    {
        Assert.Equal("Gui", Mode(Parse()));
        Assert.Equal("VssElevatedHelper", Mode(Parse(VssElevationCoordinator.HelperCliArgument)));
        Assert.Equal("VssElevatedHelper", Mode(Parse(VssElevationCoordinator.HelperCliArgument,
            "--result-file", "fixture-result.json", "--parent-pid", "123")));
        var original = Backspace.Program.PendingRestorePath;
        try
        {
            Assert.Equal("Gui", Mode(Parse("--restore-prompt", @"C:\fixture\original.txt")));
            Assert.Equal(@"C:\fixture\original.txt", Backspace.Program.PendingRestorePath);
        }
        finally { Backspace.Program.PendingRestorePath = original; }
    }

    private static object Parse(params string[] args) => typeof(Backspace.Program)
        .GetMethod("ParseMode", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, new object[] { args })!;
    private static string Mode(object parsed) => parsed.GetType().GetProperty("Mode")!.GetValue(parsed)!.ToString()!;
}
