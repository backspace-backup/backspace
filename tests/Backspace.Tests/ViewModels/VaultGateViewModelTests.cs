using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>
/// U153: the vault password is asked only when a credential is about to be
/// saved (or read, on a new computer). Cancelling that question saves
/// nothing: no vault is created, no credential and no reference to one is
/// written, and nothing starts.
/// </summary>
public sealed class VaultGateViewModelTests
{
    /// <summary>Answers the password window like a person would.</summary>
    internal sealed class ScriptedPrompt(params string?[] answers) : IVaultPrompt
    {
        private readonly Queue<string?> _answers = new(answers);
        public List<VaultPromptMode> Asked { get; } = new();
        public List<string?> Errors { get; } = new();
        public async Task<bool> AskAsync(VaultPromptRequest request)
        {
            Asked.Add(request.Mode);
            while (_answers.Count > 0)
            {
                var answer = _answers.Dequeue();
                if (answer is null) return false;               // Cancel
                var error = await request.Submit(answer);
                if (error is null) return true;
                Errors.Add(error);
            }
            return false;
        }
    }

    /// <summary>The suite's vault set aside: the state of a person who never
    /// saved a credential. Put back afterwards.</summary>
    private sealed class WithoutVault : IDisposable
    {
        private readonly IVaultPrompt? _prompt = VaultGate.Prompt;
        private readonly string _held = AppPaths.VaultFile + ".held-" + Guid.NewGuid().ToString("N");
        public WithoutVault(IVaultPrompt prompt)
        {
            File.Move(AppPaths.VaultFile, _held);
            if (File.Exists(AppPaths.VaultFile + ".bak")) File.Move(AppPaths.VaultFile + ".bak", _held + ".bak");
            VaultGate.Prompt = prompt;
            Assert.Equal(VaultState.NoVault, ServiceLocator.Secrets.Refresh());
        }
        public void Dispose()
        {
            VaultGate.Prompt = _prompt;
            File.Delete(AppPaths.VaultFile);
            File.Delete(AppPaths.VaultFile + ".bak");
            File.Move(_held, AppPaths.VaultFile);
            if (File.Exists(_held + ".bak")) File.Move(_held + ".bak", AppPaths.VaultFile + ".bak");
            ServiceLocator.Secrets.Refresh();
        }
    }

    private static DestinationEditorViewModel NewEncryptedLocal(string folder) => new(new Destination
    {
        Name = "Vault gate fixture " + Guid.NewGuid().ToString("N")[..6], Kind = DestinationKind.LocalFolder, PathOrSubpath = folder, Encrypted = true,
    }, isNew: true) { Kind = DestinationKind.LocalFolder, StoragePasswordInput = "typed-storage-password", PathOrSubpath = folder };

    [AvaloniaFact]
    public async Task Cancelling_the_first_password_saves_no_destination_and_no_vault()
    {
        var folder = Directory.CreateTempSubdirectory("vault-gate-").FullName;
        var prompt = new ScriptedPrompt((string?)null);
        using (new WithoutVault(prompt))
        {
            using var vm = NewEncryptedLocal(folder);
            var saved = await vm.SaveAsync();
            Assert.True(saved is not null || prompt.Asked.Count == 0 || vm.SummaryError?.Contains("Nothing was saved") == true, $"{vm.SummaryError} {vm.NameError} {vm.PathError} {vm.KindError}");
            Assert.Null(saved);
            Assert.Equal(new[] { VaultPromptMode.Create }, prompt.Asked);
            Assert.False(File.Exists(AppPaths.VaultFile));
            Assert.Null(vm.Working.StoragePasswordRef);
            Assert.Contains("Nothing was saved", vm.SummaryError);
        }
    }

