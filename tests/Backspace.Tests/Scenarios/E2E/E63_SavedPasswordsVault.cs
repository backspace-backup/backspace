using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>Shared set-up for the saved-passwords scenarios (U153): each
/// computer is a <see cref="SecretsStore"/> on the configuration's vault.bin
/// with its own key folder (its "%LOCALAPPDATA%\Backspace"); the real engine
/// reads credentials through it; the password window is the real
/// <see cref="VaultPasswordWindow"/>, typed into and screenshotted.</summary>
internal sealed class VaultScenario : IAsyncDisposable
{
    private readonly ScenarioContext _ctx;
    private readonly RealEngineFixture _fixture;
    private readonly SecretsStore _suiteSecrets = ServiceLocator.Secrets;
    private readonly IVaultPrompt? _suitePrompt = VaultGate.Prompt;
    private readonly string _held = AppPaths.VaultFile + ".held-" + Guid.NewGuid().ToString("N");
    private readonly string _root;

    public VaultScenario(ScenarioContext ctx, RealEngineFixture fixture)
    {
        _ctx = ctx;
        _fixture = fixture;
        _root = Path.Combine(Path.GetTempPath(), "backspace-vault-scenario-" + Guid.NewGuid().ToString("N"));
        // The suite's own vault is set aside: this scenario starts as a
        // person who never saved a credential.
        File.Move(AppPaths.VaultFile, _held);
        if (File.Exists(AppPaths.VaultFile + ".bak")) File.Move(AppPaths.VaultFile + ".bak", _held + ".bak");
    }

    public string MachineRoot(string computer) => Path.Combine(_root, computer);

    /// <summary>Opens Backspace "on" a computer: its own key folder.</summary>
    public SecretsStore UseComputer(string computer)
    {
        var store = new SecretsStore(AppPaths.VaultFile,
            new VaultKeyCache(Path.Combine(MachineRoot(computer), "keys"), "user", new DpapiSecretsEncryption()));
        Swap(store);
        return store;
    }

    private void Swap(SecretsStore store)
    {
        typeof(ServiceLocator).GetProperty(nameof(ServiceLocator.Secrets))!.SetMethod!.Invoke(null, new object[] { store });
        var engine = new ResticBackrestEngine(_fixture.Sidecar, store);
        typeof(ServiceLocator).GetProperty(nameof(ServiceLocator.Engine))!.SetMethod!.Invoke(null, new object[] { engine });
        ServiceLocator.RebuildEngineDependents();
    }

    public ValueTask DisposeAsync()
    {
        VaultGate.Prompt = _suitePrompt;
        typeof(ServiceLocator).GetProperty(nameof(ServiceLocator.Secrets))!.SetMethod!.Invoke(null, new object[] { _suiteSecrets });
        typeof(ServiceLocator).GetProperty(nameof(ServiceLocator.Engine))!.SetMethod!.Invoke(null, new object[] { _fixture.Engine });
        ServiceLocator.RebuildEngineDependents();
        foreach (var file in Directory.GetFiles(AppPaths.ConfigRoot, "vault.bin*").Where(f => !f.Contains(".held-")))
            File.Delete(file);
        foreach (var file in Directory.GetFiles(AppPaths.ConfigRoot, "secrets.bin*")) File.Delete(file);
        File.Delete(VaultConversion.RecordPath(AppPaths.ConfigRoot));
        File.Move(_held, AppPaths.VaultFile);
        if (File.Exists(_held + ".bak")) File.Move(_held + ".bak", AppPaths.VaultFile + ".bak");
        _suiteSecrets.Refresh();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    /// <summary>The real password window, typed into like a person would:
    /// each answer is tried in turn (null cancels); each step is captured.</summary>
    public sealed class WindowPrompt(ScenarioContext ctx, string label, params string?[] answers) : IVaultPrompt
    {
        private readonly Queue<string?> _answers = new(answers);
        public List<VaultPromptMode> Asked { get; } = new();
        public Task<bool> AskAsync(VaultPromptRequest request)
            => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => AskOnUiThreadAsync(request));

        private async Task<bool> AskOnUiThreadAsync(VaultPromptRequest request)
        {
            Asked.Add(request.Mode);
            var window = new VaultPasswordWindow(request);
            ctx.Driver.Show(window);
            ctx.Driver.Pump();
            ctx.Snapshot(window, $"{label} - {request.Mode.ToString().ToLowerInvariant()} window",
                PromptNote(request.Mode));
            var attempt = 0;
            while (_answers.Count > 0)
            {
                var answer = _answers.Dequeue();
                if (answer is null) { window.Close(); return false; }
                window.EnterPassword(answer);
                ctx.Driver.Pump();
                await window.ConfirmAsync();
                ctx.Driver.Pump();
                if (window.Completed) return true;
                attempt++;
                ctx.Snapshot(window, $"{label} - {request.Mode.ToString().ToLowerInvariant()} refused {attempt}",
                    "The password was refused and explained inside the window; nothing was written, the window stays open for another try: " + window.Error);
            }
            window.Close();
            return false;
        }

        private static string PromptNote(VaultPromptMode mode) => mode switch
        {
            VaultPromptMode.Create => "First credential: Backspace asks to choose a password for the saved passwords (none was asked before). With both boxes empty, the window warns that saved passwords would only be obscured.",
            VaultPromptMode.Unlock => "A computer without the key: the password is asked once, only because an action needs a saved password.",
            VaultPromptMode.Convert => "The one-time move of the earlier per-computer files: choose a password; everything is moved and checked before any old file goes.",
            _ => mode.ToString(),
        };
    }

