using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core.Logging;
using Cuebot.Kick.Pusher;
using Cuebot.Kick.Tests.Support;

namespace Cuebot.Kick.Tests;

/// <summary>Pusher protocol 7 as used by kick.com: handshake, subscribe, ping/pong, errors and reconnects.</summary>
public sealed class PusherProtocolTests
{
    private static readonly Uri Url = new("wss://ws-us2.pusher.test/app/key?protocol=7");
    private readonly AppLog _log = new();
    private readonly FakePusherServer _server = new();

    [Fact]
    public void Frames_with_string_encoded_data_are_decoded()
    {
        var frame = PusherFrame.Parse("""{"event":"App\\Events\\ChatMessageEvent","data":"{\"id\":\"x\"}","channel":"chatrooms.1.v2"}""");
        Assert.Equal("App\\Events\\ChatMessageEvent", frame.Event);
        Assert.Equal("ChatMessageEvent", frame.ShortEvent);
        Assert.Equal("chatrooms.1.v2", frame.Channel);
        Assert.Equal("""{"id":"x"}""", frame.Data);
    }

    [Fact]
    public void Frames_with_object_data_and_no_channel_are_decoded()
    {
        var frame = PusherFrame.Parse("""{"event":"pusher:ping","data":{}}""");
        Assert.Equal(PusherFrame.Ping, frame.Event);
        Assert.Null(frame.Channel);
        Assert.Equal("{}", frame.Data);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"data":"x"}""")]
    [InlineData("not json")]
    public void Non_pusher_messages_are_rejected(string text) =>
        Assert.ThrowsAny<JsonException>(() => PusherFrame.Parse(text));

    [Fact]
    public void Outgoing_messages_follow_the_protocol()
    {
        var subscribe = JsonDocument.Parse(PusherFrame.SubscribeMessage("chatrooms.42.v2")).RootElement;
        Assert.Equal("pusher:subscribe", subscribe.GetProperty("event").GetString());
        Assert.Equal("chatrooms.42.v2", subscribe.GetProperty("data").GetProperty("channel").GetString());
        Assert.Equal("", subscribe.GetProperty("data").GetProperty("auth").GetString());
        Assert.Equal("pusher:pong", JsonDocument.Parse(PusherFrame.PongMessage).RootElement.GetProperty("event").GetString());
        Assert.Equal("pusher:ping", JsonDocument.Parse(PusherFrame.PingMessage).RootElement.GetProperty("event").GetString());
    }

    [Theory]
    [InlineData(4001, "Fatal")]
    [InlineData(4100, "Backoff")]
    [InlineData(4201, "Immediately")]
    [InlineData(1006, "Backoff")]
    [InlineData(0, "Backoff")]
    public void Close_codes_are_classified(int code, string expected) => Assert.Equal(Enum.Parse<PusherReconnect>(expected), PusherClient.Classify(code));

    [Fact]
    public void Established_frame_exposes_socket_and_activity_timeout()
    {
        var frame = PusherFrame.Parse(FakePusherTransport.Frame("pusher:connection_established", null, new { socket_id = "1.2", activity_timeout = 120 }));
        Assert.Equal(("1.2", 120), frame.ReadEstablished());
    }

    [Fact]
    public async Task Subscribes_to_every_channel_after_the_handshake_and_raises_events()
    {
        var client = new PusherClient(_server.Create, Url, TestTimings.Fast, _log);
        var messages = new ConcurrentQueue<PusherFrame>();
        string? socket = null;
        client.Message += messages.Enqueue;
        client.Connected += s => socket = s;
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(["chatrooms.1.v2", "channel.2"], cts.Token);

        var conn = await _server.NextConnectionAsync();
        Assert.Equal(Url, conn.Url);
        conn.Establish();
        var first = await conn.NextSentAsync();
        var second = await conn.NextSentAsync();
        Assert.Equal("pusher:subscribe", first.GetProperty("event").GetString());
        Assert.Equal("chatrooms.1.v2", first.GetProperty("data").GetProperty("channel").GetString());
        Assert.Equal("channel.2", second.GetProperty("data").GetProperty("channel").GetString());
        await Wait.UntilAsync(() => socket == "123.456", "connected");

        conn.Push("""{"event":"pusher_internal:subscription_succeeded","data":"{}","channel":"chatrooms.1.v2"}""");
        conn.Event("App\\Events\\ChatMessageEvent", "chatrooms.1.v2", new { id = "m1" });
        await Wait.UntilAsync(() => messages.Count == 1, "the channel event");
        Assert.Equal("ChatMessageEvent", messages.Single().ShortEvent);

        await cts.CancelAsync();
        await run.WaitAsync(Wait.Timeout);
        Assert.True(conn.Disposed);
    }

    [Fact]
    public async Task Answers_server_pings_with_pong()
    {
        var client = new PusherClient(_server.Create, Url, TestTimings.Fast, _log);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(["chatrooms.1.v2"], cts.Token);
        var conn = await _server.NextConnectionAsync();
        conn.Establish();
        await conn.NextSentAsync(); // subscribe

        conn.Push("""{"event":"pusher:ping","data":{}}""");
        Assert.Equal("pusher:pong", await conn.NextSentEventAsync());
        await cts.CancelAsync();
        await run.WaitAsync(Wait.Timeout);
    }

    [Fact]
    public async Task Pings_after_activity_timeout_and_stays_connected_when_pong_arrives()
    {
        var client = new PusherClient(_server.Create, Url, TestTimings.Fast, _log);
        var disconnects = 0;
        client.Disconnected += _ => Interlocked.Increment(ref disconnects);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(["c"], cts.Token);
        var conn = await _server.NextConnectionAsync();
        conn.Establish(activityTimeout: 1);
        await conn.NextSentAsync(); // subscribe

        Assert.Equal("pusher:ping", await conn.NextSentEventAsync()); // after ~1s of silence
        conn.Push("""{"event":"pusher:pong","data":"{}"}""");
        await Task.Delay(300); // longer than the pong timeout
        Assert.Equal(0, disconnects);
        Assert.Equal(1, _server.Attempts);
        await cts.CancelAsync();
        await run.WaitAsync(Wait.Timeout);
    }

    [Fact]
    public async Task Reconnects_and_resubscribes_when_the_pong_never_comes()
    {
        var client = new PusherClient(_server.Create, Url, TestTimings.Fast, _log);
        var reasons = new ConcurrentQueue<string>();
        client.Disconnected += reasons.Enqueue;
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(["chatrooms.9.v2"], cts.Token);
        var conn = await _server.NextConnectionAsync();
        conn.Establish(activityTimeout: 1);
        await conn.NextSentAsync();
        Assert.Equal("pusher:ping", await conn.NextSentEventAsync());

        var again = await _server.NextConnectionAsync();
        Assert.Contains("pong", reasons.Single());
        again.Establish();
        var sub = await again.NextSentAsync();
        Assert.Equal("chatrooms.9.v2", sub.GetProperty("data").GetProperty("channel").GetString());
        await cts.CancelAsync();
        await run.WaitAsync(Wait.Timeout);
    }

    [Fact]
    public async Task Reconnects_after_the_server_closes_and_after_connect_failures()
    {
        _server.FailNextConnects(2);
        var client = new PusherClient(_server.Create, Url, TestTimings.Fast, _log);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(["c"], cts.Token);

        var conn = await _server.NextConnectionAsync();
        Assert.Equal(3, _server.Attempts);
        conn.Establish();
        await conn.NextSentAsync();
        conn.Close(4200, "please reconnect");

        var next = await _server.NextConnectionAsync();
        next.Establish();
        await next.NextSentAsync();
        Assert.Equal(4, _server.Attempts);
        await cts.CancelAsync();
        await run.WaitAsync(Wait.Timeout);
    }

    [Fact]
    public async Task Pusher_error_frame_ends_the_session_with_its_code()
    {
        var client = new PusherClient(_server.Create, Url, TestTimings.Fast, _log);
        var session = client.RunSessionAsync(["c"], () => { }, default);
        var conn = await _server.NextConnectionAsync();
        conn.Push(FakePusherTransport.Frame("pusher:error", null, new { code = 4001, message = "App key not found" }));
        var end = await session.WaitAsync(Wait.Timeout);
        Assert.Equal(PusherReconnect.Fatal, end.Reconnect);
        Assert.Contains("App key not found", end.Reason);
    }

    [Fact]
    public async Task Malformed_messages_are_ignored()
    {
        var client = new PusherClient(_server.Create, Url, TestTimings.Fast, _log);
        var messages = new ConcurrentQueue<PusherFrame>();
        client.Message += messages.Enqueue;
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(["c"], cts.Token);
        var conn = await _server.NextConnectionAsync();
        conn.Establish();
        await conn.NextSentAsync();
        conn.Push("garbage");
        conn.Push("""{"no":"event"}""");
        conn.Event("App\\Events\\FollowersUpdated", "channel.1", new { followersCount = 3 });
        await Wait.UntilAsync(() => messages.Count == 1, "the valid event");
        Assert.Equal(1, _server.Attempts);
        await cts.CancelAsync();
        await run.WaitAsync(Wait.Timeout);
    }
}
