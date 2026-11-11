using System.Net;
using System.Text;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.StreamApps.Tests.Support;
using Cuebot.StreamApps.Veadotube;

namespace Cuebot.StreamApps.Tests;

/// <summary>The veadotube fixes and features from the 2026-11 docs review (against a scripted veadotube).</summary>
public sealed class VeadotubeFeatureTests : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private readonly TestEnvironment _env = new();
    private readonly GetHandler _http = new();
    private IReadOnlyList<VeadotubeInstance> _instances =
    [
        new("mini-08df3798638fd042-003c2d43", "veadotube mini", "2.2", "127.0.0.1:40657", DateTimeOffset.UtcNow),
        new("veado-08df3798638fd043-003c2d44", "veadotube", "1.0", "127.0.0.1:2424", DateTimeOffset.UtcNow),
    ];
    private VeadotubeService _veado = null!;

    public Task InitializeAsync()
    {
        _veado = new VeadotubeService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, () => _instances, TestTimings.Fast, _http);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _veado.DisposeAsync();
        _env.Dispose();
    }

    private static string Nodes(object body) => "nodes:" + JsonSerializer.Serialize(body);

    private static JsonElement Sent(string text) => JsonDocument.Parse(text[(text.IndexOf(':') + 1)..]).RootElement;

    private static object State(string id, string hash = "") => new { id, name = id, thumbHash = hash };

    /// <summary>Connects to "veadotube mini" and answers: avatar state (idle/happy/angry), push-to-talk off, a "hype" number 2 of 0-10.</summary>
    private async Task<FakeSocket> ConnectAsync()
    {
        _veado.Settings.Instance = "mini";
        await _veado.ConnectAsync();
        var socket = await _env.Server.NextAsync();
        socket.Push("""instance:{"event":"info","name":"veadotube mini","id":"mini-08df3798638fd042-003c2d43","version":"2.2","language":"en","server":"127.0.0.1:40657"}""");
        await socket.NextSentAsync(); // listen
        await socket.NextSentAsync(); // list
        socket.Push(Nodes(new
        {
            @event = "list",
            entries = new object[]
            {
                new { type = "stateEvents", id = "mini", name = "avatar state" }, new { type = "boolean", id = "mini", name = "push-to-talk" },
                new { type = "number", id = "n1", name = "hype" },
            },
        }));
        for (var i = 0; i < 7; i++) await socket.NextSentAsync(); // listen/list/peek, listen/get, listen/get
        socket.Push(Nodes(new
        {
            @event = "payload", type = "stateEvents", id = "mini", name = "avatar state",
            payload = new { @event = "list", states = new[] { State("idle", "h1"), State("happy"), State("angry") } },
        }));
        socket.Push(Nodes(new { @event = "payload", type = "stateEvents", id = "mini", name = "avatar state", payload = new { @event = "peek", state = "idle" } }));
        socket.Push(Nodes(new { @event = "payload", type = "boolean", id = "mini", name = "push-to-talk", payload = false }));
        socket.Push(Nodes(new { @event = "payload", type = "number", id = "n1", name = "hype", payload = new { value = 2, min = 0, max = 10 } }));
        await Wait.UntilAsync(() => _veado.StatusMessage.Contains("showing idle") && _veado.NumberNodes().Any(n => _veado.NumberValue(n) is not null),
            "connected state");
        return socket;
    }

    /// <summary>The next message Cuebot sends that is not a thumbnail request.</summary>
    private static async Task<JsonElement> NextCommandAsync(FakeSocket socket)
    {
        while (true)
        {
            var m = Sent(await socket.NextSentAsync());
            if (m.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object && p.Str("event") == "thumb") continue;
            return m;
        }
    }

    [Fact]
    public async Task Info_names_the_veadotube_and_thumbnails_are_kept_per_state()
    {
        var socket = await ConnectAsync();
        Assert.Equal("Connected to veadotube mini 2.2: 3 avatar states, showing idle", _veado.StatusMessage);
        var thumb = Sent(await socket.NextSentAsync()).GetProperty("payload");
        Assert.Equal("thumb", thumb.Str("event"));
        Assert.Equal("idle", thumb.Str("state")); // only states with a thumbHash

        socket.Push(Nodes(new
        {
            @event = "payload", type = "stateEvents", id = "mini", name = "avatar state",
            payload = new { @event = "thumb", state = "idle", hash = "h1", width = 128, height = 128, png = Convert.ToBase64String(Png) },
        }));
        var node = _veado.StateNode()!;
        await Wait.UntilAsync(() => _veado.Thumbnail(node, "idle") is not null, "thumbnail");
        Assert.Equal(Png, _veado.Thumbnail(node, "idle"));
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), _veado.CurrentThumbnailUrl(node));
    }

    [Fact]
    public async Task An_unknown_state_makes_cuebot_read_the_state_list_again()
    {
        var socket = await ConnectAsync();
        socket.Push(Nodes(new { @event = "payload", type = "stateEvents", id = "mini", name = "avatar state", payload = new { @event = "peek", state = "sleepy" } }));
        var relist = await NextCommandAsync(socket);
        Assert.Equal("list", relist.GetProperty("payload").Str("event"));
    }

    [Fact]
    public async Task Numbers_keep_their_range_report_changes_and_reset()
    {
        var socket = await ConnectAsync();
        var hype = _veado.NumberNodes().Single();
        Assert.Equal(new VeadotubeNumber(2, 0, 10), _veado.NumberValue(hype));

        await _veado.SetNumberAsync("hype", 5, false, null, null, "");
        var set = (await NextCommandAsync(socket)).GetProperty("payload");
        Assert.Equal("set", set.Str("event"));
        Assert.Equal(5, set.GetProperty("value").GetProperty("value").GetDouble());
        Assert.Equal(10, set.GetProperty("value").GetProperty("max").GetDouble()); // the range is not dropped

        await _veado.SetNumberAsync("hype", 1, true, null, null, "");
        Assert.Equal(1, (await NextCommandAsync(socket)).GetProperty("payload").GetProperty("value").GetDouble()); // add keeps it anyway

        await _veado.SetNumberAsync("hype", 3, false, -5, 5, "");
        Assert.Equal(-5, (await NextCommandAsync(socket)).GetProperty("payload").GetProperty("value").GetProperty("min").GetDouble());
        await Assert.ThrowsAsync<ArgumentException>(() => _veado.SetNumberAsync("hype", 3, false, 1, null, ""));

        socket.Push(Nodes(new { @event = "payload", type = "number", id = "n1", name = "hype", payload = new { value = 7, min = 0, max = 10 } }));
        var changed = await _env.WaitForEventAsync(VeadotubeEvents.NumberChanged);
        Assert.Equal(7.0, changed.Args["value"]);
        Assert.Equal(2.0, changed.Args["previousValue"]);
        Assert.Equal("hype", changed.Args["nodeName"]);

        await _veado.ResetNodeAsync("Number", "hype");
        Assert.Equal("clear", (await NextCommandAsync(socket)).GetProperty("payload").Str("event"));
        Assert.Null(_veado.NumberValue(hype));
    }

    [Fact]
    public async Task Timed_states_and_switches_go_back_by_themselves()
    {
        var socket = await ConnectAsync();
        await _veado.ChangeStateAsync("Push", "angry", "", "", TimeSpan.FromMilliseconds(100));
        Assert.Equal("push", (await NextCommandAsync(socket)).GetProperty("payload").Str("event"));
        var pop = (await NextCommandAsync(socket)).GetProperty("payload");
        Assert.Equal("pop", pop.Str("event"));
        Assert.Equal("angry", pop.Str("state"));

        await _veado.ChangeStateAsync("Set", "happy", "", "", TimeSpan.FromMilliseconds(100));
        Assert.Equal("happy", (await NextCommandAsync(socket)).GetProperty("payload").Str("state"));
        var back = (await NextCommandAsync(socket)).GetProperty("payload");
        Assert.Equal(("set", "idle"), (back.Str("event"), back.Str("state"))); // the state before

        await _veado.SetSwitchAsync(SwitchMode.On, "", "", TimeSpan.FromMilliseconds(100));
        Assert.True((await NextCommandAsync(socket)).GetProperty("payload").GetProperty("value").GetBoolean());
        Assert.False((await NextCommandAsync(socket)).GetProperty("payload").GetProperty("value").GetBoolean()); // like letting go
    }

    [Fact]
    public async Task Random_states_skip_the_one_showing_and_can_be_limited()
    {
        var socket = await ConnectAsync();
        for (var i = 0; i < 10; i++)
        {
            var id = await _veado.ChangeStateAsync("Random", "", "", "", null);
            Assert.NotEqual("idle", id);
            Assert.Equal("set", (await NextCommandAsync(socket)).GetProperty("payload").Str("event"));
        }
        Assert.Equal("angry", await _veado.ChangeStateAsync("Random", "idle, angry", "", "", null));
        await Assert.ThrowsAsync<StreamAppRequestException>(() => _veado.ChangeStateAsync("Random", "nope", "", "", null));
    }

    [Fact]
    public async Task Another_open_veadotube_is_controlled_with_get_commands()
    {
        await ConnectAsync();
        _http.Answer = args => string.Join(" ", args) switch
        {
            "nodes" => "stateEvents s1\nboolean b1\nnumber n9",
            "nodes stateEvents s1 list" => "#1\n#2",
            "nodes stateEvents s1 peek" => "#1",
            "nodes number n9" => "glow",
            "nodes number n9 get" => "0.5 0 1",
            _ => "",
        };

        Assert.Equal("#2", await _veado.ChangeStateAsync("Random", "", "", "veadotube", null));
        Assert.Equal(["nodes", "stateEvents", "s1", "set", "#2"], _http.Commands.Last());
        Assert.All(_http.Hosts, h => Assert.Equal("127.0.0.1:2424", h));

        await _veado.SetNumberAsync("glow", 0.8, false, null, null, "veadotube");
        Assert.Equal(["nodes", "number", "n9", "set", "0.8", "0", "1"], _http.Commands.Single(c => c.Contains("set") && c[1] == "number"));

        var (state, _, states, image) = await _veado.GetStateAsync("", "127.0.0.1:2424");
        Assert.Equal("#1", state);
        Assert.Equal(2, states.Count);
        Assert.Equal("", image);
        await Assert.ThrowsAsync<StreamAppRequestException>(() => _veado.ChangeStateAsync("Set", "#1", "", "nonexistent", null));
    }

    [Fact]
    public async Task Push_to_talk_without_a_value_counts_its_first_press_as_a_change()
    {
        _veado.Settings.Instance = "mini";
        await _veado.ConnectAsync();
        var socket = await _env.Server.NextAsync();
        await socket.NextSentAsync();
        await socket.NextSentAsync();
        socket.Push(Nodes(new { @event = "list", entries = new[] { new { type = "boolean", id = "mini", name = "push-to-talk" } } }));
        await socket.NextSentAsync(); // listen
        await socket.NextSentAsync(); // get: veadotube mini 2.2 does not answer while there is no value
        await Task.Delay(1200);
        socket.Push(Nodes(new { @event = "payload", type = "boolean", id = "mini", name = "push-to-talk", payload = true }));
        var on = await _env.WaitForEventAsync(VeadotubeEvents.SwitchChanged);
        Assert.Equal("On", on.Args["state"]);
    }

    [Fact]
    public void Commands_render_for_the_websocket_and_the_command_line()
    {
        var ranged = new VeadotubeCommand("set", Number: 2, Min: 0, Max: 10);
        Assert.Equal(["set", "2", "0", "10"], ranged.Arguments());
        Assert.Equal("""{"event":"set","value":{"value":2,"min":0,"max":10}}""", JsonSerializer.Serialize(ranged.Payload()));
        Assert.Equal(["set", "1"], new VeadotubeCommand("set", Bool: true).Arguments());
        Assert.Equal("http://127.0.0.1:2424/?cmd0=nodes&cmd1=stateEvents&cmd2=mini&cmd3=set&cmd4=%231",
            VeadotubeProtocol.CommandUrl("127.0.0.1:2424", ["nodes", "stateEvents", "mini", "set", "#1"]).AbsoluteUri);
        Assert.Equal(new VeadotubeNumber(3, 0, 10), VeadotubeProtocol.NumberText("3 0 10"));
        Assert.Equal(new VeadotubeNumber(3, null, null), VeadotubeProtocol.NumberText("3"));
        Assert.Null(VeadotubeProtocol.NumberText(""));
    }

    private sealed class GetHandler : HttpMessageHandler
    {
        public Func<string[], string> Answer { get; set; } = _ => "";
        public List<string[]> Commands { get; } = [];
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var args = Enumerable.Range(0, 10).Select(i => query[$"cmd{i}"]).TakeWhile(a => a is not null).Select(a => a!).ToArray();
            lock (Commands)
            {
                Commands.Add(args);
                Hosts.Add(request.RequestUri.Authority);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer(args), Encoding.UTF8, "text/plain") };
            response.Headers.Add("Veadotube-Command", "Error"); // what veadotube 2.2 sends, also on success
            return Task.FromResult(response);
        }
    }
}