    public static Destination NewDestination(string folder, string storagePassword, out DestinationEditorViewModel editor)
    {
        editor = new DestinationEditorViewModel(new Destination
        {
            Name = "Vault scenario folder", Kind = DestinationKind.LocalFolder, PathOrSubpath = folder, Encrypted = true,
        }, isNew: true) { Kind = DestinationKind.LocalFolder, StoragePasswordInput = storagePassword, PathOrSubpath = folder };
        return editor.Working;
    }

    public static Backup NewBackup(string[] sources, Destination destination)
    {
        var backup = new Backup
        {
            Name = "Documents (saved passwords)",
            UseVss = false, VssDisabledAcknowledged = true,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(), Threads = 2,
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } },
        };
        foreach (var s in sources) backup.SourcePaths.Add(s);
        return backup;
    }

    /// <summary>Run now, through the card's command: the same gate a click hits.</summary>
    public static async Task<RunRecord?> RunNowAsync(ScenarioContext ctx, BackupsView view, Backup backup)
    {
        ctx.Driver.Pump();
        var vm = (BackupsViewModel)view.DataContext!;
        var card = vm.Cards.Single(c => c.Snapshot.Id == backup.Id);
        var before = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).Count;
        await vm.RunNowCommand.ExecuteAsync(card);
        ctx.Driver.Pump();
        var history = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id);
        return history.Count > before ? history.OrderByDescending(r => r.StartedUtc).First() : null;
    }

    /// <summary>Every byte of every file under <paramref name="root"/>, searched
    /// for <paramref name="secret"/> as UTF-8 and UTF-16 (JSON-escaped forms
    /// included). Returns the files where it was found.</summary>
    public static List<string> FilesContaining(string root, string secret)
    {
        var needles = new[] { Encoding.UTF8.GetBytes(secret), Encoding.Unicode.GetBytes(secret) };
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (IOException)
            {
                try { using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); bytes = new byte[s.Length]; s.ReadExactly(bytes); }
                // An empty lock file held exclusively by a running engine or
                // Backspace process holds nothing; any other unreadable file fails the scan.
                catch (IOException) when (file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) && new FileInfo(file).Length == 0) { continue; }
            }
            if (needles.Any(n => bytes.AsSpan().IndexOf(n) >= 0)) found.Add(file);
        }
        return found;
    }
}

