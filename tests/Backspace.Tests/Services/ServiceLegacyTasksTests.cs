using System;
using Backspace.Services.Platform;
using Backspace.Services.Platform.Windows;
using Microsoft.Win32.TaskScheduler;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceLegacyTasksTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Exe = @"C:\Portable Backspace\Backspace.exe";
    private const string Stub = @"C:\Portable Backspace\Backspace.config\Backspace-stub.exe";

    [Theory]
    [InlineData("Backspace", ScheduledTaskKind.Backup, "Documents (Check)")]
    [InlineData("EDB", ScheduledTaskKind.Check, "A & B's documents")]
    [InlineData("Duplimate", ScheduledTaskKind.Prune, "Archive")]
    public void MigrationRecognizesTheExactOwnerAndLiteralWrapper(string prefix, ScheduledTaskKind kind, string name)
    {
        using var scheduler = new TaskService(); using var definition = scheduler.NewTask();
        definition.Principal.UserId = Owner; definition.Principal.LogonType = TaskLogonType.S4U;
        definition.Actions.Add(LegacyUserTasks.BuildExecAction(Exe, Stub, name, kind));
        var taskName = prefix + " — " + name + (kind == ScheduledTaskKind.Backup ? "" : " (" + kind + ")");
        Assert.True(ServiceLegacyTasks.Matches(taskName, definition, Exe, Owner, new[] { Stub }));
        Assert.False(ServiceLegacyTasks.Matches(taskName, definition, @"C:\Other\Backspace.exe", Owner, new[] { Stub }));
        Assert.False(ServiceLegacyTasks.Matches(taskName, definition, Exe, "S-1-5-21-100-200-300-1002", new[] { Stub }));
        Assert.False(ServiceLegacyTasks.Matches("Other — " + name, definition, Exe, Owner, new[] { Stub }));
        Assert.False(ServiceLegacyTasks.Matches(taskName, definition, Exe, Owner, new[] { @"C:\Other\Backspace-stub.exe" }));
        ((ExecAction)definition.Actions[0]).Arguments += " extra";
        Assert.False(ServiceLegacyTasks.Matches(taskName, definition, Exe, Owner, new[] { Stub }));
    }

    [Fact]
    public void DirectActionStillRequiresTheExactArgumentsAndOnlyOneAction()
    {
        using var scheduler = new TaskService(); using var definition = scheduler.NewTask();
        definition.Principal.UserId = Owner; definition.Principal.LogonType = TaskLogonType.S4U;
        definition.Actions.Add(new ExecAction(Exe, "--run \"Documents\"", @"C:\Portable Backspace"));
        Assert.True(ServiceLegacyTasks.Matches("Backspace — Documents", definition, Exe, Owner, Array.Empty<string>()));
        definition.Actions.Add(new ExecAction("cmd.exe", "/c something-else"));
        Assert.False(ServiceLegacyTasks.Matches("Backspace — Documents", definition, Exe, Owner, Array.Empty<string>()));
    }

    [Fact]
    public void OriginalCmdWrapperIsRecognizedWithoutExecutingOrLooselyParsingIt()
    {
        using var scheduler = new TaskService(); using var definition = scheduler.NewTask();
        definition.Principal.UserId = Owner; definition.Principal.LogonType = TaskLogonType.S4U;
        var arguments = "/D /S /C \"if exist \"\"" + Exe + "\"\" ( \"\"" + Exe + "\"\" --run \"\"Documents\"\" ) else ( \"\"" + Stub + "\"\" )\"";
        definition.Actions.Add(new ExecAction("cmd.exe", arguments, @"C:\Portable Backspace"));
        Assert.True(ServiceLegacyTasks.Matches("Backspace — Documents", definition, Exe, Owner, new[] { Stub }));
        ((ExecAction)definition.Actions[0]).Arguments = arguments + " & exit 0";
        Assert.False(ServiceLegacyTasks.Matches("Backspace — Documents", definition, Exe, Owner, new[] { Stub }));
    }
}
