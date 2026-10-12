using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S63_SettingsPersistence : ScenarioBase
{
    protected override string ScenarioName => "S63-settings-persistence";
    [AvaloniaFact] public Task Run_S63() => Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var before = ServiceLocator.Config.Current;
        const string resendRef = "scenario:s63-resend";
        const string mailgunRef = "scenario:s63-mailgun";
        const string healthRef = "scenario:s63-health";
        const string oauthRef = "scenario:s63-oauth";
        foreach (var key in new[] { resendRef, mailgunRef, healthRef, oauthRef })
            ServiceLocator.Secrets.Set(key, "synthetic-test-credential");
        ServiceLocator.Config.Update(c =>
        {
            c.Mail = new MailSettings { Provider = MailProvider.Resend, FromAddress = "backups@example.test",
                ToAddress = "original@example.test", ToAddresses = new() { "original@example.test" },
                ResendApiKeyRef = resendRef, MailgunApiKeyRef = mailgunRef, MailgunDomain = "old.example.test" };
            c.Notifications.OnFailure.Email = true;
            c.Monitoring.HealthchecksApiKeyRef = healthRef;
            c.RcloneOAuthApps = new() { new RcloneOAuthApp { Provider = DestinationKind.DropboxAppScoped,
                ClientId = "old-client", ClientSecretRef = oauthRef } };
        });
        try
        {
            using var vm = new SettingsViewModel();
            var view = new SettingsView { DataContext = vm };
            var window = ctx.Driver.Show(new Window { Width = 1000, Height = 720, Content = view });
            await OpenSection("Notifications");

            // Changing the provider must survive a completely new ConfigStore,
            // even without a manual Save or another unrelated settings edit.
            var provider = view.FindControl<ComboBox>("MailProviderPicker")!;
            await Reveal(provider);
            provider.Focus(); Press(PhysicalKey.ArrowDown);
            Assert.Equal(MailProvider.Mailgun, vm.MailProvider);
            Assert.Equal(MailProvider.Mailgun, ReadSaved().Mail.Provider);
            await Type("MailgunDomainField", "mail.example.test");
            Assert.Equal("mail.example.test", ReadSaved().Mail.MailgunDomain);

            await Type("MailRecipientField", "second@example.test");
            await Click(view.FindControl<Button>("AddMailRecipientButton")!);
            Assert.Equal(new[] { "original@example.test", "second@example.test" }, ReadSaved().Mail.ToAddresses);
            var remove = window.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.CommandParameter, "original@example.test"));
            await Click(remove);
            Assert.Equal(new[] { "second@example.test" }, ReadSaved().Mail.ToAddresses);
            Assert.Equal("second@example.test", ReadSaved().Mail.ToAddress);

            // Refresh from an unrelated config write: the formerly unsaved
            // recipient and domain must not disappear or revert.
            ServiceLocator.Config.Update(c => c.Logging.RetentionDays++);
            Assert.Equal(new[] { "second@example.test" }, vm.MailToList);
            Assert.Equal("mail.example.test", vm.MailgunDomain);
            using (var reopened = new SettingsViewModel())
            {
                Assert.Equal(MailProvider.Mailgun, reopened.MailProvider);
                Assert.Equal("mail.example.test", reopened.MailgunDomain);
                Assert.Equal(new[] { "second@example.test" }, reopened.MailToList);
            }
            await Reveal(view.FindControl<TextBox>("MailRecipientField")!);
            ctx.Snapshot(window, "email-changes-survive-reopening",
                "Mailgun and second@example.test remain after a fresh configuration read and Settings reload. The original recipient was removed using its actual button; no Save button was clicked.");

            // Negative cases: a duplicate or malformed recipient must not
            // change the persisted list, even while the rest auto-saves.
            await Type("MailRecipientField", "SECOND@example.test");
            await Click(view.FindControl<Button>("AddMailRecipientButton")!);
            Assert.Single(ReadSaved().Mail.ToAddresses);
            await Type("MailRecipientField", "not-an-address");
            await Click(view.FindControl<Button>("AddMailRecipientButton")!);
            Assert.True(vm.TestEmailFailed);
            Assert.Equal(new[] { "second@example.test" }, ReadSaved().Mail.ToAddresses);
            await Type("MailRecipientField", "second@example.test");
            await Click(view.FindControl<Button>("AddMailRecipientButton")!);
            Assert.False(vm.TestEmailFailed);
            Assert.Empty(vm.TestEmailStatus);
            Assert.Empty(vm.MailToInput);
            Assert.Single(ReadSaved().Mail.ToAddresses);

            await Type("MailgunApiKeyField", "");
            Assert.Null(ReadSaved().Mail.MailgunApiKeyRef);
            await Type("MailgunDomainField", "sandboxabc123.mailgun.org");
            Assert.True(view.FindControl<TextBox>("MailSenderField")!.IsReadOnly);
            Assert.Equal("backspace@sandboxabc123.mailgun.org", vm.MailFrom);
            await Reveal(provider); provider.Focus(); Press(PhysicalKey.ArrowUp);
            Assert.Equal(MailProvider.Resend, ReadSaved().Mail.Provider);
            Assert.False(view.FindControl<TextBox>("MailSenderField")!.IsReadOnly);
            Assert.Equal("onboarding@resend.dev", vm.MailFrom);
            await Type("MailSenderField", "owner@example.test");
            Assert.Equal("owner@example.test", ReadSaved().Mail.FromAddress);
            await Type("ResendApiKeyField", "");
            Assert.Null(ReadSaved().Mail.ResendApiKeyRef);
            ServiceLocator.Config.Update(c => c.Logging.RetentionDays++);
            Assert.Equal("", vm.ResendApiKey);
            Assert.Equal("", vm.MailgunApiKey);
            await Reveal(view.FindControl<TextBox>("ResendApiKeyField")!);
            ctx.Snapshot(window, "cleared-email-key-stays-empty",
                "The Resend key is empty and Email setup explains that a key is required. A fresh disk read and an unrelated refresh both confirmed that the old saved key was disconnected.");

            // Clicking Send test accepts and saves a pending valid recipient.
            // The intentionally missing key prevents any network request.
            await Type("MailRecipientField", "test-button@example.test");
            await Click(view.FindControl<Button>("SendTestEmailButton")!);
            Assert.Equal(new[] { "second@example.test", "test-button@example.test" }, ReadSaved().Mail.ToAddresses);
            Assert.Empty(vm.MailToInput);
            Assert.True(vm.TestEmailFailed);
            Assert.Contains("key", vm.TestEmailStatus, StringComparison.OrdinalIgnoreCase);

            await OpenSection("Watchdog (Healthchecks.io)");
            await Type("HealthchecksApiKeyField", "");
            Assert.Null(ReadSaved().Monitoring.HealthchecksApiKeyRef);
            ServiceLocator.Config.Update(c => c.RcloneOAuthApps.Clear());
            Assert.Equal("", vm.DropboxClientId);
            Assert.Equal("", vm.DropboxClientSecret);
            using (var reopened = new SettingsViewModel())
            {
                Assert.False(reopened.IsMailgunSandbox);
                Assert.Equal("owner@example.test", reopened.MailFrom);
                Assert.Equal("", reopened.ResendApiKey);
                Assert.Equal("", reopened.MailgunApiKey);
                Assert.Equal("", reopened.HealthchecksApiKey);
                Assert.Equal("", reopened.DropboxClientId);
                Assert.Equal("", reopened.DropboxClientSecret);
            }
            Assert.True(ServiceLocator.Secrets.TryGet(oauthRef, out _));
            ctx.Snapshot(window, "cleared-watchdog-key-stays-empty",
                "Healthchecks has no API key after clearing the actual field. Removing a cloud app's saved settings also cleared its old values from the open Settings page.");
            await OpenSection("Cloud OAuth apps");
            await Reveal(view.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Text == "Cloud OAuth apps"));
            ctx.Snapshot(window, "cloud-app-registration-guidance",
                "Settings explains that Dropbox has Backspace's registered app, while OneDrive and Google Drive currently need a registered client ID. No live sign-in was attempted.");
            ctx.Report.RecordNote("Adversarial checks and scope",
                "Real SettingsView pointer clicks and keyboard text input, plus a new ConfigStore reading the real saved JSON after each change. Duplicate and malformed recipients do not alter the list. Send test saves a pending valid recipient before reporting the deliberately missing key, without a network request. Switching from a Mailgun sandbox to Resend unlocks FROM; a custom Resend sender survives a fresh Settings instance despite the saved sandbox domain. Removed recipients stay removed; cleared keys stay blank after reload. Shared encrypted values are retained without active references. These tests do not send email or call a live cloud account.");

            async Task OpenSection(string title)
            {
                var header = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == title);
                var expander = header.GetVisualAncestors().OfType<Expander>().First();
                if (!expander.IsExpanded) await Click(header);
            }
            async Task Reveal(Control control)
            {
                control.BringIntoView(); window.UpdateLayout();
                await ctx.Driver.PumpAsync(100);
                control.BringIntoView(); ctx.Driver.Pump(); window.UpdateLayout();
            }
            async Task Click(Control control)
            {
                await Reveal(control);
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                Assert.InRange(point.Y, 0, window.Bounds.Height);
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                await ctx.Driver.PumpAsync(100);
            }
            async Task Type(string name, string text)
            {
                var field = view.FindControl<TextBox>(name)!;
                await Reveal(field); field.Focus();
                Press(PhysicalKey.A, RawInputModifiers.Control);
                Press(PhysicalKey.Delete);
                if (text.Length > 0) window.KeyTextInput(text);
                ctx.Driver.Pump();
                Assert.Equal(text, field.Text ?? "");
            }
            void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
            { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); ctx.Driver.Pump(); }
        }
        finally
        {
            ServiceLocator.Config.Update(c =>
            {
                c.Mail = before.Mail; c.Monitoring = before.Monitoring;
                c.Notifications = before.Notifications; c.RcloneOAuthApps = before.RcloneOAuthApps;
                c.Logging.RetentionDays = before.Logging.RetentionDays;
            });
            foreach (var key in new[] { resendRef, mailgunRef, healthRef, oauthRef })
                ServiceLocator.Secrets.Remove(key);
        }
    }

    private static AppConfig ReadSaved()
    {
        var store = new ConfigStore();
        store.Load();
        return store.Current;
    }
}
