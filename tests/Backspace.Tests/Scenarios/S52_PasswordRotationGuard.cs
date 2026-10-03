using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S52_PasswordRotationGuard : ScenarioBase
{
    protected override string ScenarioName => "S52-password-rotation-close-guard";
    [AvaloniaFact] public Task Run_S52() => Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var original = ServiceLocator.Engine;
        var replacement = DispatchProxy.Create<IBackupEngine, PasswordMutationProxy>();
        var mutation = (PasswordMutationProxy)(object)replacement;
        var engineProperty = typeof(ServiceLocator).GetProperty(nameof(ServiceLocator.Engine))!;
        var owner = new Window { Width = 720, Height = 480, Content = new TextBlock { Text = "Password safety proof" } };
        ctx.Driver.Show(owner);
        var dialog = new ChangeStoragePasswordWindow(new Destination { Name = "Test archive" }, "default", "old-test-password");
        try
        {
            engineProperty.SetMethod!.Invoke(null, new object[] { replacement });
            var result = dialog.ShowDialog<bool>(owner);
            ctx.Driver.Pump();
            dialog.FindControl<TextBox>("NewPasswordBox")!.Text = "new-test-password";
            dialog.FindControl<TextBox>("ConfirmPasswordBox")!.Text = "new-test-password";
            Assert.True(ctx.Driver.ClickButton(dialog, "Change password"));
            await mutation.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            ctx.Driver.Pump();
            Assert.False(dialog.FindControl<Button>("ConfirmButton")!.IsEnabled);

            dialog.Close(); // actual window close event while irreversible work is pending
            ctx.Driver.Pump();
            Assert.True(dialog.IsVisible);
            Assert.False(result.IsCompleted);
            Assert.True(mutation.Token.IsCancellationRequested);
            Assert.Contains("finish and save", dialog.FindControl<TextBlock>("StatusInfo")!.Text);
            ctx.Snapshot(dialog, "close waits for saved password",
                "Closing during rotation leaves the dialog open, disables repeated actions, and explains that the new password must be saved before it can close.");

            mutation.Finished.SetResult((0, "Credential committed safely."));
            Assert.True(await result.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("new-test-password", dialog.NewPassword);
            Assert.False(dialog.IsVisible);
        }
        finally
        {
            mutation.Finished.TrySetResult((0, "Fixture cleanup."));
            engineProperty.SetMethod!.Invoke(null, new object[] { original });
            dialog.Close();
            owner.Close();
        }
    }

    public class PasswordMutationProxy : DispatchProxy
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<(int, string)> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(IBackupEngine.ChangeStoragePasswordAsync))
                throw new InvalidOperationException("Unexpected fixture call: " + method.Name);
            Token = (CancellationToken)args![^1]!;
            Started.TrySetResult();
            // Deliberately ignore cancellation after the mutation begins, just
            // like production key rotation. Close must await this completion.
            return Finished.Task;
        }
    }
}
