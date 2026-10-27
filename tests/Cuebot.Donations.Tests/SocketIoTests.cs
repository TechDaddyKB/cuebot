using System.Text.Json;
using Cuebot.Core.Logging;
using Cuebot.Donations.SocketIo;
using Cuebot.Donations.Tests.Support;

namespace Cuebot.Donations.Tests;

public sealed class SocketIoFramingTests
{
    [Fact]
    public void Parses_the_engine_io_open_packet()
    {
        var open = SocketIoFraming.ParseOpen("""0{"sid":"abc","upgrades":[],"pingInterval":25000,"pingTimeout":20000,"maxPayload":1000000}""");
        Assert.NotNull(open);
        Assert.Equal("abc", open.Sid);
        Assert.Equal(TimeSpan.FromSeconds(25), open.PingInterval);
        Assert.Equal(TimeSpan.FromSeconds(20), open.PingTimeout);
        Assert.Null(SocketIoFraming.ParseOpen("40"));
        Assert.Null(SocketIoFraming.ParseOpen("0not json"));
    }

    [Theory]
    [InlineData("40", (int)SocketIoType.Connect, "/", null, "")]
    [InlineData("""40{"sid":"x"}""", (int)SocketIoType.Connect, "/", null, """{"sid":"x"}""")]
    [InlineData("41", (int)SocketIoType.Disconnect, "/", null, "")]
    [InlineData("""42["event",{"a":1}]""", (int)SocketIoType.Event, "/", null, """["event",{"a":1}]""")]
    [InlineData("""4212["event",1]""", (int)SocketIoType.Event, "/", 12L, """["event",1]""")]
    [InlineData("""42/admin,["x"]""", (int)SocketIoType.Event, "/admin", null, """["x"]""")]
    [InlineData("""42/admin,7["x"]""", (int)SocketIoType.Event, "/admin", 7L, """["x"]""")]
    [InlineData("""44{"message":"Not authorized"}""", (int)SocketIoType.ConnectError, "/", null, """{"message":"Not authorized"}""")]
    [InlineData("""451-["bin",{"_placeholder":true,"num":0}]""", (int)SocketIoType.BinaryEvent, "/", null, """["bin",{"_placeholder":true,"num":0}]""")]
    public void Parses_socket_io_packets(string frame, int type, string ns, long? ack, string data)
    {
        var packet = SocketIoFraming.ParsePacket(frame);
        Assert.NotNull(packet);
        Assert.Equal(((SocketIoType)type, ns, ack, data), (packet.Type, packet.Namespace, packet.AckId, packet.Data));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("4")]
    [InlineData("4x")]
    public void Rejects_non_message_frames(string frame) => Assert.Null(SocketIoFraming.ParsePacket(frame));

    [Fact]
    public void Reads_event_names_arguments_and_connect_errors()
    {
        var ev = SocketIoFraming.ParsePacket("""42["event",{"type":"donation"},2]""")!.ReadEvent();
        Assert.NotNull(ev);
        Assert.Equal("event", ev.Value.Name);
        Assert.Equal(2, ev.Value.Args.Count);
        Assert.Equal("donation", ev.Value.Args[0].GetProperty("type").GetString());
        Assert.Null(SocketIoFraming.ParsePacket("""42[1,2]""")!.ReadEvent());
        Assert.Null(SocketIoFraming.ParsePacket("""42{}""")!.ReadEvent());

        Assert.Equal("Not authorized", SocketIoFraming.ParsePacket("""44{"message":"Not authorized"}""")!.ErrorMessage());
        Assert.Equal("Invalid namespace", SocketIoFraming.ParsePacket("""44"Invalid namespace" """.Trim())!.ErrorMessage());
    }

    [Fact]
    public void Formats_connect_and_event_packets()
    {
        Assert.Equal("40", SocketIoFraming.Connect());
        Assert.Equal("""40{"token":"t"}""", SocketIoFraming.Connect("""{"token":"t"}"""));
        Assert.Equal("""42["authenticate",{"method":"jwt","token":"abc"}]""",
            SocketIoFraming.Event("authenticate", new { method = "jwt", token = "abc" }));
    }

    [Fact]
    public void Builds_websocket_urls_with_the_protocol_version()
    {
        var url = SocketIoFraming.Url(new Uri("https://sockets.streamlabs.com"), 4, new Dictionary<string, string> { ["token"] = "a b+c" });
        Assert.Equal("wss://sockets.streamlabs.com/socket.io/?token=a%20b%2Bc&EIO=4&transport=websocket", url.AbsoluteUri);
        Assert.Equal("ws://localhost:3000/socket.io/?EIO=3&transport=websocket",
            SocketIoFraming.Url(new Uri("http://localhost:3000/"), 3, new Dictionary<string, string>()).AbsoluteUri);
    }
}

public sealed class SocketIoClientTests
{
    private const string Open = """0{"sid":"s1","upgrades":[],"pingInterval":200,"pingTimeout":200,"maxPayload":1000000}""";
    private readonly FakeServer _server = new();
    private readonly AppLog _log = new();

    private SocketIoClient Client() => new(_server.Create, TestTimings.Fast, _log, "Test");

