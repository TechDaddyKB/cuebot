using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Donations;
using Cuebot.App.Views.Donations;
using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Streamlabs;
using Cuebot.UiTests.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests.Donations;

/// <summary>
/// The Donations page: tokens are masked and saved, options write through, test events reach the last-events list
/// and the activity feed, and the page renders without binding errors in both themes (screenshots
/// tests/Cuebot.UiTests/screenshots/donations-*.png).
/// </summary>
public sealed class DonationsPageTests : IAsyncDisposable
{
    private const string SocketToken = "SL-socket-token-ABCDEFGHIJ1234";
    private const string Jwt = "eyJhbGciOi.SE-jwt-token.signatureWXYZ";
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-donations-").FullName;
    private readonly ServiceProvider _services;

    public DonationsPageTests() =>
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dataDir)).AddDonations().BuildServiceProvider();

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private static Window Show(DonationsViewModel vm)
    {
        var window = new Window
        {
            Content = new DonationsView { DataContext = vm },
            Width = 1100,
            Height = 1500,
            Background = Application.Current!.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
        };
        window.Show();
        UiPump.Pump();
        return window;
    }

    [AvaloniaFact]
    public void Page_masks_tokens_saves_options_and_lists_test_events_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            TestApp.BindingErrors.Clear();
            var hub = Get<ChatHub>();
            var streamlabs = Get<StreamlabsService>();
            var streamElements = Get<StreamElementsService>();
            var vm = new DonationsViewModel(streamlabs, streamElements);
            var window = Show(vm);
            Assert.True(vm.HasNoEvents);
            UiPump.Save(window, "donations-empty");

            // Tokens: stored trimmed, shown only masked.
            vm.Streamlabs.Token = "  " + SocketToken + " ";
            vm.StreamElements.Token = Jwt;
            Assert.Equal(SocketToken, streamlabs.Settings.SocketToken);
            Assert.Equal(Jwt, streamElements.Settings.JwtToken);
            Assert.Equal("Saved token: ••••••••1234", vm.Streamlabs.MaskedToken);
            Assert.Equal("Saved token: ••••••••WXYZ", vm.StreamElements.MaskedToken);
            UiPump.Pump();
            var boxes = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Name is "StreamlabsToken" or "StreamElementsToken").ToArray();
            Assert.Equal(2, boxes.Length);
            Assert.All(boxes, b => Assert.Equal('•', b.PasswordChar));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").Contains(SocketToken, StringComparison.Ordinal));

            // Options write through.
            vm.Streamlabs.AutoConnect = false;
            vm.Streamlabs.RelayPlatformEvents = true;
            vm.StreamElements.UseLegacyRealtime = true;
            Assert.False(streamlabs.Settings.AutoConnect);
            Assert.True(streamlabs.Settings.RelayPlatformEvents);
            Assert.Equal(StreamElementsTransport.LegacyRealtime, streamElements.Settings.Transport);
            vm.StreamElements.UseLegacyRealtime = false;

            // Local test events.
            vm.Streamlabs.SelectedTestKind = "donation";
            vm.Streamlabs.SendTestCommand.Execute(null);
            vm.StreamElements.SelectedTestKind = "merch";
            vm.StreamElements.SendTestCommand.Execute(null);
            vm.StreamElements.SelectedTestKind = "tip";
            vm.StreamElements.SendTestCommand.Execute(null);
            UiPump.Pump();
            Assert.Equal(3, vm.RecentEvents.Count);
            Assert.All(vm.RecentEvents, r => Assert.True(r.IsTest));
            Assert.Contains(vm.RecentEvents, r => r.Summary == "[Test] TestDonor donated $13.37");
            Assert.Contains("nothing was sent", vm.StreamElements.Info);
            Assert.Equal(3, hub.RecentFeed().Count);
            Assert.Contains(hub.RecentFeed(), f => f.Kind == FeedKind.Tip && f.Platform == "StreamElements" && f.Amount == "$4.20");

            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"donations-{name}");
            }

            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    [AvaloniaFact]
    public async Task Connect_without_a_token_shows_an_error()
    {
        var vm = new DonationsViewModel(Get<StreamlabsService>(), Get<StreamElementsService>());
        await vm.Streamlabs.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("token", vm.Streamlabs.Error);
        Assert.Equal("Error", vm.Streamlabs.State);
        Assert.DoesNotContain(Get<AppLog>().Recent(), e => e.Message.Contains(SocketToken, StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { /* settings flush may still hold the file briefly */ }
    }
}
