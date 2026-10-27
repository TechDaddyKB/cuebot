using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core.Logging;
using Cuebot.Kick.Pusher;
using Cuebot.Kick.Realtime;
using Cuebot.Kick.Tests.Support;

namespace Cuebot.Kick.Tests;

/// <summary>
/// kick.com's real-time protocol: Centrifugo JSON commands (connect, subscribe, ping, refresh, errors, reconnects)
/// carrying Kick's <c>{"event", "data"}</c> envelopes.
/// </summary>
public sealed class RealtimeProtocolTests
{
    private static readonly Uri Url = new("wss://realtime.test/connection/websocket");
    private readonly AppLog _log = new();
    private readonly FakeRealtimeServer _server = new();
    private int _credentialCalls;
    private int _refreshCalls;

    private CentrifugoClient Client() => new(_server.Create,
        (_, _) => Task.FromResult(new KickRealtimeCredentials(Url, $"token-{Interlocked.Increment(ref _credentialCalls)}")),
        (_, _) => Task.FromResult($"refreshed-{Interlocked.Increment(ref _refreshCalls)}"),
        TestTimings.Fast, _log);

    [Fact]
    public void Envelopes_with_string_encoded_data_become_frames()
    {
        var pub = JsonDocument.Parse("""{"data":{"event":"App\\Events\\ChatMessageEvent","data":"{\"id\":\"x\"}"}}""").RootElement;
        var frame = CentrifugoClient.ToFrame("chatrooms.1.v2", pub)!;
        Assert.Equal("ChatMessageEvent", frame.ShortEvent);
        Assert.Equal("chatrooms.1.v2", frame.Channel);
        Assert.Equal("""{"id":"x"}""", frame.Data);
    }

    [Fact]
    public void Envelopes_that_are_themselves_encoded_or_carry_object_data_become_frames()
    {
        var encoded = JsonDocument.Parse("""{"data":"{\"event\":\"StreamerIsLive\",\"data\":{\"livestream\":{\"id\":1}}}"}""").RootElement;
        var frame = CentrifugoClient.ToFrame("channel.2", encoded)!;
        Assert.Equal("StreamerIsLive", frame.Event);
        Assert.Equal("""{"livestream":{"id":1}}""", frame.Data);
    }

    [Theory]
    [InlineData("""{"data":{"no":"event"}}""")]
    [InlineData("""{"data":"not json"}""")]
    [InlineData("""{"offset":3}""")]
    public void Publications_without_an_envelope_are_ignored(string pub) =>
        Assert.Null(CentrifugoClient.ToFrame("c", JsonDocument.Parse(pub).RootElement));

    [Fact]
    public void Commands_follow_the_protocol()
    {
        var subscribe = JsonDocument.Parse(CentrifugoClient.Command("subscribe", new { channel = "chatrooms.42.v2", flag = 1 }, 7)).RootElement;
        Assert.Equal(7, subscribe.GetProperty("id").GetInt32());
        Assert.Equal("chatrooms.42.v2", subscribe.GetProperty("subscribe").GetProperty("channel").GetString());
    }

    [Theory]
    [InlineData(3501, "Fatal")]
    [InlineData(4501, "Fatal")]
    [InlineData(3005, "Backoff")]
    [InlineData(4004, "Backoff")]
    [InlineData(1006, "Backoff")]
    [InlineData(0, "Backoff")]
    public void Close_codes_are_classified(int code, string expected) =>
        Assert.Equal(Enum.Parse<CentrifugoReconnect>(expected), CentrifugoClient.Classify(code));

    [Fact]
    public async Task Connects_with_the_token_subscribes_to_every_channel_and_raises_events()
    {
        var client = Client();
        var messages = new ConcurrentQueue<PusherFrame>();
        var subscribed = new ConcurrentQueue<string>();
        string? clientId = null;
        client.Message += messages.Enqueue;
        client.SubscriptionSucceeded += subscribed.Enqueue;
        client.Connected += c => clientId = c;
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(["chatrooms.1.v2", "channel.2"], cts.Token);

        var conn = await _server.NextConnectionAsync();
        Assert.Equal(Url, conn.Url);
        Assert.Equal("token-1", (await conn.EstablishAsync()).GetProperty("token").GetString());
        var first = await conn.NextSentAsync();
        var second = await conn.NextSentAsync();
        Assert.Equal("chatrooms.1.v2", first.GetProperty("subscribe").GetProperty("channel").GetString());
        Assert.Equal("channel.2", second.GetProperty("subscribe").GetProperty("channel").GetString());
        await Wait.UntilAsync(() => clientId == "client-1", "connected");

        conn.Push($$$"""{"id":{{{first.GetProperty("id").GetInt32()}}},"subscribe":{}}""");
        conn.Push($$$"""{"id":{{{second.GetProperty("id").GetInt32()}}},"error":{"code":103,"message":"permission denied"}}""");
        conn.Event("App\\Events\\ChatMessageEvent", "chatrooms.1.v2", new { id = "m1" });
        await Wait.UntilAsync(() => messages.Count == 1, "the channel event");
        Assert.Equal("ChatMessageEvent", messages.Single().ShortEvent);
        Assert.Equal(["chatrooms.1.v2"], subscribed);

        await cts.CancelAsync();
        await run.WaitAsync(Wait.Timeout);
        Assert.True(conn.Disposed);
    }

