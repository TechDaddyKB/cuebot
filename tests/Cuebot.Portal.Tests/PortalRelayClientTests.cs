using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Webhooks;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;

namespace Cuebot.Portal.Tests;

public sealed class PortalRelayClientTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly PortalRelayTimings Fast = new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(95));

    /// <summary>The relay's side of one connection, scripted by the test.</summary>
    private sealed class FakeRelay : IPortalRelayTransport
    {
        public readonly Channel<RelayMessage?> ToCuebot = Channel.CreateUnbounded<RelayMessage?>();
        public readonly Channel<RelayMessage> FromCuebot = Channel.CreateUnbounded<RelayMessage>();
        public Uri? Url;

        public int? CloseCode { get; set; }
        public string? CloseReason { get; set; }

        public Task ConnectAsync(Uri url, CancellationToken ct)
        {
            Url = url;
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken ct) => FromCuebot.Writer.WriteAsync(new(false, Encoding.UTF8.GetBytes(text)), ct).AsTask();

        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => FromCuebot.Writer.WriteAsync(new(true, data.ToArray()), ct).AsTask();

        public async Task<RelayMessage?> ReceiveAsync(CancellationToken ct) => await ToCuebot.Reader.ReadAsync(ct);

        public void Text(object json) => ToCuebot.Writer.TryWrite(new(false, JsonSerializer.SerializeToUtf8Bytes(json)));

        public void Binary(uint ch, byte[] payload) => ToCuebot.Writer.TryWrite(new(true, PortalRelayClient.Frame(ch, payload)));

        public void Close(int code)
        {
            CloseCode = code;
            ToCuebot.Writer.TryWrite(null);
        }

        public async Task<RelayMessage> NextAsync()
        {
            using var timeout = new CancellationTokenSource(Wait);
            return await FromCuebot.Reader.ReadAsync(timeout.Token);
        }

        public async Task<JsonNode> NextJsonAsync()
        {
            var m = await NextAsync();
            Assert.False(m.Binary);
            return JsonNode.Parse(m.Data)!;
        }

        public async Task<(uint Ch, byte[] Payload)> NextFrameAsync()
        {
            var m = await NextAsync();
            Assert.True(m.Binary);
            return (BinaryPrimitives.ReadUInt32BigEndian(m.Data), m.Data[4..]);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly PortalSettings _settings = new() { Enabled = true };
    private readonly PortalDirectory _directory;
    private readonly AppLog _log = new();
    private readonly Channel<FakeRelay> _relays = Channel.CreateUnbounded<FakeRelay>();
    private readonly PortalRelayClient _client;

    public PortalRelayClientTests()
    {
        _directory = new PortalDirectory(_settings, () => { });
        _client = new PortalRelayClient(_directory, _log, () =>
        {
            var relay = new FakeRelay();
            _relays.Writer.TryWrite(relay);
            return relay;
        }, Fast);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _log.Dispose();
    }

    private async Task<FakeRelay> NextRelayAsync()
    {
        using var timeout = new CancellationTokenSource(Wait);
        return await _relays.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>Connects and plays the relay's half of the challenge, checking Cuebot's answer like the Worker does.</summary>
    private async Task<FakeRelay> ConnectAsync()
    {
        await _client.ConnectAsync();
        var relay = await NextRelayAsync();
        var nonce = RandomNumberGenerator.GetBytes(32);
        relay.Text(new { type = "challenge", version = 1, nonce = PortalKeys.Base64Url(nonce) });
        var auth = await relay.NextJsonAsync();
        Assert.Equal("auth", (string?)auth["type"]);
        var key = PortalKeys.FromBase64Url((string)auth["key"]!);
        Assert.Equal(_directory.RelayId, PortalKeys.RelayId(key));
        byte[] signed = [.. Encoding.UTF8.GetBytes(PortalRelayClient.ConnectLabel), .. nonce, .. Encoding.UTF8.GetBytes(relay.Url!.Authority)];
        Assert.True(PortalKeys.Verify(key, signed, PortalKeys.FromBase64Url((string)auth["signature"]!)));
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(_directory.RouteSecret)), PortalKeys.FromBase64Url((string)auth["hooks"]!));
        relay.Text(new { type = "ready", version = 1, maxDevices = 20 });
        await Until(() => _client.State == ConnectionState.Connected);
        return relay;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Addresses_use_the_shared_relay_unless_another_is_set()
    {
        var id = _directory.RelayId;
        Assert.Equal($"wss://relay.techdaddydigital.com/connect?id={id}", PortalRelayClient.ConnectUri("", id)!.ToString());
        Assert.Equal($"wss://cuebot-portal.x.workers.dev/connect?id={id}", PortalRelayClient.ConnectUri("https://cuebot-portal.x.workers.dev/", id)!.ToString());
        Assert.Equal($"ws://localhost:8787/connect?id={id}", PortalRelayClient.ConnectUri("http://localhost:8787", id)!.ToString());
        Assert.Null(PortalRelayClient.ConnectUri("ftp://nope", id));

        Assert.Null(_client.WebhookUrl("kofi"));
        _directory.SharedWebhooks = true;
        Assert.Equal($"https://hooks.techdaddydigital.com/{id}/{_directory.RouteSecret}/kofi", _client.WebhookUrl("kofi"));
        _directory.RelayUrl = "https://cuebot-portal.x.workers.dev";
        Assert.Equal($"https://cuebot-portal.x.workers.dev/h/{id}/{_directory.RouteSecret}/kofi", _client.WebhookUrl("kofi"));
    }

    [Fact]
    public void The_route_secret_is_made_once_and_can_be_rotated()
    {
        var first = _directory.RouteSecret;
        Assert.True(first.Length >= 40);
        Assert.Equal(first, _directory.RouteSecret);
        _directory.RotateRouteSecret();
        Assert.NotEqual(first, _directory.RouteSecret);
    }

    [Fact]
    public async Task Proves_its_identity_and_carries_an_encrypted_device_channel()
    {
        var relay = await ConnectAsync();
        relay.Text(new { type = "open", ch = 77u });

        // The device: handshake against the pinned fingerprint, then a ping.
        var (ephemeral, hello) = PortalHandshake.Start();
        relay.Binary(77, hello);
        var (ch, serverHello) = await relay.NextFrameAsync();
        Assert.Equal(77u, ch);
        using var session = PortalHandshake.Finish(ephemeral, serverHello, _directory.Identity.Fingerprint);
        ephemeral.Dispose();

        relay.Binary(77, session.Encrypt("""{"op":"ping"}"""u8));
        var (_, reply) = await relay.NextFrameAsync();
        var answer = JsonNode.Parse(session.Decrypt(reply))!;
        Assert.Equal("pong", (string?)answer["op"]);

        relay.Binary(77, session.Encrypt("""{"op":"actions.list"}"""u8));
        var (_, later) = await relay.NextFrameAsync();
        Assert.Equal("error", (string?)JsonNode.Parse(session.Decrypt(later))!["op"]);
        Assert.Equal(1, _client.DeviceCount);

        relay.Text(new { type = "close", ch = 77u });
        await Until(() => _client.DeviceCount == 0);
    }

    [Fact]
    public async Task Closes_a_device_that_breaks_the_secure_channel()
    {
        var relay = await ConnectAsync();
        relay.Text(new { type = "open", ch = 5u });
        relay.Binary(5, [1, 2, 3]);
        var close = await relay.NextJsonAsync();
        Assert.Equal("close", (string?)close["type"]);
        Assert.Equal(5u, (uint)close["ch"]!);
        Assert.Equal(PortalRelayClient.ProtocolCloseCode, (int)close["code"]!);
    }

    [Fact]
    public async Task Turns_devices_away_while_the_portal_is_closed()
    {
        _directory.Enabled = false;
        _directory.SharedWebhooks = true;
        var relay = await ConnectAsync();
        relay.Text(new { type = "open", ch = 9u });
        var close = await relay.NextJsonAsync();
        Assert.Equal(PortalRelayClient.PortalClosedCloseCode, (int)close["code"]!);
    }

    [Fact]
    public async Task Opens_sealed_webhooks_and_hands_them_on()
    {
        _directory.SharedWebhooks = true;
        var received = Channel.CreateUnbounded<WebhookDelivery>();
        _client.WebhookSink = (d, ct) => received.Writer.WriteAsync(d, ct).AsTask();
        var relay = await ConnectAsync();

        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "delivery", id = "d1", service = "kofi", path = "", method = "POST", query = "",
            headers = new Dictionary<string, string> { ["content-type"] = "application/x-www-form-urlencoded" },
            bodyBase64 = Convert.ToBase64String("data=hi"u8.ToArray()), receivedAt = "2026-11-05T12:00:00Z",
        });
        relay.Binary(PortalRelayClient.HookChannel, PortalSeal.Seal(_directory.Identity.PublicKey, json));
        relay.Binary(PortalRelayClient.HookChannel, [1, 2, 3]);   // not sealed: ignored

        using var timeout = new CancellationTokenSource(Wait);
        var delivery = await received.Reader.ReadAsync(timeout.Token);
        Assert.Equal("kofi", delivery.Service);
        Assert.Equal("data=hi", delivery.BodyText);
        Assert.Equal("application/x-www-form-urlencoded", delivery.Header("Content-Type"));
    }

    [Fact]
    public async Task Stops_for_good_when_the_relay_refuses_the_key()
    {
        await _client.ConnectAsync();
        var relay = await NextRelayAsync();
        relay.Close(PortalRelayClient.AuthFailedCloseCode);
        await Until(() => _client.State == ConnectionState.Error);
        Assert.Contains("identity key", _client.StatusMessage);
    }

    [Fact]
    public async Task Reconnects_after_the_relay_drops_and_gives_up_when_replaced()
    {
        var relay = await ConnectAsync();
        relay.Close(1006);
        var second = await NextRelayAsync();
        Assert.NotSame(relay, second);
        second.Close(PortalRelayClient.ReplacedCloseCode);   // closed during the challenge: an ordinary failure, retried
        var third = await NextRelayAsync();
        Assert.NotNull(third);
    }

    // ---- Kick events through the shared intake (built-in Kick app) ----

    private bool _kickWanted = true;

    private void KickRouting(string? token = "kick-user-token")
    {
        _directory.Enabled = false;
        _client.KickRouteWanted = () => _kickWanted;
        _client.KickRouteToken = _ => Task.FromResult(_kickWanted ? token : null);
    }

    [Fact]
    public async Task Registers_the_Kick_route_on_every_connection_and_removes_it_when_no_longer_wanted()
    {
        KickRouting();
        var statuses = new List<string>();
        _client.KickRouteStatusChanged += statuses.Add;
        await _client.StartAsync();   // Kick events alone keep the relay up
        var relay = await ConnectAfterStartAsync();

        var route = await relay.NextJsonAsync();
        Assert.Equal("kick-route", (string?)route["type"]);
        Assert.Equal("kick-user-token", (string?)route["token"]);
        relay.Text(new { type = "kick-route", rid = (int)route["rid"]!, ok = true, userId = 4242 });
        await Until(() => _client.KickRouteStatus == "Kick events arrive through Cuebot's relay");

        _kickWanted = false;
        _directory.SharedWebhooks = true;   // keeps the connection, so the route can be removed
        await _client.RefreshKickRouteAsync();
        var unroute = await relay.NextJsonAsync();
        Assert.Equal("kick-unroute", (string?)unroute["type"]);
        Assert.Equal(4242, (long)unroute["userId"]!);
        Assert.DoesNotContain(statuses, s => s.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reports_a_refused_Kick_route()
    {
        KickRouting();
        await _client.StartAsync();
        var relay = await ConnectAfterStartAsync();
        var route = await relay.NextJsonAsync();
        relay.Text(new { type = "kick-route", rid = (int)route["rid"]!, ok = false, error = "The token is not for Cuebot's Kick app" });
        await Until(() => _client.KickRouteStatus.Contains("not for Cuebot's Kick app", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Kick_events_get_through_without_shared_webhooks_but_other_services_do_not()
    {
        KickRouting();
        var received = Channel.CreateUnbounded<WebhookDelivery>();
        _client.WebhookSink = (d, ct) => received.Writer.WriteAsync(d, ct).AsTask();
        await _client.StartAsync();
        var relay = await ConnectAfterStartAsync();
        await relay.NextJsonAsync();   // the route request

        byte[] Delivery(string id, string service) => PortalSeal.Seal(_directory.Identity.PublicKey, JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "delivery", id, service, path = "", method = "POST", query = "", headers = new Dictionary<string, string>(),
            bodyBase64 = Convert.ToBase64String("{}"u8.ToArray()), receivedAt = "2026-11-11T12:00:00Z",
        }));
        relay.Binary(PortalRelayClient.HookChannel, Delivery("d1", "kofi"));
        relay.Binary(PortalRelayClient.HookChannel, Delivery("d2", "kick"));

        using var timeout = new CancellationTokenSource(Wait);
        var delivery = await received.Reader.ReadAsync(timeout.Token);
        Assert.Equal(("d2", "kick"), (delivery.Id, delivery.Service));
    }

    /// <summary>Plays the relay's half of the challenge for a connection that StartAsync opened.</summary>
    private async Task<FakeRelay> ConnectAfterStartAsync()
    {
        var relay = await NextRelayAsync();
        relay.Text(new { type = "challenge", version = 1, nonce = PortalKeys.Base64Url(RandomNumberGenerator.GetBytes(32)) });
        Assert.Equal("auth", (string?)(await relay.NextJsonAsync())["type"]);
        relay.Text(new { type = "ready", version = 1, maxDevices = 20 });
        await Until(() => _client.State == ConnectionState.Connected);
        return relay;
    }

    [Fact]
    public async Task Starts_only_when_the_portal_or_shared_webhooks_are_on()
    {
        _directory.Enabled = false;
        await _client.StartAsync();
        Assert.Equal(ConnectionState.Disconnected, _client.State);
        _directory.SharedWebhooks = true;
        await _client.StartAsync();
        Assert.NotNull(await NextRelayAsync());
        _directory.SharedWebhooks = false;
        await _client.StartAsync();
        Assert.Equal(ConnectionState.Disconnected, _client.State);
    }
}
