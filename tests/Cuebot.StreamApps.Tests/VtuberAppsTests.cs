using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cuebot.Core;
using Cuebot.StreamApps.Obsbot;
using Cuebot.StreamApps.Tests.Support;
using Cuebot.StreamApps.Veadotube;
using Cuebot.StreamApps.Vnyan;
using Cuebot.StreamApps.Vrchat;
using Cuebot.StreamApps.Warudo;

namespace Cuebot.StreamApps.Tests;

public sealed class VeadotubeInstanceTests
{
    [Fact]
    public void Instances_are_read_filtered_and_picked()
    {
        var dir = Directory.CreateTempSubdirectory("veado-instances").FullName;
        try
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
            File.WriteAllText(Path.Combine(dir, "mini-08df3798638fd042-003c2d43"),
                $$"""{"name":"veadotube mini","id":"mini-08df3798638fd042-003c2d43","version":"2.2","language":"en","server":"127.0.0.1:40657","time":{{now.ToUnixTimeSeconds() - 3}}}""");
            File.WriteAllText(Path.Combine(dir, "veado-08df3790000fd042-00001111"),
                $$"""{"name":"veadotube","id":"veado-08df3790000fd042-00001111","version":"1.0","server":"127.0.0.1:2424","time":{{now.ToUnixTimeSeconds()}}}""");
            File.WriteAllText(Path.Combine(dir, "mini-0000000000000001-00000001"),
                $$"""{"name":"old","id":"mini-0000000000000001-00000001","server":"127.0.0.1:1","time":{{now.ToUnixTimeSeconds() - 60}}}""");
            File.WriteAllText(Path.Combine(dir, "broken"), "{not json");

            var found = VeadotubeInstances.Find(dir, now);
            Assert.Equal(["veadotube", "veadotube mini"], found.Select(i => i.Name));   // the first opened first; stale and broken skipped
            Assert.Equal("veado", found[0].Type);
            Assert.Equal("veadotube", VeadotubeInstances.Pick(found, "")!.Name);
            Assert.Equal("veadotube mini", VeadotubeInstances.Pick(found, "mini")!.Name);
            Assert.Equal("veadotube mini", VeadotubeInstances.Pick(found, "VEADOTUBE MINI")!.Name);
            Assert.Null(VeadotubeInstances.Pick(found, "nope"));
            Assert.Empty(VeadotubeInstances.Find(Path.Combine(dir, "missing"), now));
            Assert.True(VeadotubeInstances.IsAddress("192.168.1.5:2424"));
            Assert.False(VeadotubeInstances.IsAddress("veadotube mini"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public sealed class VeadotubeServiceTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private IReadOnlyList<VeadotubeInstance> _instances = [new("mini-08df3798638fd042-003c2d43", "veadotube mini", "2.2", "127.0.0.1:40657", DateTimeOffset.UtcNow)];
    private VeadotubeService _veado = null!;

    public Task InitializeAsync()
    {
        _veado = new VeadotubeService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, () => _instances, TestTimings.Fast);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _veado.DisposeAsync();
        _env.Dispose();
    }

    private static string Nodes(object body) => "nodes:" + JsonSerializer.Serialize(body);

    private static JsonElement Sent(string text)
    {
        Assert.StartsWith("nodes:", text);
        return JsonDocument.Parse(text["nodes:".Length..]).RootElement;
    }

    /// <summary>Connects and answers like veadotube mini: two nodes, three states, push-to-talk not set yet.</summary>
    private async Task<FakeSocket> ConnectAsync()
    {
        await _veado.ConnectAsync();
        var socket = await _env.Server.NextAsync();
        Assert.Equal("ws://127.0.0.1:40657/?n=Cuebot", socket.Url!.ToString());
        Assert.Equal("listen", Sent(await socket.NextSentAsync()).GetProperty("event").GetString());
        Assert.Equal("list", Sent(await socket.NextSentAsync()).GetProperty("event").GetString());
        socket.Push(Nodes(new
        {
            @event = "list",
            entries = new[] { new { type = "stateEvents", id = "mini", name = "avatar state" }, new { type = "boolean", id = "mini", name = "push-to-talk" } },
        }));
        var asked = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var m = Sent(await socket.NextSentAsync());
            asked.Add($"{m.GetProperty("type").GetString()}:{m.GetProperty("payload").GetProperty("event").GetString()}");
        }
        Assert.Equal(["stateEvents:listen", "stateEvents:list", "stateEvents:peek", "boolean:listen", "boolean:get"], asked);
        socket.Push(Nodes(new
        {
            @event = "payload", type = "stateEvents", id = "mini", name = "avatar state",
            payload = new { @event = "list", states = new[] { new { id = "idle", name = "idle" }, new { id = "happy", name = "happy" }, new { id = "angry", name = "angry" } } },
        }));
        socket.Push(Nodes(new { @event = "payload", type = "stateEvents", id = "mini", name = "avatar state", payload = new { @event = "peek", state = "idle" } }));
        socket.Push(Nodes(new { @event = "payload", type = "boolean", id = "mini", name = "push-to-talk", payload = new { } }));
        await Wait.UntilAsync(() => _veado.StatusMessage.Contains("showing idle"), "state list");
        return socket;
    }

    [Fact]
    public async Task Connects_to_the_open_instance_and_reports_state_changes()
    {
        var socket = await ConnectAsync();
        Assert.Equal("Connected to veadotube mini 2.2: 3 avatar states, showing idle", _veado.StatusMessage);
        Assert.Equal(["idle", "happy", "angry"], _veado.AllStateNames());
        await _env.WaitForEventAsync(VeadotubeEvents.Connected);
        Assert.Empty(_env.Events(VeadotubeEvents.StateChanged));   // the first state is not a change

        socket.Push(Nodes(new { @event = "payload", type = "stateEvents", id = "mini", name = "avatar state", payload = new { @event = "peek", state = "happy" } }));
        var changed = await _env.WaitForEventAsync(VeadotubeEvents.StateChanged);
        Assert.Equal("happy", changed.Args["state"]);
        Assert.Equal("idle", changed.Args["previousState"]);
        Assert.Equal("veadotube mini", changed.Args["veadotube.instance"]);

        // "No value yet" counts as off, so the first press is a change.
        socket.Push(Nodes(new { @event = "payload", type = "boolean", id = "mini", name = "push-to-talk", payload = true }));
        var ptt = await _env.WaitForEventAsync(VeadotubeEvents.SwitchChanged);
        Assert.Equal("push-to-talk", ptt.Args["nodeName"]);
        Assert.Equal("On", ptt.Args["state"]);
    }

    [Fact]
    public async Task State_and_switch_commands_send_node_payloads()
    {
        var socket = await ConnectAsync();
        Assert.Equal("angry", await _veado.ChangeStateAsync("Push", "ANGRY"));
        var push = Sent(await socket.NextSentAsync());
        Assert.Equal("payload", push.GetProperty("event").GetString());
        Assert.Equal("stateEvents", push.GetProperty("type").GetString());
        Assert.Equal("mini", push.GetProperty("id").GetString());
        Assert.Equal("push", push.GetProperty("payload").GetProperty("event").GetString());
        Assert.Equal("angry", push.GetProperty("payload").GetProperty("state").GetString());

        Assert.Null(await _veado.ChangeStateAsync("Clear", ""));
        var clear = Sent(await socket.NextSentAsync()).GetProperty("payload");
        Assert.Equal("clear", clear.GetProperty("event").GetString());
        Assert.False(clear.TryGetProperty("state", out _));

        var unknown = await Assert.ThrowsAsync<StreamAppRequestException>(() => _veado.ChangeStateAsync("Set", "sleepy"));
        Assert.Contains("idle, happy, angry", unknown.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => _veado.ChangeStateAsync("Wiggle", "idle"));

        Assert.True(await _veado.SetSwitchAsync(SwitchMode.On));
        var on = Sent(await socket.NextSentAsync());
        Assert.Equal("boolean", on.GetProperty("type").GetString());
        Assert.Equal("set", on.GetProperty("payload").GetProperty("event").GetString());
        Assert.True(on.GetProperty("payload").GetProperty("value").GetBoolean());

        await _veado.SetSwitchAsync(SwitchMode.Toggle);
        Assert.Equal("toggle", Sent(await socket.NextSentAsync()).GetProperty("payload").GetProperty("event").GetString());
        await Assert.ThrowsAsync<StreamAppRequestException>(() => _veado.SetNumberAsync("volume", 2, false));
    }

    [Fact]
    public async Task Not_open_is_a_friendly_status_and_it_keeps_looking()
    {
        _instances = [];
        await _veado.ConnectAsync();
        await Wait.UntilAsync(() => _veado.StatusMessage.Contains("veadotube is not open"), "status");
        Assert.Equal(ConnectionState.Connecting, _veado.State);
        Assert.Empty(_env.Server.Attempts);

        _instances = [new("mini-1-2", "veadotube mini", "2.2", "127.0.0.1:5555", DateTimeOffset.UtcNow)];
        var socket = await _env.Server.NextAsync();
        Assert.Equal("ws://127.0.0.1:5555/?n=Cuebot", socket.Url!.ToString());
    }
}

public sealed class VnyanServiceTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Send_posts_the_action_and_payload()
    {
        var handler = new Handler((_, _) => Json("""{ "result": 0 }"""));
        var vnyan = new VnyanService(_env.Settings, _env.Log, new HttpClient(handler));
        await vnyan.SendAsync("Wave", new Dictionary<string, string> { ["user"] = "Ann", ["bits"] = "100" });

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:8069/", request.RequestUri!.ToString());
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("Wave", json.GetProperty("action").GetString());
        Assert.Equal("Ann", json.GetProperty("payload").GetProperty("user").GetString());
        Assert.Equal("100", json.GetProperty("payload").GetProperty("bits").GetString());
        Assert.Equal(ConnectionState.Connected, vnyan.State);
        await Assert.ThrowsAsync<ArgumentException>(() => vnyan.SendAsync(" ", new Dictionary<string, string>()));
    }