/// <summary>
/// U153: the first credential asks for a password, the backup runs, and no
/// storage password is readable anywhere under Backspace.config (checked
/// byte by byte, with a positive control on this computer's engine files).
/// Then a second computer: the banner says the passwords are locked, Run now
/// asks once, a wrong password is refused, the right one runs against the
/// same repository and the earlier run is in the history.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E63_SavedPasswordsVault : BackupE2EBase
{
    public E63_SavedPasswordsVault(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E63-saved-passwords-vault";

    [AvaloniaFact]
    public Task Run_E63() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        await using var scenario = new VaultScenario(ctx, fixture);
        var storagePassword = "E63-storage-" + Guid.NewGuid().ToString("N");
        const string vaultPassword = "travel with me";
        var sources = FixtureFactory.Generate(ctx.SourcesDir, FixtureSize.Smoke, seed: 63);
        var folder = ctx.MakeLocalDestination("vault-repo");

        // Computer A: nothing saved yet; the first credential asks.
        var a = scenario.UseComputer("computer-a");
        Assert.Equal(VaultState.NoVault, a.Refresh());
        var create = new VaultScenario.WindowPrompt(ctx, "computer A", vaultPassword);
        VaultGate.Prompt = create;
        var destination = VaultScenario.NewDestination(folder, storagePassword, out var editor);
        using (editor)
        {
            var saved = await editor.SaveAsync();
            Assert.NotNull(saved);
        }
        Assert.Equal(new[] { VaultPromptMode.Create }, create.Asked);
        var backup = VaultScenario.NewBackup(sources, destination);
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(destination); cfg.Backups.Add(backup); });

        var viewA = new BackupsView { DataContext = new BackupsViewModel() };
        var hostA = ctx.Driver.Show(new Window { Width = 1100, Height = 760, Content = viewA });
        var first = await VaultScenario.RunNowAsync(ctx, viewA, backup);
        Assert.NotNull(first);
        Assert.True(first!.Status is BackupRunStatus.Success or BackupRunStatus.Warning, first.Summary);
        Assert.Single(create.Asked);
        ctx.Snapshot(hostA, "computer A - backup done, no banner",
            "After choosing the password, the backup ran on computer A. No saved-passwords banner: the vault is open here.");

        // Falsifiable: the storage password is nowhere under Backspace.config,
        // byte by byte, while this computer's engine files do hold it (the
        // scan finds it there, so it would find it in the folder too).
        var inConfig = VaultScenario.FilesContaining(AppPaths.ConfigRoot, storagePassword);
        Assert.True(inConfig.Count == 0, "Found the storage password in: " + string.Join(", ", inConfig));
        Assert.NotEmpty(VaultScenario.FilesContaining(fixture.Sidecar.DataDir, storagePassword));
        Assert.True(File.Exists(AppPaths.VaultFile));
        ctx.Report.RecordNote("byte scan", $"{AppPaths.ConfigRoot}: 0 files contain the storage password; positive control: it is found in this computer engine folder.");
        hostA.Close();

        // Computer B: the same folder, no key yet.
        var b = scenario.UseComputer("computer-b");
        Assert.Equal(VaultState.Locked, b.Refresh());
        var unlock = new VaultScenario.WindowPrompt(ctx, "computer B", "not the password", vaultPassword);
        VaultGate.Prompt = unlock;
        var viewB = new BackupsView { DataContext = new BackupsViewModel() };
        var hostB = ctx.Driver.Show(new Window { Width = 1100, Height = 760, Content = viewB });
        Assert.True(ctx.Driver.WaitFor(() => ((BackupsViewModel)viewB.DataContext!).Vault.IsVisible), "The locked banner must show.");
        ctx.Snapshot(hostB, "computer B - locked banner",
            "A new computer: a banner (not a dialog) says the saved passwords are locked here, with Enter password. Nothing was asked at start.");
        Assert.Empty(unlock.Asked);

        var second = await VaultScenario.RunNowAsync(ctx, viewB, backup);
        Assert.Equal(new[] { VaultPromptMode.Unlock }, unlock.Asked);
        Assert.NotNull(second);
        Assert.True(second!.Status is BackupRunStatus.Success or BackupRunStatus.Warning, second.Summary);
        Assert.True(ctx.Driver.WaitFor(() => !((BackupsViewModel)viewB.DataContext!).Vault.IsVisible), "The banner must go once unlocked.");
        ctx.Snapshot(hostB, "computer B - after unlock and backup", "The right password unlocked computer B; the backup ran against the existing repository and the banner is gone.");

        var history = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).Where(r => r.Status != BackupRunStatus.Running).ToList();
        Assert.True(history.Count >= 2, "Both runs must be in the history.");
        var historyWindow = ctx.Driver.Show(new BackupHistoryWindow { DataContext = new BackupHistoryViewModel(backup, history, checks: false), Width = 900, Height = 560 });
        ctx.Driver.Pump();
        ctx.Snapshot(historyWindow, "computer B - history shows the earlier run",
            "Run history on computer B: the run made on computer A is listed under the new one.");
        historyWindow.Close();
        hostB.Close();
        Assert.Equal(VaultState.Unlocked, scenario.UseComputer("computer-b").Refresh());
    }
}