    [AvaloniaFact]
    public async Task Choosing_an_empty_password_saves_the_destination_without_further_questions()
    {
        var folder = Directory.CreateTempSubdirectory("vault-gate-").FullName;
        var prompt = new ScriptedPrompt("");
        using (new WithoutVault(prompt))
        {
            using var vm = NewEncryptedLocal(folder);
            var saved = await vm.SaveAsync();
            Assert.True(saved is not null || prompt.Asked.Count == 0 || vm.SummaryError?.Contains("Nothing was saved") == true, $"{vm.SummaryError} {vm.NameError} {vm.PathError} {vm.KindError}");
            Assert.NotNull(saved);
            Assert.True(File.Exists(AppPaths.VaultFile));
            Assert.True(ServiceLocator.Secrets.NoPassword);
            Assert.Equal("typed-storage-password", ServiceLocator.Secrets.Get(saved!.StoragePasswordRef!));
            // A second credential needs no question: the vault is open now.
            using var again = NewEncryptedLocal(folder);
            Assert.NotNull(await again.SaveAsync());
            Assert.Single(prompt.Asked);
        }
    }

    [AvaloniaFact]
    public async Task A_destination_without_credentials_never_asks()
    {
        var folder = Directory.CreateTempSubdirectory("vault-gate-").FullName;
        var prompt = new ScriptedPrompt((string?)null);
        using (new WithoutVault(prompt))
        {
            using var vm = new DestinationEditorViewModel(new Destination
            { Name = "Plain", Kind = DestinationKind.LocalFolder, PathOrSubpath = folder, Encrypted = false }, isNew: true)
            { Kind = DestinationKind.LocalFolder, PathOrSubpath = folder, Encrypted = false };
            var plain = await vm.SaveAsync();
            Assert.True(plain is not null, $"{vm.SummaryError} {vm.NameError} {vm.PathError} {vm.KindError}");
            Assert.Empty(prompt.Asked);
            Assert.False(File.Exists(AppPaths.VaultFile));
        }
    }

    [AvaloniaFact]
    public async Task Cancelling_before_cloud_sign_in_never_opens_the_browser()
    {
        var prompt = new ScriptedPrompt((string?)null);
        using (new WithoutVault(prompt))
        {
            using var vm = new DestinationEditorViewModel(new Destination { Name = "Dropbox", Kind = DestinationKind.DropboxAppScoped }, true);
            await vm.OpenOAuthHelperCommand.ExecuteAsync(null);
            Assert.Equal(new[] { VaultPromptMode.Create }, prompt.Asked);
            Assert.Contains("wasn't started", vm.AuthStatus);
            Assert.False(vm.AuthBusy);
            Assert.False(File.Exists(AppPaths.VaultFile));
        }
    }

    [AvaloniaFact]
    public async Task Cancelling_before_a_storage_password_change_opens_nothing()
    {
        var prompt = new ScriptedPrompt((string?)null);
        using (new WithoutVault(prompt))
        {
            using var vm = new DestinationEditorViewModel(new Destination { Name = "Encrypted", Kind = DestinationKind.LocalFolder, Encrypted = true }, false);
            Assert.False(await vm.EnsureVaultForPasswordChangeAsync());
            Assert.False(File.Exists(AppPaths.VaultFile));
        }
    }

    [AvaloniaFact]
    public async Task A_test_email_keeps_typed_keys_in_memory()
    {
        var before = File.ReadAllBytes(AppPaths.VaultFile);
        var keys = new Dictionary<string, string> { ["mail:resendApiKey-test"] = "re_test_key" };
        var settings = new MailSettings { Provider = Backspace.Models.MailProvider.Resend, FromAddress = "a@example.invalid", ResendApiKeyRef = "mail:resendApiKey-test" };
        // No network here: the send fails, but nothing is written to the vault.
        _ = await ServiceLocator.Mail.SendTestAsync(settings, new[] { "b@example.invalid" },
            new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(1)).Token, keys).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : (false, ""));
        Assert.Equal(before, File.ReadAllBytes(AppPaths.VaultFile));
        Assert.False(ServiceLocator.Secrets.TryGet("mail:resendApiKey-test", out _));
    }
}