    [Fact]
    public async Task Refusals_and_closed_ports_are_explained()
    {
        var vnyan = new VnyanService(_env.Settings, _env.Log, new HttpClient(new Handler((_, _) => Json("""{ "result": 2 }"""))));
        var refused = await Assert.ThrowsAsync<StreamAppRequestException>(() => vnyan.TestAsync());
        Assert.Contains("result 2", refused.Message);
        Assert.Equal(ConnectionState.Error, vnyan.State);

        var closed = new VnyanService(_env.Settings, _env.Log,
            new HttpClient(new Handler((_, _) => throw new HttpRequestException("Connection refused"))));
        var ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => closed.TestAsync());
        Assert.Contains("Is VNyan open?", ex.Message);
    }
}

public sealed class VrchatServiceTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private readonly FakeOsc _osc = new();
    private VrchatService _vrchat = null!;

    private sealed class FakeOsc : IOscTransport
    {
        private readonly Channel<OscMessage?> _incoming = Channel.CreateUnbounded<OscMessage?>();
        public List<OscMessage> Sent { get; } = [];
        public (string Host, int Port, int ListenPort)? Opened { get; private set; }
        public string Display => "fake";
        public string? CloseReason => null;
        public string? Warning { get; set; }

        public Task OpenAsync(string host, int port, int listenPort, CancellationToken ct)
        {
            Opened = (host, port, listenPort);
            return Task.CompletedTask;
        }

        public Task SendAsync(OscMessage message, CancellationToken ct)
        {
            lock (Sent) Sent.Add(message);
            return Task.CompletedTask;
        }

        public async Task<OscMessage?> ReceiveAsync(CancellationToken ct) => await _incoming.Reader.ReadAsync(ct);

        public void Push(OscMessage message) => _incoming.Writer.TryWrite(message);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        _vrchat = new VrchatService(_env.Settings, _env.Bus, _env.Log, () => _osc);
        await _vrchat.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        await _vrchat.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public async Task Opens_vrchat_ports_and_turns_osc_into_triggers()
    {
        Assert.Equal(("127.0.0.1", 9000, 9001), _osc.Opened);
        Assert.Equal(ConnectionState.Connected, _vrchat.State);

        _osc.Push(new OscMessage("/avatar/change", "avtr_123"));
        Assert.Equal("avtr_123", (await _env.WaitForEventAsync(VrchatEvents.AvatarChanged)).Args["avatarId"]);

        _osc.Push(new OscMessage("/avatar/parameters/VelocityX", 0.5f));   // noisy: remembered, not a trigger
        _osc.Push(new OscMessage("/avatar/parameters/Hat", true));
        _osc.Push(new OscMessage("/avatar/parameters/Hat", true));          // unchanged: no second event
        _osc.Push(new OscMessage("/avatar/parameters/Mood", 3));
        await _env.WaitForEventAsync(VrchatEvents.ParameterChanged, e => (string?)e.Args["parameter"] == "Mood");
        var events = _env.Events(VrchatEvents.ParameterChanged);
        Assert.Equal(["Hat", "Mood"], events.Select(e => (string?)e.Args["parameter"]));
        Assert.Equal(true, events[0].Args["value"]);
        Assert.Equal("Bool", events[0].Args["type"]);
        Assert.Equal("avtr_123", events[0].Args["vrchat.avatarId"]);
        Assert.Equal(0.5, (double)_vrchat.Parameters["VelocityX"]!, 3);
    }

    [Fact]
    public async Task Parameters_and_chatbox_are_sent_with_the_right_osc_types()
    {
        Assert.Equal(true, await _vrchat.SetParameterAsync("Hat", VrchatParameterType.Bool, "on"));
        Assert.Equal(7, await _vrchat.SetParameterAsync("/avatar/parameters/Mood", VrchatParameterType.Int, "7"));
        Assert.Equal(0.25f, await _vrchat.SetParameterAsync("Blush", VrchatParameterType.Float, "0.25"));
        Assert.Equal(["/avatar/parameters/Hat", "/avatar/parameters/Mood", "/avatar/parameters/Blush"], _osc.Sent.Select(m => m.Address));
        Assert.IsType<int>(_osc.Sent[1].Args[0]);
        Assert.IsType<float>(_osc.Sent[2].Args[0]);

        _osc.Push(new OscMessage("/avatar/parameters/Hat", true));
        await Wait.UntilAsync(() => _vrchat.Parameters.ContainsKey("Hat"), "Hat");
        Assert.Equal(false, await _vrchat.SetParameterAsync("Hat", VrchatParameterType.Bool, "toggle"));

        await Assert.ThrowsAsync<ArgumentException>(() => _vrchat.SetParameterAsync("Mood", VrchatParameterType.Int, "300"));
        await Assert.ThrowsAsync<ArgumentException>(() => _vrchat.SetParameterAsync("Blush", VrchatParameterType.Float, "2"));

        var sent = await _vrchat.SendChatboxAsync(new string('a', 200), immediately: true, sound: false);
        Assert.Equal(144, sent.Length);
        var chat = _osc.Sent.Last();
        Assert.Equal("/chatbox/input", chat.Address);
        Assert.Equal([sent, true, false], chat.Args);
    }

    [Fact]
    public async Task A_taken_listen_port_still_sends_but_says_so()
    {
        await _vrchat.DisconnectAsync();
        Assert.Throws<StreamAppNotConnectedException>(() => _vrchat.SetTypingAsync(true).GetAwaiter().GetResult());
        _osc.Warning = "reply port 9001 is in use, listening on 50000";
        await _vrchat.ConnectAsync();
        Assert.Contains("another app uses port 9001", _vrchat.StatusMessage);
    }
}

