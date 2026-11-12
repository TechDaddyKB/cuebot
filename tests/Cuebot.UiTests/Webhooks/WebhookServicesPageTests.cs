using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Webhooks;
using Cuebot.App.Views.Webhooks;
using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Webhooks;
using Cuebot.UiTests.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests.Webhooks;

/// <summary>
/// The Webhook services page: secrets are masked and saved, switches write through, test events reach the last-events
/// list and the activity feed, custom hooks can be added and removed, and the page renders without binding errors in
/// both themes (screenshots tests/Cuebot.UiTests/screenshots/webhooks-*.png).
/// </summary>
public sealed class WebhookServicesPageTests : IAsyncDisposable
{
    private const string KofiToken = "kofi-verification-token-ABCD1234";
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-webhooks-").FullName;
    private readonly ServiceProvider _services;

    public WebhookServicesPageTests() =>
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dataDir)).AddWebhooks().BuildServiceProvider();

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    [AvaloniaFact]
    public void Page_masks_secrets_saves_switches_sends_tests_and_manages_custom_hooks()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            TestApp.BindingErrors.Clear();
            var hub = Get<ChatHub>();
            var services = Get<WebhookServices>();
            var vm = new WebhookServicesViewModel(services);
            var window = new Window
            {
                Content = new WebhookServicesView { DataContext = vm },
                Width = 1100,
                Height = 2200,
                Background = app.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
            };
            window.Show();
            UiPump.Pump();
            Assert.True(vm.HasNoEvents);
            Assert.Equal(["Ko-fi", "Patreon", "Fourthwall", "Shopify", "Throne"], vm.Services.Select(s => s.Name));

            var kofi = vm.Services[0];
            kofi.Secret = "  " + KofiToken + " ";
            Assert.Equal(KofiToken, services.Kofi.Settings.Secret);
            Assert.Equal("Saved: ••••••••1234", kofi.MaskedSecret);
            kofi.Enabled = false;
            Assert.False(services.Kofi.Settings.Enabled);
            kofi.Enabled = true;
            UiPump.Pump();
            var secrets = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Name == "Secret").ToArray();
            Assert.Equal(5, secrets.Length);
            Assert.All(secrets, b => Assert.Equal('•', b.PasswordChar));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").Contains(KofiToken, StringComparison.Ordinal));

            // Throne works without a secret (its signature is checked); the URL token is optional and, once generated, shown
            // once so it can be put in the URL.
            var throne = vm.Services[4];
            Assert.True(services.Throne.IsConfigured);
            Assert.Equal("URL token (optional)", throne.SecretLabel);
            Assert.Equal("No URL token — Throne's signature alone is checked", throne.MaskedSecret);
            Assert.Contains("signs every delivery", throne.Help, StringComparison.Ordinal);
            Assert.Contains("JSON", vm.Services[3].Help, StringComparison.Ordinal);
            Assert.Equal("No webhook secret saved — deliveries are refused", vm.Services[1].MaskedSecret);
            throne.GenerateCommand.Execute(null);
            Assert.Equal(services.Throne.Settings.Secret, throne.GeneratedToken);
            Assert.Equal(32, throne.GeneratedToken.Length);
            Assert.StartsWith("Saved: ", throne.MaskedSecret, StringComparison.Ordinal);

            kofi.SelectedTestKind = "donation";
            kofi.SendTestCommand.Execute(null);
            vm.Services[2].SelectedTestKind = "subscriptionPurchased";
            vm.Services[2].SendTestCommand.Execute(null);
            UiPump.Pump();
            Assert.Equal(2, vm.RecentEvents.Count);
            Assert.All(vm.RecentEvents, r => Assert.True(r.IsTest));
            Assert.Contains(vm.RecentEvents, r => r.Summary == "[Test] Ko-fi Team donated $3.00 on Ko-fi");
            Assert.Contains("nothing was sent", kofi.Info);
            Assert.Contains(hub.RecentFeed(), f => f.Kind == FeedKind.Tip && f.Platform == "Ko-fi" && f.Amount == "$3.00");

            vm.Custom.NewName = "door bell";
            vm.Custom.AddCommand.Execute(null);
            var hook = Assert.Single(vm.Custom.Hooks);
            Assert.Equal("door-bell", hook.Name);
            Assert.Equal("door-bell", Assert.Single(services.Custom.Hooks).Name);
            vm.Custom.NewName = "door-bell";
            vm.Custom.AddCommand.Execute(null);
            Assert.Contains("already exists", vm.Custom.Error);
            var oldToken = hook.Token;
            hook.RegenerateCommand.Execute(null);
            Assert.NotEqual(oldToken, services.Custom.Hooks[0].Token);
            hook.TestCommand.Execute(null);
            UiPump.Pump();
            Assert.Equal(3, vm.RecentEvents.Count);

            // Patreon extras: the API tokens are masked like the secrets, and nothing is called without a token.
            var patreon = vm.Patreon;
            patreon.AccessToken = "  patreon-access-token-WXYZ9876 ";
            Assert.Equal("patreon-access-token-WXYZ9876", services.Patreon.Options.AccessToken);
            Assert.Contains("••••••••9876", patreon.SavedTokens, StringComparison.Ordinal);
            patreon.Currency = "eur";
            Assert.Equal("EUR", services.Patreon.Options.Currency);
            patreon.StaleHours = 12;
            Assert.Equal(12, services.Patreon.Options.StaleHours);
            UiPump.Pump();
            var tokenBoxes = window.GetVisualDescendants().OfType<TextBox>().Where(t => (t.Name ?? "").StartsWith("Patreon", StringComparison.Ordinal)).ToArray();
            Assert.Equal(3, tokenBoxes.Length);
            Assert.All(tokenBoxes, b => Assert.Equal('•', b.PasswordChar));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").Contains("patreon-access-token", StringComparison.Ordinal));
            patreon.DisconnectCommand.Execute(null);
            Assert.Equal("", services.Patreon.Options.AccessToken);
            Assert.StartsWith("Not connected", patreon.Status, StringComparison.Ordinal);

            // Fourthwall extras: the API user and password are masked, the card says the relay is needed.
            var fourthwall = vm.Fourthwall;
            fourthwall.ApiPassword = "  fw-password-QRST4321 ";
            Assert.Equal("fw-password-QRST4321", services.Fourthwall.Options.ApiPassword);
            Assert.Contains("••••••••4321", fourthwall.SavedCredentials, StringComparison.Ordinal);
            fourthwall.HideSupporterNames = true;
            Assert.True(services.Fourthwall.Options.HideSupporterNames);
            fourthwall.EntryCommand = " !join ";
            Assert.Equal("!join", services.Fourthwall.Options.EntryCommand);
            UiPump.Pump();
            var fwBoxes = window.GetVisualDescendants().OfType<TextBox>().Where(t => (t.Name ?? "").StartsWith("FourthwallApi", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, fwBoxes.Length);
            Assert.All(fwBoxes, b => Assert.Equal('•', b.PasswordChar));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").Contains("fw-password", StringComparison.Ordinal));
            Assert.Contains("port 443", vm.Services.Single(c => c.Route == "fourthwall").Help, StringComparison.Ordinal);
            fourthwall.DisconnectCommand.Execute(null);
            Assert.Equal("", services.Fourthwall.Options.ApiPassword);
            Assert.StartsWith("Not connected", fourthwall.Status, StringComparison.Ordinal);

            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"webhooks-{name}");
            }

            hook.RemoveCommand.Execute(null);
            Assert.Empty(services.Custom.Hooks);
            Assert.True(vm.Custom.HasNoHooks);
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { /* settings flush may still hold the file briefly */ }
    }
}