    [Fact]
    public async Task Answers_server_pings_and_reads_batched_messages()
    {
        var client = Client();
        var messages = new ConcurrentQueue<PusherFrame>();
        client.Message += messages.Enqueue;
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(["c"], cts.Token);
        var conn = await _server.NextConnectionAsync();
        await conn.EstablishAsync();
        await conn.NextSentAsync(); // subscribe

        conn.Push("{}");
        Assert.Equal(JsonValueKind.Object, (await conn.NextSentAsync()).ValueKind);
        conn.Push(FakeRealtimeTransport.Frame("A", "c", new { n = 1 }) + "\n" + FakeRealtimeTransport.Frame("B", "c", new { n = 2 }));
        await Wait.UntilAsync(() => messages.Count == 2, "both batched events");
        await cts.CancelAsync();
        await run.WaitAsync(Wait.Timeout);
    }

    [Fact]
    public async Task Reconnects_with_new_credentials_when_the_server_goes_silent()
    {
        var client = Client();
        var reasons = new ConcurrentQueue<string>();
        client.Disconnected += reasons.Enqueue;
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(["chatrooms.9.v2"], cts.Token);
        var conn = await _server.NextConnectionAsync();
        await conn.EstablishAsync(ping: 1);
        await conn.NextSentAsync();

        var again = await _server.NextConnectionAsync(); // after ~1.2 s without a ping
        Assert.Contains("ping", reasons.Single());
        Assert.Equal("token-2", (await again.EstablishAsync()).GetProperty("token").GetString());
        Assert.Equal("chatrooms.9.v2", await again.NextSubscribedChannelAsync());
        await cts.CancelAsync();
        await run.WaitAsync(Wait.Timeout);
    }

    [Fact]
    public async Task Refreshes_the_token_before_it_expires()
    {
        var client = Client();
        var disconnects = 0;
        client.Disconnected += _ => Interlocked.Increment(ref disconnects);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(["c"], cts.Token);
        var conn = await _server.NextConnectionAsync();
        await conn.EstablishAsync(ttl: 1); // refresh right away (ttl is shorter than the margin)
        await conn.NextSentAsync(); // subscribe

        var refresh = await conn.NextSentAsync();
        Assert.Equal("refreshed-1", refresh.GetProperty("refresh").GetProperty("token").GetString());
        conn.Push($$$"""{"id":{{{refresh.GetProperty("id").GetInt32()}}},"refresh":{"expires":true,"ttl":3600}}""");
        await Task.Delay(300);
        Assert.Equal(0, disconnects);
        Assert.Equal(1, _server.Attempts);
        await cts.CancelAsync();
        await run.WaitAsync(Wait.Timeout);
    }

    [Fact]
    public async Task Reconnects_after_the_server_closes_and_after_connect_failures()
    {
        _server.FailNextConnects(2);
        var client = Client();
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(["c"], cts.Token);

        var conn = await _server.NextConnectionAsync();
        Assert.Equal(3, _server.Attempts);
        await conn.EstablishAsync();
        await conn.NextSentAsync();
        conn.Close(3005, "please reconnect");

        var next = await _server.NextConnectionAsync();
        await next.EstablishAsync();
        await next.NextSentAsync();
        Assert.Equal(4, _server.Attempts);
        await cts.CancelAsync();
        await run.WaitAsync(Wait.Timeout);
    }

    [Fact]
    public async Task Connect_errors_and_disconnect_pushes_end_the_session()
    {
        var client = Client();
        var session = client.RunSessionAsync(["c"], () => { }, default);
        var conn = await _server.NextConnectionAsync();
        var connect = await conn.NextSentAsync();
        conn.Push($$$"""{"id":{{{connect.GetProperty("id").GetInt32()}}},"error":{"code":109,"message":"token expired"}}""");
        var end = await session.WaitAsync(Wait.Timeout);
        Assert.Equal(CentrifugoReconnect.Immediately, end.Reconnect);
        Assert.Contains("token expired", end.Reason);

        session = client.RunSessionAsync(["c"], () => { }, default);
        conn = await _server.NextConnectionAsync();
        await conn.EstablishAsync();
        await conn.NextSentAsync();
        conn.Push("""{"push":{"disconnect":{"code":3501,"reason":"bad request"}}}""");
        end = await session.WaitAsync(Wait.Timeout);
        Assert.Equal(CentrifugoReconnect.Fatal, end.Reconnect);
    }

    [Fact]
    public async Task Malformed_messages_are_ignored()
    {
        var client = Client();
        var messages = new ConcurrentQueue<PusherFrame>();
        client.Message += messages.Enqueue;
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(["c"], cts.Token);
        var conn = await _server.NextConnectionAsync();
        await conn.EstablishAsync();
        await conn.NextSentAsync();
        conn.Push("garbage");
        conn.Push("""{"push":{"channel":"c","pub":{"data":{"no":"event"}}}}""");
        conn.Event("App\\Events\\FollowersUpdated", "channel.1", new { followersCount = 3 });
        await Wait.UntilAsync(() => messages.Count == 1, "the valid event");
        Assert.Equal(1, _server.Attempts);
        await cts.CancelAsync();
        await run.WaitAsync(Wait.Timeout);
    }
}