/// <summary>U153: a vault saved without a password opens on another computer
/// by itself: Run now asks nothing.</summary>
[Collection(ScenarioCollection.Name)]
public sealed class E64_NoPasswordNewComputer : BackupE2EBase
{
    public E64_NoPasswordNewComputer(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E64-no-password-new-computer";

    [AvaloniaFact]
    public Task Run_E64() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        await using var scenario = new VaultScenario(ctx, fixture);
        var sources = FixtureFactory.Generate(ctx.SourcesDir, FixtureSize.Smoke, seed: 64);
        var folder = ctx.MakeLocalDestination("no-password-repo");
        scenario.UseComputer("computer-a");
        var create = new VaultScenario.WindowPrompt(ctx, "computer A", "");
        VaultGate.Prompt = create;
        var destination = VaultScenario.NewDestination(folder, "E64-storage-" + Guid.NewGuid().ToString("N"), out var editor);
        using (editor) Assert.NotNull(await editor.SaveAsync());
        Assert.True(ServiceLocator.Secrets.NoPassword);
        var backup = VaultScenario.NewBackup(sources, destination);
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(destination); cfg.Backups.Add(backup); });
        var viewA = new BackupsView { DataContext = new BackupsViewModel() };
        var hostA = ctx.Driver.Show(new Window { Width = 1100, Height = 760, Content = viewA });
        Assert.True((await VaultScenario.RunNowAsync(ctx, viewA, backup))!.Status is BackupRunStatus.Success or BackupRunStatus.Warning);
        hostA.Close();

        scenario.UseComputer("computer-b");
        var never = new VaultScenario.WindowPrompt(ctx, "computer B");
        VaultGate.Prompt = never;
        var viewB = new BackupsView { DataContext = new BackupsViewModel() };
        var hostB = ctx.Driver.Show(new Window { Width = 1100, Height = 760, Content = viewB });
        var second = await VaultScenario.RunNowAsync(ctx, viewB, backup);
        Assert.Empty(never.Asked);
        Assert.NotNull(second);
        Assert.True(second!.Status is BackupRunStatus.Success or BackupRunStatus.Warning, second.Summary);
        Assert.False(((BackupsViewModel)viewB.DataContext!).Vault.IsVisible);
        ctx.Snapshot(hostB, "computer B - ran without any question",
            "Saved without a password on computer A: on computer B the backup ran at once, with no banner and no password window.");
        hostB.Close();
    }
}

