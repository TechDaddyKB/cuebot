using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Webhooks;
using Cuebot.App.Views.Webhooks;
using Cuebot.Core.Desktop.Clipboard;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;
using Cuebot.Relay;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.Webhooks;

/// <summary>
/// The Webhooks page: relay secrets are masked and saved, each handler gets its copyable webhook URL, and deliveries from
/// a scripted relay show up in the list without binding errors in both themes (screenshots webhooks-*.png).
/// </summary>
public sealed class WebhooksPageTests : IDisposable
{
    private const string Token = "relay-connect-token-ABCDEFGHIJKLMN1234";
    private const string Route = "0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-ui-webhooks-").FullName;
    private readonly AppLog _log = new();
    private readonly JsonFile<RelaySettings> _settings;
    private readonly ScriptedRelay _relay = new();
    private readonly RelayClient _client;
    private readonly FakeClipboard _clipboard = new();

    public WebhooksPageTests()
    {
        _settings = new JsonFile<RelaySettings>(Path.Combine(_dir, "relay.json"), _log);
        _client = new RelayClient(_settings, [new Handler("kofi", "Ko-fi"), new Handler("kick", "Kick")], new EventBus(_log), _log,
            () => _relay, RelayTimings.Default with { ReconnectDelay = TimeSpan.FromMilliseconds(50) });
    }

    public void Dispose()
    {
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public async Task Page_saves_masked_settings_shows_urls_and_deliveries_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            TestApp.BindingErrors.Clear();
            var vm = new WebhooksViewModel(_client, _clipboard);
            var window = new Window
            {
                Content = new WebhooksView { DataContext = vm },
                Width = 1100,
                Height = 1200,
                Background = app.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
            };
            window.Show();
            UiPump.Pump();
            Assert.Equal(["Kick", "Ko-fi"], vm.Routes.Select(r => r.DisplayName));
            Assert.All(vm.Routes, r => Assert.False(r.HasUrl));
            Assert.True(vm.HasNoDeliveries);
            UiPump.Save(window, "webhooks-empty");

            vm.Url = " https://cuebot-relay.someone.workers.dev/ ";
            vm.ConnectToken = " " + Token + " ";
            vm.RouteSecret = Route;
            var s = _settings.Value;
            Assert.Equal("https://cuebot-relay.someone.workers.dev", s.Url);
            Assert.Equal(Token, s.ConnectToken);
            Assert.Equal("Saved connect token: ••••••••1234", vm.MaskedToken);
            Assert.Equal($"https://cuebot-relay.someone.workers.dev/hook/{Route}/kofi", vm.Routes[1].Url);
            Assert.Equal("", vm.Missing);

            await vm.Routes[1].CopyCommand.ExecuteAsync(null);
            Assert.Equal(vm.Routes[1].Url, _clipboard.Text);
            Assert.Contains("Ko-fi", vm.Info);

            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(s.Enabled);
            await Until(() => vm.IsConnected, "connection");
            _relay.Deliver("d1", "kofi");
            _relay.Deliver("d2", "mystery", "extra");
            await Until(() => vm.Deliveries.Count == 2, "deliveries");
            Assert.Equal(("mystery", "NotConfigured", "/extra"), (vm.Deliveries[0].Service, vm.Deliveries[0].Outcome, vm.Deliveries[0].Path));
            Assert.Equal(("Ko-fi", "Handled"), (vm.Deliveries[1].Service, vm.Deliveries[1].Outcome));

            UiPump.Pump();
            var secretBoxes = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Name is "RelayToken" or "RelayRouteSecret").ToArray();
            Assert.Equal(2, secretBoxes.Length);
            Assert.All(secretBoxes, b => Assert.Equal('•', b.PasswordChar));
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
            Assert.DoesNotContain(texts, t => t.Contains(Token, StringComparison.Ordinal));

            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"webhooks-{name}");
            }
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            Assert.DoesNotContain(_log.Recent(), e => e.Message.Contains(Token, StringComparison.Ordinal) || e.Message.Contains(Route, StringComparison.Ordinal));
            await vm.DisconnectCommand.ExecuteAsync(null);
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    [AvaloniaFact]
    public async Task Connect_without_settings_shows_what_is_missing()
    {
        var vm = new WebhooksViewModel(_client, _clipboard);
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("relay URL", vm.Error);
        Assert.Equal("Error", vm.State);
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(10);
            UiPump.Pump();
        }
    }

    private sealed class Handler(string service, string name) : IWebhookHandler
    {
        public string Service => service;
        public string DisplayName => name;
        public Task<WebhookOutcome> HandleAsync(WebhookDelivery delivery, CancellationToken ct) => Task.FromResult(WebhookOutcome.Handled);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public bool IsAvailable => true;

        public Task SetTextAsync(string text, CancellationToken ct = default)
        {
            Text = text;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync(CancellationToken ct = default) => Task.FromResult(Text);
    }

    /// <summary>A relay connection that never closes and delivers what the test pushes.</summary>
    private sealed class ScriptedRelay : IRelayTransport
    {
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();

        public int? CloseCode => null;
        public string? CloseReason => null;

        public Task ConnectAsync(Uri url, string bearerToken, CancellationToken ct) => Task.CompletedTask;
        public Task SendAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public async Task<string?> ReceiveAsync(CancellationToken ct) => await _messages.Reader.ReadAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Deliver(string id, string service, string path = "") => _messages.Writer.TryWrite(JsonSerializer.Serialize(new
        {
            type = "delivery",
            id,
            service,
            path,
            method = "POST",
            query = "",
            headers = new Dictionary<string, string>(),
            bodyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")),
            receivedAt = DateTimeOffset.UtcNow.ToString("O"),
        }));
    }
}
