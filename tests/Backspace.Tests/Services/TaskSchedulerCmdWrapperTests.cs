using Backspace.Services.Platform;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins the exact cmd.exe-wrapper argument shape that the Windows task
/// scheduler implementation uses for every backup task. Quoting under
/// <c>cmd /S /C "..."</c> is tricky enough that a casual refactor could
/// break every scheduled task silently; these tests assert the literal
/// shape so a regression is loud at build time.
/// </summary>
public class TaskSchedulerCmdWrapperTests
{
    [Fact]
    public void BuildCmdWrapperArguments_producesIfExistFallbackShape()
    {
        var args = WindowsTaskScheduler.BuildCmdWrapperArguments(
            mainExe: @"C:\Users\me\Downloads\Backspace.exe",
            stubExe: @"C:\Users\me\AppData\Local\Programs\Backspace\Backspace-stub.exe",
            backupName: "documents");

        // Outer cmd /D /S /C "..." with each path/name doubled-up
        // (cmd's "" → " escape inside the /S /C string). Using
        // standard escapes here for clarity — \" is one quote char.
        var expected =
            "/D /S /C \"if exist \"\"C:\\Users\\me\\Downloads\\Backspace.exe\"\" " +
            "( \"\"C:\\Users\\me\\Downloads\\Backspace.exe\"\" --run \"\"documents\"\" ) " +
            "else ( \"\"C:\\Users\\me\\AppData\\Local\\Programs\\Backspace\\Backspace-stub.exe\"\" )\"";
        Assert.Equal(expected, args);
    }

    [Fact]
    public void BuildCmdWrapperArguments_handlesPathWithSpaces()
    {
        // Spaces are why we quote in the first place. Pin that the path
        // segments come out as a single quoted token, not split into
        // separate cmd arguments.
        var args = WindowsTaskScheduler.BuildCmdWrapperArguments(
            mainExe: @"C:\Program Files\Backspace\Backspace.exe",
            stubExe: @"C:\Users\me\AppData\Local\Programs\Backspace\Backspace-stub.exe",
            backupName: "my_backup");

        Assert.Contains("\"\"C:\\Program Files\\Backspace\\Backspace.exe\"\"", args);
        Assert.Contains("--run \"\"my_backup\"\"", args);
    }

    [Fact]
    public void BuildCmdWrapperArguments_PruneKindUsesPruneFlag()
    {
        // When the scheduled task fires the standalone prune entry,
        // the cmd wrapper has to invoke Backspace with `--prune`
        // (not `--run`). If this flag were wrong, the OS scheduler
        // would fire prune as a backup and the user's "Weekly prune"
        // would turn into "Weekly extra backup" — bad.
        var args = WindowsTaskScheduler.BuildCmdWrapperArguments(
            mainExe: @"C:\Users\me\Backspace.exe",
            stubExe: @"C:\Users\me\stub.exe",
            backupName: "docs",
            kind: ScheduledTaskKind.Prune);
        Assert.Contains("--prune \"\"docs\"\"", args);
        Assert.DoesNotContain("--run \"\"docs\"\"", args);
    }

    [Fact]
    public void BuildCmdWrapperArguments_CheckKindUsesCheckFlag()
    {
        var args = WindowsTaskScheduler.BuildCmdWrapperArguments(
            mainExe: @"C:\Users\me\Backspace.exe",
            stubExe: @"C:\Users\me\stub.exe",
            backupName: "docs",
            kind: ScheduledTaskKind.Check);
        Assert.Contains("--check \"\"docs\"\"", args);
        Assert.DoesNotContain("--run \"\"docs\"\"", args);
    }

    [Fact]
    public void BuildCmdWrapperArguments_BackupKindIsTheDefault()
    {
        // Sanity: omitting `kind` preserves --run behaviour. The
        // overload's default keeps every existing call site working.
        var args = WindowsTaskScheduler.BuildCmdWrapperArguments(
            mainExe: @"C:\Users\me\Backspace.exe",
            stubExe: @"C:\Users\me\stub.exe",
            backupName: "docs");
        Assert.Contains("--run \"\"docs\"\"", args);
    }
}