public sealed class WarudoServiceTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private WarudoService _warudo = null!;
    private FakeSocket _socket = null!;

    public async Task InitializeAsync()
    {
        _warudo = new WarudoService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast);
        await _warudo.ConnectAsync();
        _socket = await _env.Server.NextAsync();
        Assert.Equal("ws://localhost:19069/", _socket.Url!.ToString());
        // Answers in the order asked, as Warudo's Stream Deck service does (its answers name no kind).
        foreach (var (type, names) in new[] { ("trigger", "[\"Wave\",\"Confetti\"]"), ("toggle", "[\"Hat\"]"), ("message", "[\"Say\"]") })
        {
            var ask = JsonDocument.Parse(await _socket.NextSentAsync()).RootElement;
            Assert.Equal("getReceivers", ask.GetProperty("action").GetString());
            Assert.Equal(type, ask.GetProperty("data").GetProperty("type").GetString());
            _socket.Push($$"""{"action":"getReceivers","error":null,"data":{{names}}}""");
        }
        Assert.Equal("getToggles", JsonDocument.Parse(await _socket.NextSentAsync()).RootElement.GetProperty("action").GetString());
        _socket.Push("""{"action":"getToggles","data":{"Hat":true}}""");
        await Wait.UntilAsync(() => _warudo.StatusMessage.Contains("receivers"), "receivers");
    }

    public async Task DisposeAsync()
    {
        await _warudo.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public void Receivers_and_toggles_are_listed()
    {
        Assert.Equal(["Confetti", "Wave"], _warudo.Receivers(WarudoReceiverKind.Trigger));
        Assert.Equal(["Hat"], _warudo.Receivers(WarudoReceiverKind.Toggle));
        Assert.Equal(["Say"], _warudo.Receivers(WarudoReceiverKind.Message));
        Assert.True(_warudo.Toggles["Hat"]);
        Assert.Equal("Connected to Warudo at localhost:19069: 2 trigger, 1 toggle and 1 message receivers", _warudo.StatusMessage);
    }

    [Fact]
    public async Task Trigger_toggle_and_message_use_warudos_stream_deck_actions()
    {
        await _warudo.TriggerAsync("Wave");
        Assert.Equal("""{"action":"trigger","data":{"receiverName":"Wave"}}""", await _socket.NextSentAsync());
        await _warudo.SendMessageAsync("Say", "hi %user%");
        Assert.Equal("""{"action":"message","data":{"receiverName":"Say","message":"hi %user%"}}""", await _socket.NextSentAsync());

        // Hat is already on: "On" sends nothing, "Off" flips it.
        Assert.True(await _warudo.SetToggleAsync("Hat", SwitchMode.On));
        Assert.False(_socket.TryReadSent(out _));
        // The answer is the state Warudo reports after the flip.
        var off = _warudo.SetToggleAsync("Hat", SwitchMode.Off);
        Assert.Equal("""{"action":"toggle","data":{"receiverName":"Hat"}}""", await _socket.NextSentAsync());
        _socket.Push("""{"action":"toggle","data":{"receiverName":"Hat","state":false,"isResponse":true}}""");
        Assert.False(await off);
        var changed = await _env.WaitForEventAsync(WarudoEvents.ToggleChanged);
        Assert.Equal("Hat", changed.Args["receiver"]);
        Assert.Equal("Off", changed.Args["state"]);
        Assert.False(_warudo.Toggles["Hat"]);
        await Assert.ThrowsAsync<ArgumentException>(() => _warudo.TriggerAsync(""));
    }
}