/// <summary>U153: an existing install (a Windows-protected secrets.bin and the
/// engine's folder inside Backspace.config) is moved once: the banner, the
/// password window, then a backup against the existing repository; the old
/// engine folder is on this computer and the old file goes after that run.</summary>
[Collection(ScenarioCollection.Name)]
public sealed class E65_ConversionFromEarlierFiles : BackupE2EBase
{
    public E65_ConversionFromEarlierFiles(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E65-conversion-from-earlier-files";

    [AvaloniaFact]
    public Task Run_E65() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        await using var scenario = new VaultScenario(ctx, fixture);
        var storagePassword = "E65-storage-" + Guid.NewGuid().ToString("N");
        var sources = FixtureFactory.Generate(ctx.SourcesDir, FixtureSize.Smoke, seed: 65);
        var folder = ctx.MakeLocalDestination("converted-repo");

        // The earlier version's files: a DPAPI secrets.bin beside config.json
        // and Backrest's folder inside Backspace.config.
        var destination = new Destination
        {
            Name = "Earlier folder", Kind = DestinationKind.LocalFolder, PathOrSubpath = folder, Encrypted = true,
            StoragePasswordRef = "dest:e65:storagepw",
        };
        new LegacySecretsVault(new DpapiSecretsEncryption(), AppPaths.LegacySecretsFile).Set(destination.StoragePasswordRef, storagePassword);
        var oldBackrest = Path.Combine(AppPaths.ConfigRoot, "backrest");
        Directory.CreateDirectory(oldBackrest);
        File.WriteAllText(Path.Combine(oldBackrest, "config.json"), "{\"modno\":1,\"repos\":[{\"id\":\"e65\",\"password\":\"" + storagePassword + "\"}]}");
        var backup = VaultScenario.NewBackup(sources, destination);
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(destination); cfg.Backups.Add(backup); });
        var machine = scenario.MachineRoot("this-computer");
        Environment.SetEnvironmentVariable(EngineWorkPaths.MachineRootVariable, machine);
        try
        {
            var secrets = scenario.UseComputer("this-computer");
            Assert.Equal(VaultState.NoVault, secrets.Refresh());
            Assert.True(secrets.ConversionPending);
            var convert = new VaultScenario.WindowPrompt(ctx, "earlier install", "moved once");
            VaultGate.Prompt = convert;

            var view = new BackupsView { DataContext = new BackupsViewModel() };
            var host = ctx.Driver.Show(new Window { Width = 1100, Height = 760, Content = view });
            Assert.True(ctx.Driver.WaitFor(() => ((BackupsViewModel)view.DataContext!).Vault.IsVisible, 15000),
                $"banner: '{((BackupsViewModel)view.DataContext!).Vault.Headline}' state={secrets.Refresh()} pending={secrets.ConversionPending}");
            ctx.Snapshot(host, "earlier install - banner before the move",
                "An install from before the portable vault: the banner asks to choose a password so the saved passwords can travel. Backups wait meanwhile.");

            // What the app does at start: ask once.
            await VaultStartup.RunAsync(secrets, askToConvert: true);
            Assert.Equal(new[] { VaultPromptMode.Convert }, convert.Asked);
            Assert.True(File.Exists(AppPaths.VaultFile));
            Assert.Equal(storagePassword, secrets.Get(destination.StoragePasswordRef));
            Assert.Equal("awaiting-first-run", VaultConversion.Read(AppPaths.ConfigRoot)!.Stage);
            Assert.False(Directory.Exists(oldBackrest));
            Assert.True(File.Exists(Path.Combine(EngineWorkPaths.RootFor(AppPaths.ConfigRoot), "backrest", "config.json")));
            Assert.True(File.Exists(AppPaths.LegacySecretsFile), "The earlier file stays until a run used the new vault.");

            var run = await VaultScenario.RunNowAsync(ctx, view, backup);
            Assert.NotNull(run);
            Assert.True(run!.Status is BackupRunStatus.Success or BackupRunStatus.Warning, run.Summary);
            Assert.True(ctx.Driver.WaitFor(() => !((BackupsViewModel)view.DataContext!).Vault.IsVisible));
            ctx.Snapshot(host, "earlier install - backup after the move",
                "After the move the backup ran with the moved storage password, against the repository it already had.");

            // The next start finishes: the earlier file goes after that successful run.
            await VaultStartup.RunAsync(secrets, askToConvert: true);
            Assert.False(File.Exists(AppPaths.LegacySecretsFile));
            Assert.Null(VaultConversion.Read(AppPaths.ConfigRoot));
            Assert.Single(convert.Asked);
            var leftovers = VaultScenario.FilesContaining(AppPaths.ConfigRoot, storagePassword);
            Assert.True(leftovers.Count == 0, "Found the storage password in: " + string.Join(", ", leftovers));
            host.Close();

            var settings = new SettingsView { DataContext = new SettingsViewModel() };
            var settingsHost = ctx.Driver.Show(new Window { Width = 1000, Height = 900, Content = settings });
            settings.FindControl<Expander>("SavedPasswordsSection")!.IsExpanded = true;
            ctx.Driver.Pump();
            ctx.Snapshot(settingsHost, "settings - saved passwords section",
                "Settings → Saved passwords after the move: open on this computer, protected by the password, with Change password.");
            settingsHost.Close();
        }
        finally { Environment.SetEnvironmentVariable(EngineWorkPaths.MachineRootVariable, null); }
    }
}
