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
public sealed class WebhooksPageTests : IAsyncDisposable
{
    private const string KofiToken = "kofi-verification-token-ABCD1234";
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-webhooks-").FullName;
    private readonly ServiceProvider _services;

    public WebhooksPageTests() =>
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
            var vm = new WebhooksViewModel(services);
            var window = new Window
            {
                Content = new WebhooksView { DataContext = vm },
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

            // Throne: a generated token is shown once so it can be put in the URL.
            var throne = vm.Services[4];
            throne.GenerateCommand.Execute(null);
            Assert.Equal(services.Throne.Settings.Secret, throne.GeneratedToken);
            Assert.Equal(32, throne.GeneratedToken.Length);

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
