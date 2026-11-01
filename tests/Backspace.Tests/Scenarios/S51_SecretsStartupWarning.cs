using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Services;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S51_SecretsStartupWarning : ScenarioBase
{
    protected override string ScenarioName => "S51-secrets-startup-warning";
    [AvaloniaFact] public Task Run_S51() => Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        File.WriteAllBytes(AppPaths.SecretsFile, bytes);
        var store = new SecretsStore();
        Assert.Null(store.LoadErrorMessage);
        var warning = await SecretsStartupWarning.CheckAsync(store);
        Assert.NotNull(warning);
        Assert.Equal(bytes, File.ReadAllBytes(AppPaths.SecretsFile));
        Assert.NotNull(await SecretsStartupWarning.CheckAsync(new SecretsStore()));
        var owner = ctx.Driver.Show(new Window { Width = 900, Height = 650 });
        var acknowledged = DestructiveConfirmWindow.ShowInfoAsync(owner, warning.Title, warning.Message);
        ctx.Driver.Pump();
        var dialog = owner.OwnedWindows.OfType<DestructiveConfirmWindow>().Single();
        Assert.Contains("preserved", dialog.FindControl<TextBlock>("SubtitleBlock")!.Text);
        ctx.Snapshot(dialog, "saved-tokens-unreadable-at-startup",
            "The warning asks the user to sign in and check access before replacing valid passwords. The encrypted file is preserved; OK acknowledges without changing it. Rendering uses a disposable unreadable vault, not a real Windows logon transition.");
        Assert.True(ctx.Driver.ClickButton(dialog, "OK"));
        Assert.True(ctx.Driver.WaitFor(() => acknowledged.IsCompleted), "The OK button must acknowledge and close the startup warning.");
        Assert.True(await acknowledged);
        Assert.Equal(bytes, File.ReadAllBytes(store.PreservedCorruptPath!));
        owner.Close();
    }
}