    private static Uri Url(int eio) => new($"wss://socket.test/socket.io/?EIO={eio}&transport=websocket");

    [Fact]
    public async Task Eio4_handshake_connects_the_namespace_answers_pings_and_raises_events()
    {
        var client = Client();
        var connected = new TaskCompletionSource<int>();
        var events = new List<(string, string)>();
        client.Connected += eio => connected.TrySetResult(eio);
        client.EventReceived += (name, args) => { lock (events) events.Add((name, args[0].GetRawText())); };
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(Url, cts.Token);

        var socket = await _server.NextAsync();
        Assert.Contains("EIO=4", socket.Url!.Query);
        socket.Push(Open);
        Assert.Equal("40", await socket.NextSentAsync());
        socket.Push("""40{"sid":"n1"}""");
        Assert.Equal(4, await connected.Task.WaitAsync(Wait.Timeout));

        socket.Push("2");
        Assert.Equal("3", await socket.NextSentAsync());
        socket.Push("""42["event",{"type":"donation"}]""");
        socket.Push("""42/other,["event",{"type":"ignored"}]""");
        socket.Push("6");
        await Wait.UntilAsync(() => { lock (events) return events.Count == 1; }, "event");
        Assert.Equal(("event", """{"type":"donation"}"""), events[0]);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Eio4_without_server_pings_times_out_and_reconnects()
    {
        var client = Client();
        var disconnects = new List<string>();
        client.Disconnected += r => { lock (disconnects) disconnects.Add(r); };
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(Url, cts.Token);

        var first = await _server.NextAsync();
        first.Push(Open);
        await first.NextSentAsync();
        first.Push("40");
        // No pings for pingInterval + pingTimeout (400 ms): the client gives up and opens a new connection.
        var second = await _server.NextAsync();
        Assert.Contains("no ping", Assert.Single(disconnects));
        Assert.Contains("EIO=4", second.Url!.Query);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Eio3_sends_its_own_pings_and_expects_pongs()
    {
        var client = Client();
        client.EngineIoVersion = 3;
        var connected = new TaskCompletionSource<int>();
        client.Connected += eio => connected.TrySetResult(eio);
        var sentOnConnect = new TaskCompletionSource();
        client.OnConnectedAsync = async (send, _) => { await send("""42["authenticate",{}]"""); sentOnConnect.TrySetResult(); };
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(Url, cts.Token);

        var socket = await _server.NextAsync();
        Assert.Contains("EIO=3", socket.Url!.Query);
        socket.Push(Open);
        Assert.Equal(3, await connected.Task.WaitAsync(Wait.Timeout));   // v2 servers connect "/" on their own
        Assert.Equal("""42["authenticate",{}]""", await socket.NextSentAsync());
        socket.Push("40");   // ignored: already connected

        Assert.Equal("2", await socket.NextSentAsync());   // after pingInterval
        socket.Push("3");
        Assert.Equal("2", await socket.NextSentAsync());
        // No pong this time: the client reconnects.
        var second = await _server.NextAsync();
        Assert.NotSame(socket, second);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Falls_back_to_the_other_protocol_version_when_the_upgrade_is_refused()
    {
        _server.Reject = url => url.Query.Contains("EIO=4", StringComparison.Ordinal);
        var client = Client();
        var connected = new TaskCompletionSource<int>();
        client.Connected += eio => connected.TrySetResult(eio);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(Url, cts.Token);

        var socket = await _server.NextAsync();
        Assert.Contains("EIO=3", socket.Url!.Query);
        socket.Push(Open);
        Assert.Equal(3, await connected.Task.WaitAsync(Wait.Timeout));
        Assert.Equal(3, client.EngineIoVersion);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Server_close_disconnect_and_connect_error_end_the_session()
    {
        var client = Client();
        Assert.Equal("server closed the session", (await Session(client, s => s.Push("1"))).Reason);
        Assert.Equal("server disconnected the socket", (await Session(client, s => s.Push("41"))).Reason);
        var refused = await Session(client, s => s.Push("""44{"message":"invalid token"}"""), connect: false);
        Assert.True(refused.Fatal);
        Assert.Contains("invalid token", refused.Reason);
        Assert.Equal("server closed the connection (1000)", (await Session(client, s => s.Close())).Reason);
    }

    [Fact]
    public async Task A_missing_handshake_is_reported_as_not_opened()
    {
        var client = Client();
        var task = client.RunSessionAsync(Url(4), 4, CancellationToken.None);
        var socket = await _server.NextAsync();
        socket.Push("""42["early"]""");
        var end = await task.WaitAsync(Wait.Timeout);
        Assert.False(end.Opened);
    }

    private async Task<SocketIoSessionEnd> Session(SocketIoClient client, Action<FakeSocket> act, bool connect = true)
    {
        var task = client.RunSessionAsync(Url(4), 4, CancellationToken.None);
        var socket = await _server.NextAsync();
        socket.Push(Open);
        Assert.Equal("40", await socket.NextSentAsync());
        if (connect) socket.Push("40");
        act(socket);
        var end = await task.WaitAsync(Wait.Timeout);
        Assert.True(end.Opened);
        Assert.True(socket.Disposed);
        return end;
    }
}
