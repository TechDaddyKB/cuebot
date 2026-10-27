using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;
using Cuebot.Relay.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Relay.Tests;

public sealed class RelayClientTests : IAsyncDisposable
{
    private const string Token = "connect-token-abcdefghijklmnopqrstuvwxyz";
    private const string Route = "route-secret-0123456789abcdef0123";

    private static readonly RelayTimings Fast = new(
        ConnectTimeout: TimeSpan.FromSeconds(5),
        ReconnectDelay: TimeSpan.FromMilliseconds(10),
        MaxReconnectDelay: TimeSpan.FromMilliseconds(40),
        PingInterval: TimeSpan.FromSeconds(30),
        SilenceTimeout: TimeSpan.FromSeconds(90),
        HandlerTimeout: TimeSpan.FromSeconds(5));

    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-relay-tests").FullName;
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly ConcurrentQueue<BotEvent> _events = new();
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private readonly FakeRelay _relay = new();
    private readonly JsonFile<RelaySettings> _settings;
    private RelayClient? _client;

    public RelayClientTests()
    {
        _bus = new EventBus(_log);
        _bus.Subscribe(_events.Enqueue);
        _log.EntryAdded += _logs.Enqueue;
        _settings = new JsonFile<RelaySettings>(Path.Combine(_dir, "relay.json"), _log);
        _settings.Value.Url = "https://cuebot-relay.someone.workers.dev";
        _settings.Value.ConnectToken = Token;
        _settings.Value.RouteSecret = Route;
        _settings.Value.Enabled = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private RelayClient Client(RelayTimings? timings = null, params IWebhookHandler[] handlers) =>
        _client = new RelayClient(_settings, handlers, _bus, _log, _relay.Create, timings ?? Fast);

    private BotEvent[] Events(string type) => _events.Where(e => e.Type == type).ToArray();

    [Theory]
    [InlineData("https://cuebot-relay.me.workers.dev", "wss://cuebot-relay.me.workers.dev/connect")]
    [InlineData("https://cuebot-relay.me.workers.dev/", "wss://cuebot-relay.me.workers.dev/connect")]
    [InlineData("cuebot-relay.me.workers.dev", "wss://cuebot-relay.me.workers.dev/connect")]
    [InlineData("wss://relay.example.com/base/", "wss://relay.example.com/base/connect")]
    [InlineData("http://localhost:8787", "ws://localhost:8787/connect")]
    public void Connect_uri_is_derived_from_the_relay_url(string url, string expected) =>
        Assert.Equal(expected, RelayClient.ConnectUri(url)!.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x.example")]
    [InlineData("https://")]
    public void Invalid_relay_urls_have_no_connect_uri(string url) => Assert.Null(RelayClient.ConnectUri(url));

    [Fact]
    public void Webhook_url_includes_the_route_secret_and_service()
    {
        var client = Client();
        Assert.Equal($"https://cuebot-relay.someone.workers.dev/hook/{Route}/kofi", client.WebhookUrl("kofi"));
        _settings.Value.RouteSecret = "";
        Assert.Null(client.WebhookUrl("kofi"));
    }

    [Fact]
    public async Task Connects_with_the_bearer_token_and_reports_status()
    {
        var client = Client();
        await client.ConnectAsync();
        var c = await _relay.WaitForConnectionAsync();
        Assert.Equal("wss://cuebot-relay.someone.workers.dev/connect", c.Url!.ToString());
        Assert.Equal(Token, c.Token);
        c.Hello(buffered: 2);
        await Wait.UntilAsync(() => client.StatusMessage.Contains("2 delivery", StringComparison.Ordinal), "hello status");
        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.Single(Events(RelayEvents.Connected));
    }

    [Fact]
    public async Task Dispatches_to_the_matching_handler_then_acks_and_records()
    {
        var kofi = new FakeHandler("kofi");
        var patreon = new FakeHandler("patreon");
        var client = Client(null, kofi, patreon);
        await client.ConnectAsync();
        var c = await _relay.WaitForConnectionAsync();

        c.Deliver("d1", "kofi", "data=%7B%7D", new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" }, "extra/path", "a=1");
        await Wait.UntilAsync(() => c.Acks.Contains("d1"), "ack");

        var d = Assert.Single(kofi.Received);
        Assert.Empty(patreon.Received);
        Assert.Equal("d1", d.Id);
        Assert.Equal("kofi", d.Service);
        Assert.Equal("extra/path", d.Path);
        Assert.Equal("a=1", d.Query);
        Assert.Equal("POST", d.Method);
        Assert.Equal("data=%7B%7D", d.BodyText);
        Assert.Equal("application/x-www-form-urlencoded", d.Header("Content-Type"));
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero), d.ReceivedAt);

        var record = Assert.Single(client.RecentDeliveries);
        Assert.Equal(("kofi", WebhookOutcome.Handled), (record.Service, record.Outcome));
        var e = Assert.Single(Events(RelayEvents.Delivery));
        Assert.Equal("kofi", e.Args["service"]);
        Assert.Equal("Handled", e.Args["outcome"]);
        Assert.Equal("d1", e.Args["deliveryId"]);
    }

    [Fact]
    public async Task Unknown_service_is_logged_and_acked()
    {
        var client = Client(null, new FakeHandler("kofi"));
        await client.ConnectAsync();
        var c = await _relay.WaitForConnectionAsync();
        c.Deliver("u1", "mystery");
        await Wait.UntilAsync(() => c.Acks.Contains("u1"), "ack");
        Assert.Equal(WebhookOutcome.NotConfigured, Assert.Single(client.RecentDeliveries).Outcome);
        Assert.Contains(_logs, l => l.Level == LogLevel.Warning && l.Message.Contains("mystery", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replayed_ids_are_acked_without_dispatching_again()
    {
        var kofi = new FakeHandler("kofi");
        var client = Client(null, kofi);
        await client.ConnectAsync();
        var c1 = await _relay.WaitForConnectionAsync();
        c1.Deliver("same", "kofi");
        await Wait.UntilAsync(() => c1.Acks.Contains("same"), "first ack");

        // The relay may replay a delivery whose ack was lost when the connection dropped.
        c1.Close(1006);
        var c2 = await _relay.WaitForConnectionAsync(2);
        c2.Deliver("same", "kofi");
        c2.Deliver("next", "kofi");
        await Wait.UntilAsync(() => c2.Acks.Contains("same") && c2.Acks.Contains("next"), "acks after reconnect");
        Assert.Equal(["same", "next"], kofi.Received.Select(d => d.Id));
        Assert.Equal(2, client.RecentDeliveries.Count);
        Assert.Single(Events(RelayEvents.Disconnected));
        Assert.Equal(2, Events(RelayEvents.Connected).Length);
    }

    [Fact]
    public async Task Handler_failures_are_recorded_and_still_acked()
    {
        var kofi = new FakeHandler("kofi") { Throw = new InvalidOperationException($"boom {Token}") };
        var client = Client(null, kofi);
        await client.ConnectAsync();
        var c = await _relay.WaitForConnectionAsync();
        c.Deliver("f1", "kofi");
        await Wait.UntilAsync(() => c.Acks.Contains("f1"), "ack");
        var record = Assert.Single(client.RecentDeliveries);
        Assert.Equal(WebhookOutcome.Failed, record.Outcome);
        Assert.DoesNotContain(Token, record.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deliveries_are_handled_in_arrival_order()
    {
        var slow = new FakeHandler("kofi") { Delay = TimeSpan.FromMilliseconds(30) };
        var client = Client(null, slow);
        await client.ConnectAsync();
        var c = await _relay.WaitForConnectionAsync();
        for (var i = 0; i < 5; i++) c.Deliver($"o{i}", "kofi");
        await Wait.UntilAsync(() => c.Acks.Count == 5, "five acks");
        Assert.Equal(["o0", "o1", "o2", "o3", "o4"], slow.Received.Select(d => d.Id));
        Assert.Equal(["o0", "o1", "o2", "o3", "o4"], c.Acks);
    }

    [Fact]
    public async Task Wrong_token_stops_without_retrying()
    {
        _relay.RejectAuth = true;
        var client = Client();
        await client.ConnectAsync();
        await Wait.UntilAsync(() => client.State == ConnectionState.Error, "error state");
        await Task.Delay(100);
        Assert.Single(_relay.Connections);
        Assert.False(client.IsRunning);
    }

    [Fact]
    public async Task Unreachable_relay_is_retried_with_backoff_and_secrets_are_redacted()
    {
        _relay.UnreachableMessage = $"cannot reach wss://x/connect?token={Token}";
        var client = Client();
        await client.ConnectAsync();
        await Wait.UntilAsync(() => _relay.Connections.Count >= 3, "retries");
        Assert.Equal(ConnectionState.Connecting, client.State);
        Assert.DoesNotContain(Token, client.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("***", client.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(_logs, l => l.Message.Contains(Token, StringComparison.Ordinal) || l.Message.Contains(Route, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Being_replaced_by_another_connection_stops_reconnecting()
    {
        var client = Client();
        await client.ConnectAsync();
        var c = await _relay.WaitForConnectionAsync();
        c.Close(RelayClient.ReplacedCloseCode, "Replaced by a newer connection");
        await Wait.UntilAsync(() => client.State == ConnectionState.Error, "error state");
        Assert.Contains("Another Cuebot", client.StatusMessage, StringComparison.Ordinal);
        await Task.Delay(100);
        Assert.Single(_relay.Connections);
    }

    [Fact]
    public async Task Sends_heartbeats_and_reconnects_when_the_relay_goes_silent()
    {
        _relay.AnswerPings = false;
        var client = Client(Fast with { PingInterval = TimeSpan.FromMilliseconds(30), SilenceTimeout = TimeSpan.FromMilliseconds(100) });
        await client.ConnectAsync();
        var c1 = await _relay.WaitForConnectionAsync();
        await _relay.WaitForConnectionAsync(2);
        Assert.Contains("""{"type":"ping"}""", c1.Sent);
        Assert.True(c1.Disposed);
    }

    [Fact]
    public async Task Start_connects_only_when_enabled_auto_connect_and_complete()
    {
        _settings.Value.Enabled = false;
        var client = Client();
        await client.StartAsync();
        Assert.False(client.IsRunning);

        _settings.Value.Enabled = true;
        _settings.Value.ConnectToken = "";
        await client.StartAsync();
        Assert.False(client.IsRunning);
        Assert.NotNull(client.MissingSettings());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());

        _settings.Value.ConnectToken = Token;
        await client.StartAsync();
        await _relay.WaitForConnectionAsync();
        await client.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, client.State);
        Assert.True(_relay.Latest.Disposed);
    }

    [Fact]
    public void Malformed_deliveries_are_rejected_by_the_parser()
    {
        static bool Parse(string json) => RelayClient.TryParseDelivery(JsonDocument.Parse(json).RootElement, out _, out _);
        Assert.False(Parse("""{"type":"delivery","service":"kofi","bodyBase64":""}"""));
        Assert.False(Parse("""{"type":"delivery","id":"x","service":"kofi","bodyBase64":"%%%"}"""));
        Assert.True(Parse("""{"type":"delivery","id":"x","service":"KoFi","bodyBase64":""}"""));
        RelayClient.TryParseDelivery(JsonDocument.Parse("""{"id":"x","service":"KoFi","headers":{"X-A":"1"},"bodyBase64":"aGk="}""").RootElement, out var d, out _);
        Assert.Equal("kofi", d.Service);
        Assert.Equal("1", d.Header("x-a"));
        Assert.Equal("hi", Encoding.UTF8.GetString(d.Body));
    }

    [Fact]
    public void AddRelay_registers_the_client_with_every_handler_and_as_a_connection_status()
    {
        var paths = new AppPaths(_dir);
        var services = new ServiceCollection()
            .AddSingleton(paths)
            .AddSingleton(_log)
            .AddSingleton(_bus)
            .AddSingleton<IWebhookHandler>(new FakeHandler("kofi"))
            .AddSingleton<IWebhookHandler>(new FakeHandler("kick"))
            .AddRelay()
            .BuildServiceProvider();
        var client = services.GetRequiredService<RelayClient>();
        Assert.Equal(["KICK", "KOFI"], client.Handlers.Select(h => h.DisplayName));
        Assert.Contains(services.GetServices<IConnectionStatus>(), s => ReferenceEquals(s, client));
    }
}
